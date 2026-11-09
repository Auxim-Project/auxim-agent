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
    private readonly List<string> _rows = [];
    private readonly StringBuilder _tail = new();
    private IReadOnlyList<string> _history = [];
    private IReadOnlyList<string> _tailRows = [];
    private bool _started;
    private bool _segment;
    private bool _previousCr;
    private int _columns;
    private int _laidOutLines;

    public bool IsDirty { get; private set; }
    public bool HasContent { get; private set; }
    public int Count => _rows.Count + _tailRows.Count;
    public string this[int index] => index < _rows.Count ? _rows[index] : _tailRows[index - _rows.Count];
    public TranscriptViewport Viewport => viewport;

    private void Start()
    {
        if (_started) return;
        _history = history().Select(TerminalTextLayout.Plain).ToArray();
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
        HasContent = IsDirty = true;
    }

    public void Prepare(int columns, int pageHeight)
    {
        if (!HasContent) return;
        if (_columns != columns)
        {
            _columns = columns;
            _rows.Clear();
            _laidOutLines = 0;
            viewport.SetTemporaryHistory(_history.SelectMany(line => ConsoleTheme.ConversationRow(line, columns, false)).ToArray());
        }

        while (_laidOutLines < _logical.Count)
        {
            _rows.AddRange(ConsoleTheme.ConversationRow(_logical[_laidOutLines++], columns, false));
        }

        _tailRows = _segment ? ConsoleTheme.ConversationRow("  " + _tail, columns, false) : [];
        viewport.UpdateTemporary(this, pageHeight);
        IsDirty = false;
    }

    public IReadOnlyList<string> Snapshot() => _logical
        .Concat(_segment ? new[] { "  " + _tail } : Array.Empty<string>())
        .Select(line => line == TerminalTextLayout.Plain(ConsoleTheme.MessageLabel("assistant", "streaming"))
            ? "Assistant  incomplete preview" : line).ToArray();

    public void Reset()
    {
        viewport.EndTemporary();
        _logical.Clear(); _rows.Clear(); _tail.Clear();
        _history = []; _tailRows = [];
        _started = _segment = _previousCr = HasContent = IsDirty = false;
        _columns = _laidOutLines = 0;
    }

    public IEnumerator<string> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
