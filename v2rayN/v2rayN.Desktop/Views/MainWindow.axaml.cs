using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Controls.Notifications;
using Avalonia.VisualTree;
using DialogHostAvalonia;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Manager;

namespace v2rayN.Desktop.Views;

public partial class MainWindow : WindowBase<MainWindowViewModel>
{
    private static Config _config;
    private readonly WindowNotificationManager? _manager;
    private CheckUpdateView? _checkUpdateView;
    private BackupAndRestoreView? _backupAndRestoreView;
    private bool _blCloseByUser = false;

    // Secure Connect dashboard
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<string> _bypassApps = [];
    private readonly ObservableCollection<string> _bypassSubnets = [];
    private bool _loadingSettings;

    private static readonly IBrush BrFg = Brush("#E8EBF6");
    private static readonly IBrush BrMuted = Brush("#8B95B4");
    private static readonly IBrush BrFaint = Brush("#5B648A");
    private static readonly IBrush BrLine = Brush("#273055");
    private static readonly IBrush BrAccent = Brush("#37D3C2");
    private static readonly IBrush BrAccent2 = Brush("#4F8CFF");
    private static readonly IBrush BrWarn = Brush("#F5B83D");
    private static readonly IBrush BrDialOn = Brush("#319296");
    private static readonly IBrush BrLockOn = Brush("#987B48");
    private static readonly BoxShadows GlowAccent = BoxShadows.Parse("0 0 8 0 #37D3C2");
    private static readonly BoxShadows GlowWarn = BoxShadows.Parse("0 0 8 0 #F5B83D");

    public MainWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;
        _manager = new WindowNotificationManager(TopLevel.GetTopLevel(this)) { MaxItems = 3, Position = NotificationPosition.TopRight };

        KeyDown += MainWindow_KeyDown;
        menuSettingsSetUWP.Click += MenuSettingsSetUWP_Click;
        menuPromotion.Click += MenuPromotion_Click;
        menuCheckUpdate.Click += MenuCheckUpdate_Click;
        btnNewUpdate.Click += MenuCheckUpdate_Click;
        menuBackupAndRestore.Click += MenuBackupAndRestore_Click;
        menuClose.Click += MenuClose_Click;
        menuAppearance.Click += MenuAppearance_Click;
        menuAppearance.Header = $"{ResUI.TbSettingsTheme} / {ResUI.TbSettingsLanguage}";

        // Own title bar: caption buttons (the bar itself is the drag area).
        btnMinimize.Click += (_, _) => WindowState = WindowState.Minimized;
        btnMaximize.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        btnClose.Click += (_, _) => Close();

        // Profiles rail: single click activates, double click also connects.
        lstCards.Tapped += LstCards_Tapped;
        lstCards.DoubleTapped += LstCards_DoubleTapped;
        lstCards.SelectionChanged += LstCards_SelectionChanged;

        // Stage
        btnDial.Click += BtnDial_Click;
        btnSettings.Click += (_, _) => OpenSettings();
        btnTun.Click += (_, _) =>
        {
            if (ViewModel?.StatusBarViewModel is { } sb)
            {
                sb.EnableTun = !sb.EnableTun;
            }
        };
        _sessionTimer.Tick += (_, _) => UpdateTimer();

        // Settings panel
        btnSettingsClose.Click += (_, _) => CloseSettings();
        settingsOverlay.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, settingsOverlay))
            {
                CloseSettings();
            }
        };
        lstBypassApps.ItemsSource = _bypassApps;
        lstBypassSubnets.ItemsSource = _bypassSubnets;
        lstBypassApps.AddHandler(Button.ClickEvent, (_, e) => RemoveTag(e, _bypassApps));
        lstBypassSubnets.AddHandler(Button.ClickEvent, (_, e) => RemoveTag(e, _bypassSubnets));
        txtAddApp.AddHandler(KeyDownEvent, (_, e) => OnAddKey(e, txtAddApp, _bypassApps, NormalizeApp), RoutingStrategies.Tunnel);
        txtAddSubnet.AddHandler(KeyDownEvent, (_, e) => OnAddKey(e, txtAddSubnet, _bypassSubnets, NormalizeSubnet), RoutingStrategies.Tunnel);
        tglKill.IsCheckedChanged += (_, _) => OnSettingToggled();
        tglLan.IsCheckedChanged += (_, _) => OnSettingToggled();

        conTheme.Content ??= new ThemeSettingView();

        this.WhenActivated(disposables =>
        {
            //servers
            this.BindCommand(ViewModel, vm => vm.AddVmessServerCmd, v => v.menuAddVmessServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddVlessServerCmd, v => v.menuAddVlessServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddShadowsocksServerCmd, v => v.menuAddShadowsocksServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddSocksServerCmd, v => v.menuAddSocksServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddHttpServerCmd, v => v.menuAddHttpServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddTrojanServerCmd, v => v.menuAddTrojanServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddHysteria2ServerCmd, v => v.menuAddHysteria2Server).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddTuicServerCmd, v => v.menuAddTuicServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddWireguardServerCmd, v => v.menuAddWireguardServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddAnytlsServerCmd, v => v.menuAddAnytlsServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddNaiveServerCmd, v => v.menuAddNaiveServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddMasqueServerCmd, v => v.menuAddMasqueServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddCustomServerCmd, v => v.menuAddCustomServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddCustomOutboundServerCmd, v => v.menuAddCustomOutboundServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddPolicyGroupServerCmd, v => v.menuAddPolicyGroupServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddProxyChainServerCmd, v => v.menuAddProxyChainServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaClipboardCmd, v => v.menuAddServerViaClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaScanCmd, v => v.menuAddServerViaScan).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaImageCmd, v => v.menuAddServerViaImage).DisposeWith(disposables);

            //sub
            this.BindCommand(ViewModel, vm => vm.SubSettingCmd, v => v.menuSubSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubUpdateCmd, v => v.menuSubUpdate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubUpdateViaProxyCmd, v => v.menuSubUpdateViaProxy).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubGroupUpdateCmd, v => v.menuSubGroupUpdate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubGroupUpdateViaProxyCmd, v => v.menuSubGroupUpdateViaProxy).DisposeWith(disposables);

            //setting
            this.BindCommand(ViewModel, vm => vm.OptionSettingCmd, v => v.menuOptionSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RoutingSettingCmd, v => v.menuRoutingSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.DNSSettingCmd, v => v.menuDNSSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.FullConfigTemplateCmd, v => v.menuFullConfigTemplate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.GlobalHotkeySettingCmd, v => v.menuGlobalHotkeySetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RebootAsAdminCmd, v => v.menuRebootAsAdmin).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ClearServerStatisticsCmd, v => v.menuClearServerStatistics).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OpenTheFileLocationCmd, v => v.menuOpenTheFileLocation).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RegionalPresetDefaultCmd, v => v.menuRegionalPresetsDefault).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RegionalPresetRussiaCmd, v => v.menuRegionalPresetsRussia).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RegionalPresetIranCmd, v => v.menuRegionalPresetsIran).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.ReloadCmd, v => v.menuReload).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.BlReloadEnabled, v => v.menuReload.IsEnabled).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.StopCoreCmd, v => v.menuStopCore).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.BlReloadEnabled, v => v.menuStopCore.IsEnabled).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.BlNewUpdate, v => v.btnNewUpdate.IsVisible).DisposeWith(disposables);

            // Profiles rail
            this.OneWayBind(ViewModel, vm => vm.ProfilesViewModel.ProfileItems, v => v.lstCards.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ProfilesViewModel.SelectedProfile, v => v.lstCards.SelectedItem).DisposeWith(disposables);
            if (ViewModel?.ProfilesViewModel?.ProfileItems != null)
            {
                ViewModel.ProfilesViewModel.ProfileItems.CollectionChanged += OnProfileItemsChanged;
                FillCountryCodes();
            }
            this.BindCommand(ViewModel, vm => vm.AddServerViaClipboardCmd, v => v.btnImport).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ProfilesViewModel.EditServerCmd, v => v.menuCtxEdit).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ProfilesViewModel.RealPingServerCmd, v => v.menuCtxPing).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ProfilesViewModel.Export2ShareUrlCmd, v => v.menuCtxShare).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ProfilesViewModel.RemoveServerCmd, v => v.menuCtxDelete).DisposeWith(disposables);

            // Status strip + settings panel TUN switch
            this.OneWayBind(ViewModel, vm => vm.StatusBarViewModel.SpeedDownDisplay, v => v.txtDown.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.StatusBarViewModel.SpeedUpDisplay, v => v.txtUp.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.CoreName, v => v.txtCore.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.StatusBarViewModel.EnableTun, v => v.tglTun.IsChecked).DisposeWith(disposables);

            // Stage: one renderer, refreshed whenever any of its inputs change.
            this.WhenAnyValue(v => v.ViewModel.ConnState).Subscribe(_ => UpdateStage()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.TunActive).Subscribe(_ => UpdateStage()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.NetworkLocked).Subscribe(_ => UpdateStage()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.KillSwitchEnabled).Subscribe(_ => UpdateStage()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.PublicIp).Subscribe(_ => UpdateStage()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.ConnectedSince).Subscribe(_ => UpdateStage()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.StatusBarViewModel.EnableTun).Subscribe(_ => UpdateStage()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.ProfilesViewModel.SelectedProfile).Subscribe(_ => UpdateStage()).DisposeWith(disposables);

            ViewModel.ReadTextFromClipboardInteraction.RegisterHandler(async interaction =>
            {
                var result = await AvaUtils.GetClipboardData(this);
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            ViewModel.ScanScreenInteraction.RegisterHandler(async interaction =>
            {
                ShowHideWindow(false);
                await Task.Delay(200);
                var result = QRCodeAvaloniaUtils.CaptureScreen();
                ShowHideWindow(true);
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            ViewModel.BrowseImageFileInteraction.RegisterHandler(async interaction =>
            {
                var result = await UI.OpenFileDialog(null);
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            ViewModel.ShowHideWindowInteraction.RegisterHandler(interaction =>
            {
                ShowHideWindow(interaction.Input);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            // The classic profiles grid and status bar views are gone; their
            // view-model interactions are served here (delete confirmation,
            // share-link clipboard, tray icon refresh).
            ViewModel.ProfilesViewModel.ShowYesNoInteraction.RegisterHandler(async interaction =>
            {
                var result = await UI.ShowYesNo(interaction.Input);
                interaction.SetOutput(result == ButtonResult.Yes);
            }).DisposeWith(disposables);

            ViewModel.ProfilesViewModel.SetClipboardDataInteraction.RegisterHandler(async interaction =>
            {
                await AvaUtils.SetClipboardData(this, interaction.Input);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.ProfilesViewModel.DispatcherRefreshServersBizInteraction.RegisterHandler(interaction =>
            {
                Dispatcher.UIThread.Post(FillCountryCodes, DispatcherPriority.Default);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.ProfilesViewModel.ProfilesFocusInteraction.RegisterHandler(interaction =>
            {
                lstCards.Focus();
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.ProfilesViewModel.AdjustMainLvColWidthInteraction.RegisterHandler(interaction =>
            {
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.StatusBarViewModel.SetClipboardDataInteraction.RegisterHandler(async interaction =>
            {
                await AvaUtils.SetClipboardData(this, interaction.Input);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.StatusBarViewModel.DispatcherRefreshIconInteraction.RegisterHandler(interaction =>
            {
                Dispatcher.UIThread.Post(RefreshIcon, DispatcherPriority.Default);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            AppEvents.SendSnackMsgRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(async content => await DelegateSnackMsg(content))
              .DisposeWith(disposables);

            AppEvents.AppExitRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(_ => StorageUI())
              .DisposeWith(disposables);

            AppEvents.ShutdownRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(Shutdown)
              .DisposeWith(disposables);
        });

        if (Utils.IsWindows())
        {
            // No visible title text (own title bar); this names the taskbar entry.
            Title = $"{Utils.GetVersion()} - {(Utils.IsAdministrator() ? ResUI.RunAsAdmin : ResUI.NotRunAsAdmin)}";

            if (!Design.IsDesignMode)
            {
                ThreadPool.RegisterWaitForSingleObject(Program.ProgramStarted, OnProgramStarted, null, -1, false);
                HotkeyManager.Instance.Init(_config, OnHotkeyHandler);
            }
        }
        else
        {
            Title = $"{Utils.GetVersion()}";
            menuAddServerViaScan.IsVisible = false;
        }

        if (_config.UiItem.AutoHideStartup && Utils.IsWindows())
        {
            WindowState = WindowState.Minimized;
        }

        AddHelpMenuItem();
        RefreshIcon();
    }

    #region Secure Connect dashboard

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    /// <summary>Renders the connect stage, chips and status strip from the view model.</summary>
    private void UpdateStage()
    {
        var vm = ViewModel;
        if (vm == null)
        {
            return;
        }

        var state = vm.ConnState;
        var on = state == EConnState.On;
        var busy = state is EConnState.Connecting or EConnState.Stopping;

        txtState.Text = state switch
        {
            EConnState.Connecting => "CONNECTING…",
            EConnState.Stopping => "DISCONNECTING…",
            EConnState.On when !vm.TunActive => "CONNECTED · PROXY ONLY",
            EConnState.On when vm.NetworkLocked => "PROTECTED · LOCKED",
            EConnState.On => "PROTECTED",
            _ => "DISCONNECTED",
        };

        var name = vm.ProfilesViewModel?.SelectedProfile?.Remarks;
        txtServerName.Text = name.IsNullOrEmpty() ? "-" : name;

        // Dial
        dialGlow.IsVisible = on;
        ringOn.IsVisible = on;
        ringBusy.IsVisible = busy;
        ringBusy.Classes.Set("spin", busy);
        dialDisc.Stroke = on ? BrDialOn : BrLine;
        iconPower.Stroke = on ? BrAccent : busy ? BrAccent2 : BrMuted;
        txtVerb.IsVisible = !on;
        txtVerb.Text = busy ? "…" : "CONNECT";
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
        txtIp.Text = vm.PublicIp.IsNullOrEmpty() ? "—" : vm.PublicIp;

        // Kill switch chip
        if (on && vm.NetworkLocked)
        {
            chipLock.BorderBrush = BrLockOn;
            dotLock.Background = BrWarn;
            dotLock.BoxShadow = GlowWarn;
            txtKill.Foreground = BrWarn;
            txtKill.Text = "Network locked";
        }
        else
        {
            chipLock.BorderBrush = BrLine;
            dotLock.Background = BrFaint;
            dotLock.BoxShadow = default;
            txtKill.Foreground = BrMuted;
            txtKill.Text = !vm.KillSwitchEnabled
                ? "Kill switch off"
                : on && !vm.TunActive ? "Kill switch needs TUN" : "Kill switch armed";
        }

        // Status strip
        dotCore.Background = on ? BrAccent : BrFaint;
        dotCore.BoxShadow = on ? GlowAccent : default;
        var tun = vm.StatusBarViewModel?.EnableTun == true;
        txtTun.Text = tun ? "singbox_tun" : "off";
        txtTun.Foreground = tun ? BrFg : BrFaint;
    }

    private void UpdateTimer()
    {
        if (ViewModel == null)
        {
            return;
        }
        var elapsed = DateTime.Now - ViewModel.ConnectedSince;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }
        txtTimer.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private async void BtnDial_Click(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null)
        {
            return;
        }
        try
        {
            switch (vm.ConnState)
            {
                case EConnState.On:
                    await vm.StopCore();
                    break;

                case EConnState.Off:
                    await vm.Reload();
                    break;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Dial", ex);
        }
    }

    private static bool IsOnCard(RoutedEventArgs e)
        => (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null;

    private async void LstCards_Tapped(object? sender, TappedEventArgs e)
    {
        // Single click on a card makes it the active server (prototype behaviour):
        // reconnects to it while connected, otherwise it is used by the next Connect.
        if (!IsOnCard(e) || ViewModel is not { } vm || lstCards.SelectedItem is not ProfileItemModel item)
        {
            return;
        }
        try
        {
            await vm.ProfilesViewModel.ActivateServer(item.IndexId, vm.IsRunning);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ActivateServer", ex);
        }
    }

    private async void LstCards_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // Double click on a card while disconnected: activate it and connect.
        if (!IsOnCard(e) || ViewModel is not { } vm || lstCards.SelectedItem is not ProfileItemModel item)
        {
            return;
        }
        try
        {
            await vm.ProfilesViewModel.ActivateServer(item.IndexId, false);
            if (vm.ConnState == EConnState.Off)
            {
                await vm.Reload();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ConnectProfile", ex);
        }
    }

    private void LstCards_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel?.ProfilesViewModel != null)
        {
            ViewModel.ProfilesViewModel.SelectedProfiles = lstCards.SelectedItems?.Cast<ProfileItemModel>().ToList() ?? new List<ProfileItemModel>();
        }
    }

    private void OnProfileItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(FillCountryCodes);

    private void FillCountryCodes()
    {
        var items = ViewModel?.ProfilesViewModel?.ProfileItems;
        if (items == null)
        {
            return;
        }

        foreach (var item in items.ToList())
        {
            if (!item.CountryCode.IsNullOrEmpty() && item.CountryCode != ShortType(item.ConfigType))
            {
                continue;
            }

            if (GeoLookupManager.Instance.TryGetCached(item.Address, out var cached))
            {
                item.CountryCode = cached;
                continue;
            }

            // Placeholder so the badge is never empty, replaced once geo resolves.
            item.CountryCode = ShortType(item.ConfigType);

            var captured = item;
            _ = Task.Run(async () =>
            {
                var code = await GeoLookupManager.Instance.GetCountryCodeAsync(captured.Address);
                if (!code.IsNullOrEmpty())
                {
                    Dispatcher.UIThread.Post(() => captured.CountryCode = code);
                }
            });
        }
    }

    private static string ShortType(EConfigType t)
    {
        var s = t.ToString();
        return (s.Length <= 2 ? s : s[..2]).ToUpperInvariant();
    }

    #endregion Secure Connect dashboard

    #region Settings panel

    private void OpenSettings()
    {
        var ks = _config.KillSwitchItem ?? new KillSwitchItem();
        _loadingSettings = true;
        try
        {
            tglKill.IsChecked = ks.EnableKillSwitch;
            tglLan.IsChecked = ks.AllowLan;
            _bypassApps.Clear();
            foreach (var app in ks.BypassApps ?? [])
            {
                _bypassApps.Add(app);
            }
            _bypassSubnets.Clear();
            foreach (var subnet in ks.BypassSubnets ?? [])
            {
                _bypassSubnets.Add(subnet);
            }
            txtAddApp.Text = string.Empty;
            txtAddSubnet.Text = string.Empty;
        }
        finally
        {
            _loadingSettings = false;
        }
        settingsOverlay.IsVisible = true;
    }

    private void CloseSettings()
    {
        if (!settingsOverlay.IsVisible)
        {
            return;
        }
        // Text left in the add boxes counts as added.
        TryAdd(txtAddApp, _bypassApps, NormalizeApp, quiet: true);
        TryAdd(txtAddSubnet, _bypassSubnets, NormalizeSubnet, quiet: true);
        settingsOverlay.IsVisible = false;
        _ = ApplySettingsAsync();
    }

    private void OnSettingToggled()
    {
        if (!_loadingSettings)
        {
            _ = ApplySettingsAsync();
        }
    }

    private async Task ApplySettingsAsync()
    {
        if (ViewModel == null)
        {
            return;
        }
        try
        {
            await ViewModel.SaveSecureConnectSettings(
                tglKill.IsChecked == true,
                tglLan.IsChecked == true,
                _bypassApps.ToList(),
                _bypassSubnets.ToList());
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SaveSecureConnectSettings", ex);
        }
    }

    private static void RemoveTag(RoutedEventArgs e, ObservableCollection<string> list)
    {
        if (e.Source is Button { Tag: string value })
        {
            list.Remove(value);
            e.Handled = true;
        }
    }

    private void OnAddKey(KeyEventArgs e, TextBox box, ObservableCollection<string> list, Func<string, string?> normalize)
    {
        if (e.Key == Key.Enter)
        {
            TryAdd(box, list, normalize, quiet: false);
            e.Handled = true;
        }
    }

    private void TryAdd(TextBox box, ObservableCollection<string> list, Func<string, string?> normalize, bool quiet)
    {
        var raw = box.Text?.Trim();
        if (raw.IsNullOrEmpty())
        {
            return;
        }
        var value = normalize(raw);
        if (value.IsNullOrEmpty())
        {
            if (!quiet)
            {
                _manager?.Show(new Avalonia.Controls.Notifications.Notification(null, $"Not valid: {raw}", NotificationType.Warning));
            }
            return;
        }
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
        }
        box.Text = string.Empty;
    }

    private static string? NormalizeApp(string raw)
    {
        var app = raw.Trim().Trim('"').Trim();
        return app.IsNullOrEmpty() || app.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ? null : app;
    }

    private static string? NormalizeSubnet(string raw)
    {
        // "a.b.c.d/nn" (or IPv6), a bare address becomes a single-host /32 or /128.
        var parts = raw.Trim().Split('/');
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var ip))
        {
            return null;
        }
        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        var prefix = max;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > max))
        {
            return null;
        }
        return $"{ip}/{prefix}";
    }

    private void MenuAppearance_Click(object? sender, RoutedEventArgs e)
    {
        // Theme / font size / language picker (the former title-bar "⋮" control).
        if (conTheme.Content is ThemeSettingView { Content: Button { Flyout: { } flyout } })
        {
            Dispatcher.UIThread.Post(() => flyout.ShowAt(mainMenu), DispatcherPriority.Background);
        }
    }

    #endregion Settings panel

    #region Event

    private void OnProgramStarted(object state, bool timeout)
    {
        Dispatcher.UIThread.Post(() =>
                ShowHideWindow(true),
            DispatcherPriority.Default);
    }

    private async Task DelegateSnackMsg(string content)
    {
        _manager?.Show(new Avalonia.Controls.Notifications.Notification(null, content, NotificationType.Information));
        await Task.CompletedTask;
    }

    private void OnHotkeyHandler(EGlobalHotkey e)
    {
        switch (e)
        {
            case EGlobalHotkey.ShowForm:
                Dispatcher.UIThread.Post(() => ShowHideWindow(null));
                break;

            case EGlobalHotkey.SystemProxyClear:
            case EGlobalHotkey.SystemProxySet:
            case EGlobalHotkey.SystemProxyUnchanged:
            case EGlobalHotkey.SystemProxyPac:
                AppEvents.SysProxyChangeRequested.Publish((ESysProxyType)((int)e - 1));
                break;
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_blCloseByUser)
        {
            return;
        }

        Logging.SaveLog("OnClosing -> " + e.CloseReason.ToString());

        switch (e.CloseReason)
        {
            case WindowCloseReason.OwnerWindowClosing or WindowCloseReason.WindowClosing:
                e.Cancel = true;
                ShowHideWindow(false);
                break;

            case WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown:
                await AppManager.Instance.AppExitAsync(false);
                break;
        }

        base.OnClosing(e);
    }

    private async void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && settingsOverlay.IsVisible)
        {
            CloseSettings();
            e.Handled = true;
            return;
        }

        // Typing in the settings panel must not trigger the global shortcuts.
        if (e.Source is TextBox)
        {
            return;
        }

        if (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta)
        {
            switch (e.Key)
            {
                case Key.V:
                    await AddServerViaClipboardAsync();
                    break;

                case Key.S:
                    await ScanScreenTaskAsync();
                    break;
            }
        }
        else
        {
            if (e.Key == Key.F5)
            {
                ViewModel?.Reload();
            }
        }
    }

    private void MenuPromotion_Click(object? sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart($"{Utils.Base64Decode(Global.PromotionUrl)}?t={DateTime.Now.Ticks}");
    }

    private void MenuSettingsSetUWP_Click(object? sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart(Utils.GetBinPath("EnableLoopback.exe"));
    }

    public async Task AddServerViaClipboardAsync()
    {
        var clipboardData = await AvaUtils.GetClipboardData(this);
        if (clipboardData.IsNotEmpty() && ViewModel != null)
        {
            await ViewModel.AddServerViaClipboardAsync(clipboardData);
        }
    }

    public async Task ScanScreenTaskAsync()
    {
        ShowHideWindow(false);

        await Task.Delay(200);

        var bytes = QRCodeAvaloniaUtils.CaptureScreen();
        if (bytes != null && ViewModel != null)
        {
            await ViewModel.ScanScreenResult(bytes);
        }

        ShowHideWindow(true);
    }

    private void MenuCheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        _checkUpdateView ??= new CheckUpdateView();
        _checkUpdateView.ViewModel = ViewModel?.CheckUpdateViewModel;
        DialogHost.Show(_checkUpdateView);

        AppEvents.HasUpdateNotified.Publish(false);
    }

    private void MenuBackupAndRestore_Click(object? sender, RoutedEventArgs e)
    {
        _backupAndRestoreView ??= new BackupAndRestoreView();
        _backupAndRestoreView.ViewModel = ViewModel?.BackupAndRestoreViewModel;
        DialogHost.Show(_backupAndRestoreView);
    }

    private async void MenuClose_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (await UI.ShowYesNo(ResUI.menuExitTips) != ButtonResult.Yes)
            {
                return;
            }

            _blCloseByUser = true;
            StorageUI();

            await AppManager.Instance.AppExitAsync(true);
        }
        catch
        {
            // Ignore
        }
    }

    private void Shutdown(bool obj)
    {
        if (obj is bool b && _blCloseByUser == false)
        {
            _blCloseByUser = b;
        }
        StorageUI();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            HotkeyManager.Instance.Dispose();
            desktop.Shutdown();
        }
    }

    #endregion Event

    #region UI

    public void ShowHideWindow(bool? blShow)
    {
        var bl = blShow ??
                    (Utils.IsLinux() || Utils.IsMacOS()
                    ? (!AppManager.Instance.ShowInTaskbar ^ (WindowState == WindowState.Minimized))
                    : !AppManager.Instance.ShowInTaskbar);
        if (bl)
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            Focus();
        }
        else
        {
            if (Utils.IsLinux() && _config.UiItem.Hide2TrayWhenClose == false)
            {
                WindowState = WindowState.Minimized;
                return;
            }

            foreach (var ownedWindow in OwnedWindows)
            {
                ownedWindow.Close();
            }
            Hide();
        }

        AppManager.Instance.ShowInTaskbar = bl;
    }

    protected override void OnLoaded(object? sender, RoutedEventArgs e)
    {
        base.OnLoaded(sender, e);
        if (_config.UiItem.AutoHideStartup)
        {
            ShowHideWindow(false);
        }
        UpdateStage();
    }

    private void RefreshIcon()
    {
        // Tray/window icon follows the system proxy mode (was done by the old status bar view).
        try
        {
            Icon = AvaUtils.GetAppIcon(_config.SystemProxyItem.SysProxyType);
            if (Application.Current is { } app && TrayIcon.GetIcons(app) is { Count: > 0 } icons)
            {
                icons[0].Icon = Icon;
                TrayIcon.SetIcons(app, icons);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("RefreshIcon", ex);
        }
    }

    private void StorageUI()
    {
        ConfigHandler.SaveWindowSizeItem(_config, GetType().Name, Width, Height);
    }

    private void AddHelpMenuItem()
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo();
        foreach (var it in coreInfo
            .Where(t => t.CoreType is not ECoreType.v2fly
                        and not ECoreType.hysteria))
        {
            var item = new MenuItem()
            {
                Tag = it.Url?.Replace(@"/releases", ""),
                Header = string.Format(ResUI.menuWebsiteItem, it.CoreType.ToString().Replace("_", " ")).UpperFirstChar()
            };
            item.Click += MenuItem_Click;
            menuHelp.Items.Add(item);
        }
    }

    private void MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
        {
            ProcUtils.ProcessStart(item.Tag?.ToString());
        }
    }

    #endregion UI
}
