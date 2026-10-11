using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Runtime;
using AIcon = global::Android.Graphics.Drawables.Icon;

namespace v2rayN.Mobile.Platform;

/// <summary>
/// The Android VPN. It only owns the TUN interface: the core (sing-box / Xray,
/// started by ServiceLib's CoreManager as a local proxy) and hev-socks5-tunnel
/// (TUN fd -> SOCKS5) do the rest. The orchestration - consent, core first,
/// then the interface - lives in <see cref="VpnController"/>.
/// Declared in AndroidManifest.xml (specialUse foreground service subtype).
/// </summary>
[Register("io.github.lkaynonl.secureconnect.SecureVpnService")]
public sealed class SecureVpnService : VpnService
{
    public const string ActionConnect = "io.github.lkaynonl.secureconnect.CONNECT";
    public const string ActionDisconnect = "io.github.lkaynonl.secureconnect.DISCONNECT";

    private const int NotificationId = 1;
    private const string ChannelId = "vpn";

    private volatile ParcelFileDescriptor? _tun;

    /// <summary>The running service instance (null when stopped).</summary>
    public static SecureVpnService? Current { get; private set; }

    public bool HasTunnel => _tun != null;

    /// <summary>"Always-on VPN" is set for this app (only meaningful while running).</summary>
    public bool AlwaysOnEnabled => OperatingSystem.IsAndroidVersionAtLeast(29) && IsAlwaysOn;

    /// <summary>"Block connections without VPN" is set - the system kill switch.</summary>
    public bool LockdownOn => OperatingSystem.IsAndroidVersionAtLeast(29) && IsLockdownEnabled;

    public override void OnCreate()
    {
        base.OnCreate();
        Current = this;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action;
        if (action == ActionDisconnect)
        {
            Dispatcher.UIThread.Post(async () => await VpnController.Instance.DisconnectAsync(null));
            return StartCommandResult.NotSticky;
        }

        ShowNotification(Tr.NotifConnecting, connected: false);

        // ActionConnect comes from our UI. Anything else is the system: an
        // always-on VPN starting (action android.net.VpnService) or a restart
        // of the sticky service after the process was killed (null intent).
        var fromSystem = action != ActionConnect;
        Dispatcher.UIThread.Post(async () => await VpnController.Instance.OnServiceStartedAsync(this, fromSystem));
        return StartCommandResult.Sticky;
    }

    public override void OnRevoke()
    {
        // The user switched the VPN off in the system UI, or another VPN app took over.
        Dispatcher.UIThread.Post(async () => await VpnController.Instance.OnRevokedAsync());
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        CloseTunnel();
        if (Current == this)
        {
            Current = null;
        }
        base.OnDestroy();
    }

    /// <summary>
    /// Creates (or re-creates, after split tunneling changes) the VPN interface
    /// and starts hev-socks5-tunnel on it. Android swaps a re-established
    /// interface in place, so apps keep their network during a re-plan.
    /// Blocks while hev restarts: call it off the UI thread.
    /// </summary>
    public (bool Ok, string Error) EstablishTunnel(TunnelPlan plan, string session, int socksPort)
    {
        try
        {
            var builder = new Builder(this);
            builder.SetSession(session);
            builder.SetMtu(TunnelPlan.Mtu);
            // Both families always get an address: a family without one is blocked
            // for the apps in the VPN, while one without routes simply goes direct
            // (e.g. IPv6 when only IPv4 subnets are tunneled).
            builder.AddAddress(TunnelPlan.Address4, TunnelPlan.Address4Prefix);
            builder.AddAddress(TunnelPlan.Address6, TunnelPlan.Address6Prefix);
            foreach (var route in plan.Routes4.Concat(plan.Routes6))
            {
                builder.AddRoute(route.AddressText, route.Length);
            }
            if (plan.AnnounceDns)
            {
                builder.AddDnsServer(TunnelPlan.Dns4);
            }

            ApplyApps(builder, plan);

            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                // A VPN must not turn Wi-Fi into a "metered" network for every app.
                builder.SetMetered(false);
            }
            var launch = LaunchPendingIntent();
            if (launch != null)
            {
                builder.SetConfigureIntent(launch);
            }

            var tun = builder.Establish();
            if (tun == null)
            {
                return (false, Tr.NotPrepared);
            }

            // hev moves to the new fd; the previous interface (if any) is closed after the switch.
            var old = _tun;
            _tun = tun;
            var started = HevTunnel.Start(tun.Fd, HevTunnel.BuildConfig(socksPort, TunnelPlan.Mtu));
            if (!ReferenceEquals(old, tun))
            {
                CloseQuietly(old);
            }
            return started ? (true, string.Empty) : (false, "hev-socks5-tunnel did not start - see the log");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("EstablishTunnel", ex);
            return (false, ex.Message);
        }
    }

    private void ApplyApps(Builder builder, TunnelPlan plan)
    {
        var allowed = 0;
        foreach (var app in plan.AllowedApps)
        {
            try
            {
                builder.AddAllowedApplication(app);
                allowed++;
            }
            catch (Exception)
            {
                LogBuffer.Instance.Add($"split tunneling: {app} is not installed, skipped");
            }
        }

        // No allowed app could be added (all uninstalled): a whitelist would now
        // mean "every app", including ours - keep the core out of its own tunnel.
        List<string> disallowed;
        if (plan.AppWhitelist && allowed > 0)
        {
            disallowed = new List<string>();
        }
        else if (plan.DisallowedApps.Count > 0)
        {
            disallowed = plan.DisallowedApps;
        }
        else
        {
            disallowed = new List<string> { PackageName ?? string.Empty };
        }
        foreach (var app in disallowed.Where(a => a.Length > 0))
        {
            try
            {
                builder.AddDisallowedApplication(app);
            }
            catch (Exception)
            {
                LogBuffer.Instance.Add($"split tunneling: {app} is not installed, skipped");
            }
        }
    }

    /// <summary>Stops hev and closes the interface; the service keeps running. Blocks up to 3 s.</summary>
    public void CloseTunnel()
    {
        HevTunnel.Stop();
        var tun = _tun;
        _tun = null;
        CloseQuietly(tun);
    }

    /// <summary>Ends the session: tunnel (off the UI thread), notification, service.</summary>
    public async Task ShutdownAsync()
    {
        await Task.Run(CloseTunnel);
        Shutdown();
    }

    /// <summary>Ends the session: tunnel, notification, service.</summary>
    public void Shutdown()
    {
        CloseTunnel();
        try
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        catch
        {
        }
        StopSelf();
    }

    public void ShowNotification(string text, bool connected)
    {
        try
        {
            var manager = GetSystemService(NotificationService) as NotificationManager;
            if (manager == null)
            {
                return;
            }
            var channel = new NotificationChannel(ChannelId, Tr.NotificationChannel, NotificationImportance.Low);
            channel.SetShowBadge(false);
            manager.CreateNotificationChannel(channel);

            var icon = Resources?.GetIdentifier("ic_stat_vpn", "drawable", PackageName) ?? 0;
            var builder = new Notification.Builder(this, ChannelId)
                .SetContentTitle("Secure Connect")
                .SetContentText(text)
                .SetSmallIcon(icon != 0 ? icon : global::Android.Resource.Drawable.IcLockLock)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .SetShowWhen(connected);
            var launch = LaunchPendingIntent();
            if (launch != null)
            {
                builder.SetContentIntent(launch);
            }

            var stopIntent = new Intent(this, typeof(SecureVpnService)).SetAction(ActionDisconnect);
            var stop = PendingIntent.GetService(this, 1, stopIntent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            if (stop != null)
            {
                var actionIcon = AIcon.CreateWithResource(this, icon != 0 ? icon : global::Android.Resource.Drawable.IcLockLock);
                builder.AddAction(new Notification.Action.Builder(actionIcon, new Java.Lang.String(Tr.Disconnect), stop).Build());
            }

            var notification = builder.Build();
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        catch (Exception ex)
        {
            // Without the notification the VPN still works (the system keeps a bound
            // VpnService alive); only the status icon is missing.
            Logging.SaveLog("VpnNotification", ex);
        }
    }

    private PendingIntent? LaunchPendingIntent()
    {
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
        return launch == null
            ? null
            : PendingIntent.GetActivity(this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }

    private static void CloseQuietly(ParcelFileDescriptor? fd)
    {
        try
        {
            fd?.Close();
        }
        catch
        {
        }
    }
}
