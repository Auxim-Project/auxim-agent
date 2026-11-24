using System.Globalization;
using System.Text;

namespace Auxim.Cli.Interactive;

// Editor state only: presentation and terminal ownership stay in LineEditor.
internal sealed class InputDraft
{
    public StringBuilder Buffer { get; } = new();
    public int Cursor;
    public int FirstVisibleRow;
    public int RequestedRows = 1;
    public int HistoryIndex;
    public bool ContainsPaste;
    public InputSubmissionKind? SubmissionKind;
    public int? PreferredColumn;
    public bool ForwardAffinity;
    public int? SelectionAnchor;
    public bool HasSelection => SelectionAnchor is { } anchor && anchor != Cursor;
    public int SelectionStart => Math.Min(SelectionAnchor ?? Cursor, Cursor);
    public int SelectionEnd => Math.Max(SelectionAnchor ?? Cursor, Cursor);
    private Snapshot? _historyDraft;
    private string? _selectedHistoryEntry;
    private sealed record Snapshot(string Text, int Cursor, int FirstVisibleRow, bool ContainsPaste,
        int? PreferredColumn, bool ForwardAffinity, int? SelectionAnchor, InputSubmissionKind? SubmissionKind);

    public void SelectAll()
    {
        SelectionAnchor = 0; Cursor = Buffer.Length; ResetPreferredColumn();
    }

    public void ExtendSelection(bool extend)
    {
        if (extend) SelectionAnchor ??= Cursor;
        else SelectionAnchor = null;
    }

    public bool CollapseSelection(int direction)
    {
        if (!HasSelection) { SelectionAnchor = null; return false; }
        Cursor = direction < 0 ? SelectionStart : SelectionEnd;
        SelectionAnchor = null; ResetPreferredColumn();
        if (Buffer.Length == 0) { SubmissionKind = null; ContainsPaste = false; }
        return true;
    }

    public bool DeleteSelection()
    {
        if (!HasSelection) { SelectionAnchor = null; return false; }
        var start = SelectionStart;
        Buffer.Remove(start, SelectionEnd - start); Cursor = start;
        SelectionAnchor = null; ResetPreferredColumn();
        if (Buffer.Length == 0) { SubmissionKind = null; ContainsPaste = false; }
        return true;
    }

    public void InsertText(string text)
    {
        var kind = SubmissionKind;
        var pasted = ContainsPaste;
        DeleteSelection(); ResetPreferredColumn();
        SubmissionKind = kind; ContainsPaste = pasted;
        Buffer.Insert(Cursor, text); Cursor += text.Length;
    }

    public void ResetPreferredColumn() { PreferredColumn = null; ForwardAffinity = false; }

    public bool MoveVertical(InputProjection projection, int delta)
    {
        PreferredColumn ??= projection.CursorColumn;
        var target = projection.VerticalTarget(delta, PreferredColumn.Value);
        if (target is null) return false;
        Cursor = target.Index;
        ForwardAffinity = target.ForwardAffinity;
        return true;
    }

    public void InsertPaste(string text)
    {
        ResetPreferredColumn();
        NormalizeCursor();
        InsertText(text);
        NormalizeCursor();
        ContainsPaste = true;
        SubmissionKind = InputSubmissionKind.AgentMessage;
    }

    public InputDraft Copy()
    {
        var copy = new InputDraft();
        copy.Restore(this);
        return copy;
    }

    public void Restore(InputDraft saved)
    {
        Buffer.Clear(); Buffer.Append(saved.Buffer);
        Cursor = saved.Cursor; FirstVisibleRow = saved.FirstVisibleRow;
        RequestedRows = saved.RequestedRows; HistoryIndex = saved.HistoryIndex;
        ContainsPaste = saved.ContainsPaste;
        SubmissionKind = saved.SubmissionKind;
        PreferredColumn = saved.PreferredColumn; ForwardAffinity = saved.ForwardAffinity;
        SelectionAnchor = saved.SelectionAnchor;
        _historyDraft = saved._historyDraft; _selectedHistoryEntry = saved._selectedHistoryEntry;
    }

    public void Reset(int historyCount)
    {
        Buffer.Clear(); Cursor = FirstVisibleRow = 0;
        RequestedRows = 1; HistoryIndex = historyCount; _historyDraft = null; _selectedHistoryEntry = null;
        ContainsPaste = false; SubmissionKind = null;
        SelectionAnchor = null;
        ResetPreferredColumn();
    }

    public bool Previous(IReadOnlyList<string> history)
    {
        if (history.Count == 0 || HistoryIndex <= 0) return false;
        if (HistoryIndex == history.Count) _historyDraft = new(Buffer.ToString(), Cursor, FirstVisibleRow, ContainsPaste, PreferredColumn, ForwardAffinity, SelectionAnchor, SubmissionKind);
        _selectedHistoryEntry = history[--HistoryIndex];
        Set(_selectedHistoryEntry);
        return true;
    }

    public bool Next(IReadOnlyList<string> history)
    {
        if (HistoryIndex >= history.Count) return false;
        if (++HistoryIndex == history.Count && _historyDraft is { } saved)
        {
            Set(saved.Text); Cursor = saved.Cursor; FirstVisibleRow = saved.FirstVisibleRow;
            ContainsPaste = saved.ContainsPaste;
            SubmissionKind = saved.SubmissionKind;
            PreferredColumn = saved.PreferredColumn; ForwardAffinity = saved.ForwardAffinity;
            SelectionAnchor = saved.SelectionAnchor;
            _historyDraft = null; _selectedHistoryEntry = null;
        }
        else
        {
            _selectedHistoryEntry = HistoryIndex == history.Count ? null : history[HistoryIndex];
            Set(_selectedHistoryEntry ?? "");
        }
        return true;
    }

    public void ResumeHistory(IReadOnlyList<string> history)
    {
        // Rebase after explicit commands append to (or trim) input history.
        // Rebase navigation without changing the current draft or saved snapshot.
        if (_historyDraft is null) { HistoryIndex = history.Count; return; }
        for (var i = history.Count - 1; i >= 0; i--)
            if (history[i] == _selectedHistoryEntry) { HistoryIndex = i; return; }
        HistoryIndex = 0;
    }

    private void Set(string text)
    {
        Buffer.Clear(); Buffer.Append(text); Cursor = text.Length; FirstVisibleRow = 0;
        ContainsPaste = false; SubmissionKind = null;
        SelectionAnchor = null;
        ResetPreferredColumn();
    }

    public void NormalizeCursor()
    {
        Cursor = Math.Clamp(Cursor, 0, Buffer.Length);
        foreach (var boundary in StringInfo.ParseCombiningCharacters(Buffer.ToString()))
            if (boundary >= Cursor) { Cursor = boundary; return; }
        Cursor = Buffer.Length;
    }
}
