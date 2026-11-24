namespace Auxim.Cli.Interactive;

// Keeps viewport state independent of terminal output and runtime/session data.
internal sealed class TranscriptViewport
{
    public bool IsActive { get; private set; }
    public IReadOnlyList<string> Lines { get; private set; } = [];
    public int Offset { get; private set; }
    private IReadOnlyList<string> _temporary = [];
    private (IReadOnlyList<string> Lines, int Offset, bool Active)? _saved;
    public int TotalLines => Lines.Count + _temporary.Count;

    public void Open(IReadOnlyList<string> lines, bool startAtBottom, int pageHeight)
    {
        EndTemporary();
        Lines = lines;
        Offset = startAtBottom ? Math.Max(0, lines.Count - pageHeight) : 0;
        IsActive = true;
    }

    public void Scroll(int delta) => Offset += delta;

    public bool ScrollClamped(int delta, int pageHeight)
    {
        var next = (int)Math.Clamp((long)Offset + delta, 0, Math.Max(0, TotalLines - Math.Max(0, pageHeight)));
        if (next == Offset) return false;
        Offset = next;
        return true;
    }

    public void Reflow(IReadOnlyList<string> physicalRows, int pageHeight, bool followBottom)
    {
        Lines = physicalRows;
        Offset = followBottom ? Math.Max(0, TotalLines - pageHeight)
            : Math.Clamp(Offset, 0, Math.Max(0, TotalLines - pageHeight));
    }

    public IEnumerable<string> GetVisibleLines(int pageHeight)
    {
        Offset = Math.Clamp(Offset, 0, Math.Max(0, TotalLines - pageHeight));
        var end = Math.Min(TotalLines, (long)Offset + pageHeight);
        for (var i = Offset; i < end; i++)
            yield return i < Lines.Count ? Lines[i] : _temporary[i - Lines.Count];
    }

    public void SetTemporaryHistory(IReadOnlyList<string> physicalRows)
    {
        _saved ??= (Lines, Offset, IsActive);
        Lines = physicalRows;
        IsActive = true;
    }

    public void UpdateTemporary(IReadOnlyList<string> physicalRows, int pageHeight, bool followBottom = true)
    {
        _temporary = physicalRows;
        var bottom = Math.Max(0, TotalLines - pageHeight);
        Offset = followBottom ? bottom : Math.Clamp(Offset, 0, bottom);
    }

    public void EndTemporary()
    {
        _temporary = [];
        if (_saved is { } saved)
        {
            (Lines, Offset, IsActive) = saved;
            _saved = null;
        }
    }

    public void Reset()
    {
        EndTemporary();
        IsActive = false;
        Lines = [];
        Offset = 0;
    }
}
