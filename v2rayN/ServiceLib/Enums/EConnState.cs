namespace ServiceLib.Enums;

/// <summary>
/// Connection state of the Secure Connect dashboard: drives the state label,
/// the dial (idle / spinning / connected) and the session timer.
/// </summary>
public enum EConnState
{
    Off,
    Connecting,
    On,
    Stopping
}
