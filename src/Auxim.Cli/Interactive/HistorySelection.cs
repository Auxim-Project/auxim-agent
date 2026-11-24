namespace Auxim.Cli.Interactive;

internal readonly record struct HistoryPosition(int Line, int Index) : IComparable<HistoryPosition>
{
    public int CompareTo(HistoryPosition other) => Line == other.Line
        ? Index.CompareTo(other.Index) : Line.CompareTo(other.Line);
}

internal sealed record HistoryLine(string Display, int Prefix = 0, bool Decoration = false, int DisplayIndent = 0)
{
    public string? Text => Decoration ? null : TerminalTextLayout.Plain(Display)[Prefix..];

    public IReadOnlyList<HistoryRow> Wrap(int line, int width, bool styled = true)
    {
        var indent = width > 2 ? DisplayIndent : 0;
        var spans = new List<List<HistoryCell>>();
        var rows = TerminalTextLayout.WrapTranscript(Display, width - indent, (row, column, cells, from, to) =>
        {
            while (spans.Count <= row) spans.Add([]);
            if (!Decoration && from >= Prefix)
                spans[row].Add(new(column + indent, cells, from - Prefix, to - Prefix));
        });
        return rows.Select((row, index) =>
        {
            var text = new string(' ', indent) + row;
            return new HistoryRow(line, styled ? Ansi.Text(text) : TerminalTextLayout.Plain(text),
                index < spans.Count ? spans[index] : []);
        }).ToArray();
    }
}

internal readonly record struct HistoryCell(int Column, int Width, int Start, int End);

internal sealed record HistoryRow(int Line, string Display, IReadOnlyList<HistoryCell> Cells)
{
    public (HistoryPosition Start, HistoryPosition End)? Hit(int column)
    {
        if (Cells.Count == 0) return null;
        foreach (var cell in Cells)
        {
            if (column < cell.Column) return (new(Line, cell.Start), new(Line, cell.Start));
            if (column < cell.Column + cell.Width) return (new(Line, cell.Start), new(Line, cell.End));
        }
        var end = new HistoryPosition(Line, Cells[^1].End);
        return (end, end);
    }

    public string Highlight(HistoryPosition start, HistoryPosition end)
    {
        if (Line < start.Line || Line > end.Line) return Display;
        var from = Line == start.Line ? start.Index : 0;
        var to = Line == end.Line ? end.Index : int.MaxValue;
        var selected = Cells.Where(cell => cell.End > from && cell.Start < to).ToArray();
        return selected.Length == 0 ? Display
            : TerminalTextLayout.HighlightCells(Display, selected[0].Column, selected[^1].Column + selected[^1].Width);
    }
}

internal sealed class HistorySelection
{
    private HistoryPosition? _anchor;
    private HistoryPosition? _end;
    private HistoryPosition _pressStart;
    private HistoryPosition _pressEnd;
    private TerminalPointer? _drag;
    private long _nextScroll;
    public bool IsDragging => _drag is not null;
    public bool HasRange => _anchor is { } anchor && _end is { } end && anchor != end;

    public (HistoryPosition Start, HistoryPosition End) Range =>
        _anchor!.Value.CompareTo(_end!.Value) <= 0 ? (_anchor.Value, _end.Value) : (_end.Value, _anchor.Value);

    public bool Clear()
    {
        var changed = HasRange;
        _anchor = _end = null;
        StopDrag();
        return changed;
    }

    public void StopDrag() => _drag = null;

    public void Press(TerminalPointer pointer, (HistoryPosition Start, HistoryPosition End)? hit, long now)
    {
        Clear();
        if (hit is null) return;
        (_pressStart, _pressEnd) = hit.Value;
        _anchor = _end = _pressStart;
        _drag = pointer;
        _nextScroll = now + 90;
    }

    public void Move(TerminalPointer pointer, (HistoryPosition Start, HistoryPosition End)? hit)
    {
        if (_drag is null) return;
        var wasMoved = _drag.Phase == PointerPhase.Drag || pointer.Phase == PointerPhase.Drag;
        _drag = pointer;
        if (wasMoved && hit is { } position)
        {
            var reverse = position.Start.CompareTo(_pressStart) < 0;
            _anchor = reverse ? _pressEnd : _pressStart;
            _end = reverse ? position.Start : position.End;
        }
        if (pointer.Phase == PointerPhase.Release) StopDrag();
    }

    public TerminalPointer? DragPointer => _drag;

    public int ScrollDirection(ScreenLayout layout, long now)
    {
        if (_drag is not { Phase: PointerPhase.Drag } pointer || layout.ContentHeight == 0 || now < _nextScroll) return 0;
        var delta = pointer.Y <= layout.ContentTop ? -1
            : pointer.Y >= layout.ContentTop + layout.ContentHeight - 1 ? 1 : 0;
        if (delta != 0) _nextScroll = now + 65;
        return delta;
    }

    public string Copy(Func<int, HistoryLine> lineAt)
    {
        if (!HasRange) return "";
        var (start, end) = Range;
        var lines = new List<string>();
        for (var line = start.Line; line <= end.Line; line++)
        {
            if (lineAt(line).Text is not { } text) continue;
            var from = line == start.Line ? start.Index : 0;
            var to = line == end.Line ? end.Index : text.Length;
            // Appending to a live line can join a combining mark or ZWJ to
            // the last grapheme. Keep the original selection endpoint.
            lines.Add(text[from..to]);
        }
        return string.Join('\n', lines);
    }

    public bool CanKeepPrefix(IReadOnlyList<HistoryLine> before, IReadOnlyList<HistoryLine> after)
    {
        if (!HasRange) return false;
        var end = Range.End;
        var start = Range.Start.Line;
        if (end.Line >= before.Count || end.Line >= after.Count) return false;
        for (var i = start; i <= end.Line; i++)
            if (before[i].Text != after[i].Text) return false;
        return true;
    }

    public static bool SameDocument(IReadOnlyList<HistoryLine>? before, IReadOnlyList<HistoryLine> after) =>
        before is not null && before.Count == after.Count
        && before.Zip(after).All(pair => pair.First.Text == pair.Second.Text);
}

internal sealed class HistoryDocument(IReadOnlyList<HistoryLine> lines) : IReadOnlyList<string>
{
    public IReadOnlyList<HistoryLine> Lines => lines;
    public int Count => lines.Count;
    public string this[int index] => new string(' ', lines[index].DisplayIndent) + lines[index].Display;
    public IEnumerator<string> GetEnumerator() => Enumerable.Range(0, Count).Select(index => this[index]).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
