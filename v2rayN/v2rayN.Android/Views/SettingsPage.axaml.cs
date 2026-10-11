using v2rayN.Mobile.Platform;

namespace v2rayN.Mobile.Views;

/// <summary>A removable tag: an app (package + label) or a subnet.</summary>
public sealed class TagItem(string key, string label)
{
    public string Key { get; } = key;

    public string Label { get; } = label;
}

/// <summary>
/// Settings: the Android kill switch (system setting - status and a shortcut),
/// LAN access, split tunneling (mode, apps, subnets), routes, language, log.
/// Split tunneling uses the shared KillSwitchItem (AndroidApps for packages,
/// BypassSubnets for subnets); a change re-creates the interface of a running
/// session - the core keeps running.
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly ObservableCollection<TagItem> _apps = [];
    private readonly ObservableCollection<TagItem> _subnets = [];
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();

        txtSecKill.Text = Tr.SectionKill;
        txtKillTitle.Text = Tr.KillTitle;
        txtKillText.Text = Tr.KillText;
        btnVpnSettingsText.Text = Tr.OpenVpnSettings;
        txtLan.Text = Tr.AllowLan;
        txtLanText.Text = Tr.AllowLanText;
        txtSecSplit.Text = Tr.SectionSplit;
        rbWhitelist.Content = Tr.ModeWhitelist;
        rbBypass.Content = Tr.ModeBypass;
        txtApps.Text = Tr.Apps;
        btnChooseAppsText.Text = Tr.Choose;
        txtSubnets.Text = Tr.Subnets;
        txtAddSubnet.PlaceholderText = Tr.SubnetHint;
        txtSplitAnd.Text = Tr.SplitAndText;
        txtSecRoutes.Text = Tr.SectionRoutes;
        txtRoutes.Text = Tr.RoutingRules;
        txtSecApp.Text = Tr.SectionApp;
        txtLanguage.Text = Tr.Language;
        txtLog.Text = Tr.Log;
        txtLogText.Text = Tr.LogText;
        txtVersion.Text = Tr.Version(Utils.GetVersionInfo());

        lstApps.ItemsSource = _apps;
        lstSubnets.ItemsSource = _subnets;

        btnVpnSettings.Click += (_, _) => MainActivity.Current?.OpenVpnSettings();
        tglLan.IsCheckedChanged += async (_, _) => await SaveAsync();
        rbWhitelist.IsCheckedChanged += async (_, _) =>
        {
            UpdateModeHint();
            await SaveAsync();
        };
        btnChooseApps.Click += (_, _) => OpenAppPicker();
        lstApps.AddHandler(Button.ClickEvent, async (_, e) => await RemoveTagAsync(e, _apps));
        lstSubnets.AddHandler(Button.ClickEvent, async (_, e) => await RemoveTagAsync(e, _subnets));
        btnAddSubnet.Click += async (_, _) => await AddSubnetAsync();
        txtAddSubnet.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await AddSubnetAsync();
            }
        };
        btnRoutes.Click += (_, _) => MainView.Current?.ShowSheet(new RoutesSheet());
        btnLog.Click += (_, _) => MainView.Current?.ShowSheet(new LogSheet());
        rbRu.IsCheckedChanged += async (_, _) => await SetLanguageAsync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        VpnController.Instance.StatusChanged += OnStatusChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        VpnController.Instance.StatusChanged -= OnStatusChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnStatusChanged() => Dispatcher.UIThread.Post(UpdateKillStatus);

    /// <summary>Reloads everything from the config (the tab was opened).</summary>
    public void Refresh()
    {
        if (!AppHost.IsReady)
        {
            return;
        }

        var config = AppManager.Instance.Config;
        var ks = config.KillSwitchItem ??= new KillSwitchItem();
        _loading = true;
        try
        {
            tglLan.IsChecked = ks.AllowLan;
            rbWhitelist.IsChecked = ks.SplitTunnelWhitelist;
            rbBypass.IsChecked = !ks.SplitTunnelWhitelist;
            var ru = config.UiItem.CurrentLanguage?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true;
            rbRu.IsChecked = ru;
            rbEn.IsChecked = !ru;

            _apps.Clear();
            foreach (var package in ks.AndroidApps ?? [])
            {
                _apps.Add(new TagItem(package, InstalledApps.LabelOf(package)));
            }
            _subnets.Clear();
            foreach (var subnet in ks.BypassSubnets ?? [])
            {
                _subnets.Add(new TagItem(subnet, subnet));
            }
        }
        finally
        {
            _loading = false;
        }

        UpdateModeHint();
        UpdateAppsCount();
        UpdateKillStatus();
        _ = UpdateRoutesInfoAsync();
    }

    private void UpdateModeHint()
        => txtModeHint.Text = rbWhitelist.IsChecked == true ? Tr.WhitelistText : Tr.BypassText;

    private void UpdateAppsCount()
        => txtAppsCount.Text = _apps.Count > 0 ? Tr.SelectedCount(_apps.Count) : Tr.NoneSelected;

    private void UpdateKillStatus()
    {
        var controller = VpnController.Instance;
        txtKillStatus.Text = controller.AlwaysOn is { } alwaysOn && controller.Lockdown is { } lockdown
            ? Tr.KillStatus(alwaysOn, lockdown)
            : Tr.KillStatusUnknown;
    }

    private async Task UpdateRoutesInfoAsync()
    {
        try
        {
            var routes = AppHost.MainVm.RoutesViewModel;
            await routes.LoadAsync();
            txtRoutesInfo.Text = $"{routes.RoutingName} · {Tr.RulesCount(routes.RuleCount)}";
        }
        catch (Exception ex)
        {
            Logging.SaveLog("RoutesInfo", ex);
        }
    }

    /// <summary>Writes the controls back to KillSwitchItem and re-plans a running tunnel.</summary>
    private async Task SaveAsync()
    {
        if (_loading || !AppHost.IsReady)
        {
            return;
        }

        var ks = AppManager.Instance.Config.KillSwitchItem ??= new KillSwitchItem();
        ks.AllowLan = tglLan.IsChecked == true;
        ks.SplitTunnelWhitelist = rbWhitelist.IsChecked == true;
        ks.AndroidApps = _apps.Select(a => a.Key).ToList();
        ks.BypassSubnets = _subnets.Select(s => s.Key).ToList();
        UpdateAppsCount();

        await AppHost.SaveConfigAsync();
        await VpnController.Instance.ReapplyTunnelAsync();
    }

    private async Task RemoveTagAsync(RoutedEventArgs e, ObservableCollection<TagItem> list)
    {
        if (e.Source is not Button { Tag: string key })
        {
            return;
        }
        e.Handled = true;
        var tag = list.FirstOrDefault(t => t.Key == key);
        if (tag != null)
        {
            list.Remove(tag);
            await SaveAsync();
        }
    }

    private async Task AddSubnetAsync()
    {
        var text = txtAddSubnet.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (!Cidr.TryParse(text, out var cidr))
        {
            MainActivity.Toast(Tr.BadSubnet(text));
            return;
        }

        var normalized = cidr.ToString();
        txtAddSubnet.Text = string.Empty;
        if (_subnets.Any(s => s.Key == normalized))
        {
            return;
        }
        _subnets.Add(new TagItem(normalized, normalized));
        await SaveAsync();
    }

    private void OpenAppPicker()
    {
        var selected = _apps.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var sheet = new AppPickerSheet(selected, async picked =>
        {
            _apps.Clear();
            foreach (var app in picked)
            {
                _apps.Add(new TagItem(app.Package, app.Label));
            }
            await SaveAsync();
        });
        MainView.Current?.ShowSheet(sheet);
    }

    private async Task SetLanguageAsync()
    {
        if (_loading || !AppHost.IsReady)
        {
            return;
        }
        var config = AppManager.Instance.Config;
        var language = rbRu.IsChecked == true ? "ru" : "en";
        if (config.UiItem.CurrentLanguage == language)
        {
            return;
        }
        config.UiItem.CurrentLanguage = language;
        await AppHost.SaveConfigAsync();
        MainActivity.Toast(Tr.RestartForLanguage);
    }
}
