using AContext = global::Android.Content.Context;

namespace v2rayN.Mobile.Platform;

/// <summary>
/// Process-wide state of the Android app. <see cref="Bootstrap"/> runs from
/// Application.OnCreate - before any activity, so it also covers the VPN
/// service started by the system (always-on) - and points ServiceLib at the
/// app's data dir and the cores inside the APK. The shared
/// <see cref="MainWindowViewModel"/> lives here, not in a view: the VPN
/// service keeps the process (and the session) alive across activities.
/// </summary>
internal static class AppHost
{
    private static MainWindowViewModel? _mainVm;

    public static AContext Context { get; private set; } = null!;

    public static string PackageName { get; private set; } = string.Empty;

    /// <summary>Where Android unpacked lib*.so - the only app location we may exec from.</summary>
    public static string NativeLibDir { get; private set; } = string.Empty;

    public static bool IsReady { get; private set; }

    public static void Bootstrap(AContext context)
    {
        if (IsReady)
        {
            return;
        }

        Context = context;
        PackageName = context.PackageName ?? string.Empty;
        NativeLibDir = context.ApplicationInfo?.NativeLibraryDir ?? string.Empty;
        var dataDir = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android files dir is not available");

        var firstRun = !File.Exists(Path.Combine(dataDir, "guiConfigs", Global.ConfigFileName));

        PlatformHost.DataDir = dataDir;
        PlatformHost.CoreExecResolver = ResolveCore;
        PlatformHost.CanRunCore = () => VpnController.Instance.SessionWanted;

        LogBuffer.Instance.Start();

        if (!AppManager.Instance.InitApp())
        {
            LogBuffer.Instance.Add(Tr.StartupFailed);
            return;
        }

        var config = AppManager.Instance.Config;
        // The VpnService is the TUN on Android: the core always runs as a local proxy.
        config.TunModeItem.EnableTun = false;
        if (firstRun)
        {
            ApplyFirstRunDefaults(config);
        }
        Tr.Init(config.UiItem.CurrentLanguage);

        AppManager.Instance.WindowDialog = new MobileWindowDialog();
        AppManager.Instance.InitComponents();

        AssetInstaller.InstallGeoFiles(context);
        CoreReaper.KillOrphans();

        LogBuffer.Instance.Add($"{Utils.GetVersion()} | Android {global::Android.OS.Build.VERSION.Release} (API {(int)global::Android.OS.Build.VERSION.SdkInt}) | {global::Android.OS.Build.Manufacturer} {global::Android.OS.Build.Model}");
        IsReady = true;
    }

    /// <summary>
    /// The shared main view model (profiles, status, routes, reload/stop).
    /// Created on first use, which must be on the UI thread after Avalonia setup.
    /// </summary>
    public static MainWindowViewModel MainVm
    {
        get
        {
            if (_mainVm == null)
            {
                _mainVm = new MainWindowViewModel();
                VpnController.Instance.Attach(_mainVm);
            }
            return _mainVm;
        }
    }

    private static string? ResolveCore(CoreInfo info) => info.CoreType switch
    {
        ECoreType.sing_box => Path.Combine(NativeLibDir, "libsingbox.so"),
        ECoreType.Xray => Path.Combine(NativeLibDir, "libxray.so"),
        _ => null,
    };

    private static void ApplyFirstRunDefaults(Config config)
    {
        // Follow the phone's language on first start (ServiceLib defaults to English).
        var lang = Java.Util.Locale.Default.Language;
        if (string.Equals(lang, "ru", StringComparison.OrdinalIgnoreCase))
        {
            config.UiItem.CurrentLanguage = "ru";
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("ru");
        }
        _ = Task.Run(() => ConfigHandler.SaveConfig(config));
    }

    /// <summary>Saves the shared config off the UI thread.</summary>
    public static Task SaveConfigAsync() => Task.Run(() => ConfigHandler.SaveConfig(AppManager.Instance.Config));
}

/// <summary>
/// Desktop dialogs (server editor, option windows...) have no Android
/// counterpart yet; ServiceLib commands that open one get a short notice.
/// </summary>
internal sealed class MobileWindowDialog : IWindowDialog
{
    public Task<bool> ShowDialogAsync<TViewModel>(TViewModel vm) where TViewModel : class
    {
        NoticeManager.Instance.Enqueue(Tr.NotOnAndroid);
        return Task.FromResult(false);
    }
}

/// <summary>Copies the geo files shipped in the APK to bin/ for Xray (once per install/update).</summary>
internal static class AssetInstaller
{
    private static readonly string[] GeoFiles = ["geoip.dat", "geosite.dat"];

    public static void InstallGeoFiles(AContext context)
    {
        try
        {
            var binDir = Utils.GetBinPath("");
            var stampFile = Path.Combine(binDir, ".geo-stamp");
            // Changes with every install or update of the APK.
            var apk = context.ApplicationInfo?.SourceDir;
            var stamp = !string.IsNullOrEmpty(apk) && File.Exists(apk)
                ? File.GetLastWriteTimeUtc(apk).Ticks.ToString(CultureInfo.InvariantCulture)
                : "0";
            if (File.Exists(stampFile)
                && File.ReadAllText(stampFile) == stamp
                && GeoFiles.All(f => File.Exists(Path.Combine(binDir, f))))
            {
                return;
            }

            var assets = context.Assets;
            if (assets == null)
            {
                return;
            }
            foreach (var name in GeoFiles)
            {
                try
                {
                    var target = Path.Combine(binDir, name);
                    var temp = target + ".tmp";
                    using (var input = assets.Open($"geo/{name}"))
                    using (var output = File.Create(temp))
                    {
                        input.CopyTo(output);
                    }
                    File.Move(temp, target, true);
                }
                catch (Exception ex)
                {
                    LogBuffer.Instance.Add($"geo asset {name} not installed: {ex.Message}");
                }
            }
            File.WriteAllText(stampFile, stamp);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("InstallGeoFiles", ex);
        }
    }
}

/// <summary>
/// Cores are child processes. If the app process died (crash, force stop)
/// they may still hold the local proxy port; kill those before a new session.
/// Android only lets an app see its own processes in /proc.
/// </summary>
internal static class CoreReaper
{
    private static readonly string[] CoreFiles = ["/libsingbox.so", "/libxray.so"];

    public static void KillOrphans()
    {
        try
        {
            var myPid = global::Android.OS.Process.MyPid();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == myPid)
                {
                    continue;
                }

                string exe;
                try
                {
                    exe = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0')[0];
                }
                catch
                {
                    continue;
                }

                if (CoreFiles.Any(f => exe.EndsWith(f, StringComparison.Ordinal)))
                {
                    global::Android.OS.Process.KillProcess(pid);
                    LogBuffer.Instance.Add($"Stopped a leftover core process {pid} ({Path.GetFileName(exe)})");
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("KillOrphans", ex);
        }
    }
}
