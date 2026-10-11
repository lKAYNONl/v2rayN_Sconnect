using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using v2rayN.Mobile.Platform;

namespace v2rayN.Mobile.Views;

/// <summary>
/// Profiles: subscription group, import (share links or a subscription URL
/// from the clipboard), add / update subscriptions, latency test, and the
/// cards. Tap a card to make it the active server (moves a running session to
/// it, like a click on the desktop rail); "⋮" has ping / copy link / delete.
/// All of it goes through the shared ProfilesViewModel.
/// </summary>
public partial class ProfilesPage : UserControl
{
    private MainWindowViewModel? _vm;
    private bool _syncingGroup;

    public ProfilesPage()
    {
        InitializeComponent();

        txtTitle.Text = Tr.Profiles;
        btnImportText.Text = Tr.Import;
        btnAddSubText.Text = Tr.AddSubscription;
        btnUpdateSubsText.Text = Tr.UpdateSubs;
        btnPingAllText.Text = Tr.PingAll;
        txtSearch.PlaceholderText = Tr.Search;
        txtEmpty.Text = Tr.EmptyProfiles;

        btnImport.Click += async (_, _) => await ImportFromClipboardAsync();
        btnAddSub.Click += async (_, _) => await AddSubscriptionAsync();
        btnUpdateSubs.Click += async (_, _) => await UpdateSubscriptionsAsync();
        btnPingAll.Click += async (_, _) => await PingAllAsync();

        cmbGroup.SelectionChanged += (_, _) =>
        {
            if (!_syncingGroup && _vm != null && cmbGroup.SelectedItem is SubItem sub)
            {
                _vm.ProfilesViewModel.SelectedSub = sub;
            }
        };
        txtSearch.TextChanged += (_, _) =>
        {
            if (_vm != null)
            {
                _vm.ProfilesViewModel.ServerFilter = txtSearch.Text ?? string.Empty;
                if (!string.IsNullOrEmpty(txtSearch.Text))
                {
                    _ = _vm.ProfilesViewModel.RefreshServers();
                }
            }
        };

        lstProfiles.Tapped += async (_, e) => await OnCardTappedAsync(e);
        lstProfiles.AddHandler(Button.ClickEvent, OnCardButtonClick);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!AppHost.IsReady)
        {
            return;
        }

        _vm = AppHost.MainVm;
        var pvm = _vm.ProfilesViewModel;
        lstProfiles.ItemsSource = pvm.ProfileItems;
        cmbGroup.ItemsSource = pvm.SubItems;
        pvm.PropertyChanged += OnProfilesChanged;
        pvm.ProfileItems.CollectionChanged += OnItemsChanged;
        SyncSelection();
        SyncGroup();
        UpdateCount();
        FillCountryCodes();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm != null)
        {
            _vm.ProfilesViewModel.PropertyChanged -= OnProfilesChanged;
            _vm.ProfilesViewModel.ProfileItems.CollectionChanged -= OnItemsChanged;
            lstProfiles.ItemsSource = null;
            cmbGroup.ItemsSource = null;
            _vm = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnProfilesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfilesViewModel.SelectedProfile))
        {
            Dispatcher.UIThread.Post(SyncSelection);
        }
        else if (e.PropertyName == nameof(ProfilesViewModel.SelectedSub))
        {
            Dispatcher.UIThread.Post(SyncGroup);
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            UpdateCount();
            FillCountryCodes();
            SyncSelection();
        });

    private void SyncSelection()
    {
        var selected = _vm?.ProfilesViewModel.SelectedProfile;
        lstProfiles.SelectedItem = selected != null && !string.IsNullOrEmpty(selected.IndexId) ? selected : null;
    }

    private void SyncGroup()
    {
        _syncingGroup = true;
        try
        {
            cmbGroup.SelectedItem = _vm?.ProfilesViewModel.SelectedSub;
        }
        finally
        {
            _syncingGroup = false;
        }
    }

    private void UpdateCount()
    {
        var count = _vm?.ProfilesViewModel.ProfileItems.Count ?? 0;
        txtCount.Text = count > 0 ? count.ToString(CultureInfo.InvariantCulture) : string.Empty;
        txtEmpty.IsVisible = count == 0 && string.IsNullOrEmpty(txtSearch.Text);
    }

    /// <summary>Country badge via geo lookup, protocol letters until it resolves (as on the desktop).</summary>
    private void FillCountryCodes()
    {
        var items = _vm?.ProfilesViewModel.ProfileItems;
        if (items == null)
        {
            return;
        }

        foreach (var item in items.ToList())
        {
            if (!string.IsNullOrEmpty(item.CountryCode) && item.CountryCode != ShortType(item.ConfigType))
            {
                continue;
            }
            if (GeoLookupManager.Instance.TryGetCached(item.Address, out var cached))
            {
                item.CountryCode = cached;
                continue;
            }

            item.CountryCode = ShortType(item.ConfigType);
            var captured = item;
            _ = Task.Run(async () =>
            {
                var code = await GeoLookupManager.Instance.GetCountryCodeAsync(captured.Address);
                if (!string.IsNullOrEmpty(code))
                {
                    Dispatcher.UIThread.Post(() => captured.CountryCode = code);
                }
            });
        }
    }

    private static string ShortType(EConfigType type)
    {
        var s = type.ToString();
        return (s.Length <= 2 ? s : s[..2]).ToUpperInvariant();
    }

    #region Cards

    private static ProfileItemModel? ItemOf(object? source)
        => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ProfileItemModel;

    private async Task OnCardTappedAsync(TappedEventArgs e)
    {
        // The "⋮" button handles itself.
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) != null || _vm is not { } vm)
        {
            return;
        }
        var item = ItemOf(e.Source);
        if (item == null)
        {
            return;
        }
        try
        {
            // Active server for the next connect; a running session moves to it.
            await vm.ProfilesViewModel.ActivateServer(item.IndexId, vm.IsRunning);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ActivateServer", ex);
        }
    }

    private void OnCardButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string } button || _vm is not { } vm)
        {
            return;
        }
        var item = ItemOf(button);
        if (item == null)
        {
            return;
        }
        e.Handled = true;

        var pvm = vm.ProfilesViewModel;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Items.Add(CreateMenuItem(Tr.MenuPing, async () =>
        {
            pvm.SelectedProfiles = [item];
            MainActivity.Toast(Tr.Testing);
            await pvm.ServerSpeedtest(ESpeedActionType.Realping);
        }));
        flyout.Items.Add(CreateMenuItem(Tr.MenuCopyLink, async () =>
        {
            pvm.SelectedProfiles = [item];
            await pvm.Export2ShareUrlAsync(false);
        }));
        flyout.Items.Add(CreateMenuItem(Tr.MenuDelete, async () =>
        {
            pvm.SelectedProfiles = [item];
            await pvm.RemoveServerAsync();
        }));
        flyout.ShowAt(button);
    }

    private static MenuItem CreateMenuItem(string header, Func<Task> action)
    {
        var menuItem = new MenuItem { Header = header, MinHeight = 44 };
        menuItem.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ProfileMenu", ex);
            }
        };
        return menuItem;
    }

    #endregion Cards

    #region Tools

    private async Task ImportFromClipboardAsync()
    {
        if (_vm is not { } vm || MainView.Current is not { } shell)
        {
            return;
        }
        try
        {
            var text = (await shell.ReadClipboardAsync())?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
                return;
            }

            // A bare https:// link is a subscription: add it and fetch its servers.
            var isSubscription = !text.Contains('\n')
                                 && (text.StartsWith(Global.HttpsProtocol, StringComparison.OrdinalIgnoreCase)
                                     || text.StartsWith(Global.HttpProtocol, StringComparison.OrdinalIgnoreCase));
            await vm.AddServerViaClipboardAsync(text);
            if (isSubscription)
            {
                await vm.ProfilesViewModel.RefreshSubscriptions();
                await UpdateSubscriptionsAsync();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Import", ex);
        }
    }

    private async Task AddSubscriptionAsync()
    {
        if (_vm is not { } vm || MainView.Current is not { } shell)
        {
            return;
        }
        var input = await shell.PromptAsync(Tr.SubscriptionUrl, "https://…", Tr.SubscriptionName, Tr.Add);
        if (input is not { } value)
        {
            return;
        }
        var (url, name) = value;
        if (!url.StartsWith(Global.HttpsProtocol, StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith(Global.HttpProtocol, StringComparison.OrdinalIgnoreCase))
        {
            NoticeManager.Instance.Enqueue(Tr.BadUrl);
            return;
        }

        try
        {
            var sub = new SubItem
            {
                Id = string.Empty,
                Url = url,
                Remarks = string.IsNullOrEmpty(name) ? (Utils.TryUri(url)?.Host ?? "subscription") : name,
            };
            if (await ConfigHandler.AddSubItem(AppManager.Instance.Config, sub) != 0)
            {
                NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
                return;
            }
            MainActivity.Toast(Tr.SubscriptionAdded);
            await vm.ProfilesViewModel.RefreshSubscriptions();
            await UpdateSubscriptionsAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AddSubscription", ex);
        }
    }

    private async Task UpdateSubscriptionsAsync()
    {
        if (_vm is not { } vm)
        {
            return;
        }
        MainActivity.Toast(Tr.Updating);
        // While connected, fetch through the tunnel's proxy (the app itself is outside the VPN).
        await vm.UpdateSubscriptionProcess(string.Empty, vm.IsRunning);
        await vm.ProfilesViewModel.RefreshSubscriptions();
    }

    private async Task PingAllAsync()
    {
        if (_vm is not { } vm || vm.ProfilesViewModel.ProfileItems.Count == 0)
        {
            return;
        }
        MainActivity.Toast(Tr.Testing);
        await vm.ProfilesViewModel.ServerSpeedtest(ESpeedActionType.FastRealping);
    }

    #endregion Tools
}
