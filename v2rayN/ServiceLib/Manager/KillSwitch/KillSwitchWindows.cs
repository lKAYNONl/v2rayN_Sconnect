namespace ServiceLib.Manager;

/// <summary>
/// Windows kill switch implemented with the built-in Windows Firewall
/// (Defender Firewall / WFP) through the NetSecurity PowerShell module.
///
/// Model (default-deny outbound + allow-list):
///   1. Save each profile's current DefaultOutboundAction to a state file.
///   2. Add a small group of Outbound/Allow rules:
///        - the core process (sing-box.exe)  -> can reach the VPN server
///        - loopback (127.0.0.1 / ::1)        -> local proxy + UI<->core
///        - DHCP (UDP 67/68)                  -> keep the physical lease alive
///        - optionally: LAN, bypass apps, bypass subnets (split tunneling)
///   3. Flip DefaultOutboundAction to Block for all profiles.
/// Result: in TUN mode the only process that egresses to the physical NIC is
/// the core itself; every other destination (incl. IPv6 and direct DNS to :53)
/// is dropped -> no leak if the core drops or during connect/disconnect.
///
/// Requires administrator rights — which TUN mode already requires, so the app
/// is elevated whenever this runs.
///
/// KNOWN LIMITATION: because default-deny only drops traffic that matches no
/// rule, a *pre-existing user Outbound/Allow rule* for some other program could
/// in principle let that program egress directly. In TUN mode this is largely
/// moot (apps hand packets to the TUN device, not to a direct socket — only the
/// core dials out), but to be bullet-proof against that case the roadmap adds a
/// native-WFP backend with its own high-weight sublayer (WireGuard-style),
/// which overrides all other rules and auto-removes on process exit.
/// </summary>
[SupportedOSPlatform("windows")]
public class KillSwitchWindows : IKillSwitchService
{
    private const string RuleGroup = "v2rayN-KillSwitch";
    private const string StateFileName = "killswitch_state.json";
    private static readonly string[] Profiles = { "Domain", "Private", "Public" };

    private bool _isArmed;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsArmed => _isArmed;

    private static string StatePath => Utils.GetConfigPath(StateFileName);

    public async Task<bool> ArmAsync(KillSwitchContext context)
    {
        if (context.CoreExePath.IsNullOrEmpty() || !File.Exists(context.CoreExePath))
        {
            Logging.SaveLog($"{RuleGroup}: core path not found, refusing to arm: {context.CoreExePath}");
            return false;
        }

        await _lock.WaitAsync();
        try
        {
            // 1) Capture and persist the current default outbound action BEFORE
            //    touching anything, so a crash mid-arm is recoverable.
            var saved = await ReadOutboundActionsAsync();
            var state = new KillSwitchState
            {
                Armed = true,
                SavedOutboundAction = saved,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            };
            await WriteStateAsync(state);

            // 2) Build + apply the allow-list and flip the default to Block.
            var ok = await RunPowerShellOkAsync(BuildArmScript(context));
            if (!ok)
            {
                // Roll back anything partially applied, clear state.
                await ApplyDisarmAsync(saved);
                await DeleteStateAsync();
                _isArmed = false;
                return false;
            }

            _isArmed = true;
            Logging.SaveLog($"{RuleGroup}: armed (core={Path.GetFileName(context.CoreExePath)}, allowLan={context.AllowLan})");
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(RuleGroup, ex);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DisarmAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var state = await ReadStateAsync();
            var saved = state?.SavedOutboundAction ?? DefaultAllowActions();
            await ApplyDisarmAsync(saved);
            await DeleteStateAsync();
            _isArmed = false;
            Logging.SaveLog($"{RuleGroup}: disarmed, outbound restored");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(RuleGroup, ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RecoverIfNeededAsync()
    {
        try
        {
            var state = await ReadStateAsync();
            if (state is not { Armed: true })
            {
                return;
            }

            // The previous run armed the lock and never disarmed cleanly.
            // Fail open on relaunch so the user is never bricked: restore the
            // saved outbound action and remove our rules.
            Logging.SaveLog($"{RuleGroup}: stale armed state from {state.TimestampUtc} found on startup — restoring network");
            await ApplyDisarmAsync(state.SavedOutboundAction.Count > 0 ? state.SavedOutboundAction : DefaultAllowActions());
            await DeleteStateAsync();
            _isArmed = false;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(RuleGroup, ex);
        }
    }

    #region PowerShell scripts

    private static string BuildArmScript(KillSwitchContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine("try {");
        // Clear any stale rules from a previous run first (idempotent re-arm).
        sb.AppendLine($"  Remove-NetFirewallRule -Group {PsQuote(RuleGroup)} -ErrorAction SilentlyContinue");

        // Core process — the only program allowed direct egress.
        sb.AppendLine(NewRule("Core", $"-Direction Outbound -Action Allow -Program {PsQuote(ctx.CoreExePath)}"));

        // Loopback — local proxy ports and UI<->core communication.
        sb.AppendLine(NewRule("Loopback", "-Direction Outbound -Action Allow -RemoteAddress 127.0.0.1,::1"));

        // DHCP — let the physical adapter keep/renew its lease under default-deny.
        sb.AppendLine(NewRule("DHCP", "-Direction Outbound -Action Allow -Protocol UDP -RemotePort 67,68"));

        // Optional: keep the local network reachable.
        if (ctx.AllowLan)
        {
            sb.AppendLine(NewRule("LAN", "-Direction Outbound -Action Allow -RemoteAddress LocalSubnet"));
        }

        // Split-tunnel bypass: extra subnets allowed direct.
        if (ctx.AllowedSubnets is { Count: > 0 })
        {
            var subnets = string.Join(",", ctx.AllowedSubnets.Select(s => s.Trim()).Where(s => !s.IsNullOrEmpty()));
            if (!subnets.IsNullOrEmpty())
            {
                sb.AppendLine(NewRule("BypassNets", $"-Direction Outbound -Action Allow -RemoteAddress {subnets}"));
            }
        }

        // Split-tunnel bypass: extra programs allowed direct. Created through the
        // firewall COM API in one loop - a folder can expand to many programs and
        // New-NetFirewallRule per program is slow and bloats the command line.
        var programs = ctx.AllowedPrograms.Where(p => !p.IsNullOrEmpty()).Select(PsQuote).ToList();
        if (programs.Count > 0)
        {
            sb.AppendLine("  $fw = New-Object -ComObject HNetCfg.FwPolicy2");
            sb.AppendLine("  $i = 0");
            sb.AppendLine($"  foreach ($p in @({string.Join(",", programs)})) {{");
            sb.AppendLine("    $r = New-Object -ComObject HNetCfg.FWRule");
            sb.AppendLine($"    $r.Name = {PsQuote(RuleGroup + " BypassApp")} + $i; $i++");
            sb.AppendLine("    $r.ApplicationName = $p");
            sb.AppendLine("    $r.Direction = 2; $r.Action = 1; $r.Profiles = 2147483647");
            sb.AppendLine($"    $r.Grouping = {PsQuote(RuleGroup)}");
            sb.AppendLine("    $r.Enabled = $true");
            sb.AppendLine("    $fw.Rules.Add($r)");
            sb.AppendLine("  }");
        }

        // Finally: default-deny all other outbound on every profile.
        sb.AppendLine("  Set-NetFirewallProfile -All -DefaultOutboundAction Block");
        sb.AppendLine("  'KS_OK'");
        sb.AppendLine("} catch { 'KS_ERR:' + $_.Exception.Message }");
        return sb.ToString();
    }

    private static string NewRule(string suffix, string extra)
    {
        var name = $"{RuleGroup} {suffix}";
        return $"  New-NetFirewallRule -DisplayName {PsQuote(name)} -Group {PsQuote(RuleGroup)} -Profile Any {extra} -ErrorAction Stop | Out-Null";
    }

    private async Task<Dictionary<string, string>> ReadOutboundActionsAsync()
    {
        // Emits lines like "Domain=Allow". Robust to localisation (enum names are invariant).
        const string script =
            "$ErrorActionPreference='SilentlyContinue';" +
            "Get-NetFirewallProfile -All | ForEach-Object { \"$($_.Name)=$($_.DefaultOutboundAction)\" }";
        var output = await RunPowerShellAsync(script) ?? string.Empty;

        var result = DefaultAllowActions();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split('=');
            if (parts.Length == 2 && Profiles.Contains(parts[0]))
            {
                result[parts[0]] = parts[1].Trim();
            }
        }
        return result;
    }

    private async Task ApplyDisarmAsync(Dictionary<string, string> saved)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
        foreach (var name in Profiles)
        {
            var action = saved.TryGetValue(name, out var a) && !a.IsNullOrEmpty() ? a : "Allow";
            if (string.Equals(action, "NotConfigured", StringComparison.OrdinalIgnoreCase))
            {
                action = "Allow"; // sane fallback; system default behaves as Allow
            }
            sb.AppendLine($"Set-NetFirewallProfile -Name {name} -DefaultOutboundAction {action}");
        }
        // Remove our rules last, so there is never a moment of default-deny with no allow-list.
        sb.AppendLine($"Remove-NetFirewallRule -Group {PsQuote(RuleGroup)} -ErrorAction SilentlyContinue");
        await RunPowerShellAsync(sb.ToString());
    }

    #endregion PowerShell scripts

    #region Helpers

    private static Dictionary<string, string> DefaultAllowActions() =>
        new() { ["Domain"] = "Allow", ["Private"] = "Allow", ["Public"] = "Allow" };

    private static string PsQuote(string s) => "'" + (s ?? string.Empty).Replace("'", "''") + "'";

    private static async Task<string?> RunPowerShellAsync(string script)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script };
        return await Utils.GetCliWrapOutput("powershell", args);
    }

    private static async Task<bool> RunPowerShellOkAsync(string script)
    {
        var output = await RunPowerShellAsync(script);
        if (output is null)
        {
            return false;
        }
        if (output.Contains("KS_ERR:"))
        {
            Logging.SaveLog($"{RuleGroup}: {output.Trim()}");
            return false;
        }
        return output.Contains("KS_OK");
    }

    private static async Task<KillSwitchState?> ReadStateAsync()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }
            var json = await File.ReadAllTextAsync(StatePath);
            return JsonUtils.Deserialize<KillSwitchState>(json);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteStateAsync(KillSwitchState state)
    {
        try
        {
            await File.WriteAllTextAsync(StatePath, JsonUtils.Serialize(state));
        }
        catch (Exception ex)
        {
            Logging.SaveLog(RuleGroup, ex);
        }
    }

    private static Task DeleteStateAsync()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    #endregion Helpers
}
