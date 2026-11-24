namespace Auxim.Cli.Interactive;

internal sealed partial class InteractiveView
{
    private readonly HistorySelection _historySelection = new();
    private HistoryDocument? _historyDocument;
    private IReadOnlyList<HistoryRow> _historyRows = [];
    private IReadOnlyList<HistoryLine> _liveSavedLines = [];
    private IReadOnlyList<HistoryLine>? _completedPreviewLines;
    private Task<ClipboardResult>? _copy;
    private string? _copyStatus;

    public bool CanInteractWithHistory => UsesFixedLayout && CanSelectTranscript && !_suspended && !HasCommandResult;
    public string? CurrentHistoryHint => CanInteractWithHistory ? HistorySelectionHint(_notice) : null;
    public bool DraggingHistory => CanInteractWithHistory && !HistoryTextSelection && _historySelection.IsDragging;
    private bool CanCopyHistory => CanInteractWithHistory && !HistoryTextSelection
        && (!_running || !_cancelling) && _historySelection.HasRange;

    private string HistorySelectionHint(string fallback) => !CanInteractWithHistory || HistoryTextSelection ? fallback
        : _copyStatus is { } status ? CanCopyHistory ? "Copy · " + status : status
        : CanCopyHistory ? _running ? "Copy · selected · Ctrl+C · Paused · Esc clear" : "Copy · selected · Ctrl+C · Esc clear" : fallback;

    internal void SyncHistoryCopyControl() => TerminalInput.SetHistoryCopyControl(CanCopyHistory);

    private void ResetHistorySelection(string? status = null)
    {
        _historySelection.Clear();
        _copyStatus = status;
        _copy = null;
        SyncHistoryCopyControl();
    }

    public bool HandleHistoryInput(TerminalInputEvent input)
    {
        if (input.IsCtrlC || input.IsSuperC)
        {
            if (CanCopyHistory && (input.IsCtrlC || input.IsSuperC && OperatingSystem.IsMacOS()))
                CopyHistory();
            else if (input.IsCtrlC) InteractiveInterrupt.HandleSignal();
            return true;
        }
        if (!CanInteractWithHistory || HistoryTextSelection) return false;
        if (input.Kind == TerminalInputEventKind.EscapeBoundary
            || input.Kind == TerminalInputEventKind.Key && input.Key.Key == ConsoleKey.Escape)
        {
            if (!_historySelection.HasRange && !_historySelection.IsDragging) return false;
            ResetHistorySelection();
            HistorySelectionChanged();
            return true;
        }
        if (input.Pointer is not { } pointer || input.Kind != TerminalInputEventKind.Pointer
            || _layout is not { } layout || pointer.Modifiers != 0) return false;

        var inside = pointer.Y >= layout.ContentTop && pointer.Y < layout.ContentTop + layout.ContentHeight
            && pointer.X >= 1 && pointer.X <= layout.ContentWidth;
        if (pointer is { Button: 0, Phase: PointerPhase.Press } && CanCopyHistory && layout.HintHeight > 0
            && pointer.Y == layout.HintRow)
        {
            var x = layout.ComposerInnerLeft + (layout.ComposerWidth >= 12 ? 2 : 0);
            if (pointer.X >= x && pointer.X < x + 4)
            {
                _historySelection.StopDrag();
                CopyHistory();
                return true;
            }
        }
        if (pointer.Button != 0)
        {
            if (_historySelection.IsDragging) { _historySelection.StopDrag(); ApplyMousePolicy(); }
            return false;
        }
        if (pointer.Phase == PointerPhase.Press)
        {
            if (!inside)
            {
                if (_historySelection.IsDragging) { _historySelection.StopDrag(); ApplyMousePolicy(); }
                return false;
            }
            _copyStatus = null;
            var hit = HistoryHit(pointer, layout);
            _historySelection.Press(pointer, hit, Environment.TickCount64);
            if (hit is not null) _followLive = false;
        }
        else
        {
            if (!_historySelection.IsDragging) return inside;
            _historySelection.Move(pointer, HistoryHit(pointer, layout, extend: true));
        }
        HistorySelectionChanged();
        return true;
    }

    private TranscriptViewport HistoryViewport => _running && _liveTranscript is { } live ? live.Viewport : _transcript;
    private HistoryRow HistoryRowAt(int row) => _running && _liveTranscript is { } live ? live.RowAt(row) : _historyRows[row];

    private (HistoryPosition Start, HistoryPosition End)? HistoryHit(TerminalPointer pointer, ScreenLayout layout,
        bool extend = false)
    {
        var viewport = HistoryViewport;
        var count = Math.Min(layout.ContentHeight, viewport.TotalLines - viewport.Offset);
        if (count <= 0 || !_running && _historyRows.Count != viewport.TotalLines) return null;
        var row = Math.Clamp(pointer.Y - layout.ContentTop, 0, count - 1);
        var column = pointer.X - 1;
        if (HistoryRowAt(viewport.Offset + row).Hit(column) is { } hit) return hit;
        if (!extend) return null;
        // An endpoint over a separator or label snaps to nearby transcript
        // text, without adding the label or its display padding to the range.
        for (var distance = 1; distance < count; distance++)
        {
            if (row - distance >= 0 && HistoryRowAt(viewport.Offset + row - distance).Hit(int.MaxValue) is { } before)
                return before;
            if (row + distance < count && HistoryRowAt(viewport.Offset + row + distance).Hit(0) is { } after)
                return after;
        }
        return null;
    }

    private void ExtendHistoryDrag(ScreenLayout layout)
    {
        if (_historySelection.DragPointer is { } pointer)
            _historySelection.Move(pointer with { Phase = PointerPhase.Drag }, HistoryHit(pointer, layout, extend: true));
    }

    public void TickHistorySelection()
    {
        if (!CanInteractWithHistory || HistoryTextSelection || _layout is not { } layout) return;
        if (TickHistorySelection(layout)) HistorySelectionChanged();
    }

    private bool TickHistorySelection(ScreenLayout layout)
    {
        var changed = false;
        if (_copy is { IsCompleted: true } pending)
        {
            _copy = null;
            var result = pending.IsCompletedSuccessfully ? pending.Result : new(false, "Clipboard unavailable.");
            if (pending.IsFaulted) _ = pending.Exception;
            if (result.TerminalSequence is { } sequence && CanCopyHistory) Console.Out.Write(sequence);
            _copyStatus = result.Status;
            changed = true;
        }
        var delta = _historySelection.ScrollDirection(layout, Environment.TickCount64);
        if (delta != 0 && HistoryViewport.ScrollClamped(delta, layout.ContentHeight))
        {
            ExtendHistoryDrag(layout);
            _liveHistoryRedraw = true;
            changed = true;
        }
        if (changed) SyncHistoryCopyControl();
        return changed;
    }

    private void CopyHistory()
    {
        if (_copy is not null || !CanCopyHistory) return;
        string text;
        if (_running && _liveTranscript is { } live) text = _historySelection.Copy(live.LineAt);
        else if (_historyDocument is { } document) text = _historySelection.Copy(index => document.Lines[index]);
        else return;
        if (text.Length == 0) return;
        _copyStatus = "Copying…";
        _copy = _clipboard.CopyAsync(text);
        HistorySelectionChanged();
    }

    private void HistorySelectionChanged()
    {
        SyncHistoryCopyControl();
        ApplyMousePolicy();
        if (_running || _layout is not { } layout) return;
        Console.Out.Write("\u001b7");
        DrawFixedContent();
        ConsoleTheme.WriteComposerHint(Console.Out, layout, HistorySelectionHint(_notice));
        Console.Out.Write("\u001b8");
        Console.Out.Flush();
    }

    private IReadOnlyList<string> PaintHistoryRows(TranscriptViewport viewport, ScreenLayout layout, bool live)
    {
        var visible = Math.Min(layout.ContentHeight, viewport.TotalLines - viewport.Offset);
        var rows = new List<string>(visible);
        for (var i = 0; i < visible; i++)
        {
            var row = HistoryRowAt(viewport.Offset + i);
            string display;
            if (live && row.Display == "Assistant  streaming") display = Ansi.Muted("Assistant · streaming, not saved");
            else if (_historySelection.HasRange && !HistoryTextSelection)
            {
                var (start, end) = _historySelection.Range;
                display = row.Highlight(start, end);
            }
            else display = row.Display;
            rows.Add(live ? ConsoleTheme.StreamingRow(display) : display);
        }
        return rows;
    }

    private IReadOnlyList<string> LayoutFixedTranscript(int width)
    {
        if (_historyDocument is null)
        {
            _historyRows = [];
            return LayoutTranscript(_transcriptSource, width);
        }
        _historyRows = _historyDocument.Lines.SelectMany((line, index) => line.Wrap(index, width)).ToArray();
        return _historyRows.Select(row => row.Display).ToArray();
    }
}
