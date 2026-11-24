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
    private int _lastCursorLine;
    private readonly Func<int, ScreenLayout?>? _inputLayout;
    private ScreenLayout? _lastLayout;
    private readonly InputDraft _draft = new();
    private IReadOnlyList<string>? _suggestions;
    private int _selectedSuggestion;
    private int _completionEnd;
    private readonly List<(int From, int To, int Index)> _suggestionHits = [];
    private readonly Func<TerminalPointer, string?>? _quickAction;
    private readonly Func<bool>? _expandResult;
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
    private readonly InteractiveView? _historyView;

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
        Func<int, InputSubmissionKind?>? historyKind = null,
        Func<TerminalPointer, string?>? quickAction = null,
        Func<bool>? expandResult = null,
        InteractiveView? historyView = null)
    {
        _history = history;
        _quickAction = quickAction;
        _expandResult = expandResult;
        _historyView = historyView;
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
        _lastRenderWidth = TerminalTextLayout.Measure(visiblePrompt);
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
                if (inputPolicy != TerminalInputPolicy.ConfigurationPrompt) HandleIdleInterrupt(prompt, visiblePrompt);
                cancellationToken.ThrowIfCancellationRequested();
                if (inputPolicy != TerminalInputPolicy.Feedback && inputPolicy != TerminalInputPolicy.ConfigurationPrompt)
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
                        if (_suggestions is null) _historyView?.TickHistorySelection();
                        Thread.Sleep(25);
                    }
                }
                var historyInput = _acceptPaste && inputPolicy is null && _historyView?.CanInteractWithHistory == true;
                var input = TerminalInput.Read(inputPolicy ?? (historyInput ? TerminalInputPolicy.HistoryEditor : TerminalInputPolicy.LineEditor), cancellationToken);
                var completionOwnsInput = _suggestions is not null && (input.Key.Key == ConsoleKey.Escape
                    || input.Pointer is { } pointerOverHint && pointerOverHint.Y == _lastLayout?.HintRow);
                if (historyInput && !completionOwnsInput && _historyView!.HandleHistoryInput(input))
                {
                    _pointerSelection.Cancel();
                    SynchronizeMouse();
                    if (_suggestions is not null) Render(prompt, visiblePrompt, buffer, cursor);
                    continue;
                }
                if (input.Kind == TerminalInputEventKind.Pointer)
                {
                    if (_acceptPaste && input.Pointer is { Button: 0, Phase: PointerPhase.Press, Modifiers: 0 } click)
                    {
                        if (_suggestions is not null && _lastLayout is { } menuLayout && click.Y == menuLayout.HintRow
                            && _inputLayout?.Invoke(_draft.RequestedRows) == menuLayout)
                        {
                            foreach (var hit in _suggestionHits)
                            {
                                if (click.X < hit.From || click.X >= hit.To) continue;
                                _selectedSuggestion = hit.Index;
                                AcceptSuggestion(buffer, ref cursor);
                                Render(prompt, visiblePrompt, buffer, cursor);
                                break;
                            }
                            continue;
                        }
                        if (buffer.Length == 0 && _quickAction?.Invoke(click) is { } action)
                        {
                            _draft.InsertText(action);
                            Render(prompt, visiblePrompt, buffer, cursor);
                            continue;
                        }
                    }
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
                if (_acceptPaste && key is { Key: ConsoleKey.F3, Modifiers: 0 } && _expandResult?.Invoke() == true)
                {
                    Render(prompt, visiblePrompt, buffer, cursor);
                    continue;
                }
                if (_suggestions is { } options && key.Modifiers == 0)
                {
                    if (key.Key is ConsoleKey.UpArrow or ConsoleKey.LeftArrow or ConsoleKey.DownArrow or ConsoleKey.RightArrow)
                    {
                        var delta = key.Key is ConsoleKey.UpArrow or ConsoleKey.LeftArrow ? -1 : 1;
                        _selectedSuggestion = (_selectedSuggestion + delta + options.Count) % options.Count;
                        ShowSuggestions(prompt, visiblePrompt, buffer, cursor);
                        continue;
                    }
                    if (key.Key is ConsoleKey.Tab or ConsoleKey.Enter)
                    {
                        AcceptSuggestion(buffer, ref cursor);
                        Render(prompt, visiblePrompt, buffer, cursor);
                        continue;
                    }
                }
                if (_acceptPaste && key.Key == ConsoleKey.Escape && _draft.HasSelection)
                {
                    _draft.SelectionAnchor = null;
                    Render(prompt, visiblePrompt, buffer, cursor);
                    continue;
                }
                if (key.Key is ConsoleKey.Backspace or ConsoleKey.Delete or ConsoleKey.LeftArrow
                    or ConsoleKey.RightArrow or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.Tab
                    || !char.IsControl(key.KeyChar)) _draft.ResetPreferredColumn();
                if (_acceptPaste && (key.Key == ConsoleKey.F2 && key.Modifiers == 0
                    || key.Key == ConsoleKey.R && key.Modifiers == ConsoleModifiers.Control)
                    && _toggleHistoryTextSelection?.Invoke() == true)
                {
                    _suggestions = null;
                    _clearSuggestions = false;
                    SynchronizeMouse();
                    continue;
                }
                var hadSuggestions = _suggestions is not null;
                if (hadSuggestions)
                {
                    _suggestions = null;
                    _clearSuggestions = true;
                    Render(prompt, visiblePrompt, buffer, cursor);
                }
                switch (key.Key)
                {
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
                        if (buffer.Length == 0 || inputPolicy == TerminalInputPolicy.ConfigurationPrompt)
                        {
                            EndInput();
                            return null;
                        }

                        break;
                    case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                        if (inputPolicy == TerminalInputPolicy.ConfigurationPrompt) throw new ConfigurationPromptCancelledException();
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
        var mode = _lastLayout is null ? TerminalMouseMode.Off
            : _historyTextSelection?.Invoke() == true ? TerminalMouseMode.NativeSelection
            : _pointerSelection.IsDragging || _historyView?.DraggingHistory == true ? TerminalMouseMode.Drag : TerminalMouseMode.Normal;
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
        if (!_acceptPaste || _draft.HasSelection || _draft.ContainsPaste
            || _draft.SubmissionKind == InputSubmissionKind.AgentMessage) return;
        var text = buffer.ToString();
        if (!text.StartsWith('/')) return;
        var matches = _completionProvider(text[..cursor]);
        if (matches.Count == 0) return;

        _completionEnd = cursor;
        while (_completionEnd < text.Length && !char.IsWhiteSpace(text[_completionEnd])) _completionEnd++;
        _selectedSuggestion = 0;
        _suggestions = matches;
        if (matches.Count == 1)
        {
            AcceptSuggestion(buffer, ref cursor);
            Render(prompt, visiblePrompt, buffer, cursor);
        }
        else ShowSuggestions(prompt, visiblePrompt, buffer, cursor);
    }

    private void AcceptSuggestion(StringBuilder buffer, ref int cursor)
    {
        ReplaceCompletion(buffer, ref cursor, _completionEnd, _suggestions![_selectedSuggestion]);
        _suggestions = null;
        _clearSuggestions = true;
    }

    private void ShowSuggestions(string prompt, string visiblePrompt, StringBuilder buffer, int cursor)
    {
        if (_lastLayout is not null)
        {
            Render(prompt, visiblePrompt, buffer, cursor);
            return;
        }

        // An inline terminal has no fixed hint row. Leave the active item
        // visible in scrollback and redraw the command below it.
        Console.WriteLine();
        Console.WriteLine($"Completion {_selectedSuggestion + 1}/{_suggestions!.Count}: {_suggestions[_selectedSuggestion]} · arrows / Tab / Esc");
        ResetRenderState();
        Render(prompt, visiblePrompt, buffer, cursor);
    }

    private string SuggestionHint(ScreenLayout layout)
    {
        _suggestionHits.Clear();
        var indent = layout.ComposerWidth >= 12 ? 2 : 0;
        var available = Math.Max(0, layout.InputColumns - indent);
        var output = new StringBuilder();
        for (var i = _selectedSuggestion; i < _suggestions!.Count; i++)
        {
            var item = i == _selectedSuggestion ? $"[{_suggestions[i]}]" : _suggestions[i];
            if (i == _selectedSuggestion) item = ConsoleTheme.Summarize(item, available);
            var width = TerminalTextLayout.Measure(item);
            if (width == 0 || output.Length + width > available) break;
            var start = layout.ComposerInnerLeft + indent + output.Length;
            _suggestionHits.Add((start, start + width, i));
            output.Append(item);
            if (output.Length + 2 <= available) output.Append("  ");
        }
        return output.ToString();
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
                ConsoleTheme.WriteComposerHint(Console.Out, layout, SuggestionHint(layout));
            else if (_clearSuggestions && layout.HintHeight > 0)
                ConsoleTheme.WriteComposerHint(Console.Out, layout, _historyView?.CurrentHistoryHint
                    ?? ConsoleTheme.ComposerHint(layout, _canGoBack?.Invoke() == true));
            _clearSuggestions = false;
            projection.Write(Console.Out, layout, _draft.FirstVisibleRow, _acceptPaste && text.Length == 0,
                _draft.SelectionStart, _draft.SelectionEnd);
            return;
        }
        var terminalWidth = TerminalWidth();
        _lastTerminalWidth = terminalWidth;
        _lastTerminalHeight = TerminalHeight();
        TrackInputWidth(terminalWidth);
        RenderInline(text, cursor, visiblePrompt, terminalWidth, prompt);
    }

    private void RenderInline(string text, int cursor, string prompt, int width, string styledPrompt)
    {
        var projection = TerminalTextLayout.LayoutInput(text, cursor, width, prompt, _draft.ForwardAffinity);
        if (!Ansi.ControlSequencesEnabled)
        {
            var row = projection.Rows[projection.CursorRow].Text;
            Console.Write('\r'); Console.Write(row);
            Console.Write(new string(' ', Math.Max(0, _lastRenderWidth - TerminalTextLayout.Measure(row))));
            Console.Write('\r');
            Console.Write(TerminalTextLayout.TakeCells(row, projection.CursorColumn));
            _lastRenderWidth = TerminalTextLayout.Measure(row);
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
    }

    private static int TerminalHeight()
    {
        try { return Console.WindowHeight > 0 ? Console.WindowHeight : 24; }
        catch (IOException) { return 24; }
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

}
