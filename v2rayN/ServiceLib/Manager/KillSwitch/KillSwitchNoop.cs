namespace ServiceLib.Manager;

/// <summary>
/// No-op backend for platforms where the firewall kill switch is not yet
/// implemented (Linux / macOS). The Windows backend is <see cref="KillSwitchWindows"/>.
/// On Linux a future backend can use nftables; on macOS, pf.
/// </summary>
public class KillSwitchNoop : IKillSwitchService
{
    public bool IsArmed => false;

    public Task<bool> ArmAsync(KillSwitchContext context) => Task.FromResult(false);

    public Task DisarmAsync() => Task.CompletedTask;

    public Task RecoverIfNeededAsync() => Task.CompletedTask;
}
