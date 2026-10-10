namespace ServiceLib.Manager;

/// <summary>
/// Singleton entry point for the kill switch. Picks the platform backend and
/// exposes a tiny surface (<see cref="ArmAsync"/> / <see cref="DisarmAsync"/> /
/// <see cref="RecoverIfNeededAsync"/>) that the rest of the app calls without
/// caring about the platform.
/// </summary>
public class KillSwitchManager
{
    private static readonly Lazy<KillSwitchManager> _instance = new(() => new());
    public static KillSwitchManager Instance => _instance.Value;

    private readonly IKillSwitchService _service;

    private KillSwitchManager()
    {
        // if/else (not a ternary) is what the platform analyzer reliably
        // recognises as an OS guard around the Windows-only backend.
        if (OperatingSystem.IsWindows())
        {
            _service = new KillSwitchWindows();
        }
        else
        {
            _service = new KillSwitchNoop();
        }
    }

    public bool IsArmed => _service.IsArmed;

    public Task<bool> ArmAsync(KillSwitchContext context) => _service.ArmAsync(context);

    public Task DisarmAsync() => _service.DisarmAsync();

    public Task RecoverIfNeededAsync() => _service.RecoverIfNeededAsync();
}
