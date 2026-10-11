using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia.Android;
using v2rayN.Mobile.Platform;
using AResult = global::Android.App.Result;

namespace v2rayN.Mobile;

/// <summary>
/// Android Application: bootstraps ServiceLib before anything else runs
/// (an always-on VPN can start the service without any activity), then
/// sets up Avalonia.
/// </summary>
[global::Android.App.ApplicationAttribute(
    Name = "io.github.lkaynonl.secureconnect.SecureConnectApp",
    Label = "Secure Connect",
    Icon = "@mipmap/ic_launcher")]
public sealed class SecureConnectApp : AvaloniaAndroidApplication<App>
{
    public SecureConnectApp(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            Logging.SaveLog("Android unhandled", e.Exception);
            LogBuffer.Instance.Add($"FATAL: {e.Exception}");
        };

        try
        {
            AppHost.Bootstrap(this);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("SecureConnect", ex.ToString());
            LogBuffer.Instance.Add($"Startup failed: {ex}");
        }

        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder)
            .With(new FontManagerOptions { DefaultFamilyName = Ui.SansFamily })
            .LogToTrace()
            .UseReactiveUI(_ => { });
}

[global::Android.App.ActivityAttribute(
    Label = "Secure Connect",
    Theme = "@style/ScTheme",
    Icon = "@mipmap/ic_launcher",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Keyboard
                           | ConfigChanges.KeyboardHidden | ConfigChanges.Density)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const int VpnConsentRequest = 0x5C01;
    private const int NotificationPermissionRequest = 0x5C02;

    private TaskCompletionSource<bool>? _vpnConsent;

    /// <summary>The visible activity (VPN consent and system screens are launched from it).</summary>
    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        VpnController.Instance.RefreshKillState();
    }

    protected override void OnDestroy()
    {
        _vpnConsent?.TrySetResult(false);
        _vpnConsent = null;
        if (Current == this)
        {
            Current = null;
        }
        base.OnDestroy();
    }

    /// <summary>Shows the system "connection request" dialog; true when the user allowed it.</summary>
    public Task<bool> RequestVpnConsentAsync(Intent consent)
    {
        _vpnConsent?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _vpnConsent = tcs;
        StartActivityForResult(consent, VpnConsentRequest);
        return tcs.Task;
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] AResult resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == VpnConsentRequest)
        {
            _vpnConsent?.TrySetResult(resultCode == AResult.Ok);
            _vpnConsent = null;
        }
    }

    /// <summary>Android 13+: the connection notification needs a runtime permission (asked once, not required).</summary>
    public void RequestNotificationPermission()
    {
        const string permission = "android.permission.POST_NOTIFICATIONS";
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(permission) != Permission.Granted)
        {
            RequestPermissions([permission], NotificationPermissionRequest);
        }
    }

    /// <summary>Settings → Network → VPN, where Always-on and "Block connections without VPN" live.</summary>
    public void OpenVpnSettings()
    {
        try
        {
            StartActivity(new Intent(global::Android.Provider.Settings.ActionVpnSettings));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("OpenVpnSettings", ex);
            try
            {
                StartActivity(new Intent(global::Android.Provider.Settings.ActionWirelessSettings));
            }
            catch
            {
            }
        }
    }

    /// <summary>A short native toast.</summary>
    public static void Toast(string text)
    {
        var context = Current ?? (Context?)AppHost.Context;
        if (context == null || string.IsNullOrEmpty(text))
        {
            return;
        }
        global::Android.Widget.Toast.MakeText(context, text, global::Android.Widget.ToastLength.Short)?.Show();
    }
}
