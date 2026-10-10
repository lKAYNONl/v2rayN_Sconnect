namespace ServiceLib.Manager;

/// <summary>
/// A platform backend that enforces the network lock ("kill switch"):
/// while armed, no traffic may leave the machine except through the active
/// tunnel, so nothing leaks if the core drops or during connect/disconnect.
/// </summary>
public interface IKillSwitchService
{
    /// <summary>True while the lock is currently enforced.</summary>
    bool IsArmed { get; }

    /// <summary>
    /// Arm the lock for the given context. Idempotent: calling it again while
    /// already armed refreshes the rules (used on reload) without opening a
    /// leak window. Returns true on success.
    /// </summary>
    Task<bool> ArmAsync(KillSwitchContext context);

    /// <summary>Remove the lock and restore the previous network state.</summary>
    Task DisarmAsync();

    /// <summary>
    /// Called once at startup. If a previous run armed the lock and never
    /// disarmed cleanly (e.g. the app crashed), restore the network state so
    /// the user is never left without connectivity and no running app to fix it.
    /// </summary>
    Task RecoverIfNeededAsync();
}
