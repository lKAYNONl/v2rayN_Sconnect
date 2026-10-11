namespace ServiceLib.Common;

/// <summary>
/// Hooks for hosts other than the desktop app - today the Android app
/// (v2rayN.Android). The desktop never sets them, so its behaviour is unchanged.
/// <para>
/// Android differs from the desktop in three places that ServiceLib touches:
/// the app may only write to its own data directory, the cores ship inside the
/// APK as <c>lib*.so</c> executables, and the core may only run while the
/// VpnService tunnel is up (a core started outside it would just be a local
/// proxy nobody uses).
/// </para>
/// </summary>
public static class PlatformHost
{
    /// <summary>
    /// Root for guiConfigs, bin, binConfigs, guiLogs... Overrides
    /// <see cref="Utils.StartupPath"/> (Android: Context.FilesDir).
    /// </summary>
    public static string? DataDir { get; set; }

    /// <summary>
    /// Resolves the executable of a core. Returning null or a missing file falls
    /// back to the usual bin/&lt;core&gt;/ lookup. Android: the app's native
    /// library dir (libsingbox.so, libxray.so).
    /// </summary>
    public static Func<CoreInfo, string?>? CoreExecResolver { get; set; }

    /// <summary>
    /// When set, a reload only starts the core if this returns true. Android
    /// gates it on the VPN session, so startup, settings changes and server
    /// switches do not start a core while disconnected.
    /// </summary>
    public static Func<bool>? CanRunCore { get; set; }

    /// <summary>True when a host has taken over the data directory.</summary>
    public static bool HasDataDir => !string.IsNullOrEmpty(DataDir);
}
