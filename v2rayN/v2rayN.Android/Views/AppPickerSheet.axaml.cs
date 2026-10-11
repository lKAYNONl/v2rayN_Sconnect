using Avalonia.VisualTree;
using v2rayN.Mobile.Platform;

namespace v2rayN.Mobile.Views;

/// <summary>
/// Installed apps with search; tap toggles. The selection is handed back when
/// the sheet closes (Done or Back).
/// </summary>
public partial class AppPickerSheet : UserControl, ISheet
{
    private readonly HashSet<string> _selected;
    private readonly Func<List<AppEntry>, Task> _onDone;
    private List<AppEntry> _all = [];
    private bool _loaded;

    public AppPickerSheet()
        : this([], _ => Task.CompletedTask)
    {
    }

    internal AppPickerSheet(HashSet<string> selected, Func<List<AppEntry>, Task> onDone)
    {
        InitializeComponent();
        _selected = selected;
        _onDone = onDone;

        txtTitle.Text = Tr.Apps;
        btnDoneText.Text = Tr.Done;
        txtSearch.PlaceholderText = Tr.SearchApps;
        txtSystem.Text = Tr.ShowSystem;
        txtLoading.Text = Tr.Loading;

        btnBack.Click += (_, _) => MainView.Current?.CloseSheet(this);
        btnDone.Click += (_, _) => MainView.Current?.CloseSheet(this);
        txtSearch.TextChanged += (_, _) => ApplyFilter();
        tglSystem.IsCheckedChanged += (_, _) => ApplyFilter();
        lstApps.Tapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is AppEntry entry)
            {
                entry.IsSelected = !entry.IsSelected;
            }
        };

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var apps = await Task.Run(InstalledApps.Load);
            foreach (var app in apps)
            {
                app.IsSelected = _selected.Contains(app.Package);
            }
            // Selected apps first, so the current choice is visible at once.
            _all = apps.OrderByDescending(a => a.IsSelected).ToList();
            _loaded = true;
            txtLoading.IsVisible = false;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("LoadApps", ex);
            txtLoading.Text = ex.Message;
        }
    }

    private void ApplyFilter()
    {
        if (!_loaded)
        {
            return;
        }
        var query = txtSearch.Text?.Trim() ?? string.Empty;
        var showSystem = tglSystem.IsChecked == true;
        lstApps.ItemsSource = _all
            .Where(a => showSystem || !a.IsSystem || a.IsSelected)
            .Where(a => query.Length == 0
                        || a.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || a.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void OnClosed()
    {
        if (!_loaded)
        {
            return;
        }
        var picked = _all.Where(a => a.IsSelected).ToList();
        _ = _onDone(picked);
    }
}
