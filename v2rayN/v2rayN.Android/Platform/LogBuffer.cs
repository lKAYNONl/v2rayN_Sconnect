namespace v2rayN.Mobile.Platform;

/// <summary>
/// The in-app log: everything ServiceLib sends to the desktop's message panel
/// (core output, connect steps, errors) plus our own tunnel events. Started
/// before the UI so sessions brought up by the system (always-on VPN) are
/// logged too. Lines are mirrored to logcat under the tag "SecureConnect".
/// </summary>
internal sealed class LogBuffer : IObserver<string>
{
    private const int MaxLines = 800;
    private const string LogcatTag = "SecureConnect";

    public static LogBuffer Instance { get; } = new();

    private readonly Lock _gate = new();
    private readonly Queue<string> _lines = new();
    private IDisposable? _subscription;

    /// <summary>Raised (on any thread) after lines were added or cleared.</summary>
    public event Action? Changed;

    public void Start()
    {
        _subscription ??= AppEvents.SendMsgViewRequested.AsObservable().Subscribe(this);
    }

    public void Add(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r', ' ');
                if (line.Length == 0)
                {
                    continue;
                }
                _lines.Enqueue(line);
                global::Android.Util.Log.Info(LogcatTag, line);
            }
            while (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
            }
        }
        Changed?.Invoke();
    }

    public string Snapshot()
    {
        lock (_gate)
        {
            return string.Join('\n', _lines);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
        Changed?.Invoke();
    }

    void IObserver<string>.OnNext(string value) => Add(value);

    void IObserver<string>.OnError(Exception error)
    {
    }

    void IObserver<string>.OnCompleted()
    {
    }
}
