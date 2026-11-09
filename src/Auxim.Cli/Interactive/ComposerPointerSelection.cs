namespace Auxim.Cli.Interactive;

// A single editor-owned gesture; no timer/reader thread or terminal I/O.
internal sealed class ComposerPointerSelection
{
    internal const int IdleTimeoutMilliseconds = 2000;
    private ScreenLayout _frame;
    private long _lastEvent;
    public bool IsDragging { get; private set; }
    public void Cancel() => IsDragging = false;

    public void Expire(ScreenLayout? frame, long now)
    {
        if (IsDragging && (frame != _frame || now - _lastEvent >= IdleTimeoutMilliseconds)) Cancel();
    }

    public bool Handle(TerminalPointer pointer, InputDraft draft, ScreenLayout frame, long now)
    {
        Expire(frame, now);
        if (pointer.Button != 0) { Cancel(); return false; }
        var inside = frame.InputColumns > 0 && pointer.Y >= frame.InputRow && pointer.Y < frame.InputRow + frame.InputRows
            && pointer.X >= frame.ComposerInnerLeft && pointer.X < frame.ComposerInnerLeft + frame.InputColumns;
        if (pointer.Phase == PointerPhase.Press)
        {
            Cancel();
            if (!inside) return false;
        }
        else if (!IsDragging) return false;

        var projection = TerminalTextLayout.LayoutInput(draft.Buffer.ToString(), draft.Cursor, frame.InputColumns,
            ConsoleTheme.ComposerPrompt(frame.ComposerWidth), draft.ForwardAffinity);
        var first = draft.FirstVisibleRow;
        // Out-of-region drags scroll by one physical row per received event.
        // A release only commits the last/clamped position, never starts scrolling.
        if (pointer.Phase == PointerPhase.Drag)
        {
            if (pointer.Y < frame.InputRow) first--;
            else if (pointer.Y >= frame.InputRow + frame.InputRows) first++;
        }
        first = Math.Clamp(first, 0, Math.Max(0, projection.Rows.Count - frame.InputRows));
        var row = Math.Clamp(first + Math.Clamp(pointer.Y - frame.InputRow, 0, frame.InputRows - 1), 0, projection.Rows.Count - 1);
        var column = (int)Math.Clamp((long)pointer.X - frame.ComposerInnerLeft, 0, Math.Max(0, frame.InputColumns - 1));
        var target = projection.HitTest(row, column);
        if (target is null) { Cancel(); return false; }
        if (pointer.Phase == PointerPhase.Press)
        {
            draft.SelectionAnchor = pointer.Modifiers.HasFlag(ConsoleModifiers.Shift) ? draft.SelectionAnchor ?? draft.Cursor : target.Index;
            _frame = frame;
            IsDragging = true;
        }
        draft.ResetPreferredColumn();
        draft.Cursor = target.Index;
        draft.ForwardAffinity = target.ForwardAffinity;
        draft.FirstVisibleRow = first;
        _lastEvent = now;
        if (pointer.Phase == PointerPhase.Release) Cancel();
        return true;
    }
}
