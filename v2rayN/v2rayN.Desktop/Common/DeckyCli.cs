using ServiceLib.Handler.Builder;

namespace v2rayN.Desktop.Common;

/// <summary>
/// Headless mode for the Steam Deck (Decky Loader) plugin. It uses the same
/// profiles, routing and split tunneling as the app, without a window:
/// <code>
///   v2rayN --decky list                 profiles and the active one
///   v2rayN --decky config &lt;id&gt; [--all]  configs to run that profile in TUN mode:
///                                       sing-box TUN + the profile's own core
///                                       (--all: the whole Deck, split lists ignored)
///   v2rayN --decky split                split tunneling and routing settings
///   v2rayN --decky split-set &lt;json&gt;     change them: {"whitelist":true,
///                                       "apps":[...],"useRoutingRules":true}
/// </code>
/// The answer is one JSON line on stdout: <c>{"ok":true,...}</c> or
/// <c>{"ok":false,"error":"..."}</c>. The plugin starts the cores itself
/// (sing-box as root for the TUN), so this mode never starts one.
/// </summary>
internal static class DeckyCli
{
    public const string Flag = "--decky";

    public static int Run(string[] args)
    {
        object answer;
        int code;
        try
        {
            (answer, code) = RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("DeckyCli", ex);
            (answer, code) = (Fail(ex.Message), 1);
        }
        Console.Out.WriteLine(JsonUtils.Serialize(answer, false));
        Console.Out.Flush();
        return code;
    }

    private static object Fail(string error) => new { ok = false, error };

    private static async Task<(object, int)> RunAsync(string[] args)
    {
        if (!AppManager.Instance.InitApp())
        {
            return (Fail("Cannot load the v2rayN settings (guiConfigs)."), 1);
        }
        AppManager.Instance.InitComponents();
        var config = AppManager.Instance.Config;
        // What the app sets up on its first start (no-ops afterwards).
        await ConfigHandler.InitBuiltinRouting(config);
        await ConfigHandler.InitBuiltinDNS(config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(config);
        await ProfileExManager.Instance.Init();
        // Copies the cores to the data folder when the app folder is read-only
        // (as in the Decky plugin) and makes them executable.
        await CoreManager.Instance.Init(config, (_, _) => Task.CompletedTask);

        var command = args.Length > 0 ? args[0] : string.Empty;
        return command switch
        {
            "list" => await ListAsync(config),
            "config" when args.Length > 1 => await ConfigAsync(config, args[1], args.Skip(2).Contains("--all")),
            "split" => (SplitAnswer(config), 0),
            "split-set" when args.Length > 1 => await SplitSetAsync(config, args[1]),
            _ => (Fail($"Usage: {Flag} list | config <profile id> [--all] | split | split-set <json>"), 2),
        };
    }

    private static async Task<(object, int)> ListAsync(Config config)
    {
        // Plain entities (no UI models): this mode runs without the app's UI setup.
        var items = await AppManager.Instance.ProfileItems(string.Empty) ?? [];
        var groups = (await AppManager.Instance.SubItems() ?? [])
            .Where(t => !t.Id.IsNullOrEmpty())
            .GroupBy(t => t.Id)
            .ToDictionary(g => g.Key, g => g.First().Remarks ?? string.Empty);
        var profiles = items
            .Where(t => t.ConfigType != EConfigType.Custom)   // custom configs are for a specific core
            .Select(t => new
            {
                id = t.IndexId,
                name = t.Remarks,
                type = t.ConfigType.ToString(),
                address = t.Address,
                port = t.Port,
                group = !t.Subid.IsNullOrEmpty() && groups.TryGetValue(t.Subid, out var group) ? group : string.Empty,
                sort = ProfileExManager.Instance.GetSort(t.IndexId),
            })
            .OrderBy(t => t.sort)
            .ToList();
        return (new { ok = true, active = config.IndexId ?? string.Empty, profiles }, 0);
    }

    /// <summary>Split tunneling and routing settings (shared with the app).</summary>
    private static object SplitAnswer(Config config)
    {
        var ks = config.KillSwitchItem ?? new();
        return new
        {
            ok = true,
            whitelist = ks.SplitTunnelWhitelist,
            apps = ks.BypassApps ?? [],
            subnets = ks.BypassSubnets ?? [],
            useRoutingRules = ks.UseRoutingRules,
        };
    }

    /// <summary>The fields <c>split-set</c> can change; missing ones stay as they are.</summary>
    internal sealed class SplitChange
    {
        public bool? Whitelist { get; set; }
        public List<string>? Apps { get; set; }
        public bool? UseRoutingRules { get; set; }
    }

    private static async Task<(object, int)> SplitSetAsync(Config config, string json)
    {
        var change = JsonUtils.Deserialize<SplitChange>(json);
        if (change is null)
        {
            return (Fail("Bad split settings JSON."), 2);
        }
        var ks = config.KillSwitchItem ??= new();
        if (change.Whitelist is { } whitelist)
        {
            ks.SplitTunnelWhitelist = whitelist;
        }
        if (change.UseRoutingRules is { } useRoutingRules)
        {
            ks.UseRoutingRules = useRoutingRules;
        }
        if (change.Apps is { } apps)
        {
            ks.BypassApps = apps
                .Select(t => t?.Trim() ?? string.Empty)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        if (await ConfigHandler.SaveConfig(config) != 0)
        {
            return (Fail("Cannot save the v2rayN settings (guiConfigs)."), 1);
        }
        return (SplitAnswer(config), 0);
    }

    private static async Task<(object, int)> ConfigAsync(Config config, string indexId, bool wholeDeck)
    {
        var node = await AppManager.Instance.GetProfileItem(indexId);
        if (node is null)
        {
            return (Fail($"Profile not found: {indexId}"), 1);
        }
        if (node.ConfigType == EConfigType.Custom)
        {
            return (Fail("Custom configs cannot be run from the plugin."), 1);
        }

        // The plugin always runs the profile in TUN mode; split tunneling decides
        // which programs use the tunnel. A copy of the settings, so the app's own
        // TUN switch stays as it is.
        var appConfig = JsonUtils.DeepCopy(config);
        if (appConfig is null)
        {
            // Never fall back to the live settings: they are saved below.
            return (Fail("Cannot copy the v2rayN settings."), 1);
        }
        appConfig.TunModeItem.EnableTun = true;
        // The TUN is always sing-box's (process-based split tunneling, DNS
        // hijack); the profile itself runs on the core the app would use for it -
        // Xray for VLESS / VMess / Trojan... - behind sing-box, exactly as in the
        // desktop app. Running everything on sing-box does not work with current
        // servers: Xray 26.9.9+ refuses REALITY clients without the post-quantum
        // key share, which sing-box (up to 1.14.4 / 1.15 alpha) does not send
        // ("reality verification failed").
        appConfig.TunModeItem.EnableLegacyProtect = true;
        if (wholeDeck && appConfig.KillSwitchItem is { } ks)
        {
            // "The whole Deck" in the plugin: no split tunneling for this run; the
            // saved lists stay for the "only listed" / "all but listed" modes.
            ks.SplitTunnelWhitelist = true;
            ks.BypassApps = [];
            ks.BypassSubnets = [];
        }

        var built = await CoreConfigContextBuilder.BuildAll(appConfig, node);
        if (!built.Success)
        {
            return (Fail(string.Join("; ", built.CombinedValidatorResult.Errors)), 1);
        }

        // "config" is always the sing-box TUN config. When the profile runs on
        // another core, that core gets "decky-main.json" and the plugin starts it
        // first ("main" in the answer); sing-box then sends everything to its
        // local SOCKS port.
        var tunFile = Utils.GetBinConfigPath("decky.json");
        var tunContext = built.PreSocksResult?.Context ?? built.MainResult.Context;
        object? main = null;
        if (built.PreSocksResult is not null)
        {
            var mainContext = built.MainResult.Context;
            var mainFile = Utils.GetBinConfigPath("decky-main.json");
            var mainResult = await CoreConfigHandler.GenerateClientConfig(mainContext, mainFile);
            if (mainResult.Success != true)
            {
                return (Fail(mainResult.Msg ?? "The core config could not be generated."), 1);
            }
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(mainContext.RunCoreType);
            var exe = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var exeMsg);
            if (coreInfo is null || exe.IsNullOrEmpty())
            {
                return (Fail(exeMsg.IsNullOrEmpty() ? $"Core not found: {mainContext.RunCoreType}" : exeMsg), 1);
            }
            main = new
            {
                core = mainContext.RunCoreType.ToString(),
                exe,
                // e.g. "run -c {0}"
                args = (coreInfo.Arguments ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t == "{0}" ? mainFile : t)
                    .ToList(),
                env = coreInfo.Environment?
                    .Where(t => t.Value is not null)
                    .ToDictionary(t => t.Key, t => string.Format(t.Value!, mainFile))
                    ?? new Dictionary<string, string>(),
                config = mainFile,
                // sing-box connects here: started only once the core listens.
                port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks),
            };
        }
        else if (tunContext.RunCoreType != ECoreType.sing_box)
        {
            // No sing-box TUN came out (should not happen with the settings above).
            return (Fail($"TUN is not available for the core {tunContext.RunCoreType}."), 1);
        }

        var result = await CoreConfigHandler.GenerateClientConfig(tunContext, tunFile);
        if (result.Success != true)
        {
            return (Fail(result.Msg ?? "sing-box config could not be generated."), 1);
        }

        // Picking a profile in the plugin makes it the active one in the app too.
        await ConfigHandler.SetDefaultServerIndex(config, indexId);

        return (new
        {
            ok = true,
            id = node.IndexId,
            name = node.Remarks,
            config = tunFile,
            main,
            cwd = Utils.GetBinConfigPath(),
            warnings = built.CombinedValidatorResult.Warnings,
        }, 0);
    }
}
