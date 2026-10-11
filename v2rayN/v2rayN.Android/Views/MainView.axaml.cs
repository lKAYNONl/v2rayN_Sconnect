using Avalonia.Input.Platform;
using v2rayN.Mobile.Platform;

namespace v2rayN.Mobile.Views;

/// <summary>A full-screen page shown above the tabs (app picker, routes, log).</summary>
public interface ISheet
{
    /// <summary>Called after the sheet was removed (Back or its own close button).</summary>
    void OnClosed();
}

/// <summary>
/// Shell: brand bar, three tabs (connect / profiles / settings), full-screen
/// sheets, a small dialog, Android Back handling and the ServiceLib
/// interactions that need UI (clipboard, delete confirmation). One instance
/// per activity; the view model is process-wide.
/// </summary>
public partial class MainView : UserControl
{
    private readonly List<IDisposable> _registrations = [];
    private TaskCompletionSource<bool>? _dialog;
    private TopLevel? _topLevel;
    private int _tab;

    public static MainView? Current { get; private set; }

    public MainView()
    {
        InitializeComponent();

        navHomeText.Text = Tr.TabConnect;
        navProfilesText.Text = Tr.TabProfiles;
        navSettingsText.Text = Tr.TabSettings;
        navHome.Click += (_, _) => SelectTab(0);
        navProfiles.Click += (_, _) => SelectTab(1);
        navSettings.Click += (_, _) => SelectTab(2);

        dlgOk.Click += (_, _) => CloseDialog(true);
        dlgCancel.Click += (_, _) => CloseDialog(false);
        dialogLayer.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, dialogLayer))
            {
                CloseDialog(false);
            }
        };

        if (!AppHost.IsReady)
        {
            shell.IsVisible = false;
            startupError.IsVisible = true;
            startupErrorText.Text = Tr.StartupFailed;
            btnStartupLogText.Text = Tr.Log;
            btnStartupLog.Click += (_, _) => ShowSheet(new LogSheet());
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Current = this;

        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null)
        {
            _topLevel.BackRequested += OnBackRequested;
            // Behind the status / navigation bars (edge-to-edge on Android 15+).
            _topLevel.Background = Ui.Brush("#11162A");
            if (_topLevel.InsetsManager is { } insets)
            {
                insets.SystemBarColor = Color.Parse("#11162A");
            }
        }

        if (AppHost.IsReady)
        {
            RegisterInteractions();
            UpdateTopDot();
            AppHost.MainVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
        _registrations.Clear();

        if (AppHost.IsReady)
        {
            AppHost.MainVm.PropertyChanged -= OnVmPropertyChanged;
        }
        if (_topLevel != null)
        {
            _topLevel.BackRequested -= OnBackRequested;
            _topLevel = null;
        }
        _dialog?.TrySetResult(false);
        _dialog = null;
        if (Current == this)
        {
            Current = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ConnState))
        {
            Dispatcher.UIThread.Post(UpdateTopDot);
        }
    }

    private void UpdateTopDot()
    {
        var on = AppHost.MainVm.ConnState == EConnState.On;
        dotTop.Background = on ? Ui.Accent : Ui.Faint;
        dotTop.BoxShadow = on ? Ui.GlowAccent : default;
    }

    #region Tabs

    public void SelectTab(int index)
    {
        _tab = index;
        pageHome.IsVisible = index == 0;
        pageProfiles.IsVisible = index == 1;
        pageSettings.IsVisible = index == 2;
        navHome.Classes.Set("active", index == 0);
        navProfiles.Classes.Set("active", index == 1);
        navSettings.Classes.Set("active", index == 2);
        if (index == 2)
        {
            pageSettings.Refresh();
        }
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (dialogLayer.IsVisible)
        {
            CloseDialog(false);
            e.Handled = true;
        }
        else if (CloseTopSheet())
        {
            e.Handled = true;
        }
        else if (_tab != 0 && AppHost.IsReady)
        {
            SelectTab(0);
            e.Handled = true;
        }
        // Otherwise Android's default: leave the app. The VPN service keeps running.
    }

    #endregion Tabs

    #region Sheets

    public void ShowSheet(Control sheet)
    {
        sheetHost.Children.Add(sheet);
        sheetHost.IsVisible = true;
    }

    public void CloseSheet(Control sheet)
    {
        if (sheetHost.Children.Remove(sheet))
        {
            (sheet as ISheet)?.OnClosed();
        }
        sheetHost.IsVisible = sheetHost.Children.Count > 0;
    }

    private bool CloseTopSheet()
    {
        if (sheetHost.Children.Count == 0)
        {
            return false;
        }
        CloseSheet(sheetHost.Children[^1]);
        return true;
    }

    #endregion Sheets

    #region Dialog

    /// <summary>Yes / No question.</summary>
    public async Task<bool> ConfirmAsync(string text)
        => (await ShowDialogAsync(text, null, null, Tr.Yes, Tr.No)).ok;

    /// <summary>Up to two text inputs; null when cancelled.</summary>
    public async Task<(string first, string second)?> PromptAsync(string title, string hint1, string? hint2, string okText)
    {
        var (ok, first, second) = await ShowDialogAsync(title, hint1, hint2, okText, Tr.Cancel);
        return ok ? (first, second) : null;
    }

    private async Task<(bool ok, string first, string second)> ShowDialogAsync(string text, string? hint1, string? hint2, string okText, string cancelText)
    {
        _dialog?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>();
        _dialog = tcs;

        dlgText.Text = text;
        dlgOkText.Text = okText;
        dlgCancelText.Text = cancelText;
        dlgInput1.Text = string.Empty;
        dlgInput2.Text = string.Empty;
        dlgInput1.PlaceholderText = hint1;
        dlgInput2.PlaceholderText = hint2;
        dlgInput1.IsVisible = hint1 != null;
        dlgInput2.IsVisible = hint2 != null;
        dialogLayer.IsVisible = true;
        if (hint1 != null)
        {
            dlgInput1.Focus();
        }

        var ok = await tcs.Task;
        return (ok, dlgInput1.Text?.Trim() ?? string.Empty, dlgInput2.Text?.Trim() ?? string.Empty);
    }

    private void CloseDialog(bool ok)
    {
        dialogLayer.IsVisible = false;
        var tcs = _dialog;
        _dialog = null;
        tcs?.TrySetResult(ok);
    }

    #endregion Dialog

    #region Clipboard & interactions

    public async Task<string?> ReadClipboardAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            return clipboard == null ? null : await clipboard.TryGetTextAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ReadClipboard", ex);
            return null;
        }
    }

    public async Task WriteClipboardAsync(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("WriteClipboard", ex);
        }
    }

    private void RegisterInteractions()
    {
        var vm = AppHost.MainVm;

        _registrations.Add(vm.ReadTextFromClipboardInteraction.RegisterHandler(async interaction =>
        {
            interaction.SetOutput(await ReadClipboardAsync());
        }));

        _registrations.Add(vm.ProfilesViewModel.ShowYesNoInteraction.RegisterHandler(async interaction =>
        {
            interaction.SetOutput(await ConfirmAsync(interaction.Input));
        }));

        _registrations.Add(vm.ProfilesViewModel.SetClipboardDataInteraction.RegisterHandler(async interaction =>
        {
            await WriteClipboardAsync(interaction.Input);
            MainActivity.Toast(Tr.Copied);
            interaction.SetOutput(RxVoid.Default);
        }));

        _registrations.Add(vm.StatusBarViewModel.SetClipboardDataInteraction.RegisterHandler(async interaction =>
        {
            await WriteClipboardAsync(interaction.Input);
            interaction.SetOutput(RxVoid.Default);
        }));

        // Desktop-only UI hooks ServiceLib still raises (grid refresh, focus,
        // column widths, tray icon): nothing to do here, but they must be
        // handled or every server refresh logs an unhandled interaction.
        _registrations.Add(vm.ProfilesViewModel.DispatcherRefreshServersBizInteraction.RegisterHandler(i => i.SetOutput(RxVoid.Default)));
        _registrations.Add(vm.ProfilesViewModel.ProfilesFocusInteraction.RegisterHandler(i => i.SetOutput(RxVoid.Default)));
        _registrations.Add(vm.ProfilesViewModel.AdjustMainLvColWidthInteraction.RegisterHandler(i => i.SetOutput(RxVoid.Default)));
        _registrations.Add(vm.StatusBarViewModel.DispatcherRefreshIconInteraction.RegisterHandler(i => i.SetOutput(RxVoid.Default)));
    }

    #endregion Clipboard & interactions
}
