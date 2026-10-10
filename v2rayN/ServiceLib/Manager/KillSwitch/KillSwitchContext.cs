namespace ServiceLib.Manager;

/// <summary>
/// Immutable snapshot describing what the kill switch must permit while armed.
/// Built at connect time from the running core and the active config so the
/// firewall state and the launch mode always agree, even if settings are
/// toggled while a reload is in flight.
/// </summary>
public class KillSwitchContext
{
    /// <summary>
    /// Full path to the running core executable (e.g. sing-box.exe).
    /// This is the only process allowed to make direct outbound connections
    /// to the physical network while the lock is armed — it is what actually
    /// dials the VPN server. Everything else must go through the TUN device
    /// (and therefore through this process), or it is dropped.
    /// </summary>
    public string CoreExePath { get; set; } = string.Empty;

    /// <summary>
    /// TUN interface name created by the core (Windows: "singbox_tun").
    /// Not needed by the Windows Firewall backend (all real egress is the core
    /// process), but kept for the planned native-WFP backend and diagnostics.
    /// </summary>
    public string? TunInterfaceName { get; set; }

    /// <summary>
    /// When true, keep the local network reachable (printers, NAS, SSH to LAN)
    /// while every other destination is forced through the tunnel.
    /// Mirrors WireSock's "Allow access to local network" option.
    /// </summary>
    public bool AllowLan { get; set; }

    /// <summary>
    /// Extra programs allowed to bypass the lock (full paths). Populated from
    /// the split-tunneling "bypass apps" list. NOTE: the firewall only *permits*
    /// these; sing-box routing rules must also exclude them from the tunnel for
    /// them to actually egress directly.
    /// </summary>
    public List<string> AllowedPrograms { get; set; } = new();

    /// <summary>
    /// Extra remote subnets allowed direct (CIDR). Populated from the
    /// split-tunneling "bypass networks" list.
    /// </summary>
    public List<string> AllowedSubnets { get; set; } = new();
}
