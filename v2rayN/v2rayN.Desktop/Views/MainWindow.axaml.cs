using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
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

    // Settings sub-pages, running-programs picker and the rule being edited
    private enum SettingsPage
    {
        Main,
        Routes,
        Editor
    }

    private Action<string>? _onProgramPicked;
    private List<ProcessPathUtils.RunningProgram> _programs = [];
    private bool _programsLoading;
    private RulesItem? _editingRule;
    private string _editingOutbound = Global.DirectTag;
    private static readonly HashSet<string> ChipProtocols = new(["http", "tls", "quic", "bittorrent"], StringComparer.OrdinalIgnoreCase);

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
            // A click outside the panel closes it (not while a rule is being edited).
            if (ReferenceEquals(e.Source, settingsOverlay) && !ruleEditorPanel.IsVisible)
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
        // "No routes" mode: the routing set's rules are ignored, all traffic is tunneled.
        tglRoutingRules.IsChecked = _config.KillSwitchItem?.UseRoutingRules != false;
        tglRoutingRules.IsCheckedChanged += (_, _) =>
        {
            UpdateRoutesSummary();
            OnSettingToggled();
        };
        rbSplitWhitelist.IsCheckedChanged += (_, _) =>
        {
            UpdateSplitTexts();
            OnSettingToggled();
        };

        // Apps: "+" = pick a running program, folder = every program in a folder.
        btnPickProgram.Click += (_, _) => OpenProgramPicker(AddBypassApp);
        btnPickFolder.Click += async (_, _) =>
        {
            var folder = await PickFolderAsync();
            if (folder != null)
            {
                AddBypassApp(folder);
            }
        };

        // Running-programs picker
        btnProcClose.Click += (_, _) => CloseProgramPicker();
        procPickerLayer.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, procPickerLayer))
            {
                CloseProgramPicker();
            }
        };
        txtProcSearch.TextChanged += (_, _) => FilterPrograms();
        lstProcesses.Tapped += (_, e) =>
        {
            if (IsOnCard(e))
            {
                PickSelectedProgram();
            }
        };
        lstProcesses.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                PickSelectedProgram();
                e.Handled = true;
            }
        };

        // Routes page
        btnOpenRoutes.Click += async (_, _) => await ShowRoutesAsync();
        btnRoutesBack.Click += (_, _) => ShowSettingsPage(SettingsPage.Main);
        btnRouteAdd.Click += (_, _) => OpenRuleEditor(null);
        lstRoutes.AddHandler(Button.ClickEvent, OnRouteCardClick);
        if (btnRoutesMenu.Flyout is MenuFlyout routesMenu)
        {
            foreach (var item in routesMenu.Items.OfType<MenuItem>())
            {
                item.Click += RoutesMenu_Click;
            }
        }
        // Back from the advanced routing editor (a separate window): refresh the cards.
        Activated += (_, _) =>
        {
            if (routesPanel.IsVisible)
            {
                _ = ReloadRoutesAsync();
            }
        };

        // Rule editor
        btnRuleBack.Click += (_, _) => ShowSettingsPage(SettingsPage.Routes);
        btnRuleCancel.Click += (_, _) => ShowSettingsPage(SettingsPage.Routes);
        btnRuleSave.Click += async (_, _) => await SaveRuleAsync();
        btnRuleDelete.Click += async (_, _) => await DeleteRuleAsync();
        btnRuleUp.Click += async (_, _) => await MoveRuleAsync(-1);
        btnRuleDown.Click += async (_, _) => await MoveRuleAsync(1);
        btnRulePickProgram.Click += (_, _) => OpenProgramPicker(AppendRuleApp);
        btnRulePickFolder.Click += async (_, _) =>
        {
            var folder = await PickFolderAsync();
            if (folder != null)
            {
                AppendRuleApp(folder);
            }
        };

        conTheme.Content ??= new ThemeSettingView();

        // Interface language (settings panel); applied after a restart.
        cmbLanguage.ItemsSource = Global.LanguageOptions;
        cmbLanguage.SelectionChanged += CmbLanguage_SelectionChanged;

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

            // Routes page
            this.OneWayBind(ViewModel, vm => vm.StatusBarViewModel.RoutingItems, v => v.cmbRoutingSet.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.StatusBarViewModel.SelectedRouting, v => v.cmbRoutingSet.SelectedItem).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RoutesViewModel.Rules, v => v.lstRoutes.ItemsSource).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.RoutesViewModel.RuleCount).Subscribe(_ => UpdateRoutesSummary()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.RoutesViewModel.RoutingName).Subscribe(_ => UpdateRoutesSummary()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.StatusBarViewModel.SelectedRouting).Subscribe(routing =>
            {
                // Another routing set chosen in the Routes page: show its rules.
                if (routesPanel.IsVisible)
                {
                    _ = ReloadRoutesAsync();
                }
            }).DisposeWith(disposables);

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

            // Linux/macOS: TUN needs the sudo password (was asked by the old status bar view).
            ViewModel.StatusBarViewModel.PasswordInputInteraction.RegisterHandler(async interaction =>
            {
                var result = await DialogHost.Show(new SudoPasswordInputView());
                var password = result?.ToString();
                if (password.IsNullOrEmpty())
                {
                    ViewModel.StatusBarViewModel.EnableTun = false;
                }
                else
                {
                    AppManager.Instance.LinuxSudoPwd = password;
                }
                interaction.SetOutput(password);
            }).DisposeWith(disposables);
            ViewModel.StatusBarViewModel.MarkUiReady();

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

            ViewModel.CoreMissingRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(async cores => await OfferCoreDownloadAsync(cores))
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

        // Whitelist split tunneling with entries: only the listed apps are tunneled.
        var ks = _config.KillSwitchItem;
        var split = ks is { SplitTunnelWhitelist: true }
                    && (ks.BypassApps?.Count > 0 || ks.BypassSubnets?.Count > 0);
        var tunnelLabel = split ? ResUI.ScStateSplit : ResUI.ScStateProtected;
        txtState.Text = state switch
        {
            EConnState.Connecting => ResUI.ScStateConnecting,
            EConnState.Stopping => ResUI.ScStateDisconnecting,
            EConnState.On when !vm.TunActive => ResUI.ScStateProxyOnly,
            EConnState.On when vm.NetworkLocked => string.Format(ResUI.ScStateLocked, tunnelLabel),
            EConnState.On => tunnelLabel,
            _ => ResUI.ScStateDisconnected,
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
        txtVerb.Text = busy ? "…" : ResUI.ScConnectVerb;
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
        if (vm.NetworkLocked)
        {
            chipLock.BorderBrush = BrLockOn;
            dotLock.Background = BrWarn;
            dotLock.BoxShadow = GlowWarn;
            txtKill.Foreground = BrWarn;
            txtKill.Text = ResUI.ScChipNetworkLocked;
        }
        else
        {
            chipLock.BorderBrush = BrLine;
            dotLock.Background = BrFaint;
            dotLock.BoxShadow = default;
            txtKill.Foreground = BrMuted;
            txtKill.Text = !vm.KillSwitchEnabled
                ? ResUI.ScChipKillOff
                : on && !vm.TunActive ? ResUI.ScChipKillNeedsTun : ResUI.ScChipKillArmed;
        }

        // Status strip
        dotCore.Background = on ? BrAccent : BrFaint;
        dotCore.BoxShadow = on ? GlowAccent : default;
        var tun = vm.StatusBarViewModel?.EnableTun == true;
        txtTun.Text = tun ? "singbox_tun" : ResUI.ScOff;
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
            rbSplitWhitelist.IsChecked = ks.SplitTunnelWhitelist;
            rbSplitBypass.IsChecked = !ks.SplitTunnelWhitelist;
            tglRoutingRules.IsChecked = ks.UseRoutingRules;
            UpdateSplitTexts();
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
            cmbLanguage.SelectedItem = Global.LanguageOptions.FirstOrDefault(t => t.Value == _config.UiItem.CurrentLanguage);
        }
        finally
        {
            _loadingSettings = false;
        }
        ShowSettingsPage(SettingsPage.Main);
        settingsOverlay.IsVisible = true;
        _ = ReloadRoutesAsync();
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
        CloseProgramPicker();
        settingsOverlay.IsVisible = false;
        ShowSettingsPage(SettingsPage.Main);
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
                rbSplitWhitelist.IsChecked == true,
                _bypassApps.ToList(),
                _bypassSubnets.ToList(),
                tglRoutingRules.IsChecked == true);
            UpdateStage();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SaveSecureConnectSettings", ex);
        }
    }

    private async void CmbLanguage_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings
            || cmbLanguage.SelectedItem is not LanguageOption language
            || language.Value == _config.UiItem.CurrentLanguage)
        {
            return;
        }
        try
        {
            _config.UiItem.CurrentLanguage = language.Value;
            await ConfigHandler.SaveConfig(_config);
            // Keep the Theme / Language flyout in sync (the value is already saved).
            if (conTheme.Content is ThemeSettingView { ViewModel: { } themeVm })
            {
                themeVm.CurrentLanguage = language.Value;
            }

            if (Utils.IsWindows() && await UI.ShowYesNo(ResUI.ScLanguageRestartAsk) == ButtonResult.Yes)
            {
                await RestartAppAsync();
                return;
            }
            _manager?.Show(new Avalonia.Controls.Notifications.Notification(null, ResUI.ScLanguageSaved, NotificationType.Information));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ChangeLanguage", ex);
        }
    }

    /// <summary>
    /// Restarts the app: stops the core and lifts the kill switch first, then
    /// starts the new instance ("rebootas" skips the single-instance check) and quits.
    /// </summary>
    private static async Task RestartAppAsync()
    {
        await AppManager.Instance.AppExitAsync(false);
        ProcUtils.RebootAsAdmin(Utils.IsAdministrator());
        AppManager.Instance.Shutdown(true);
    }

    /// <summary>Split tunneling texts for the selected mode (whitelist / go direct).</summary>
    private void UpdateSplitTexts()
    {
        if (rbSplitWhitelist.IsChecked == true)
        {
            txtSplitAppsSub.Text = ResUI.ScSplitAppsSubWhitelist;
            txtSplitSubnetsSub.Text = ResUI.ScSplitSubnetsSubWhitelist;
            txtSplitHint.Text = ResUI.ScSplitHintWhitelist;
        }
        else
        {
            txtSplitAppsSub.Text = ResUI.ScSplitAppsSubBypass;
            txtSplitSubnetsSub.Text = ResUI.ScSplitSubnetsSubBypass;
            txtSplitHint.Text = ResUI.ScSplitHintBypass;
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
                _manager?.Show(new Avalonia.Controls.Notifications.Notification(null, string.Format(ResUI.ScNotValid, raw), NotificationType.Warning));
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

    private void ShowSettingsPage(SettingsPage page)
    {
        settingsPanel.IsVisible = page == SettingsPage.Main;
        routesPanel.IsVisible = page == SettingsPage.Routes;
        ruleEditorPanel.IsVisible = page == SettingsPage.Editor;
    }

    private void AddBypassApp(string value)
    {
        var app = NormalizeApp(value);
        if (!app.IsNullOrEmpty() && !_bypassApps.Contains(app, StringComparer.OrdinalIgnoreCase))
        {
            _bypassApps.Add(app);
        }
    }

    /// <summary>Folder chooser; returns the folder with a trailing separator ("every program inside").</summary>
    private async Task<string?> PickFolderAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = ResUI.ScChooseFolder,
                AllowMultiple = false,
            });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            return path.IsNullOrEmpty() ? null : ProcessPathUtils.NormalizeFolder(path);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("PickFolder", ex);
            return null;
        }
    }

    #endregion Settings panel

    #region Running programs picker

    private async void OpenProgramPicker(Action<string> onPicked)
    {
        _onProgramPicked = onPicked;
        _programsLoading = true;
        _programs = [];
        txtProcSearch.Text = string.Empty;
        lstProcesses.ItemsSource = null;
        txtProcStatus.Text = ResUI.ScLoading;
        procPickerLayer.IsVisible = true;
        txtProcSearch.Focus();
        try
        {
            _programs = await Task.Run(() => ProcessPathUtils.GetRunningPrograms());
        }
        catch (Exception ex)
        {
            Logging.SaveLog("GetRunningPrograms", ex);
        }
        finally
        {
            _programsLoading = false;
        }
        FilterPrograms();
    }

    private void FilterPrograms()
    {
        if (_programsLoading)
        {
            return;
        }
        var query = txtProcSearch.Text?.Trim();
        var list = query.IsNullOrEmpty()
            ? _programs
            : _programs.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                   || p.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        lstProcesses.ItemsSource = list;
        txtProcStatus.Text = _programs.Count == 0
            ? ResUI.ScNoPrograms
            : string.Format(ResUI.ScProgramsCount, list.Count, _programs.Count);
    }

    private void PickSelectedProgram()
    {
        if (lstProcesses.SelectedItem is not ProcessPathUtils.RunningProgram program)
        {
            return;
        }
        var onPicked = _onProgramPicked;
        CloseProgramPicker();
        onPicked?.Invoke(program.Path);
    }

    private void CloseProgramPicker()
    {
        procPickerLayer.IsVisible = false;
        _onProgramPicked = null;
        lstProcesses.ItemsSource = null;
    }

    #endregion Running programs picker

    #region Routes page

    private async Task ShowRoutesAsync()
    {
        await ReloadRoutesAsync();
        ShowSettingsPage(SettingsPage.Routes);
    }

    private async Task ReloadRoutesAsync()
    {
        if (ViewModel?.RoutesViewModel is not { } routes)
        {
            return;
        }
        try
        {
            await routes.LoadAsync((cmbRoutingSet.SelectedItem as RoutingItem)?.Id);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("LoadRoutes", ex);
        }
    }

    private void UpdateRoutesSummary()
    {
        if (ViewModel?.RoutesViewModel is not { } routes)
        {
            return;
        }
        var count = routes.RuleCount;
        var rulesOn = tglRoutingRules.IsChecked == true;
        txtRoutesSummary.Text = !rulesOn
            ? ResUI.ScRoutesOffSummary
            : routes.RoutingName.IsNullOrEmpty()
                ? ResUI.ScRoutesSub
                : string.Format(count == 1 ? ResUI.ScRoutesSummaryOne : ResUI.ScRoutesSummaryMany, count, routes.RoutingName);
        // The rules stay editable in "no routes" mode, they are just not applied.
        btnOpenRoutes.Opacity = rulesOn ? 1 : 0.55;
        txtRoutesEmpty.IsVisible = count == 0;
    }

    private async void OnRouteCardClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.RoutesViewModel is not { } routes
            || e.Source is not Button { DataContext: RouteRuleModel card } button)
        {
            return;
        }
        e.Handled = true;
        try
        {
            switch (button.Tag as string)
            {
                case "edit":
                    OpenRuleEditor(card.Id);
                    break;

                case "copy":
                    await routes.DuplicateAsync(card.Id);
                    break;

                case "toggle":
                    await routes.SetEnabledAsync(card.Id, button is ToggleButton { IsChecked: true });
                    break;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("RouteCard", ex);
        }
    }

    private async void RoutesMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || ViewModel is not { } vm)
        {
            return;
        }
        try
        {
            switch (tag)
            {
                case "preset-russia":
                    await ApplyPresetAsync(EPresetType.Russia);
                    break;

                case "preset-iran":
                    await ApplyPresetAsync(EPresetType.Iran);
                    break;

                case "preset-default":
                    await ApplyPresetAsync(EPresetType.Default);
                    break;

                case "advanced":
                    if (vm.RoutingSettingCmd is System.Windows.Input.ICommand command && command.CanExecute(null))
                    {
                        command.Execute(null);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("RoutesMenu", ex);
        }
    }

    private async Task ApplyPresetAsync(EPresetType type)
    {
        if (ViewModel == null
            || await UI.ShowYesNo(string.Format(ResUI.ScPresetConfirm, PresetName(type))) != ButtonResult.Yes)
        {
            return;
        }
        await ViewModel.ApplyRegionalPreset(type);
        await ViewModel.RoutesViewModel.LoadAsync();
    }

    private static string PresetName(EPresetType type) => type switch
    {
        EPresetType.Russia => ResUI.menuRegionalPresetsRussia,
        EPresetType.Iran => ResUI.menuRegionalPresetsIran,
        _ => ResUI.menuRegionalPresetsDefault,
    };

    #endregion Routes page

    #region Rule editor

    private void OpenRuleEditor(string? id)
    {
        if (ViewModel?.RoutesViewModel is not { } routes)
        {
            return;
        }
        var rule = id.IsNullOrEmpty() ? null : routes.GetRuleCopy(id);
        var isNew = rule is null;
        rule ??= new RulesItem { Enabled = true, OutboundTag = Global.DirectTag };
        _editingRule = rule;
        _editingOutbound = rule.OutboundTag.IsNullOrEmpty() ? Global.ProxyTag : rule.OutboundTag;

        txtRuleEditorTitle.Text = isNew ? ResUI.ScNewRule : ResUI.ScEditRule;
        txtRuleName.Text = rule.Remarks ?? string.Empty;
        tglRuleEnabled.IsChecked = rule.Enabled;
        rbRuleDirect.IsChecked = _editingOutbound == Global.DirectTag;
        rbRuleBlock.IsChecked = _editingOutbound == Global.BlockTag;
        rbRuleProxy.IsChecked = _editingOutbound != Global.DirectTag && _editingOutbound != Global.BlockTag;

        txtRuleDomains.Text = string.Join(Environment.NewLine, rule.Domain ?? []);
        txtRuleIps.Text = string.Join(Environment.NewLine, rule.Ip ?? []);
        txtRuleApps.Text = string.Join(Environment.NewLine, rule.Process ?? []);

        var protocols = rule.Protocol ?? [];
        tglProtoHttp.IsChecked = protocols.Contains("http", StringComparer.OrdinalIgnoreCase);
        tglProtoTls.IsChecked = protocols.Contains("tls", StringComparer.OrdinalIgnoreCase);
        tglProtoQuic.IsChecked = protocols.Contains("quic", StringComparer.OrdinalIgnoreCase);
        tglProtoBt.IsChecked = protocols.Contains("bittorrent", StringComparer.OrdinalIgnoreCase);

        txtRulePort.Text = rule.Port ?? string.Empty;
        var network = rule.Network?.Replace(" ", string.Empty).ToLowerInvariant();
        rbNetTcp.IsChecked = network == "tcp";
        rbNetUdp.IsChecked = network == "udp";
        rbNetAny.IsChecked = network is not ("tcp" or "udp");

        btnRuleDelete.IsVisible = !isNew;
        btnRuleUp.IsVisible = !isNew;
        btnRuleDown.IsVisible = !isNew;
        UpdateRulePosition();
        ShowSettingsPage(SettingsPage.Editor);
        txtRuleName.Focus();
    }

    private void UpdateRulePosition()
    {
        var routes = ViewModel?.RoutesViewModel;
        var index = routes?.IndexOf(_editingRule?.Id) ?? -1;
        var count = routes?.RuleCount ?? 0;
        txtRulePosition.Text = index >= 0 ? string.Format(ResUI.ScRulePosition, index + 1, count) : ResUI.ScRuleNewOnTop;
        btnRuleUp.IsEnabled = index > 0;
        btnRuleDown.IsEnabled = index >= 0 && index < count - 1;
    }

    private void AppendRuleApp(string value)
    {
        var app = NormalizeApp(value);
        if (app.IsNullOrEmpty())
        {
            return;
        }
        var lines = SplitLines(txtRuleApps.Text) ?? [];
        if (!lines.Contains(app, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(app);
        }
        txtRuleApps.Text = string.Join(Environment.NewLine, lines);
    }

    private static List<string>? SplitLines(string? text)
    {
        var lines = (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return lines.Count > 0 ? lines : null;
    }

    private static bool IsValidPortList(string ports)
    {
        foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-');
            if (range.Length > 2 || range.Any(p => !int.TryParse(p, out var n) || n < 0 || n > 65535))
            {
                return false;
            }
        }
        return true;
    }

    private async Task SaveRuleAsync()
    {
        if (ViewModel?.RoutesViewModel is not { } routes || _editingRule is not { } rule)
        {
            return;
        }

        rule.Remarks = txtRuleName.Text?.Trim();
        rule.Enabled = tglRuleEnabled.IsChecked == true;
        rule.OutboundTag = rbRuleDirect.IsChecked == true ? Global.DirectTag
            : rbRuleBlock.IsChecked == true ? Global.BlockTag
            : _editingOutbound is Global.DirectTag or Global.BlockTag ? Global.ProxyTag
            : _editingOutbound;

        rule.Domain = SplitLines(txtRuleDomains.Text);
        rule.Ip = SplitLines(txtRuleIps.Text);
        rule.Process = SplitLines(txtRuleApps.Text);

        // Chips for the common protocols; any other protocol the rule had is kept.
        var protocols = (rule.Protocol ?? []).Where(p => !ChipProtocols.Contains(p)).ToList();
        if (tglProtoHttp.IsChecked == true)
        {
            protocols.Add("http");
        }
        if (tglProtoTls.IsChecked == true)
        {
            protocols.Add("tls");
        }
        if (tglProtoQuic.IsChecked == true)
        {
            protocols.Add("quic");
        }
        if (tglProtoBt.IsChecked == true)
        {
            protocols.Add("bittorrent");
        }
        rule.Protocol = protocols.Count > 0 ? protocols : null;

        var port = txtRulePort.Text?.Replace(" ", string.Empty);
        if (!port.IsNullOrEmpty() && !IsValidPortList(port))
        {
            _manager?.Show(new Avalonia.Controls.Notifications.Notification(null, ResUI.ScPortInvalid, NotificationType.Warning));
            return;
        }
        rule.Port = port.IsNullOrEmpty() ? null : port;
        rule.Network = rbNetTcp.IsChecked == true ? "tcp" : rbNetUdp.IsChecked == true ? "udp" : null;

        if (rule.Domain is null && rule.Ip is null && rule.Process is null && rule.Protocol is null
            && rule.Port.IsNullOrEmpty() && rule.Network.IsNullOrEmpty() && !(rule.InboundTag?.Count > 0))
        {
            _manager?.Show(new Avalonia.Controls.Notifications.Notification(null, ResUI.ScRuleNeedsCondition, NotificationType.Warning));
            return;
        }

        try
        {
            await routes.UpsertAsync(rule);
            ShowSettingsPage(SettingsPage.Routes);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SaveRule", ex);
        }
    }

    private async Task DeleteRuleAsync()
    {
        if (ViewModel?.RoutesViewModel is not { } routes || _editingRule is not { } rule || routes.IndexOf(rule.Id) < 0)
        {
            return;
        }
        var name = rule.Remarks.IsNullOrEmpty() ? ResUI.ScThisRule : $"\"{rule.Remarks}\"";
        if (await UI.ShowYesNo(string.Format(ResUI.ScDeleteConfirm, name)) != ButtonResult.Yes)
        {
            return;
        }
        await routes.DeleteAsync(rule.Id);
        ShowSettingsPage(SettingsPage.Routes);
    }

    private async Task MoveRuleAsync(int delta)
    {
        if (ViewModel?.RoutesViewModel is not { } routes || _editingRule is not { } rule)
        {
            return;
        }
        await routes.MoveAsync(rule.Id, delta);
        UpdateRulePosition();
    }

    #endregion Rule editor

    #region Settings menu

    /// <summary>
    /// Connect failed because core executables are missing from the bin folder:
    /// offer the "Check update" window, which downloads them.
    /// </summary>
    private async Task OfferCoreDownloadAsync(string cores)
    {
        try
        {
            // The window may be hidden in the tray (or not shown yet at startup):
            // a dialog needs a visible owner.
            ShowHideWindow(true);
            if (await UI.ShowYesNo(string.Format(ResUI.ScCoreMissingAsk, cores)) == ButtonResult.Yes)
            {
                MenuCheckUpdate_Click(this, new RoutedEventArgs());
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("OfferCoreDownload", ex);
        }
    }

    private void MenuAppearance_Click(object? sender, RoutedEventArgs e)
    {
        // Theme / font size / language picker (the former title-bar "⋮" control).
        if (conTheme.Content is ThemeSettingView { Content: Button { Flyout: { } flyout } })
        {
            Dispatcher.UIThread.Post(() => flyout.ShowAt(mainMenu), DispatcherPriority.Background);
        }
    }

    #endregion Settings menu

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
        if (e.Key == Key.Escape && procPickerLayer.IsVisible)
        {
            CloseProgramPicker();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && settingsOverlay.IsVisible)
        {
            // Step back: editor -> routes -> settings -> closed.
            if (ruleEditorPanel.IsVisible)
            {
                ShowSettingsPage(SettingsPage.Routes);
            }
            else if (routesPanel.IsVisible)
            {
                ShowSettingsPage(SettingsPage.Main);
            }
            else
            {
                CloseSettings();
            }
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
