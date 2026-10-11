using v2rayN.Mobile.Platform;

namespace v2rayN.Mobile.Views;

/// <summary>The in-app log (core output, connect steps). Copy it into a bug report.</summary>
public partial class LogSheet : UserControl, ISheet
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private volatile bool _dirty = true;

    public LogSheet()
    {
        InitializeComponent();

        txtTitle.Text = Tr.Log;
        btnCopyText.Text = Tr.Copy;
        btnClearText.Text = Tr.Clear;

        btnBack.Click += (_, _) => MainView.Current?.CloseSheet(this);
        btnClear.Click += (_, _) => LogBuffer.Instance.Clear();
        btnCopy.Click += async (_, _) =>
        {
            if (MainView.Current is { } shell)
            {
                await shell.WriteClipboardAsync(LogBuffer.Instance.Snapshot());
                MainActivity.Toast(Tr.Copied);
            }
        };
        _refresh.Tick += (_, _) => Flush();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LogBuffer.Instance.Changed += OnChanged;
        _dirty = true;
        Flush();
        _refresh.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LogBuffer.Instance.Changed -= OnChanged;
        _refresh.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public void OnClosed()
    {
    }

    private void OnChanged() => _dirty = true;

    private void Flush()
    {
        if (!_dirty)
        {
            return;
        }
        _dirty = false;
        var atEnd = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 24;
        txtLog.Text = LogBuffer.Instance.Snapshot();
        if (atEnd)
        {
            Dispatcher.UIThread.Post(() => scroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }
}
