namespace ServiceLib.Manager;

/// <summary>
/// Persisted on disk while the lock is armed, so a crash can be recovered on
/// the next launch (see <see cref="IKillSwitchService.RecoverIfNeededAsync"/>).
/// Written BEFORE the firewall is modified and cleared AFTER it is restored.
/// </summary>
public class KillSwitchState
{
    public bool Armed { get; set; }

    /// <summary>
    /// The DefaultOutboundAction of each firewall profile before we armed, so
    /// we can put it back exactly. Keys: "Domain", "Private", "Public".
    /// Values: "Allow" / "Block" / "NotConfigured".
    /// </summary>
    public Dictionary<string, string> SavedOutboundAction { get; set; } = new();

    public string TimestampUtc { get; set; } = string.Empty;
}
