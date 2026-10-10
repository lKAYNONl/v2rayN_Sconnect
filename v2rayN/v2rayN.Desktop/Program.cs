using v2rayN.Desktop.Common;
using v2rayN.Desktop.Manager;

namespace v2rayN.Desktop;

internal class Program
{
    public static EventWaitHandle ProgramStarted;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OnStartup(args) == false)
        {
            Environment.Exit(0);
            return;
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static bool OnStartup(string[]? Args)
    {
        if (Utils.IsWindows())
        {
            var exePathKey = Utils.GetMd5(Utils.GetExePath());
            var rebootas = (Args ?? []).Any(t => t == Global.RebootAs);
            ProgramStarted = new EventWaitHandle(false, EventResetMode.AutoReset, exePathKey, out var bCreatedNew);
            if (!rebootas && !bCreatedNew)
            {
                ProgramStarted.Set();
                return false;
            }
            if (rebootas)
            {
                WaitForPreviousInstance();
            }
        }
        else
        {
            _ = new Mutex(true, "v2rayN", out var bOnlyOneInstance);
            if (!bOnlyOneInstance)
            {
                return false;
            }
        }

        if (!AppManager.Instance.InitApp())
        {
            return false;
        }

        AppManager.Instance.WindowDialog = new WindowDialog();
        return true;
    }

    /// <summary>
    /// A restart ("rebootas": run as administrator, language change) starts this
    /// instance before the old one has quit. Let the old one stop its core and lift
    /// the kill switch first, so the two sessions never overlap.
    /// </summary>
    private static void WaitForPreviousInstance()
    {
        try
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var exePath = Utils.GetExePath();
            foreach (var other in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
            {
                using (other)
                {
                    if (other.Id == current.Id)
                    {
                        continue;
                    }
                    try
                    {
                        // Only this copy of the app (another v2rayN elsewhere is not ours).
                        if (string.Equals(other.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                        {
                            other.WaitForExit(15000);
                        }
                    }
                    catch
                    {
                        // No access to that process: nothing to wait for.
                    }
                }
            }
        }
        catch
        {
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
           .UsePlatformDetect()
           //.WithInterFont()
           .WithFontByDefault()
#if DEBUG
           .WithDeveloperTools()
#endif
           .LogToTrace()
           .UseReactiveUI(_ => { });

        if (OperatingSystem.IsMacOS())
        {
            var showInDock = Design.IsDesignMode || AppManager.Instance.Config.UiItem.MacOSShowInDock;
            builder = builder.With(new MacOSPlatformOptions { ShowInDock = showInDock });
        }

        return builder;
    }
}
