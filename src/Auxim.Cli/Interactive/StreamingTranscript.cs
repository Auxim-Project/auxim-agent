using System.Collections;
using System.Text;

namespace Auxim.Cli.Interactive;

// One run's view-only content. Completed logical lines are laid out once per
// width; only the unfinished line is reflowed as text arrives.
internal sealed class StreamingTranscript(
    Func<IReadOnlyList<string>> history,
    string userInput,
    TranscriptViewport viewport) : IReadOnlyList<string>
{
    private readonly List<string> _logical = [];
    private readonly List<HistoryRow> _rows = [];
    private readonly StringBuilder _tail = new();
    private IReadOnlyList<HistoryLine> _history = [];
    private IReadOnlyList<HistoryRow> _historyRows = [];
    private IReadOnlyList<HistoryRow> _tailRows = [];
    private bool _started;
    private bool _segment;
    private int _segmentCount;
    private bool _hasTools;
    private bool _previousCr;
    private int _columns;
    private int _laidOutLines;
    private int _pageHeight;
    private bool _followBottom;

    public bool IsDirty { get; private set; }
    public bool HasContent { get; private set; }
    public int Count => _rows.Count + _tailRows.Count;
    public string this[int index] => (index < _rows.Count ? _rows[index] : _tailRows[index - _rows.Count]).Display;

    public HistoryRow RowAt(int index) => index < _historyRows.Count ? _historyRows[index]
        : index - _historyRows.Count < _rows.Count ? _rows[index - _historyRows.Count]
        : _tailRows[index - _historyRows.Count - _rows.Count];
    public HistoryLine LineAt(int index) => index < _history.Count ? _history[index]
        : index - _history.Count < _logical.Count ? DescribeLine(_logical[index - _history.Count]) : DescribeLine("  " + _tail);

    internal static HistoryLine DescribeLine(string line) => line.StartsWith("  ", StringComparison.Ordinal)
        ? new(line[2..], DisplayIndent: 2)
        : new(line, Decoration: line.Length != 0);
    public TranscriptViewport Viewport => viewport;

    private void Start()
    {
        if (_started) return;
        _logical.Add(TerminalTextLayout.Plain(ConsoleTheme.MessageLabel("user")));
        _logical.AddRange(TerminalTextLayout.Plain(userInput).ReplaceLineEndings("\n").Split('\n').Select(line => "  " + line));
        _logical.Add("");
        _started = true;
    }

    public void Append(string text)
    {
        if (text.Length == 0) return;
        Start();
        if (!_segment)
        {
            _logical.Add(TerminalTextLayout.Plain(ConsoleTheme.MessageLabel("assistant", "streaming")));
            _segment = true;
            _segmentCount++;
        }

        foreach (var c in text)
        {
            if (c == '\n' && _previousCr) { _previousCr = false; continue; }
            _previousCr = c == '\r';
            if (c is '\r' or '\n')
            {
                _logical.Add("  " + _tail);
                _tail.Clear();
            }
            else if (!char.IsControl(c) || c == '\t')
            {
                _tail.Append(c);
            }
        }

        HasContent = IsDirty = true;
    }

    public void EndSegment()
    {
        if (!_segment) return;
        _logical.Add("  " + _tail);
        _logical.Add("");
        _tail.Clear();
        _segment = false;
        _previousCr = false;
        IsDirty = true;
    }

    public void AddTool(string text)
    {
        Start();
        EndSegment();
        _logical.Add(TerminalTextLayout.Plain(text));
        _hasTools = true;
        HasContent = IsDirty = true;
    }

    public void Prepare(int columns, int pageHeight, bool followBottom = true)
    {
        Start();
        if (!IsDirty && _columns == columns && _pageHeight == pageHeight && _followBottom == followBottom) return;
        _pageHeight = pageHeight;
        _followBottom = followBottom;
        if (_columns != columns)
        {
            if (_columns == 0)
            {
                var saved = history();
                _history = saved is HistoryDocument document ? document.Lines
                    : saved.Select(line => new HistoryLine(TerminalTextLayout.Plain(line))).ToArray();
            }
            _columns = columns;
            _rows.Clear();
            _laidOutLines = 0;
            _historyRows = _history.SelectMany((line, index) => line.Wrap(index, columns, styled: false)).ToArray();
            viewport.SetTemporaryHistory(_historyRows.Select(row => row.Display).ToArray());
        }

        while (_laidOutLines < _logical.Count)
        {
            var index = _laidOutLines++;
            _rows.AddRange(DescribeLine(_logical[index]).Wrap(_history.Count + index, columns, styled: false));
        }

        _tailRows = _segment ? DescribeLine("  " + _tail).Wrap(_history.Count + _logical.Count, columns, styled: false) : [];
        viewport.UpdateTemporary(this, pageHeight, followBottom);
        IsDirty = false;
    }

    // Only a single tool-free response can have the same logical positions
    // in the preview and the saved conversation. The view compares both texts.
    public IReadOnlyList<HistoryLine>? ComparableSnapshot()
    {
        if (_segmentCount != 1 || _hasTools) return null;
        return [.. _history, .. _logical.Select(DescribeLine),
            .. _segment ? new[] { DescribeLine("  " + _tail), new HistoryLine("") } : []];
    }

    public IReadOnlyList<string> Snapshot() => _logical
        .Concat(_segment ? new[] { "  " + _tail } : Array.Empty<string>())
        .Select(line => line == TerminalTextLayout.Plain(ConsoleTheme.MessageLabel("assistant", "streaming"))
            ? "Assistant  incomplete preview" : line).ToArray();

    public void Reset()
    {
        viewport.EndTemporary();
        _logical.Clear(); _rows.Clear(); _tail.Clear();
        _history = []; _historyRows = []; _tailRows = [];
        _started = _segment = _previousCr = HasContent = IsDirty = false;
        _columns = _laidOutLines = _pageHeight = _segmentCount = 0;
        _hasTools = false;
    }

    public IEnumerator<string> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
