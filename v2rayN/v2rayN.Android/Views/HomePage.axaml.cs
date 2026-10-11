using System.ComponentModel;
using v2rayN.Mobile.Platform;

namespace v2rayN.Mobile.Views;

/// <summary>
/// The connect stage, rendered by one method from the shared view model like
/// the desktop dashboard (UpdateStage): state label, active server, dial with
/// session timer, exit IP, system kill switch and live speed.
/// </summary>
public partial class HomePage : UserControl
{
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MainWindowViewModel? _vm;

    public HomePage()
    {
        InitializeComponent();

        txtCoreLabel.Text = Tr.Core;
        txtMbps.Text = Tr.Mbps;
        _sessionTimer.Tick += (_, _) => UpdateTimer();

        btnDial.Click += async (_, _) => await OnDialAsync();
        btnServer.Click += (_, _) => MainView.Current?.SelectTab(1);
        btnKill.Click += (_, _) => MainActivity.Current?.OpenVpnSettings();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!AppHost.IsReady)
        {
            return;
        }

        _vm = AppHost.MainVm;
        _vm.PropertyChanged += OnChanged;
        _vm.StatusBarViewModel.PropertyChanged += OnChanged;
        _vm.ProfilesViewModel.PropertyChanged += OnChanged;
        VpnController.Instance.StatusChanged += OnStatusChanged;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnChanged;
            _vm.StatusBarViewModel.PropertyChanged -= OnChanged;
            _vm.ProfilesViewModel.PropertyChanged -= OnChanged;
            _vm = null;
        }
        VpnController.Instance.StatusChanged -= OnStatusChanged;
        _sessionTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnStatusChanged() => Dispatcher.UIThread.Post(Render);

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.ConnState):
            case nameof(MainWindowViewModel.PublicIp):
            case nameof(MainWindowViewModel.ConnectedSince):
            case nameof(MainWindowViewModel.CoreName):
            case nameof(StatusBarViewModel.SpeedUpDisplay):
            case nameof(StatusBarViewModel.SpeedDownDisplay):
            case nameof(ProfilesViewModel.SelectedProfile):
                if (Dispatcher.UIThread.CheckAccess())
                {
                    Render();
                }
                else
                {
                    Dispatcher.UIThread.Post(Render);
                }
                break;
        }
    }

    private async Task OnDialAsync()
    {
        var vm = _vm;
        if (vm == null)
        {
            return;
        }
        try
        {
            var controller = VpnController.Instance;
            switch (vm.ConnState)
            {
                case EConnState.On:
                    await controller.DisconnectAsync(null);
                    break;

                case EConnState.Connecting when controller.SessionWanted:
                    // Stuck while connecting: let the user cancel.
                    await controller.DisconnectAsync(null);
                    break;

                case EConnState.Off:
                    await controller.ConnectAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Dial", ex);
        }
    }

    private void Render()
    {
        var vm = _vm;
        if (vm == null)
        {
            return;
        }

        var state = vm.ConnState;
        var on = state == EConnState.On;
        var busy = state is EConnState.Connecting or EConnState.Stopping;
        var controller = VpnController.Instance;

        // Whitelist split tunneling with entries: only the listed apps/subnets are tunneled.
        var ks = AppManager.Instance.Config.KillSwitchItem;
        var split = ks is { SplitTunnelWhitelist: true }
                    && (ks.AndroidApps?.Count > 0 || ks.BypassSubnets?.Count > 0);
        var tunnelLabel = split ? Tr.StateSplit : Tr.StateProtected;
        txtState.Text = state switch
        {
            EConnState.Connecting => Tr.StateConnecting,
            EConnState.Stopping => Tr.StateStopping,
            EConnState.On when controller.Lockdown == true => tunnelLabel + Tr.StateLockedSuffix,
            EConnState.On => tunnelLabel,
            _ => Tr.StateOff,
        };
        txtState.Foreground = on ? Ui.Accent : Ui.Muted;

        // Active server
        var profile = vm.ProfilesViewModel.SelectedProfile;
        var hasProfile = !string.IsNullOrEmpty(profile?.IndexId);
        txtServerName.Text = hasProfile ? profile!.Remarks : Tr.NoProfile;
        txtServerInfo.Text = hasProfile ? profile!.ProtocolInfo : Tr.ChooseProfileHint;

        // Dial
        dialGlow.IsVisible = on;
        ringOn.IsVisible = on;
        ringBusy.IsVisible = busy;
        ringBusy.Classes.Set("spin", busy);
        dialDisc.Stroke = on ? Ui.DialOn : Ui.Line;
        iconPower.Stroke = on ? Ui.Accent : busy ? Ui.Accent2 : Ui.Muted;
        txtVerb.IsVisible = !on;
        txtVerb.Text = busy ? "…" : Tr.DialConnect;
        txtTimer.IsVisible = on;
        if (on)
        {
            UpdateTimer();
            _sessionTimer.Start();
        }
        else
        {
            _sessionTimer.Stop();
        }

        // IP chip
        txtIp.Text = string.IsNullOrEmpty(vm.PublicIp) ? "—" : vm.PublicIp;
        dotIp.Background = on && !string.IsNullOrEmpty(vm.PublicIp) ? Ui.Accent2 : Ui.Faint;

        // Kill switch chip: Android's own "Block connections without VPN".
        if (on && controller.Lockdown == true)
        {
            chipKill.BorderBrush = Ui.LockOn;
            dotKill.Background = Ui.Warn;
            dotKill.BoxShadow = Ui.GlowWarn;
            txtKill.Foreground = Ui.Warn;
            txtKill.Text = Tr.KillLocked;
        }
        else
        {
            chipKill.BorderBrush = Ui.Line;
            dotKill.Background = Ui.Faint;
            dotKill.BoxShadow = default;
            txtKill.Foreground = Ui.Muted;
            txtKill.Text = on ? Tr.KillOffConnected : Tr.KillSystem;
        }

        // Status strip
        dotCore.Background = on ? Ui.Accent : Ui.Faint;
        dotCore.BoxShadow = on ? Ui.GlowAccent : default;
        txtCore.Text = on ? vm.CoreName : "—";
        txtDown.Text = on ? vm.StatusBarViewModel.SpeedDownDisplay : "0.0";
        txtUp.Text = on ? vm.StatusBarViewModel.SpeedUpDisplay : "0.0";
    }

    private void UpdateTimer()
    {
        if (_vm == null)
        {
            return;
        }
        var elapsed = DateTime.Now - _vm.ConnectedSince;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }
        txtTimer.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
