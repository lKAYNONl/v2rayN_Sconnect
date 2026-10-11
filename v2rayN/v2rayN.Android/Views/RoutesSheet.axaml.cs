using System.ComponentModel;
using Avalonia.Controls.Primitives;
using v2rayN.Mobile.Platform;

namespace v2rayN.Mobile.Views;

/// <summary>
/// Routes (NekoBox style, shared with the desktop's Routes page): pick the
/// routing set, switch rules on/off, reorder or delete them, apply a regional
/// preset. Every change goes through RoutesViewModel, which saves the set and
/// reloads a running core.
/// </summary>
public partial class RoutesSheet : UserControl, ISheet
{
    private MainWindowViewModel? _vm;
    private bool _loading;

    public RoutesSheet()
    {
        InitializeComponent();

        txtTitle.Text = Tr.Routes;
        btnPresetsText.Text = Tr.Presets;
        txtSet.Text = Tr.RoutingSet.ToUpperInvariant();
        txtHint.Text = Tr.RulesHint;

        btnBack.Click += (_, _) => MainView.Current?.CloseSheet(this);
        btnPresets.Click += (_, _) => ShowPresets();
        cmbRouting.SelectionChanged += async (_, _) => await OnRoutingChangedAsync();
        lstRules.AddHandler(Button.ClickEvent, OnRuleButtonClick);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!AppHost.IsReady)
        {
            return;
        }
        _vm = AppHost.MainVm;
        lstRules.ItemsSource = _vm.RoutesViewModel.Rules;
        _vm.StatusBarViewModel.PropertyChanged += OnStatusBarChanged;
        _ = LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm != null)
        {
            _vm.StatusBarViewModel.PropertyChanged -= OnStatusBarChanged;
            lstRules.ItemsSource = null;
            cmbRouting.ItemsSource = null;
            _vm = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    public void OnClosed()
    {
    }

    private async Task LoadAsync()
    {
        if (_vm is not { } vm)
        {
            return;
        }
        _loading = true;
        try
        {
            await vm.StatusBarViewModel.RefreshRoutingsMenu();
            cmbRouting.ItemsSource = vm.StatusBarViewModel.RoutingItems;
            cmbRouting.SelectedItem = vm.StatusBarViewModel.SelectedRouting;
            await vm.RoutesViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("RoutesLoad", ex);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The active set changed (here or by a preset): show its rules.</summary>
    private void OnStatusBarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusBarViewModel.SelectedRouting) && !_loading)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                if (_vm != null)
                {
                    await _vm.RoutesViewModel.LoadAsync(_vm.StatusBarViewModel.SelectedRouting?.Id);
                }
            });
        }
    }

    private async Task OnRoutingChangedAsync()
    {
        if (_loading || _vm is not { } vm || cmbRouting.SelectedItem is not RoutingItem routing)
        {
            return;
        }
        if (vm.StatusBarViewModel.SelectedRouting?.Id == routing.Id)
        {
            return;
        }
        // Makes it the active set and reloads a running core (StatusBarViewModel logic).
        vm.StatusBarViewModel.SelectedRouting = routing;
        await vm.RoutesViewModel.LoadAsync(routing.Id);
    }

    private async void OnRuleButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || e.Source is not Button { Tag: string id } button)
        {
            return;
        }
        e.Handled = true;
        var routes = vm.RoutesViewModel;

        try
        {
            if (button is ToggleButton toggle)
            {
                await routes.SetEnabledAsync(id, toggle.IsChecked == true);
                return;
            }

            var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            flyout.Items.Add(CreateMenuItem(Tr.MoveUp, () => routes.MoveAsync(id, -1)));
            flyout.Items.Add(CreateMenuItem(Tr.MoveDown, () => routes.MoveAsync(id, 1)));
            flyout.Items.Add(CreateMenuItem(Tr.MenuDelete, async () =>
            {
                if (MainView.Current is { } shell && await shell.ConfirmAsync($"{Tr.MenuDelete}?"))
                {
                    await routes.DeleteAsync(id);
                }
            }));
            flyout.ShowAt(button);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("RouteRule", ex);
        }
    }

    private void ShowPresets()
    {
        if (_vm is not { } vm)
        {
            return;
        }
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Items.Add(CreateMenuItem(ResUI.menuRegionalPresetsDefault, () => ApplyPresetAsync(vm, EPresetType.Default)));
        flyout.Items.Add(CreateMenuItem(ResUI.menuRegionalPresetsRussia, () => ApplyPresetAsync(vm, EPresetType.Russia)));
        flyout.Items.Add(CreateMenuItem(ResUI.menuRegionalPresetsIran, () => ApplyPresetAsync(vm, EPresetType.Iran)));
        flyout.ShowAt(btnPresets);
    }

    private async Task ApplyPresetAsync(MainWindowViewModel vm, EPresetType type)
    {
        // Routing + DNS preset, geo files for the region, then a reload of a running core.
        await vm.ApplyRegionalPreset(type);
        await LoadAsync();
    }

    private static MenuItem CreateMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header, MinHeight = 44 };
        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("RoutesMenu", ex);
            }
        };
        return item;
    }
}
