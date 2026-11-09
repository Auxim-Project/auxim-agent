using System.Globalization;
using System.Text;

namespace Auxim.Cli.Interactive;

internal sealed class LineEditor
{
    private readonly IReadOnlyList<string> _history;
    private readonly Func<string, IReadOnlyList<string>> _completionProvider;
    private readonly Action<int, Action>? _scrollHandler;
    private readonly Action<int, Action>? _wheelHandler;
    private int _lastRenderWidth;
    private int _lastRenderLines = 1;
    private int _lastCursorLine;
    private readonly Func<int, ScreenLayout?>? _inputLayout;
    private ScreenLayout? _lastLayout;
    private readonly InputDraft _draft = new();
    private string? _suggestions;
    private bool _clearSuggestions;
    private Func<bool>? _canGoBack;
    private readonly Func<bool>? _isNewChat;
    private InputDraft? _mainDraft;
    private InputDraft? _newChatDraft;
    private bool _editingNewChat;
    private static bool _pasteFallbackShown;
    private readonly Func<int, InputSubmissionKind?>? _historyKind;
    public InputSubmission? Submission { get; private set; }
    public bool SubmittedPaste { get; private set; }
    private bool _acceptPaste;
    private readonly Func<bool>? _isReadingView;
    private int? _projectionColumns;
    private int _lastTerminalWidth;
    private int _lastTerminalHeight;
    private readonly ComposerPointerSelection _pointerSelection = new();
    private readonly Func<bool>? _canPointComposer;
    private readonly Func<bool>? _historyTextSelection;
    private readonly Func<bool>? _toggleHistoryTextSelection;
    private long _dragRevision;

    public LineEditor(
        IReadOnlyList<string> history,
        Func<string, IReadOnlyList<string>> completionProvider,
        Action<int, Action>? scrollHandler = null,
        Func<int, ScreenLayout?>? inputLayout = null,
        Func<bool>? isNewChat = null,
        Func<bool>? isReadingView = null,
        Action<int, Action>? wheelHandler = null,
        Func<bool>? canPointComposer = null,
        Func<bool>? historyTextSelection = null,
        Func<bool>? toggleHistoryTextSelection = null,
        Func<int, InputSubmissionKind?>? historyKind = null)
    {
        _history = history;
        _historyKind = historyKind;
        _completionProvider = completionProvider;
        _scrollHandler = scrollHandler;
        _wheelHandler = wheelHandler;
        _canPointComposer = canPointComposer;
        _historyTextSelection = historyTextSelection;
        _toggleHistoryTextSelection = toggleHistoryTextSelection;
        _inputLayout = inputLayout;
        _isNewChat = isNewChat;
        _isReadingView = isReadingView;
    }

    public string? ReadLine(
        string prompt,
        string visiblePrompt,
        Func<bool>? canNavigateBack = null,
        bool scrollNavigation = false,
        Action? navigateBack = null,
        TerminalInputPolicy? inputPolicy = null,
        CancellationToken cancellationToken = default,
        bool acceptPaste = false)
    {
        SubmittedPaste = false;
        Submission = null;
        _acceptPaste = acceptPaste;
        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            var line = Console.ReadLine();
            Submission = line is null ? null : InputSubmission.Fresh(line);
            return line;
        }

        using var pasteInput = acceptPaste ? TerminalInput.BeginPasteInput() : null;
        if (acceptPaste && pasteInput is null && !_pasteFallbackShown)
        {
            _pasteFallbackShown = true;
            Console.Error.WriteLine("Bracketed paste unavailable; unmarked paste cannot be distinguished from typing. Use /paste for multiline input.");
        }

        _canGoBack = canNavigateBack;
        LoadDraft(_isNewChat?.Invoke() == true);
        _lastLayout = _inputLayout?.Invoke(_draft.RequestedRows);
        if (_lastLayout is null) Console.Write(prompt);
        _lastRenderWidth = DisplayWidth(visiblePrompt);
        _lastRenderLines = 1;
        _lastCursorLine = 0;
        var buffer = _draft.Buffer;
        ref var cursor = ref _draft.Cursor;
        _suggestions = null;
        if (_lastLayout is not null) Render(prompt, visiblePrompt, buffer, cursor);
        ref var historyIndex = ref _draft.HistoryIndex;
        _pointerSelection.Cancel();
        try
        {
            while (true)
            {
                SynchronizeMouse();
                HandleIdleInterrupt(prompt, visiblePrompt);
                cancellationToken.ThrowIfCancellationRequested();
                if (inputPolicy != TerminalInputPolicy.Feedback)
                {
                    while (!TerminalInput.KeyAvailable)
                    {
                        SynchronizeMouse();
                        HandleIdleInterrupt(prompt, visiblePrompt);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_inputLayout is not null && _lastLayout is not null)
                        {
                            var layout = _inputLayout(_draft.RequestedRows);
                            if (layout != _lastLayout) Render(prompt, visiblePrompt, buffer, cursor);
                        }
                        else if (_acceptPaste && (TerminalWidth() != _lastTerminalWidth || TerminalHeight() != _lastTerminalHeight))
                            Render(prompt, visiblePrompt, buffer, cursor);
                        Thread.Sleep(25);
                    }
                }
                var input = TerminalInput.Read(inputPolicy ?? TerminalInputPolicy.LineEditor, cancellationToken);
                if (input.Kind == TerminalInputEventKind.Pointer)
                {
                    if (_acceptPaste && _canPointComposer?.Invoke() == true && _historyTextSelection?.Invoke() != true
                        && _lastLayout is { } pointerLayout && input.Pointer is { } pointer
                        && _pointerSelection.Handle(pointer, _draft, pointerLayout, Environment.TickCount64))
                    {
                        _suggestions = null; _clearSuggestions = true;
                        _dragRevision = TerminalMouse.Revision;
                        SynchronizeMouse();
                        Render(prompt, visiblePrompt, buffer, cursor);
                    }
                    else SynchronizeMouse();
                    continue;
                }
                _pointerSelection.Cancel();
                SynchronizeMouse();
                if (input.Kind == TerminalInputEventKind.EndOfInput) return null;
                if (input.Kind == TerminalInputEventKind.MouseWheel)
                {
                    if (scrollNavigation && _historyTextSelection?.Invoke() != true)
                        ScrollTranscript(input.ScrollDelta, prompt, visiblePrompt, buffer, cursor, wheel: true);
                    continue;
                }
                if (input.Kind == TerminalInputEventKind.Paste && acceptPaste)
                {
                    if (input.Text is not null)
                    {
                        _draft.InsertPaste(input.Text);
                        _suggestions = null; _clearSuggestions = true;
                        Render(prompt, visiblePrompt, buffer, cursor);
                    }
                    else if (_lastLayout is { } pasteLayout)
                        ConsoleTheme.WriteComposerHint(Console.Out, pasteLayout, "Paste discarded: too large, invalid UTF-8 or interrupted.");
                    else Console.Error.WriteLine("Paste discarded: too large, invalid UTF-8 or interrupted.");
                    continue;
                }
                if (acceptPaste && ComposerInputActions.Decide(input) == ComposerInputAction.InsertNewline)
                {
                    _suggestions = null; _clearSuggestions = true;
                    _draft.SubmissionKind = InputSubmissionKind.AgentMessage;
                    _draft.InsertText("\n");
                    Render(prompt, visiblePrompt, buffer, cursor);
                    continue;
                }
                if (input.Kind != TerminalInputEventKind.Key)
                {
                    if (inputPolicy == TerminalInputPolicy.Feedback) return null;
                    continue;
                }

                var key = input.Key;
                if (_acceptPaste && key.Key == ConsoleKey.Escape && _draft.HasSelection)
                {
                    _draft.SelectionAnchor = null;
                    Render(prompt, visiblePrompt, buffer, cursor);
                    continue;
                }
                if (key.Key is ConsoleKey.Backspace or ConsoleKey.Delete or ConsoleKey.LeftArrow
                    or ConsoleKey.RightArrow or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.Tab
                    || !char.IsControl(key.KeyChar)) _draft.ResetPreferredColumn();
                var hadSuggestions = _suggestions is not null;
                if (hadSuggestions)
                {
                    _suggestions = null;
                    _clearSuggestions = true;
                    Render(prompt, visiblePrompt, buffer, cursor);
                }
                switch (key.Key)
                {
                    case ConsoleKey.R when _acceptPaste && key.Modifiers == ConsoleModifiers.Control:
                        if (_toggleHistoryTextSelection?.Invoke() == true)
                        {
                            SynchronizeMouse();
                            _clearSuggestions = false;
                            Render(prompt, visiblePrompt, buffer, cursor);
                        }
                        break;
                    case ConsoleKey.A when _acceptPaste && key.Modifiers == ConsoleModifiers.Control:
                        _draft.SelectAll();
                        Render(prompt, visiblePrompt, buffer, cursor);
                        break;
                    case ConsoleKey.Escape:
                        if (ShouldNavigateBack(hadSuggestions, canNavigateBack?.Invoke() == true))
                        {
                            var wasNewChat = _editingNewChat;
                            if (wasNewChat) _newChatDraft = _draft.Copy();
                            else _mainDraft = _draft.Copy();
                            navigateBack?.Invoke();
                            TerminalInput.DiscardQueuedNavigation();
                            if (wasNewChat != (_isNewChat?.Invoke() == true)) LoadDraft(_isNewChat?.Invoke() == true);
                            ResetRenderState();
                            Render(prompt, visiblePrompt, buffer, cursor);
                        }
                        break;
                    case ConsoleKey.Enter:
                        SubmittedPaste = _draft.ContainsPaste || _draft.SubmissionKind == InputSubmissionKind.AgentMessage;
                        Submission = _draft.SubmissionKind == InputSubmissionKind.AgentMessage || SubmittedPaste
                            ? new(buffer.ToString(), InputSubmissionKind.AgentMessage)
                            : InputSubmission.Fresh(buffer.ToString());
                        if (_editingNewChat) _newChatDraft = null;
                        else _mainDraft = null;
                        EndInput();
                        return buffer.ToString();
                    case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                        if (buffer.Length == 0)
                        {
                            EndInput();
                            return null;
                        }

                        break;
                    case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                        if (!acceptPaste) { EndInput(); return null; }
                        ClearInterruptedDraft(prompt, visiblePrompt);
                        break;
                    case ConsoleKey.Backspace:
                        if (_draft.DeleteSelection()) { Render(prompt, visiblePrompt, buffer, cursor); break; }
                        if (cursor > 0)
                        {
                            var previous = PreviousTextElementIndex(buffer.ToString(), cursor);
                            buffer.Remove(previous, cursor - previous);
                            cursor = previous;
                            if (buffer.Length == 0) { _draft.SubmissionKind = null; _draft.ContainsPaste = false; }
                            Render(prompt, visiblePrompt, buffer, cursor);
                        }

                        break;
                    case ConsoleKey.Delete:
                        if (_draft.DeleteSelection()) { Render(prompt, visiblePrompt, buffer, cursor); break; }
                        if (cursor < buffer.Length)
                        {
                            var text = buffer.ToString();
                            var next = NextTextElementIndex(text, cursor);
                            buffer.Remove(cursor, next - cursor);
                            if (buffer.Length == 0) { _draft.SubmissionKind = null; _draft.ContainsPaste = false; }
                            Render(prompt, visiblePrompt, buffer, cursor);
                        }

                        break;
                    case ConsoleKey.LeftArrow:
                        if (_acceptPaste && key.Modifiers.HasFlag(ConsoleModifiers.Shift)) _draft.ExtendSelection(true);
                        else if (_draft.CollapseSelection(-1)) { Render(prompt, visiblePrompt, buffer, cursor); break; }
                        if (cursor > 0)
                        {
                            cursor = PreviousTextElementIndex(buffer.ToString(), cursor);
                            Render(prompt, visiblePrompt, buffer, cursor);
                        }

                        break;
                    case ConsoleKey.RightArrow:
                        if (_acceptPaste && key.Modifiers.HasFlag(ConsoleModifiers.Shift)) _draft.ExtendSelection(true);
                        else if (_draft.CollapseSelection(1)) { Render(prompt, visiblePrompt, buffer, cursor); break; }
                        if (cursor < buffer.Length)
                        {
                            cursor = NextTextElementIndex(buffer.ToString(), cursor);
                            Render(prompt, visiblePrompt, buffer, cursor);
                        }

                        break;
                    case ConsoleKey.Home:
                        _draft.ExtendSelection(false);
                        cursor = 0;
                        Render(prompt, visiblePrompt, buffer, cursor);
                        break;
                    case ConsoleKey.End:
                        _draft.ExtendSelection(false);
                        cursor = buffer.Length;
                        Render(prompt, visiblePrompt, buffer, cursor);
                        break;
                    case ConsoleKey.PageUp:
                        ScrollTranscript(-10, prompt, visiblePrompt, buffer, cursor);
                        break;
                    case ConsoleKey.PageDown:
                        ScrollTranscript(10, prompt, visiblePrompt, buffer, cursor);
                        break;
                    case ConsoleKey.P when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    case ConsoleKey.UpArrow when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                        MoveHistoryPrevious(prompt, visiblePrompt, buffer, ref cursor, ref historyIndex);
                        break;
                    case ConsoleKey.N when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    case ConsoleKey.DownArrow when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                        MoveHistoryNext(prompt, visiblePrompt, buffer, ref cursor, ref historyIndex);
                        break;
                    case ConsoleKey.UpArrow:
                        if (scrollNavigation && _isReadingView?.Invoke() == true && !key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                        {
                            ScrollTranscript(-1, prompt, visiblePrompt, buffer, cursor);
                            break;
                        }

                        if (_acceptPaste) MoveVertical(-1, prompt, visiblePrompt, buffer, cursor, key.Modifiers.HasFlag(ConsoleModifiers.Shift));
                        else MoveHistoryPrevious(prompt, visiblePrompt, buffer, ref cursor, ref historyIndex);
                        break;
                    case ConsoleKey.DownArrow:
                        if (scrollNavigation && _isReadingView?.Invoke() == true && !key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                        {
                            ScrollTranscript(1, prompt, visiblePrompt, buffer, cursor);
                            break;
                        }

                        if (_acceptPaste) MoveVertical(1, prompt, visiblePrompt, buffer, cursor, key.Modifiers.HasFlag(ConsoleModifiers.Shift));
                        else MoveHistoryNext(prompt, visiblePrompt, buffer, ref cursor, ref historyIndex);
                        break;
                    case ConsoleKey.Tab:
                        Complete(prompt, visiblePrompt, buffer, ref cursor);
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            _draft.InsertText(key.KeyChar.ToString());
                            if (_lastLayout is not null) _draft.NormalizeCursor();
                            Render(prompt, visiblePrompt, buffer, cursor);
                        }

                        break;
                }
            }
        }
        finally
        {
            _pointerSelection.Cancel();
            if (_canPointComposer is not null) TerminalMouse.DisableAlternateScroll();
        }
    }

    private void HandleIdleInterrupt(string prompt, string visiblePrompt)
    {
        if (InteractiveInterrupt.Current?.TakeIdleInterrupt() != true) return;
        if (_acceptPaste) ClearInterruptedDraft(prompt, visiblePrompt);
    }

    private void ClearInterruptedDraft(string prompt, string visiblePrompt)
    {
        _pointerSelection.Cancel();
        _suggestions = null; _clearSuggestions = true;
        if (_draft.HasSelection) _draft.SelectionAnchor = null;
        else _draft.Reset(_history.Count);
        SynchronizeMouse();
        Render(prompt, visiblePrompt, _draft.Buffer, _draft.Cursor);
    }

    private void SynchronizeMouse()
    {
        if (_canPointComposer is null) return;
        _pointerSelection.Expire(_lastLayout, Environment.TickCount64);
        if (_pointerSelection.IsDragging && _dragRevision != TerminalMouse.Revision) _pointerSelection.Cancel();
        if (_canPointComposer.Invoke() != true || _historyTextSelection?.Invoke() == true) _pointerSelection.Cancel();
        var mode = _lastLayout is null || _historyTextSelection?.Invoke() == true ? TerminalMouseMode.Off
            : _pointerSelection.IsDragging ? TerminalMouseMode.Drag : TerminalMouseMode.Normal;
        TerminalMouse.SetMode(mode);
        if (_pointerSelection.IsDragging) _dragRevision = TerminalMouse.Revision;
    }

    private void TrackInputWidth(int columns)
    {
        if (_projectionColumns is { } old && old != columns) _draft.ResetPreferredColumn();
        _projectionColumns = columns;
    }

    private void MoveVertical(int delta, string prompt, string visiblePrompt, StringBuilder buffer, int cursor, bool extend)
    {
        var anchor = _draft.SelectionAnchor;
        _draft.ExtendSelection(extend);
        var columns = _lastLayout?.InputColumns ?? TerminalWidth();
        TrackInputWidth(columns);
        var projection = TerminalTextLayout.LayoutInput(buffer.ToString(), cursor, columns,
            _lastLayout is { } layout ? ConsoleTheme.ComposerPrompt(layout.ComposerWidth) : visiblePrompt, _draft.ForwardAffinity);
        if (_draft.MoveVertical(projection, delta) || anchor != _draft.SelectionAnchor)
            Render(prompt, visiblePrompt, buffer, _draft.Cursor);
    }

    private void MoveHistoryPrevious(
        string prompt,
        string visiblePrompt,
        StringBuilder buffer,
        ref int cursor,
        ref int historyIndex)
    {
        if (_lastLayout is not null || _acceptPaste)
        {
            if (_draft.Previous(_history))
            {
                if (_draft.HistoryIndex < _history.Count && _historyKind is not null)
                {
                    _draft.SubmissionKind = _historyKind(_draft.HistoryIndex);
                    _draft.ContainsPaste = _draft.SubmissionKind == InputSubmissionKind.AgentMessage;
                }
                Render(prompt, visiblePrompt, buffer, cursor);
            }
            return;
        }
        if (_history.Count == 0 || historyIndex <= 0)
        {
            return;
        }

        historyIndex--;
        ReplaceBuffer(buffer, _history[historyIndex], ref cursor);
        Render(prompt, visiblePrompt, buffer, cursor);
    }

    private void MoveHistoryNext(
        string prompt,
        string visiblePrompt,
        StringBuilder buffer,
        ref int cursor,
        ref int historyIndex)
    {
        if (_lastLayout is not null || _acceptPaste)
        {
            if (_draft.Next(_history))
            {
                if (_draft.HistoryIndex < _history.Count && _historyKind is not null)
                {
                    _draft.SubmissionKind = _historyKind(_draft.HistoryIndex);
                    _draft.ContainsPaste = _draft.SubmissionKind == InputSubmissionKind.AgentMessage;
                }
                Render(prompt, visiblePrompt, buffer, cursor);
            }
            return;
        }
        if (historyIndex >= _history.Count)
        {
            return;
        }

        historyIndex++;
        ReplaceBuffer(buffer, historyIndex == _history.Count ? "" : _history[historyIndex], ref cursor);
        Render(prompt, visiblePrompt, buffer, cursor);
    }

    private void ScrollTranscript(
        int delta,
        string prompt,
        string visiblePrompt,
        StringBuilder buffer,
        int cursor, bool wheel = false)
    {
        (wheel ? _wheelHandler ?? _scrollHandler : _scrollHandler)?.Invoke(delta, () =>
        {
            ResetRenderState();
            Render(prompt, visiblePrompt, buffer, cursor);
        });
    }

    private void Complete(string prompt, string visiblePrompt, StringBuilder buffer, ref int cursor)
    {
        if (_draft.HasSelection) return;
        _draft.SelectionAnchor = null;
        var input = buffer.ToString();
        var prefixLength = _lastLayout is null ? input.Length : CompletionPrefixLength(input);
        if (_lastLayout is not null && (prefixLength == 0 || cursor > prefixLength)) return;
        var matches = _completionProvider(_lastLayout is null ? input : input[..cursor]);
        if (matches.Count == 0)
        {
            return;
        }

        if (matches.Count == 1)
        {
            if (_lastLayout is null)
                ReplaceBuffer(buffer, matches[0] + (input.Contains(' ') ? "" : " "), ref cursor);
            else
                ReplaceCompletion(buffer, ref cursor, prefixLength, matches[0]);
            Render(prompt, visiblePrompt, buffer, cursor);
            return;
        }

        if (_lastLayout is not null)
        {
            _suggestions = string.Join("  ", matches);
            Render(prompt, visiblePrompt, buffer, cursor);
            return;
        }
        Console.WriteLine();
        Console.WriteLine(string.Join("  ", matches.Select(Ansi.Accent)));
        // Cursor is now 2 lines below the input — update tracking so the
        // next Render moves back up past suggestions + old input line.
        _lastCursorLine += 2;
        _lastRenderLines += 2;
        // Don't call Render — suggestions remain visible until next keystroke.
    }

    private static int CompletionPrefixLength(string text)
    {
        if (!text.StartsWith('/')) return 0;
        var index = 0;
        while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
        return index;
    }

    internal static void ReplaceCompletion(StringBuilder buffer, ref int cursor, int prefixLength, string match)
    {
        var suffix = prefixLength < buffer.Length;
        buffer.Remove(0, prefixLength).Insert(0, match + (suffix ? "" : " "));
        cursor = match.Length + (suffix ? 0 : 1);
    }

    private static void ReplaceBuffer(StringBuilder buffer, string value, ref int cursor)
    {
        buffer.Clear();
        buffer.Append(value);
        cursor = buffer.Length;
    }

    private void LoadDraft(bool newChat)
    {
        _editingNewChat = newChat;
        var saved = _isNewChat is null ? null : newChat ? _newChatDraft : _mainDraft;
        if (saved is null) _draft.Reset(_history.Count);
        else { _draft.Restore(saved); _draft.ResumeHistory(_history); }
    }

    internal static bool ShouldNavigateBack(bool hasSuggestions, bool canGoBack) =>
        !hasSuggestions && canGoBack;

    private void Render(string prompt, string visiblePrompt, StringBuilder buffer, int cursor)
    {
        var text = buffer.ToString();
        var fixedLayout = _inputLayout?.Invoke(_draft.RequestedRows);
        _lastLayout = fixedLayout;
        if (fixedLayout is { } layout)
        {
            TrackInputWidth(layout.InputColumns);
            var projection = TerminalTextLayout.LayoutInput(text, cursor, layout.InputColumns, ConsoleTheme.ComposerPrompt(layout.ComposerWidth), _draft.ForwardAffinity);
            _draft.RequestedRows = projection.Rows.Count;
            var adjusted = _inputLayout!(_draft.RequestedRows)!.Value;
            if (adjusted.InputColumns != layout.InputColumns)
            {
                TrackInputWidth(adjusted.InputColumns);
                projection = TerminalTextLayout.LayoutInput(text, cursor, adjusted.InputColumns, ConsoleTheme.ComposerPrompt(adjusted.ComposerWidth), _draft.ForwardAffinity);
                _draft.RequestedRows = projection.Rows.Count;
                adjusted = _inputLayout(_draft.RequestedRows)!.Value;
            }
            layout = adjusted;
            _lastLayout = layout;
            _draft.FirstVisibleRow = projection.FollowCursor(_draft.FirstVisibleRow, layout.InputRows);
            if (_suggestions is not null && layout.HintHeight > 0)
                ConsoleTheme.WriteComposerHint(Console.Out, layout, _suggestions);
            else if (_clearSuggestions && layout.HintHeight > 0)
                ConsoleTheme.WriteComposerHint(Console.Out, layout, ConsoleTheme.ComposerHint(layout, _canGoBack?.Invoke() == true));
            _clearSuggestions = false;
            projection.Write(Console.Out, layout, _draft.FirstVisibleRow, text.Length == 0,
                _draft.SelectionStart, _draft.SelectionEnd);
            return;
        }
        var terminalWidth = TerminalWidth();
        _lastTerminalWidth = terminalWidth;
        _lastTerminalHeight = TerminalHeight();
        TrackInputWidth(terminalWidth);
        if (_acceptPaste || _draft.ContainsPaste || text.Any(char.IsControl))
        {
            RenderInlinePaste(text, cursor, visiblePrompt, terminalWidth, prompt);
            return;
        }
        var fullWidth = DisplayWidth(visiblePrompt) + DisplayWidth(text);

        if (Ansi.ControlSequencesEnabled)
        {
            var lines = WrapInput(prompt, visiblePrompt, text, terminalWidth);
            var cursorPosition = CursorPosition(visiblePrompt, text[..cursor], terminalWidth);

            // Move cursor to start of previous render area
            // _lastCursorLine is the 0-based line within the previous block where the cursor sits
            if (_lastCursorLine > 0)
            {
                Console.Write($"\u001b[{_lastCursorLine}A");
            }

            Console.Write("\r");
            // Clear from cursor to end of screen — clears all wrapped lines
            Console.Write("\u001b[0J");

            WriteWrappedLines(lines);

            var linesFromEnd = lines.Count - 1 - cursorPosition.Line;
            if (linesFromEnd > 0)
            {
                Console.Write($"\u001b[{linesFromEnd}A");
            }

            Console.Write("\r");
            if (cursorPosition.Column > 0)
            {
                Console.Write($"\u001b[{cursorPosition.Column}C");
            }

            _lastRenderWidth = fullWidth;
            _lastRenderLines = lines.Count;
            _lastCursorLine = cursorPosition.Line;
            return;
        }

        // Non-ANSI fallback — best-effort single-line redraw
        Console.Write('\r');
        Console.Write(prompt);
        Console.Write(text);
        if (_lastRenderWidth > fullWidth)
        {
            Console.Write(new string(' ', _lastRenderWidth - fullWidth));
        }

        Console.Write('\r');
        Console.Write(prompt);
        if (cursor > 0)
        {
            Console.Write(text[..cursor]);
        }

        _lastRenderWidth = fullWidth;
    }

    private void RenderInlinePaste(string text, int cursor, string prompt, int width, string styledPrompt)
    {
        var projection = TerminalTextLayout.LayoutInput(text, cursor, width, prompt, _draft.ForwardAffinity);
        if (!Ansi.ControlSequencesEnabled)
        {
            var row = projection.Rows[projection.CursorRow].Text;
            Console.Write('\r'); Console.Write(row);
            Console.Write(new string(' ', Math.Max(0, _lastRenderWidth - ConsoleTheme.CellWidth(row))));
            Console.Write('\r');
            var beforeCursor = new StringBuilder();
            var cells = 0;
            var elements = StringInfo.GetTextElementEnumerator(row);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var next = cells + TerminalTextLayout.CellWidth(element);
                if (next > projection.CursorColumn) break;
                beforeCursor.Append(element); cells = next;
            }
            Console.Write(beforeCursor.ToString());
            _lastRenderWidth = ConsoleTheme.CellWidth(row);
            return;
        }
        if (_lastCursorLine > 0) Console.Write($"\u001b[{_lastCursorLine}A");
        Console.Write("\r\u001b[0J");
        var height = Math.Max(1, TerminalHeight() - 2);
        _draft.FirstVisibleRow = projection.FollowCursor(_draft.FirstVisibleRow, height);
        var visible = Enumerable.Range(_draft.FirstVisibleRow, Math.Min(height, projection.Rows.Count - _draft.FirstVisibleRow))
            .Select(row => projection.SelectedRow(row, _draft.SelectionStart, _draft.SelectionEnd, controls: true)).ToArray();
        if (_draft.FirstVisibleRow == 0 && visible[0].StartsWith(prompt, StringComparison.Ordinal))
            visible[0] = styledPrompt + visible[0][prompt.Length..];
        WriteWrappedLines(visible);
        var cursorRow = projection.CursorRow - _draft.FirstVisibleRow;
        var up = visible.Length - 1 - cursorRow;
        if (up > 0) Console.Write($"\u001b[{up}A");
        Console.Write('\r');
        if (projection.CursorColumn > 0) Console.Write($"\u001b[{projection.CursorColumn}C");
        _lastCursorLine = cursorRow;
        _lastRenderLines = visible.Length;
    }

    private static int TerminalHeight()
    {
        try { return Console.WindowHeight > 0 ? Console.WindowHeight : 24; }
        catch (IOException) { return 24; }
    }

    private static IReadOnlyList<string> WrapInput(
        string prompt,
        string visiblePrompt,
        string text,
        int terminalWidth)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0;
        AppendPrompt(lines, current, ref currentWidth, prompt, visiblePrompt, terminalWidth);
        AppendText(lines, current, ref currentWidth, text, terminalWidth);
        lines.Add(current.ToString());
        return lines;
    }

    private static void AppendPrompt(
        List<string> lines,
        StringBuilder current,
        ref int currentWidth,
        string prompt,
        string visiblePrompt,
        int terminalWidth)
    {
        var promptWidth = DisplayWidth(visiblePrompt);
        if (currentWidth > 0 && currentWidth + promptWidth > terminalWidth)
        {
            lines.Add(current.ToString());
            current.Clear();
            currentWidth = 0;
        }

        current.Append(prompt);
        currentWidth += promptWidth;
        if (currentWidth >= terminalWidth)
        {
            lines.Add(current.ToString());
            current.Clear();
            currentWidth = 0;
        }
    }

    private static void AppendText(
        List<string> lines,
        StringBuilder current,
        ref int currentWidth,
        string text,
        int terminalWidth)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var runeText = rune.ToString();
            var width = RuneWidth(rune);
            if (currentWidth > 0 && currentWidth + width > terminalWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
                currentWidth = 0;
            }

            current.Append(runeText);
            currentWidth += width;
            if (currentWidth >= terminalWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
                currentWidth = 0;
            }
        }
    }

    private static (int Line, int Column) CursorPosition(string visiblePrompt, string textBeforeCursor, int terminalWidth)
    {
        var line = 0;
        var column = 0;
        foreach (var rune in (visiblePrompt + textBeforeCursor).EnumerateRunes())
        {
            var width = RuneWidth(rune);
            if (column > 0 && column + width > terminalWidth)
            {
                line++;
                column = 0;
            }

            column += width;
            if (column >= terminalWidth)
            {
                line++;
                column = 0;
            }
        }

        return (line, column);
    }

    private static void WriteWrappedLines(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (index > 0)
            {
                Console.WriteLine();
            }

            Console.Write(lines[index]);
        }
    }

    private void EndInput()
    {
        if (_lastLayout is null) Console.WriteLine();
    }

    private void ResetRenderState()
    {
        _lastRenderWidth = 0;
        _lastRenderLines = 1;
        _lastCursorLine = 0;
    }

    internal static int TerminalWidth()
    {
        try
        {
            return Math.Max(Console.WindowWidth, 1);
        }
        catch (IOException)
        {
            return 80;
        }
    }

    internal static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += RuneWidth(rune);
        }

        return width;
    }

    private static int PreviousTextElementIndex(string text, int cursor)
    {
        if (cursor <= 0)
        {
            return 0;
        }

        var indexes = StringInfo.ParseCombiningCharacters(text);
        var previous = 0;
        foreach (var index in indexes)
        {
            if (index >= cursor)
            {
                break;
            }

            previous = index;
        }

        return previous;
    }

    private static int NextTextElementIndex(string text, int cursor)
    {
        if (cursor >= text.Length)
        {
            return text.Length;
        }

        var indexes = StringInfo.ParseCombiningCharacters(text);
        foreach (var index in indexes)
        {
            if (index > cursor)
            {
                return index;
            }
        }

        return text.Length;
    }

    private static int RuneWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format
            or UnicodeCategory.Control)
        {
            return 0;
        }

        return IsWide(rune.Value) ? 2 : 1;
    }

    private static bool IsWide(int value)
    {
        return value is >= 0x1100 and <= 0x115f
            or >= 0x231a and <= 0x231b
            or >= 0x2329 and <= 0x232a
            or >= 0x23e9 and <= 0x23ec
            or >= 0x23f0 and <= 0x23f0
            or >= 0x23f3 and <= 0x23f3
            or >= 0x25fd and <= 0x25fe
            or >= 0x2614 and <= 0x2615
            or >= 0x2648 and <= 0x2653
            or >= 0x267f and <= 0x267f
            or >= 0x2693 and <= 0x2693
            or >= 0x26a1 and <= 0x26a1
            or >= 0x26aa and <= 0x26ab
            or >= 0x26bd and <= 0x26be
            or >= 0x26c4 and <= 0x26c5
            or >= 0x26ce and <= 0x26ce
            or >= 0x26d4 and <= 0x26d4
            or >= 0x26ea and <= 0x26ea
            or >= 0x26f2 and <= 0x26f3
            or >= 0x26f5 and <= 0x26f5
            or >= 0x26fa and <= 0x26fa
            or >= 0x26fd and <= 0x26fd
            or >= 0x2705 and <= 0x2705
            or >= 0x270a and <= 0x270b
            or >= 0x2728 and <= 0x2728
            or >= 0x274c and <= 0x274c
            or >= 0x274e and <= 0x274e
            or >= 0x2753 and <= 0x2755
            or >= 0x2757 and <= 0x2757
            or >= 0x2795 and <= 0x2797
            or >= 0x27b0 and <= 0x27b0
            or >= 0x27bf and <= 0x27bf
            or >= 0x2b1b and <= 0x2b1c
            or >= 0x2b50 and <= 0x2b50
            or >= 0x2b55 and <= 0x2b55
            or >= 0x2e80 and <= 0xa4cf
            or >= 0xac00 and <= 0xd7a3
            or >= 0xf900 and <= 0xfaff
            or >= 0xfe10 and <= 0xfe19
            or >= 0xfe30 and <= 0xfe6f
            or >= 0xff00 and <= 0xff60
            or >= 0xffe0 and <= 0xffe6
            or >= 0x1f300 and <= 0x1f64f
            or >= 0x1f900 and <= 0x1f9ff
            or >= 0x20000 and <= 0x3fffd;
    }
}
