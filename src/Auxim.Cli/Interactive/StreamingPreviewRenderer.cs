using System.Text;

namespace Auxim.Cli.Interactive;

internal enum PreviewOutputMode { Workspace, Inline, Redirected }

// Paints only visible physical rows. The view owns the transcript; this class
// retains just the last screen frame and an optional bounded inline region.
internal sealed class StreamingPreviewRenderer(
    TextWriter output,
    TextWriter diagnostics,
    PreviewOutputMode mode,
    Func<(int Width, int Height)> size,
    StreamingTranscript transcript,
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
        if (mode == PreviewOutputMode.Workspace)
        {
            refreshWorkspace!();
            return;
        }

        var dimensions = size();
        if (!transcript.HasContent) return;
        if (dimensions.Width < 2 || dimensions.Height < 3)
        {
            _uncertain = _initialized;
            ClearRegion();
            _lastSize = dimensions;
            return;
        }
        var geometryChanged = dimensions != _lastSize;
        if (!transcript.IsDirty && !geometryChanged && !_invalidated) return;
        var columns = ConsoleTheme.ReadableWidth(dimensions.Width - 1); // never trigger terminal auto-wrap
        var capacity = Math.Min(6, dimensions.Height - 2);
        transcript.Prepare(columns, capacity);
        var rows = transcript.Viewport.GetVisibleLines(capacity).ToArray();
        var frame = new StringBuilder();

        if (!_initialized || geometryChanged || _invalidated)
        {
            // Resize invalidates coordinates in native scrollback.
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

        if (frame.Length > 0)
        {
            _initialized = _uncertain = true;
            output.Write(frame.ToString());
            output.Flush();
            _uncertain = false;
        }
        _previous = Enumerable.Range(0, _regionHeight).Select(i => i < rows.Length ? rows[i] : "").ToArray();
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
        ClearRegion();
        _suspended = true;
    }

    public void Resume()
    {
        _suspended = false;
        _invalidated = true;
    }

    private void ClearRegion()
    {
        if (mode != PreviewOutputMode.Inline || !_initialized) return;
        try
        {
            if (_uncertain)
            {
                output.Write("\u001b[2J\u001b[H");
            }
            else
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
