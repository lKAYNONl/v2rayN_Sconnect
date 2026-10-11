using v2rayN.Mobile.Platform;
using v2rayN.Mobile.Views;

namespace v2rayN.Mobile;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Logging.SaveLog("CurrentDomain_UnhandledException", ex);
                LogBuffer.Instance.Add($"FATAL: {ex}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logging.SaveLog("TaskScheduler_UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ServiceLib's snack messages become Android toasts (and log lines).
        if (AppHost.IsReady)
        {
            AppEvents.SendSnackMsgRequested.AsObservable().Subscribe(new ToastObserver());
        }

        // One view per activity; the view model is process-wide (AppHost).
        if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            activityLifetime.MainViewFactory = () => new MainView();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private sealed class ToastObserver : IObserver<string>
    {
        public void OnNext(string value)
        {
            LogBuffer.Instance.Add(value);
            // "Operation success" after every disconnect is noise on a phone.
            if (value == ResUI.OperationSuccess)
            {
                return;
            }
            Dispatcher.UIThread.Post(() => MainActivity.Toast(value));
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}

/// <summary>Palette and fonts of the Secure Connect design (same as the desktop dashboard).</summary>
internal static class Ui
{
    public const string SansFamily = "avares://v2rayN.Android/Assets/Fonts#Manrope";
    public const string MonoFamily = "avares://v2rayN.Android/Assets/Fonts#IBM Plex Mono";

    public static readonly IBrush Fg = Brush("#E8EBF6");
    public static readonly IBrush Muted = Brush("#8B95B4");
    public static readonly IBrush Faint = Brush("#5B648A");
    public static readonly IBrush Line = Brush("#273055");
    public static readonly IBrush Surface = Brush("#161C35");
    public static readonly IBrush Elevated = Brush("#1D2647");
    public static readonly IBrush Accent = Brush("#37D3C2");
    public static readonly IBrush Accent2 = Brush("#4F8CFF");
    public static readonly IBrush Warn = Brush("#F5B83D");
    public static readonly IBrush Danger = Brush("#FF5D6C");
    public static readonly IBrush DialOn = Brush("#319296");
    public static readonly IBrush LockOn = Brush("#987B48");
    public static readonly IBrush SelectedBorder = Brush("#3D63B3");
    public static readonly BoxShadows GlowAccent = BoxShadows.Parse("0 0 8 0 #37D3C2");
    public static readonly BoxShadows GlowWarn = BoxShadows.Parse("0 0 8 0 #F5B83D");

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();
}
