using System.Runtime.InteropServices;

namespace v2rayN.Mobile.Platform;

/// <summary>
/// hev-socks5-tunnel (MIT, github.com/heiher/hev-socks5-tunnel) loaded in-process
/// from libhev-socks5-tunnel.so: reads IP packets from the VPN interface fd and
/// turns TCP/UDP flows into SOCKS5 connections to the core's local inbound.
/// The library's own JNI glue is never used - JNI_OnLoad only runs for
/// System.loadLibrary, and P/Invoke loads the library with dlopen.
/// </summary>
internal static class HevTunnel
{
    private const string Lib = "hev-socks5-tunnel";

    [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_main_from_str")]
    private static extern int NativeMainFromStr(byte[] config, uint length, int tunFd);

    [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_quit")]
    private static extern void NativeQuit();

    [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_stats")]
    private static extern void NativeStats(out nuint txPackets, out nuint txBytes, out nuint rxPackets, out nuint rxBytes);

    private static readonly Lock Gate = new();
    private static Thread? _thread;

    public static bool IsRunning => _thread is { IsAlive: true };

    /// <summary>
    /// hev configuration. With an fd from VpnService only the MTU matters on the
    /// tunnel side; "udp: udp" relays UDP with standard SOCKS5 UDP ASSOCIATE,
    /// which both the sing-box mixed and the Xray socks inbound speak.
    /// </summary>
    public static string BuildConfig(int socksPort, int mtu)
    {
        var sb = new StringBuilder();
        sb.Append("tunnel:\n");
        sb.Append("  mtu: ").Append(mtu.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("socks5:\n");
        sb.Append("  port: ").Append(socksPort.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("  address: 127.0.0.1\n");
        sb.Append("  udp: 'udp'\n");
        sb.Append("misc:\n");
        sb.Append("  tcp-read-write-timeout: 300000\n");
        sb.Append("  udp-read-write-timeout: 60000\n");
        sb.Append("  log-level: warn\n");
        return sb.ToString();
    }

    /// <summary>
    /// Starts the tunnel on its own thread (hev blocks until quit). False when
    /// it stopped right away: bad config, library missing, or a stop request
    /// left over from the previous run (hev then returns at once - retried).
    /// Blocks for a moment: call it off the UI thread.
    /// </summary>
    public static bool Start(int tunFd, string config)
    {
        lock (Gate)
        {
            StopLocked();

            var bytes = Encoding.UTF8.GetBytes(config);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var thread = new Thread(() => Run(bytes, tunFd))
                {
                    IsBackground = true,
                    Name = "hev-socks5-tunnel",
                };
                thread.Start();
                if (!thread.Join(TimeSpan.FromMilliseconds(300)))
                {
                    _thread = thread;
                    return true;
                }
            }
            return false;
        }
    }

    private static void Run(byte[] config, int tunFd)
    {
        try
        {
            var rc = NativeMainFromStr(config, (uint)config.Length, tunFd);
            NoticeManager.Instance.SendMessageEx($"hev-socks5-tunnel stopped (code {rc})");
        }
        catch (Exception ex)
        {
            // DllNotFoundException / EntryPointNotFoundException end up here.
            Logging.SaveLog("HevTunnel", ex);
            NoticeManager.Instance.SendMessageEx($"hev-socks5-tunnel failed: {ex.Message}");
        }
    }

    /// <summary>Stops the tunnel and waits for its thread (up to 3 s - call it off the UI thread). The fd stays open (it belongs to the caller).</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            StopLocked();
        }
    }

    private static void StopLocked()
    {
        var thread = _thread;
        _thread = null;
        if (thread is not { IsAlive: true })
        {
            return;
        }
        try
        {
            NativeQuit();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("HevTunnel.Quit", ex);
        }
        if (!thread.Join(TimeSpan.FromSeconds(3)))
        {
            NoticeManager.Instance.SendMessageEx("hev-socks5-tunnel did not stop in 3 s");
        }
    }

    /// <summary>Bytes through the VPN interface since start (tx = to the apps, rx = from them).</summary>
    public static (long TxBytes, long RxBytes) GetStats()
    {
        if (!IsRunning)
        {
            return (0, 0);
        }
        try
        {
            NativeStats(out _, out var tx, out _, out var rx);
            return ((long)tx, (long)rx);
        }
        catch
        {
            return (0, 0);
        }
    }
}
