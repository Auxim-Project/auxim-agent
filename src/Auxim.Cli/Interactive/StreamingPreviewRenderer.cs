using System.Text;

namespace Auxim.Cli.Interactive;

internal enum PreviewOutputMode { Alternate, Inline, Redirected }

// Paints only visible physical rows. The view owns the transcript; this class
// retains just the last screen frame and an optional bounded inline region.
internal sealed class StreamingPreviewRenderer(
    TextWriter output,
    TextWriter diagnostics,
    PreviewOutputMode mode,
    Func<(int Width, int Height)> size,
    StreamingTranscript transcript,
    Func<ScreenLayout>? prepareLayout = null,
    Action? suspendLayout = null,
    Action? resumeLayout = null,
    Action<string>? appendWorkspace = null,
    Action? refreshWorkspace = null)
{
    private string[] _previous = [];
    private (int Width, int Height) _lastSize;
    private int _regionHeight;
    private bool _initialized;
    private bool _suspended;
    private bool _segmentVisible;
    private bool _hadText;
    private bool _invalidated;
    private bool _uncertain;
    private ScreenLayout _layout;

    public void Append(string text)
    {
        var plain = TerminalTextLayout.Plain(text);
        if (plain.Length == 0) return;
        _hadText = true;
        transcript.Append(plain);
        appendWorkspace?.Invoke(plain);
        if (mode == PreviewOutputMode.Redirected)
        {
            if (!_segmentVisible) diagnostics.Write("[preview] ");
            diagnostics.Write(plain.Replace("\r", ""));
            diagnostics.Flush();
        }
        _segmentVisible = true;
        Refresh();
    }

    public void Refresh()
    {
        if (_suspended || mode == PreviewOutputMode.Redirected) return;
        if (refreshWorkspace is not null)
        {
            refreshWorkspace();
            return;
        }
        var dimensions = size();
        var previousLayout = _layout;
        if (mode == PreviewOutputMode.Alternate)
            _layout = prepareLayout?.Invoke() ?? ScreenLayout.Create(dimensions.Width, dimensions.Height);
        if (!transcript.HasContent) return;
        if (dimensions.Width < 2 || dimensions.Height < 3)
        {
            _uncertain = _initialized;
            ClearRegion();
            _lastSize = dimensions;
            return;
        }
        var geometryChanged = dimensions != _lastSize || mode == PreviewOutputMode.Alternate && previousLayout != _layout;
        if (!transcript.IsDirty && !geometryChanged && !_invalidated) return;
        var columns = ConsoleTheme.ReadableWidth(dimensions.Width - 1); // never trigger terminal auto-wrap
        var capacity = mode == PreviewOutputMode.Alternate ? _layout.ContentHeight : Math.Max(1, dimensions.Height - 2);
        if (capacity == 0)
        {
            _previous = [];
            _initialized = false;
            _lastSize = dimensions;
            return;
        }
        if (mode == PreviewOutputMode.Inline) capacity = Math.Min(6, capacity);
        transcript.Prepare(columns, capacity);
        var rows = transcript.Viewport.GetVisibleLines(capacity).ToArray();
        var frame = new StringBuilder();

        if (mode == PreviewOutputMode.Alternate)
        {
            if (!_initialized || geometryChanged || _invalidated)
            {
                for (var i = 0; i < capacity; i++)
                    frame.Append($"\u001b[{_layout.ContentTop + i};1H\u001b[2K");
                _previous = [];
            }
            for (var i = 0; i < Math.Min(capacity, Math.Max(rows.Length, _previous.Length)); i++)
            {
                var line = i < rows.Length ? rows[i] : "";
                if (i < _previous.Length && _previous[i] == line) continue;
                frame.Append($"\u001b[{_layout.ContentTop + i};1H\u001b[2K{ConsoleTheme.StreamingRow(line)}");
            }
            if (frame.Length > 0) frame.Append($"\u001b[{_layout.InputRow};{_layout.InputStartColumn}H");
        }
        else
        {
            if (!_initialized || geometryChanged || _invalidated)
            {
                // Resize reflow invalidates relative coordinates. Rebuild once,
                // rather than moving through an unknown native scrollback area.
                if (_initialized && geometryChanged) frame.Append("\u001b[2J\u001b[H");
                _regionHeight = capacity;
                frame.Append('\r').Append('\n', _regionHeight);
                _previous = [];
            }
            for (var i = 0; i < _regionHeight; i++)
            {
                var line = i < rows.Length ? rows[i] : "";
                if (i < _previous.Length && _previous[i] == line) continue;
                var up = _regionHeight - i;
                frame.Append($"\u001b[{up}A\r\u001b[2K{ConsoleTheme.StreamingRow(line)}\r\u001b[{up}B");
            }
        }

        if (frame.Length > 0)
        {
            _initialized = _uncertain = true;
            output.Write(frame.ToString());
            output.Flush();
            _uncertain = false;
        }
        _previous = mode == PreviewOutputMode.Inline
            ? Enumerable.Range(0, _regionHeight).Select(i => i < rows.Length ? rows[i] : "").ToArray()
            : rows;
        _lastSize = dimensions;
        _initialized = true;
        _invalidated = false;
    }

    public void EndSegment()
    {
        transcript.EndSegment();
        if (_segmentVisible && mode == PreviewOutputMode.Redirected)
        {
            diagnostics.WriteLine();
            diagnostics.Flush();
        }
        _segmentVisible = false;
    }

    public void AddTool(string text)
    {
        transcript.AddTool(text);
        Refresh();
    }

    public void Suspend()
    {
        if (mode != PreviewOutputMode.Alternate || suspendLayout is not null)
            ClearRegion();
        _suspended = true;
        suspendLayout?.Invoke();
    }

    public void Resume()
    {
        resumeLayout?.Invoke();
        _suspended = false;
        _invalidated = true;
    }

    private void ClearRegion()
    {
        if (!_initialized) return;
        try
        {
            if (mode == PreviewOutputMode.Alternate)
            {
                var dimensions = size();
                var current = prepareLayout?.Invoke() ?? ScreenLayout.Create(dimensions.Width, dimensions.Height);
                for (var i = 0; i < current.ContentHeight; i++)
                    ScreenLayout.WriteRow(output, current.ContentTop + i, current.Columns, "");
                output.Write($"\u001b[{current.InputRow};{current.InputStartColumn}H");
            }
            else if (_uncertain)
            {
                output.Write("\u001b[2J\u001b[H");
            }
            else if (mode == PreviewOutputMode.Inline)
            {
                for (var i = 0; i < _regionHeight; i++)
                {
                    var up = _regionHeight - i;
                    output.Write($"\u001b[{up}A\r\u001b[2K\u001b[{up}B");
                }
                output.Write($"\u001b[{_regionHeight}A\r");
            }
            output.Flush();
        }
        finally
        {
            _initialized = false;
            _previous = [];
            _regionHeight = 0;
            _uncertain = false;
        }
    }

    public IReadOnlyList<string> Snapshot() => transcript.Snapshot();

    public void Finish(string? incompleteStatus)
    {
        EndSegment();
        ClearRegion();
        if (_hadText && incompleteStatus is not null && mode == PreviewOutputMode.Redirected)
        {
            diagnostics.WriteLine($"[preview {incompleteStatus}; not saved]");
            diagnostics.Flush();
        }
    }

    public void Reset()
    {
        transcript.Reset();
        _previous = [];
        _initialized = _suspended = _segmentVisible = _hadText = _invalidated = _uncertain = false;
        _regionHeight = 0;
        _lastSize = default;
    }
}
