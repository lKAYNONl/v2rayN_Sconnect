using System.ComponentModel;
using Android.Content;
using Android.Net;

namespace v2rayN.Mobile.Platform;

/// <summary>
/// Connect / disconnect orchestration. Order of a connect:
/// VPN consent (VpnService.prepare) -> start <see cref="SecureVpnService"/> ->
/// start the core through the shared view model's Reload (the
/// <see cref="PlatformHost.CanRunCore"/> gate opens only now) -> create the
/// VPN interface. Core first: if it fails the phone keeps its normal network.
/// Afterwards a core restart (server switch, routing change) keeps the
/// interface up - traffic waits instead of leaking. Runs on the UI thread;
/// blocking tunnel work (hev start/stop) is moved to the thread pool.
/// </summary>
internal sealed class VpnController
{
    private const int MaxRestarts = 3;
    private static readonly TimeSpan RestartsForgottenAfter = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DispatcherTimer? _watchdog;
    private bool _connecting;
    private bool _watchdogBusy;
    private int _restarts;
    private DateTime _lastRestart;
    private MainWindowViewModel? _attachedVm;

    public static VpnController Instance { get; } = new();

    /// <summary>A VPN session is requested: the core may run.</summary>
    public bool SessionWanted { get; private set; }

    /// <summary>Last seen "Always-on VPN" of the running service (null: unknown / disconnected).</summary>
    public bool? AlwaysOn { get; private set; }

    /// <summary>Last seen "Block connections without VPN" - the Android kill switch.</summary>
    public bool? Lockdown { get; private set; }

    /// <summary>Kill switch state or tunnel changed (UI thread).</summary>
    public event Action? StatusChanged;

    private static MainWindowViewModel Vm => AppHost.MainVm;

    /// <summary>Keeps the notification in step with server switches during a session.</summary>
    public void Attach(MainWindowViewModel vm)
    {
        if (_attachedVm != null)
        {
            return;
        }
        _attachedVm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ConnState)
            && _attachedVm?.ConnState == EConnState.On
            && SecureVpnService.Current is { HasTunnel: true })
        {
            _ = RefreshNotificationAsync();
        }
    }

    /// <summary>The dial: ask for consent if needed, then start the service.</summary>
    public async Task ConnectAsync()
    {
        if (_connecting || SessionWanted || !AppHost.IsReady)
        {
            return;
        }
        var activity = MainActivity.Current;
        if (activity == null)
        {
            return;
        }

        _connecting = true;
        try
        {
            if (await ConfigHandler.GetDefaultServer(AppManager.Instance.Config) == null)
            {
                NoticeManager.Instance.Enqueue(ResUI.CheckServerSettings);
                return;
            }

            var consent = VpnService.Prepare(activity);
            if (consent != null && !await activity.RequestVpnConsentAsync(consent))
            {
                NoticeManager.Instance.Enqueue(Tr.VpnDenied);
                return;
            }
            activity.RequestNotificationPermission();

            SessionWanted = true;
            ResetRestarts();
            Vm.ConnState = EConnState.Connecting;
            var intent = new Intent(activity, typeof(SecureVpnService)).SetAction(SecureVpnService.ActionConnect);
            activity.StartService(intent);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("VpnConnect", ex);
            LogBuffer.Instance.Add($"connect failed: {ex.Message}");
            SessionWanted = false;
            Vm.ConnState = Vm.IsRunning ? EConnState.On : EConnState.Off;
        }
        finally
        {
            _connecting = false;
        }
    }

    /// <summary>OnStartCommand of the service: bring the session up.</summary>
    public async Task OnServiceStartedAsync(SecureVpnService service, bool fromSystem)
    {
        if (!AppHost.IsReady)
        {
            service.Shutdown();
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (fromSystem)
            {
                if (SessionWanted && service.HasTunnel)
                {
                    return;
                }
                LogBuffer.Instance.Add("VPN started by the system (always-on)");
                SessionWanted = true;
                ResetRestarts();
            }
            if (!SessionWanted)
            {
                // Cancelled before the service came up.
                await service.ShutdownAsync();
                return;
            }

            var vm = Vm;
            vm.ConnState = EConnState.Connecting;
            await vm.InitTask;
            // On an always-on cold start the startup reload already ran the core.
            if (!CoreManager.Instance.IsCoreRunning || !vm.IsRunning)
            {
                await vm.Reload();
            }
            var coreUp = await WaitForCoreAsync(vm);

            // Disconnect pressed meanwhile: DisconnectAsync (waiting for the gate) cleans up.
            if (!SessionWanted)
            {
                return;
            }
            if (!coreUp)
            {
                await FailLockedAsync(Tr.CoreFailed);
                return;
            }

            var error = await ApplyTunnelAsync(service);
            if (error != null)
            {
                await FailLockedAsync(Tr.TunFailed(error));
                return;
            }
            StartWatchdog();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("VpnSession", ex);
            await FailLockedAsync(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The dial, the notification button, or an error: end the session.</summary>
    public async Task DisconnectAsync(string? reason)
    {
        if (!AppHost.IsReady)
        {
            return;
        }

        // Before the gate: a connect in progress sees it and stops early.
        SessionWanted = false;
        _watchdog?.Stop();
        if (Vm.ConnState == EConnState.Connecting)
        {
            Vm.ConnState = EConnState.Stopping;
        }

        await _gate.WaitAsync();
        try
        {
            await EndSessionAsync();
            if (reason != null)
            {
                LogBuffer.Instance.Add(reason);
                NoticeManager.Instance.Enqueue(reason);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("VpnDisconnect", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The system revoked the VPN (switched off in settings, another VPN app).</summary>
    public async Task OnRevokedAsync()
    {
        if (SessionWanted)
        {
            await DisconnectAsync(Tr.Revoked);
        }
    }

    /// <summary>Split tunneling / LAN settings changed: rebuild the interface of a running session.</summary>
    public async Task ReapplyTunnelAsync()
    {
        if (!SessionWanted || SecureVpnService.Current is not { HasTunnel: true })
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (SessionWanted && SecureVpnService.Current is { HasTunnel: true } service)
            {
                var error = await ApplyTunnelAsync(service);
                if (error != null)
                {
                    await FailLockedAsync(Tr.TunFailed(error));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-reads "Always-on" / "Block without VPN" from the running service.</summary>
    public void RefreshKillState()
    {
        if (SecureVpnService.Current is { HasTunnel: true } service)
        {
            SetKillState(service.AlwaysOnEnabled, service.LockdownOn);
        }
    }

    private async Task<string?> ApplyTunnelAsync(SecureVpnService service)
    {
        var config = AppManager.Instance.Config;
        var plan = TunnelPlan.Build(config.KillSwitchItem, AppHost.PackageName);
        foreach (var bad in plan.InvalidSubnets)
        {
            LogBuffer.Instance.Add($"split tunneling: '{bad}' is not a subnet, ignored");
        }

        var server = await CurrentServerNameAsync();
        var socksPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var (ok, error) = await Task.Run(() => service.EstablishTunnel(plan, server, socksPort));
        if (!ok)
        {
            return error;
        }

        LogBuffer.Instance.Add($"VPN interface up -> socks 127.0.0.1:{socksPort}; {plan.Describe()}");
        service.ShowNotification(Tr.NotifConnected(server), connected: true);
        SetKillState(service.AlwaysOnEnabled, service.LockdownOn);
        return null;
    }

    private async Task RefreshNotificationAsync()
    {
        if (SecureVpnService.Current is { HasTunnel: true } service)
        {
            service.ShowNotification(Tr.NotifConnected(await CurrentServerNameAsync()), connected: true);
        }
    }

    private static async Task<string> CurrentServerNameAsync()
    {
        try
        {
            var item = await AppManager.Instance.GetProfileItem(AppManager.Instance.Config.IndexId);
            return string.IsNullOrEmpty(item?.Remarks) ? "Secure Connect" : item.Remarks;
        }
        catch
        {
            return "Secure Connect";
        }
    }

    /// <summary>
    /// After Reload: true when the core runs. If another reload was in flight
    /// (it then runs ours too), wait for it, up to 15 s.
    /// </summary>
    private async Task<bool> WaitForCoreAsync(MainWindowViewModel vm)
    {
        for (var i = 0; i < 150 && SessionWanted; i++)
        {
            if (!vm.IsReloading)
            {
                return CoreManager.Instance.IsCoreRunning;
            }
            await Task.Delay(100);
        }
        return SessionWanted && CoreManager.Instance.IsCoreRunning;
    }

    /// <summary>Waits (up to <paramref name="timeout"/>) for a running reload to finish.</summary>
    private static async Task WaitReloadIdleAsync(MainWindowViewModel vm, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (vm.IsReloading && DateTime.UtcNow < until)
        {
            await Task.Delay(100);
        }
    }

    /// <summary>Tunnel down, service stopped, core stopped - also one a reload was just starting. Holds the gate.</summary>
    private async Task EndSessionAsync()
    {
        _watchdog?.Stop();
        if (SecureVpnService.Current is { } service)
        {
            await service.ShutdownAsync();
        }

        var vm = Vm;
        if (vm.IsRunning || vm.ConnState != EConnState.Off || CoreManager.Instance.IsCoreRunning)
        {
            await vm.StopCore();
        }
        // A reload (server switch, watchdog) may still be starting a core: it
        // stops it itself once it sees the closed gate; make sure of it.
        await WaitReloadIdleAsync(vm, TimeSpan.FromSeconds(10));
        if (CoreManager.Instance.IsCoreRunning)
        {
            await vm.StopCore();
        }
        SetKillState(null, null);
    }

    /// <summary>A failure during a session: end it and tell the user. Holds the gate.</summary>
    private async Task FailLockedAsync(string message)
    {
        SessionWanted = false;
        try
        {
            await EndSessionAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("VpnFail", ex);
        }
        LogBuffer.Instance.Add(message);
        NoticeManager.Instance.Enqueue(message);
    }

    private void SetKillState(bool? alwaysOn, bool? lockdown)
    {
        if (AlwaysOn == alwaysOn && Lockdown == lockdown)
        {
            return;
        }
        AlwaysOn = alwaysOn;
        Lockdown = lockdown;
        StatusChanged?.Invoke();
    }

    private void ResetRestarts()
    {
        _restarts = 0;
        _lastRestart = DateTime.MinValue;
    }

    /// <summary>
    /// Every 5 s while connected: restart a core or a tunnel forwarder that
    /// died (a few times in a row), and pick up kill switch changes made in the
    /// system settings.
    /// </summary>
    private void StartWatchdog()
    {
        if (_watchdog == null)
        {
            _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _watchdog.Tick += async (_, _) => await WatchdogTickAsync();
        }
        _watchdog.Start();
    }

    private async Task WatchdogTickAsync()
    {
        if (_watchdogBusy)
        {
            return;
        }
        if (!SessionWanted)
        {
            _watchdog?.Stop();
            return;
        }

        _watchdogBusy = true;
        try
        {
            RefreshKillState();

            var vm = Vm;
            if (vm.IsReloading)
            {
                return;     // a core restart is under way
            }

            var coreUp = CoreManager.Instance.IsCoreRunning;
            var tunnelUp = HevTunnel.IsRunning || SecureVpnService.Current is not { HasTunnel: true };
            if (coreUp && tunnelUp)
            {
                if (_restarts > 0 && DateTime.UtcNow - _lastRestart > RestartsForgottenAfter)
                {
                    _restarts = 0;
                }
                return;
            }

            await _gate.WaitAsync();
            try
            {
                if (!SessionWanted)
                {
                    return;
                }
                // Re-check under the gate: a re-plan or reload that held it may have fixed things.
                coreUp = CoreManager.Instance.IsCoreRunning;
                tunnelUp = HevTunnel.IsRunning || SecureVpnService.Current is not { HasTunnel: true };
                if (vm.IsReloading || (coreUp && tunnelUp))
                {
                    return;
                }
                if (++_restarts > MaxRestarts)
                {
                    await FailLockedAsync(coreUp ? Tr.TunFailed("hev-socks5-tunnel stopped") : Tr.CoreFailed);
                    return;
                }
                _lastRestart = DateTime.UtcNow;

                if (!coreUp)
                {
                    LogBuffer.Instance.Add($"The core exited - restarting ({_restarts}/{MaxRestarts})");
                    await vm.Reload();
                }
                else if (SecureVpnService.Current is { HasTunnel: true } service)
                {
                    LogBuffer.Instance.Add($"The tunnel forwarder stopped - restarting ({_restarts}/{MaxRestarts})");
                    var error = await ApplyTunnelAsync(service);
                    if (error != null)
                    {
                        LogBuffer.Instance.Add(Tr.TunFailed(error));
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("VpnWatchdog", ex);
        }
        finally
        {
            _watchdogBusy = false;
        }
    }
}
