using System.ComponentModel;
using Android.Content.PM;

namespace v2rayN.Mobile.Platform;

/// <summary>One row of the split tunneling app picker.</summary>
public sealed class AppEntry : INotifyPropertyChanged
{
    private bool _isSelected;

    public AppEntry(string package, string label, bool isSystem)
    {
        Package = package;
        Label = string.IsNullOrWhiteSpace(label) ? package : label;
        IsSystem = isSystem;
        Initial = Label.Length > 0 ? char.ToUpperInvariant(Label[0]).ToString() : "?";
    }

    public string Package { get; }

    public string Label { get; }

    public bool IsSystem { get; }

    /// <summary>Letter badge (icons would cost a bitmap per app).</summary>
    public string Initial { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal static class InstalledApps
{
    /// <summary>All installed apps except ours, sorted by name. Slow (labels): call off the UI thread.</summary>
    public static List<AppEntry> Load()
    {
        var context = AppHost.Context;
        var pm = context.PackageManager;
        if (pm == null)
        {
            return [];
        }

        var own = context.PackageName;
        var result = new List<AppEntry>();
#pragma warning disable CA1422 // GetInstalledApplications(int) is deprecated on API 33+ but still works.
        var apps = pm.GetInstalledApplications((PackageInfoFlags)0);
#pragma warning restore CA1422
        foreach (var info in apps)
        {
            var package = info.PackageName;
            if (string.IsNullOrEmpty(package) || package == own)
            {
                continue;
            }

            string label;
            try
            {
                label = info.LoadLabel(pm) ?? package;
            }
            catch
            {
                label = package;
            }

            var isSystem = (info.Flags & ApplicationInfoFlags.System) != 0
                           && (info.Flags & ApplicationInfoFlags.UpdatedSystemApp) == 0;
            result.Add(new AppEntry(package, label, isSystem));
        }

        return result
            .OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Package, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Display name of a package, or the package itself when it is not installed.</summary>
    public static string LabelOf(string package)
    {
        try
        {
            var pm = AppHost.Context.PackageManager;
#pragma warning disable CA1422
            var info = pm?.GetApplicationInfo(package, 0);
#pragma warning restore CA1422
            return info != null && pm != null ? info.LoadLabel(pm) ?? package : package;
        }
        catch
        {
            return package;
        }
    }
}
