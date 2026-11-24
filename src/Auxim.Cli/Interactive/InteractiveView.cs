using Auxim.Core.Runtime;
using System.Text.Json;

namespace Auxim.Cli.Interactive;

// Owns session presentation and view transitions; terminal/input lifecycles stay
// in InteractiveScreen and LineEditor, and command execution stays in the shell.
internal sealed partial class InteractiveView(IAuximRuntime runtime, DateTimeOffset startedAt, IClipboard? clipboard = null,
    TextWriter? output = null, Func<(int Width, int Height)>? size = null, bool? fixedLayout = null)
{
    private readonly IAuximRuntime _runtime = runtime;
    private TextWriter Output => output ?? Console.Out;
    private readonly IClipboard _clipboard = clipboard ?? new Clipboard();
    private readonly DateTimeOffset _startedAt = startedAt;
    private readonly TranscriptViewport _transcript = new();
    private IReadOnlyList<string> _transcriptSource = [];
    private int _transcriptWidth;
    private int _lastPageHeight;

    private ScreenLayout? _layout;
    private int _inputRows = 1;
    private bool _suspended;
    private bool _running;
    private volatile bool _cancelling;
    private bool _paintedCancelling;
    private StreamingTranscript? _liveTranscript;
    private bool _followLive;
    private bool _liveHistoryRedraw;
    private int _liveHistoryOffset;
    private InteractivePage? _paintedRunPage;
    private string? _paintedRunHint;
    private IReadOnlyList<string> _incompletePreview = [];
    public void MarkCancelling() => _cancelling = true;
    public void RetainIncompletePreview(IReadOnlyList<string> lines) => _incompletePreview = lines;
    private string _title = "New Chat";
    public InteractivePage Page { get; private set; } = InteractivePage.NewChat;
    public bool CanGoBack => HasCommandResult || Page is InteractivePage.History or InteractivePage.Resume or InteractivePage.SessionPreview or InteractivePage.NewChat;
    public bool HasCommandResult => _commandHeading is not null;
    private readonly TranscriptViewport _resultViewport = new();
    private string? _commandHeading;
    private IReadOnlyList<string> _commandLines = [];
    private int _resultWidth;
    private bool _resultExpanded;
    private bool _newSessionPending = true;
    private string? _pendingSessionTitle;
    private string? _mainSessionId;
    private IReadOnlyList<string> _mainResult = [];
    private MainRunActivity? _turnActivity;
    private IReadOnlyList<string> _turnContext = [];
    private DateTimeOffset? _runStarted;
    private string? _lastOutcome;
    private string? _latestReply;
    private IReadOnlyList<string> _paintedRows = [];
    private ScreenLayout? _paintedLayout;
    private IReadOnlyList<AuximRuntimeSessionSummary> _allResumeSessions = [];
    private IReadOnlyList<AuximRuntimeSessionSummary> _resumeSessions = [];
    private string _resumeQuery = "";
    private int _resumeTop;
    private int _resumeSelection;
    private int _resumeInlineOffset;
    private bool _historyTextSelection;
    private bool CanSelectTranscript => Page is InteractivePage.History or InteractivePage.SessionPreview;
    public bool HistoryTextSelection => CanSelectTranscript && !HasCommandResult && _historyTextSelection;

    private string SessionPreviewHint => HistoryTextSelection
        ? "←/Esc list · Enter switch · F2 browse · Drag + Copy"
        : "←/Esc list · Enter switch · ↑↓/PgUp/PgDn scroll · F2 native";
    public bool CanPointComposer => (Page is InteractivePage.Main or InteractivePage.NewChat or InteractivePage.History)
        && !HistoryTextSelection && !_running && !_suspended;

    private string IncompleteHistoryLabel => _lastOutcome?.StartsWith("Cancelled", StringComparison.Ordinal) == true
        ? "Cancelled output · not saved" : "Failed output · not saved";

    private string HistoryMouseHint => _incompletePreview.Count != 0
        ? $"{IncompleteHistoryLabel.Replace(" output", "")} · {(HistoryTextSelection ? "F2 browse" : "F2 select")} · Esc Main"
        : ConsoleTheme.ContentWidth() < 50
        ? HistoryTextSelection ? "Drag + Copy · F2 browse" : "Drag select · Esc Main"
        : HistoryTextSelection ? "Native selection · drag + Copy · F2 browse · Esc Main"
            : "Wheel browse · drag select · Esc Main";

    public void ApplyMousePolicy()
    {
        if (!UsesFixedLayout) return;
        // Active turns already own a cancellation input poll. Keep reporting
        // wheel events to that poll so the terminal cannot scroll the display.
        TerminalMouse.SetMode(_suspended ? TerminalMouseMode.Off
            : HistoryTextSelection ? TerminalMouseMode.NativeSelection
            : DraggingHistory ? TerminalMouseMode.Drag : TerminalMouseMode.Normal);
    }

    public bool ToggleHistoryTextSelection()
    {
        if (!CanSelectTranscript || HasCommandResult) return false;
        ResetHistorySelection();
        _historyTextSelection = !_historyTextSelection;
        _notice = Page == InteractivePage.SessionPreview ? SessionPreviewHint : HistoryMouseHint;
        TerminalInput.DiscardQueuedNavigation();
        ApplyMousePolicy();
        if (UsesFixedLayout && _layout is { } layout)
        {
            // Preserve the editor cursor. Do not repaint History or Composer
            // text on a mouse-mode switch.
            Output.Write("\u001b7");
            ConsoleTheme.WriteComposerHint(Output, layout, _notice);
            Output.Write("\u001b8");
            Output.Flush();
        }
        else ConsoleTheme.Hint(_notice);
        return true;
    }

    // New Chat is an intent: opening it never creates or overwrites a saved session.
    private AuximRuntimeSession NewChatSummary() =>
        new("", string.IsNullOrWhiteSpace(_pendingSessionTitle) ? "New conversation" : _pendingSessionTitle, default, default, false, []);
    private string _notice = "";
    public bool UsesFixedLayout => fixedLayout ?? InteractiveScreen.IsAlternateScreenActive;

    // Called only by the current foreground owner (editor or preview pump).
    public ScreenLayout? PrepareInputLayout(int requestedRows = 1)
    {
        if (!UsesFixedLayout || _suspended) return null;
        _inputRows = requestedRows;
        EnsureFixedLayout();
        return _layout;
    }

    private string RunningPage => Page == InteractivePage.History ? "History" : "Main";
    private string RunHint => Page != InteractivePage.History ? ConsoleTheme.RunningHint
        : HistoryTextSelection ? "Copy frozen page · F2 browse · Esc Main"
        : _followLive ? "Follow live · not saved · ↑ Pause · Esc Main"
        : "Paused · End Follow live · Esc Main";

    private void EnsureFixedLayout(bool force = false, ScreenLayout? target = null)
    {
        var size = TerminalSize();
        var next = target ?? ScreenLayout.Create(size.Width, size.Height, dashboard: !_running && !HasCommandResult && Page == InteractivePage.NewChat, inputRows: _running ? 1 : _inputRows);
        if (!force && _layout == next) return;
        var previous = _layout;
        var heightOnly = !force && previous is { } old && old.Width == next.Width && old.Height == next.Height
            && old.IsDashboard == next.IsDashboard;
        _layout = next;
        if (previous is { } geometry && (geometry.Width != next.Width || geometry.Height != next.Height)
            && _historySelection.IsDragging)
        {
            _historySelection.StopDrag();
            ApplyMousePolicy();
        }
        _paintedLayout = null;
        _paintedRunHint = null;
        if (heightOnly && previous is { } prior)
        {
            var first = next.IsDashboard ? Math.Min(prior.WelcomeRow, next.WelcomeRow) : Math.Min(prior.ComposerTop, next.ComposerTop);
            var last = next.IsDashboard ? Math.Max(prior.ShortcutsRow, next.ShortcutsRow) : next.Height;
            for (var row = first; row <= last; row++) ScreenLayout.WriteRow(Output, row, next.Columns, "");
        }
        else
        {
            // Size/page changes invalidate physical coordinates once. Ordinary
            // input and capped internal scrolling never repaint this surface.
            if (_running && previous is { IsDashboard: false })
            {
                // The next workspace frame replaces every content row. Only
                // clear the gap and old Composer margins during resize.
                for (var row = next.ComposerTop - next.GapHeight; row <= next.Height; row++)
                    ScreenLayout.WriteRow(Output, row, next.Columns, "");
            }
            else
                for (var row = 1; row <= next.Height; row++) ScreenLayout.WriteRow(Output, row, next.Columns, "");
            if (next.HeaderHeight > 0)
                ScreenLayout.WriteRow(Output, 1, next.Columns, ConsoleTheme.AlignToComposer(ConsoleTheme.Header(next.IsDashboard ? "New Chat" : _running ? RunningPage : _title,
                    _running ? _cancelling ? "Cancelling" : "Running" : "Ready", next.ComposerWidth, decorated: true), next));
            if (next.HeaderHeight == 2)
                ScreenLayout.WriteRow(Output, 2, next.Columns, ConsoleTheme.AlignToComposer(ConsoleTheme.HeaderRule(next.ComposerWidth), next));
        }
        if (next.IsDashboard)
        {
            foreach (var (row, text) in RenderWelcomeRows(next, _runtime.GetStatus(), NewChatSummary()))
                ScreenLayout.WriteRow(Output, row, next.Columns, text);
        }
        else if (!_running) DrawFixedContent();
        ConsoleTheme.WriteComposer(Output, next, _running,
            HistorySelectionHint(_running ? RunHint : _notice.Length > 0 ? _notice : ConsoleTheme.ComposerHint(next, CanGoBack)));
    }

    private void DrawFixedContent()
    {
        var layout = _layout!.Value;
        if (layout.IsDashboard) return;
        IReadOnlyList<string> rows;
        if (Page == InteractivePage.Resume)
        {
            rows = ResumeRows(layout);
        }
        else if (Page == InteractivePage.Main && _turnActivity is not null)
        {
            rows = WorkspaceRows(layout);
        }
        else if (IsTranscript)
        {
            var follow = _transcript.Offset >= Math.Max(0, _transcript.TotalLines - _lastPageHeight);
            if (_transcriptWidth != layout.ContentWidth)
            {
                _transcriptWidth = layout.ContentWidth;
                _transcript.Reflow(LayoutFixedTranscript(_transcriptWidth), layout.ContentHeight, follow);
            }
            else if (follow && _lastPageHeight != layout.ContentHeight)
                _transcript.Reflow(_transcript.Lines, layout.ContentHeight, true);
            _lastPageHeight = layout.ContentHeight;
            rows = _historyDocument is not null ? PaintHistoryRows(_transcript, layout, live: false)
                : _transcript.GetVisibleLines(layout.ContentHeight).ToArray();
        }
        else
        {
            rows = HasCommandResult && Page == InteractivePage.NewChat ? MainLines() : [];
        }
        var visible = rows.Take(layout.ContentHeight).ToList();
        if (HasCommandResult && layout.ContentHeight > 0) OverlayCommandResult(visible, layout);
        PaintContent(Output, layout, visible);
    }

    // Only visible rows are cached. Idle/edge scrolls emit no terminal output.
    internal bool PaintContent(TextWriter output, ScreenLayout layout, IReadOnlyList<string> visible)
    {
        var changed = false;
        for (var i = 0; i < layout.ContentHeight; i++)
        {
            var line = i < visible.Count ? visible[i] : "";
            if (_paintedLayout == layout && i < _paintedRows.Count && _paintedRows[i] == line) continue;
            ScreenLayout.WriteRow(output, layout.ContentTop + i, layout.Columns, line);
            changed = true;
        }
        if (changed)
        {
            _paintedLayout = layout;
            _paintedRows = Enumerable.Range(0, layout.ContentHeight)
                .Select(i => i < visible.Count ? visible[i] : "").ToArray();
        }
        return changed;
    }

    internal static IReadOnlyList<(int Row, string Text)> RenderWelcomeRows(
        ScreenLayout layout, AuximRuntimeStatus status, AuximRuntimeSession session)
    {
        var rows = new List<(int Row, string Text)>();
        var title = Ansi.Bold(Ansi.Text(ConsoleTheme.WelcomeTitle));
        var subtitle = Ansi.Muted(ConsoleTheme.WelcomeSubtitle);
        if (layout.IsWelcomeLayout)
        {
            rows.Add((layout.WelcomeRow, ConsoleTheme.CenterLine(title, layout)));
            rows.Add((layout.WelcomeRow + 1, ConsoleTheme.CenterLine(subtitle, layout)));
        }
        else
        {
            var limit = layout.MetadataRow > 0 ? layout.MetadataRow - 1 : layout.ShortcutsRow;
            var lines = TerminalTextLayout.WrapStyled(title, Math.Max(1, layout.ComposerWidth))
                .Concat(TerminalTextLayout.WrapStyled(subtitle, Math.Max(1, layout.ComposerWidth)));
            var row = layout.ContentTop;
            foreach (var line in lines)
            {
                if (row >= limit) break;
                rows.Add((row++, ConsoleTheme.AlignToComposer(line, layout)));
            }
        }
        if (layout.ShortcutsRow > 0)
        {
            var shortcuts = TerminalTextLayout.WrapStyled(ConsoleTheme.WelcomeShortcuts(layout.ComposerWidth), layout.ContentWidth)[0];
            rows.Add((layout.ShortcutsRow, layout.IsWelcomeLayout ? ConsoleTheme.CenterLine(shortcuts, layout) : ConsoleTheme.AlignToComposer(shortcuts, layout)));
        }
        if (layout.MetadataRow > 0)
        {
            rows.Add((layout.MetadataRow, ConsoleTheme.AlignToComposer(ConsoleTheme.DashboardStatus($"{status.ModelProvider}/{status.ModelName}",
                session.Title, session.Messages.Count, layout.ComposerWidth), layout)));
        }
        return rows;
    }

    public bool CanEmbedApproval => UsesFixedLayout && !_suspended && ApprovalPanel.Fits(TerminalSize());

    internal IReadOnlyList<string> ApprovalContext(int width)
    {
        var session = CurrentSession();
        var name = session is null ? "No active session" : session.Title;
        var activity = _running ? _turnActivity?.Latest ?? "Agent running" : "Direct tool call";
        return
        [
            Ansi.Accent(ConsoleTheme.Summarize($"Agent / Session   {TerminalTextLayout.Plain(name)}", width)),
            Ansi.Muted(ConsoleTheme.Summarize($"Activity   {TerminalTextLayout.Plain(activity)}", width)),
            ""
        ];
    }

    internal void PaintApproval(IReadOnlyList<string> rows, ScreenLayout layout, bool feedback, bool layoutChanged)
    {
        EnsureFixedLayout(target: layout);
        PaintContent(Output, layout, rows);
        if (layoutChanged || feedback)
            ConsoleTheme.WriteComposer(Output, layout, true,
                feedback ? "Feedback for the model   Enter sends   Ctrl+C cancels" : "Approval owns input   Ctrl+C cancel");
        Output.Write($"\u001b[{layout.InputRow};{layout.InputStartColumn}H");
        Output.Flush();
    }

    internal ScreenLayout ApprovalLayout()
    {
        var size = TerminalSize();
        if (!ApprovalPanel.Fits(size))
            throw new InvalidOperationException("Terminal became too small to review the approval. Resize and retry.");
        return ScreenLayout.Create(size.Width, size.Height);
    }

    internal void EndApprovalPanel()
    {
        if (!UsesFixedLayout) return;
        if (!_running)
        {
            DrawFixedContent();
            ConsoleTheme.WriteComposer(Output, _layout!.Value, false, ConsoleTheme.ComposerHint(_layout.Value, CanGoBack));
        }
        else
        {
            // The next preview frame replaces the panel's content rows.
            _paintedLayout = null;
            _paintedRunHint = null;
            ConsoleTheme.WriteComposer(Output, _layout!.Value, true, RunHint);
        }
    }

    public void SuspendLayout(bool clear = true)
    {
        if (!UsesFixedLayout) return;
        _suspended = true;
        _historySelection.StopDrag();
        SyncHistoryCopyControl();
        InteractiveScreen.SetHistoryScrollActive(false);
        if (clear) InteractiveScreen.Clear();
    }

    public void RestoreLayout(bool restoreMouse = true)
    {
        if (!UsesFixedLayout) return;
        _suspended = false;
        EnsureFixedLayout(force: true);
        if (restoreMouse) ApplyMousePolicy();
    }

    public void EndRun(string? status = null, string? response = null)
    {
        _running = false;
        _lastOutcome = status ?? "Completed";
        if (_runStarted is { } start)
            _lastOutcome += $" · {FormatDuration(DateTimeOffset.Now - start)}";
        _latestReply = response is null ? null : MarkdownConsoleRenderer.RenderToLines(response)
            .Select(TerminalTextLayout.Plain).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        _notice = status ?? "";
        if (UsesFixedLayout)
        {
            _suspended = false;
            _inputRows = 1;
            _turnContext = ReadMainContext();
            if (Page == InteractivePage.History)
            {
                _title = "History";
                var history = HistoryLines(GetCurrentTurns());
                if (_historySelection.HasRange)
                {
                    if (!_historySelection.CanKeepPrefix(_liveSavedLines, history.Lines)
                        && !HistorySelection.SameDocument(_completedPreviewLines, history.Lines))
                        ResetHistorySelection("Selection cleared · transcript replaced");
                    else if (_copy is null) _copyStatus = null;
                }
                _historySelection.StopDrag();
                SetTranscriptSource(history, startAtBottom: _followLive);
                if (!_followLive) _transcript.ScrollClamped(_liveHistoryOffset, _lastPageHeight);
                _notice = HistoryMouseHint;
            }
            _liveTranscript = null;
            _liveSavedLines = [];
            _completedPreviewLines = null;
            EnsureFixedLayout();
            var layout = _layout!.Value;
            WritePageHeader(layout, RunningPage, "Ready");
            DrawFixedContent();
            ConsoleTheme.WriteComposer(Output, layout, false,
                HistorySelectionHint(_notice.Length > 0 ? _notice : ConsoleTheme.ComposerHint(layout, CanGoBack)));
            SyncHistoryCopyControl();
            ApplyMousePolicy();
        }
        else SetTranscriptSource(MainLines(), startAtBottom: false);
    }

    public bool IsTranscript => _transcript.IsActive;

    public StreamingPreview BeginStreamingPreview(string userInput)
    {
        var mode = UsesFixedLayout ? PreviewOutputMode.Workspace
            : Ansi.ControlSequencesEnabled ? PreviewOutputMode.Inline : PreviewOutputMode.Redirected;
        var saved = RenderTurnsToLines(UsesFixedLayout ? GetCurrentTurns() : []);
        _liveSavedLines = saved.Lines;
        ResetHistorySelection();
        var transcript = new StreamingTranscript(() => saved, userInput,
            UsesFixedLayout ? new TranscriptViewport() : _transcript);
        _liveTranscript = UsesFixedLayout ? transcript : null;
        _followLive = true;
        _liveHistoryOffset = 0;
        _liveHistoryRedraw = false;
        _paintedRunPage = null;
        _paintedRunHint = null;
        _historyTextSelection = false;
        _notice = "";
        _runStarted = DateTimeOffset.Now;
        _lastOutcome = _latestReply = null;
        _turnActivity = new MainRunActivity();
        _turnContext = ReadMainContext();
        Page = InteractivePage.Main;
        _title = "Main";
        _cancelling = _paintedCancelling = false;
        _incompletePreview = [];
        _running = true;
        if (UsesFixedLayout)
        {
            _inputRows = 1;
            _suspended = false;
            ApplyMousePolicy();
            EnsureFixedLayout();
            ConsoleTheme.WriteComposer(Output, _layout!.Value, true, RunHint);
        }

        var renderer = new StreamingPreviewRenderer(Output, Console.Error, mode, TerminalSize, transcript,
            appendWorkspace: UsesFixedLayout ? _turnActivity.Append : null,
            refreshWorkspace: UsesFixedLayout ? PaintWorkspace : null);
        renderer.Refresh();
        return new StreamingPreview(renderer, runtimeEvent =>
        {
            renderer.AddTool(DescribeToolEvent(runtimeEvent));
            if (mode == PreviewOutputMode.Redirected) PrintToolEvent(runtimeEvent);
        }, observe: runtimeEvent =>
        {
            _turnActivity.Observe(runtimeEvent);
            if (runtimeEvent is RuntimeApprovalRequestedEvent)
            {
                _historySelection.StopDrag();
                TerminalInput.SetHistoryCopyControl(false);
            }
            else if (runtimeEvent is RuntimeApprovalResolvedEvent) SyncHistoryCopyControl();
        }, navigate: UsesFixedLayout ? NavigateRunningHistory : null, onCompleted: () =>
        {
            if (_historySelection.HasRange && _historySelection.Range.End.Line >= _liveSavedLines.Count)
                _completedPreviewLines = transcript.ComparableSnapshot();
        });
    }

    private void WritePageHeader(ScreenLayout layout, string page, string status)
    {
        if (layout.HeaderHeight > 0)
            ScreenLayout.WriteRow(Output, 1, layout.Columns, ConsoleTheme.AlignToComposer(
                ConsoleTheme.Header(page, status, layout.ComposerWidth, decorated: true), layout));
    }

    // Only called under StreamingPreview's output lock once the turn starts.
    private bool NavigateRunningHistory(TerminalInputEvent input)
    {
        if (!_running || _liveTranscript is not { } transcript) return false;
        if (HandleHistoryInput(input)) return false;
        if (input.Kind == TerminalInputEventKind.EscapeBoundary || input.Key.Key == ConsoleKey.Escape)
        {
            if (Page != InteractivePage.History) return false;
            _historyTextSelection = false;
            TerminalInput.DiscardHistoryWheel();
            Page = InteractivePage.Main;
            ResetHistorySelection();
            ApplyMousePolicy();
            return true;
        }

        var layout = _layout!.Value;
        var delta = input.Kind == TerminalInputEventKind.MouseWheel ? input.ScrollDelta * 3 : input.Key.Key switch
        {
            ConsoleKey.UpArrow => -1,
            ConsoleKey.DownArrow => 1,
            ConsoleKey.PageUp => -Math.Max(1, layout.ContentHeight - 1),
            ConsoleKey.PageDown => Math.Max(1, layout.ContentHeight - 1),
            _ => 0
        };
        if (Page != InteractivePage.History)
        {
            if (input.Kind != TerminalInputEventKind.MouseWheel) return false;
            Page = InteractivePage.History;
            _liveHistoryRedraw = true;
            // A wheel-down entry opens live. A wheel-up entry starts reading back.
            if (delta > 0) return false;
        }
        if (input.Key.Key is ConsoleKey.F2 or ConsoleKey.R)
        {
            ResetHistorySelection();
            _historyTextSelection = !_historyTextSelection;
            if (_historyTextSelection) _followLive = false;
            else _liveHistoryRedraw = true;
            ApplyMousePolicy();
            return false;
        }
        if (input.Key.Key == ConsoleKey.End)
        {
            _followLive = !HistoryTextSelection && !_historySelection.IsDragging;
            _liveHistoryRedraw = true;
            if (HistoryTextSelection)
            {
                transcript.Prepare(layout.ContentWidth, layout.ContentHeight, followBottom: false);
                transcript.Viewport.ScrollClamped(int.MaxValue, layout.ContentHeight);
            }
            return false;
        }
        if (delta == 0 || HistoryTextSelection && input.Kind == TerminalInputEventKind.MouseWheel) return false;
        transcript.Prepare(layout.ContentWidth, layout.ContentHeight, _followLive);
        if (delta < 0) _followLive = false;
        var scrolled = transcript.Viewport.ScrollClamped(delta, layout.ContentHeight);
        if (scrolled && DraggingHistory && input.Kind == TerminalInputEventKind.MouseWheel)
        {
            ExtendHistoryDrag(layout);
            SyncHistoryCopyControl();
        }
        _liveHistoryRedraw |= scrolled;
        return false;
    }

    private void PaintWorkspace()
    {
        EnsureFixedLayout();
        var layout = _layout!.Value;
        var changed = false;
        if (_paintedRunPage != Page || _paintedCancelling != _cancelling)
        {
            _paintedRunPage = Page;
            _paintedCancelling = _cancelling;
            WritePageHeader(layout, RunningPage, _cancelling ? "Cancelling" : "Running");
            changed = true;
        }
        if (Page == InteractivePage.History && _liveTranscript is { } transcript)
        {
            // F2 leaves the visible frame stable for native terminal selection.
            if (!HistoryTextSelection || _liveHistoryRedraw || _paintedLayout != layout)
            {
                transcript.Prepare(layout.ContentWidth, layout.ContentHeight, _followLive);
                if (!HistoryTextSelection) TickHistorySelection(layout);
                _liveHistoryOffset = transcript.Viewport.Offset;
                changed |= PaintContent(Output, layout, PaintHistoryRows(transcript.Viewport, layout, live: true));
            }
            _liveHistoryRedraw = false;
        }
        else changed |= PaintContent(Output, layout, WorkspaceRows(layout));

        var hint = _cancelling ? "Cancelling · waiting for current operation" : HistorySelectionHint(RunHint);
        if (_cancelling) SyncHistoryCopyControl();
        if (_paintedRunHint != hint)
        {
            _paintedRunHint = hint;
            ConsoleTheme.WriteComposerHint(Output, layout, hint);
            changed = true;
        }

        if (!changed) return;
        Output.Write($"\u001b[{layout.InputRow};{layout.InputStartColumn}H");
        Output.Flush();
    }

    private IReadOnlyList<string> WorkspaceRows(ScreenLayout layout)
    {
        var activity = _turnActivity!;
        var width = layout.ContentWidth;
        var capacity = layout.ContentHeight;
        var status = _running ? _cancelling ? "Cancelling" : activity.Stage : _lastOutcome ?? "Completed";
        var rows = new List<string>();
        if (capacity >= 5)
        {
            rows.Add(Ansi.Accent("Agent / Session"));
            rows.Add(Detail(_turnContext[0], width));
            if (capacity >= 10) rows.Add(Detail(_turnContext[1], width));
        }
        rows.Add(Ansi.Bold(Ansi.Accent($"Activity · {ConsoleTheme.Summarize(CleanSummary(status), Math.Max(1, width - 11))}")));

        var preview = _latestReply ?? (_running || _lastOutcome?.StartsWith("Completed", StringComparison.Ordinal) == false
            ? activity.Preview : "");
        if (!string.IsNullOrWhiteSpace(preview) && rows.Count < capacity)
        {
            var label = _running ? "Live preview" : _latestReply is not null ? "Latest reply"
                : status.StartsWith("Cancelled", StringComparison.Ordinal) ? "Cancelled preview · not saved" : "Preview · not saved";
            var previewRows = preview.ReplaceLineEndings("\n").Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .SelectMany(line => TerminalTextLayout.Wrap(CleanSummary(line), Math.Max(1, width - 2)));
            var budget = Math.Min(3, Math.Max(1, (capacity - rows.Count) / 2));
            rows.Add(Ansi.Muted(ConsoleTheme.Summarize(label, width)));
            rows.AddRange(previewRows.TakeLast(budget).Select(line => Ansi.Muted("  " + line)));
        }

        var showActions = !_running && capacity - rows.Count >= 6;
        var eventBudget = Math.Max(0, capacity - rows.Count - (showActions ? 2 : 0));
        var availableEvents = activity.Events.ToArray();
        if (eventBudget > 1 && activity.EventCount > Math.Min(availableEvents.Length, eventBudget))
        {
            rows.Add(Detail($"Showing latest {eventBudget - 1} of {activity.EventCount} events", width));
            eventBudget--;
        }
        rows.AddRange(availableEvents.TakeLast(eventBudget).Select(line => Detail(line, width)));
        if (showActions)
        {
            rows.Add("");
            rows.Add(Detail("/new  /session  /history", width));
        }
        return rows.Take(capacity).ToArray();
    }

    private IReadOnlyList<string> ReadMainContext()
    {
        var status = _runtime.GetStatus();
        var session = CurrentSession();
        return
        [
            session is null ? "No active session" : $"Session  {session.Title} / {session.Messages.Count} messages",
            $"Model config  {status.ModelProvider}/{status.ModelName}"
        ];
    }

    private static string CleanSummary(string text) =>
        string.Join(" ", TerminalTextLayout.Plain(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Detail(string text, int width) =>
        Ansi.Muted("  " + ConsoleTheme.Summarize(CleanSummary(text), Math.Max(1, width - 2)));

    private (int Width, int Height) TerminalSize()
    {
        if (size is not null) return size();
        try { return (LineEditor.TerminalWidth(), Math.Max(1, Console.WindowHeight)); }
        catch (IOException) { return (80, 24); }
    }

    private static string DescribeToolEvent(RuntimeEvent runtimeEvent) => runtimeEvent switch
    {
        RuntimeToolStartedEvent started => $"Tool  {started.ToolName}  running  /  {started.ResourceAccesses.Count} resources",
        RuntimeToolCompletedEvent completed => $"Tool  {completed.ToolName}  {completed.Outcome}  /  {completed.OutputLength} chars",
        _ => $"Runtime  {runtimeEvent.Kind}",
    };

    public void PrintNewChat()
    {
        if (UsesFixedLayout && !_suspended)
        {
            _title = "New Chat";
            EnsureFixedLayout(force: true);
            return;
        }
        var status = _runtime.GetStatus();
        var session = NewChatSummary();
        Console.WriteLine(ConsoleTheme.Header("New Chat", "Ready", ConsoleTheme.ContentWidth()));
        foreach (var line in RenderDashboardLines(status, session,
            InteractiveScreen.IsAlternateScreenActive ? "alternate" : "inline", ConsoleTheme.ContentWidth()))
            Console.WriteLine(line);
    }

    internal static IReadOnlyList<string> RenderDashboardLines(
        AuximRuntimeStatus status, AuximRuntimeSession session, string mode, int width)
    {
        var lines = new List<string> { "" };
        lines.AddRange(ConsoleTheme.HeadingLines("Welcome to Auxim.", null, width));
        lines.AddRange(ConsoleTheme.FooterLines(width, "An agent for your workspace."));
        lines.Add("");
        lines.AddRange(TerminalTextLayout.WrapStyled(
            Ansi.Accent("/history") + Ansi.Muted(" conversation   ") + Ansi.Accent("/model")
            + Ansi.Muted(" setup   ") + Ansi.Accent("/help") + Ansi.Muted(" commands"), width));
        lines.Add("");
        var summaryWidth = Math.Max(1, width - (width < 28 ? 2 : 10)) * 2;
        lines.AddRange(ConsoleTheme.MetadataLines("Model", ConsoleTheme.Summarize($"{status.ModelProvider}/{status.ModelName}", summaryWidth), width).Select(Ansi.Muted));
        lines.AddRange(ConsoleTheme.MetadataLines("Session", ConsoleTheme.Summarize(session.Title, summaryWidth), width).Select(Ansi.Muted));
        var shortId = session.Id.Length > 8 ? session.Id[^8..] : session.Id;
        lines.AddRange(ConsoleTheme.MetadataLines("", $"{session.Messages.Count} messages / {shortId} / {mode}", width).Select(Ansi.Muted));
        lines.Add("");
        lines.AddRange(ConsoleTheme.FooterLines(width, session.Messages.Count == 0
            ? "Ready for your first message." : "Continue this conversation."));
        return lines;
    }

    public void PrintToolEvent(RuntimeEvent runtimeEvent)
    {
        Console.WriteLine();
        Console.WriteLine(ConsoleTheme.StreamingRow(DescribeToolEvent(runtimeEvent)));
    }

    public void PrintHelp()
    {
        ConsoleTheme.Section("Commands");
        ConsoleTheme.CommandGrid(InteractiveCommandCatalog.Commands);

        ConsoleTheme.Section("Input");
        ConsoleTheme.Hint("mouse wheel scrolls history; Up/Down edits input", "Ctrl+P/Ctrl+N input history", "Use your terminal's selection modifier while mouse tracking is active", "Ctrl+D exit");
        ConsoleTheme.Hint("Left/Right cursor", "Tab completion", "line \\ continues input", "/paste ends with .");
        ConsoleTheme.Hint("Enter sends; Ctrl+J inserts a newline when CR/LF are distinct", "otherwise use /paste");
        ConsoleTheme.Hint("Shift+Enter requires explicit modified-Enter encoding from your terminal; no universal support");
        ConsoleTheme.Hint("Ctrl+C cancels the active turn; idle clears selection, then draft; /exit quits");
        ConsoleTheme.Hint("Ctrl+A selects all input", "Shift+arrows extend selection", "Esc clears selection before navigation");
        ConsoleTheme.Hint("History: End follows live output; Esc returns to Main",
            "F2 or Ctrl+R toggles native selection; use your terminal's Copy command");
        ConsoleTheme.Hint("/history opens history", "/show <n> opens one turn", "/jump <n> shows history from a turn", "Esc returns to Main", "/session selects a saved session");
        Console.WriteLine();
    }

    public void PrintContext()
    {
        var session = CurrentSession();
        if (session is null) { ConsoleTheme.Hint("No active session. Send a message to start one."); return; }
        var userMessages = session.Messages.Count(message => message.Role == "user");
        var assistantMessages = session.Messages.Count(message => message.Role == "assistant");
        ConsoleTheme.Section("Context");
        ConsoleTheme.Pair("session", session.Id);
        ConsoleTheme.Pair("title", session.Title);
        ConsoleTheme.Pair("messages", session.Messages.Count.ToString());
        ConsoleTheme.Pair("user turns", userMessages.ToString());
        ConsoleTheme.Pair("assistant", assistantMessages.ToString());
        ConsoleTheme.Pair("chars", session.Messages.Sum(message => message.Content.Length).ToString());
    }

    private HistoryDocument HistoryLines(IEnumerable<SessionTurn> turns)
    {
        var lines = RenderTurnsToLines(turns);
        if (_incompletePreview.Count == 0) return lines;
        return new([.. lines.Lines, new("", Decoration: true), new(Ansi.Warning(IncompleteHistoryLabel), Decoration: true),
            .. _incompletePreview.Select(StreamingTranscript.DescribeLine)]);
    }

    public void PrintHistory(IReadOnlyList<string> args)
    {
        var turns = GetCurrentTurns();
        if (turns.Count == 0 && _incompletePreview.Count == 0)
        {
            PrintStatus("No turns in the current session.");
            return;
        }

        var count = ParsePositiveInt(args.FirstOrDefault(), defaultValue: Math.Min(20, turns.Count));
        var start = Math.Max(0, turns.Count - count);
        OpenTranscript(
            $"Conversation History ({turns.Count} {(turns.Count == 1 ? "turn" : "turns")})",
            HistoryLines(turns.Skip(start)),
            startAtBottom: false);
    }

    public void ShowTurn(IReadOnlyList<string> args)
    {
        var turn = FindTurn(args.FirstOrDefault());
        if (turn is null)
        {
            return;
        }

        RenderTurn(turn);
    }

    public void JumpToTurn(IReadOnlyList<string> args)
    {
        var turns = GetCurrentTurns();
        if (!TryParseTurnNumber(args.FirstOrDefault(), turns.Count, out var turnNumber))
        {
            return;
        }

        OpenTranscript(
            $"History from turn {turnNumber}",
            RenderTurnsToLines(turns.Where(turn => turn.Number >= turnNumber)),
            startAtBottom: false);
    }

    public void TailTurns(IReadOnlyList<string> args)
    {
        var turns = GetCurrentTurns();
        var count = ParsePositiveInt(args.FirstOrDefault(), defaultValue: Math.Min(5, turns.Count));
        var start = Math.Max(1, turns.Count - count + 1);
        JumpToTurn([start.ToString()]);
    }

    private SessionTurn? FindTurn(string? rawNumber)
    {
        var turns = GetCurrentTurns();
        if (!TryParseTurnNumber(rawNumber, turns.Count, out var turnNumber))
        {
            return null;
        }

        return turns.First(turn => turn.Number == turnNumber);
    }

    private bool TryParseTurnNumber(string? rawNumber, int maxTurn, out int turnNumber)
    {
        turnNumber = 0;
        if (!int.TryParse(rawNumber, out var parsed) || parsed < 1 || parsed > maxTurn)
        {
            var warning = maxTurn == 0 ? "Current session has no turns."
                : $"Usage: /show <1-{maxTurn}> or /jump <1-{maxTurn}>";
            if (UsesFixedLayout) PrintStatus(warning);
            else Console.WriteLine(Ansi.Warning(warning));
            return false;
        }

        turnNumber = parsed;
        return true;
    }

    private static int ParsePositiveInt(string? raw, int defaultValue)
    {
        return int.TryParse(raw, out var value) && value > 0 ? value : defaultValue;
    }

    private IReadOnlyList<SessionTurn> GetCurrentTurns() => GetTurns(CurrentSession());

    private static IReadOnlyList<SessionTurn> GetTurns(AuximRuntimeSession? session)
    {
        var turns = new List<SessionTurn>();
        if (session is null) return turns;
        AgentMessage? pendingUser = null;
        foreach (var message in session.Messages)
        {
            if (message.Role == "user")
            {
                if (pendingUser is not null)
                {
                    turns.Add(new SessionTurn(turns.Count + 1, pendingUser, null));
                }

                pendingUser = message;
                continue;
            }

            if (message.Role == "assistant" && pendingUser is not null)
            {
                turns.Add(new SessionTurn(turns.Count + 1, pendingUser, message));
                pendingUser = null;
            }
        }

        if (pendingUser is not null)
        {
            turns.Add(new SessionTurn(turns.Count + 1, pendingUser, null));
        }

        return turns;
    }

    private void RenderTurn(SessionTurn turn)
    {
        OpenTranscript($"Turn {turn.Number}", RenderTurnsToLines([turn]), startAtBottom: false);
    }

    public void AppendAssistantResponse(string response)
    {
        if (UsesFixedLayout) return;
        var turns = GetCurrentTurns();
        if (turns.Count == 0)
        {
            PrintStatus("No conversation to append to.");
            return;
        }

        // Project the authoritative result onto the saved conversation, never
        // the concatenation of content from different model/tool iterations.
        turns = turns.Take(turns.Count - 1)
            .Append(turns[^1] with { Assistant = new AgentMessage("assistant", response) })
            .ToArray();

        _mainResult = RenderTurnsToLines(turns.TakeLast(1));
        _latestReply = MarkdownConsoleRenderer.RenderToLines(response).Select(TerminalTextLayout.Plain)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        _mainSessionId = _runtime.GetStatus().CurrentSessionId;
        ShowMain();
        if (!UsesFixedLayout)
            foreach (var line in _mainResult) Console.WriteLine(line);
    }

    private void OpenTranscript(string title, IReadOnlyList<string> lines, bool startAtBottom, Action? redrawPrompt = null, InteractivePage page = InteractivePage.History, int initialOffset = 0)
    {
        if (page != InteractivePage.History || Page != InteractivePage.History) _historyTextSelection = false;
        ResetHistorySelection();
        Page = page;
        _notice = page switch
        {
            InteractivePage.History => HistoryMouseHint,
            InteractivePage.SessionPreview => SessionPreviewHint,
            _ => ""
        };
        _title = title;
        _running = false;
        SetTranscriptSource(lines, startAtBottom);
        _transcript.ScrollClamped(initialOffset, _lastPageHeight);
        ApplyMousePolicy();
        RenderTranscriptViewport(redrawPrompt, force: true);
    }

    private void SetTranscriptSource(IReadOnlyList<string> lines, bool startAtBottom)
    {
        _transcriptSource = UsesFixedLayout ? lines : Page == InteractivePage.Main
            ? new[] { ConsoleTheme.Header("Main", "Ready", ConsoleTheme.ContentWidth()), "" }.Concat(lines).ToArray()
            : BuildTranscriptFrame(_title, lines);
        _transcriptWidth = ConsoleTheme.ContentWidth();
        _historyDocument = UsesFixedLayout && CanSelectTranscript ? lines as HistoryDocument : null;
        var physicalRows = LayoutFixedTranscript(_transcriptWidth);
        var size = TerminalSize();
        _lastPageHeight = UsesFixedLayout ? ScreenLayout.Create(size.Width, size.Height, inputRows: _inputRows).ContentHeight
            : HistoryLayout(_transcriptWidth, WindowHeight(), physicalRows.Count).BodyHeight;
        _transcript.Open(physicalRows, startAtBottom, _lastPageHeight);
    }

    public void ScrollWheel(int delta, Action redrawPrompt)
    {
        if (!_running && !HistoryTextSelection) ScrollTranscript(delta, redrawPrompt, wheel: true);
    }

    public void ScrollTranscript(int delta, Action redrawPrompt) => ScrollTranscript(delta, redrawPrompt, wheel: false);

    private void ScrollTranscript(int delta, Action redrawPrompt, bool wheel)
    {
        if (HasCommandResult)
        {
            if (PrepareInputLayout(_inputRows) is { } resultLayout
                && _resultViewport.ScrollClamped(delta, ResultHeight(resultLayout) - 1))
                RenderTranscriptViewport(redrawPrompt);
            return;
        }
        if (Page == InteractivePage.Main)
        {
            if (delta == 0 || !wheel && delta > 0) return;
            var turns = GetCurrentTurns();
            if (turns.Count == 0 && _incompletePreview.Count == 0) return;
            // A wheel gesture enters History once; subsequent gestures only move its viewport.
            Page = InteractivePage.History;
            _historyTextSelection = false;
            _notice = HistoryMouseHint;
            _title = "History";
            SetTranscriptSource(HistoryLines(turns), startAtBottom: true);
            _transcript.ScrollClamped(delta, _lastPageHeight);
            RenderTranscriptViewport(redrawPrompt, force: true);
            return;
        }
        if (!CanSelectTranscript) return;
        var layout = PrepareInputLayout(_inputRows);
        var height = layout?.ContentHeight ?? _lastPageHeight;
        if (!_transcript.ScrollClamped(delta, height)) return;
        if (wheel && DraggingHistory && layout is { } frame)
        {
            ExtendHistoryDrag(frame);
            HistorySelectionChanged();
        }
        else RenderTranscriptViewport(redrawPrompt);
    }

    public void PrepareConversation()
    {
        if (_newSessionPending) _runtime.CreateSession(_pendingSessionTitle);
        _pendingSessionTitle = null;
        _newSessionPending = false;
        Page = InteractivePage.Main;
        _title = "Main";
        ResetMainResultIfSessionChanged();
        // Cancellation restores the working surface, not the History viewport it was launched from.
        SetTranscriptSource(MainLines(), startAtBottom: false);
    }

    public void StartNewSession(string? title = null) => ShowNewChat(title);

    public void ShowNewChat(string? title = null)
    {
        _historyTextSelection = false;
        ResetHistorySelection();
        Page = InteractivePage.NewChat;
        _newSessionPending = true;
        _pendingSessionTitle = title;
        _notice = "";
        _transcript.Reset();
        _transcriptSource = [];
        _transcriptWidth = _lastPageHeight = 0;
        InteractiveScreen.SetHistoryScrollActive(false);
        PrintNewChat();
    }

    // CurrentSessionId is the persisted selection pointer, not the newest UpdatedAt entry.
    private AuximRuntimeSession? CurrentSession()
    {
        var id = _runtime.GetStatus().CurrentSessionId;
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return _runtime.GetSession(id); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { return null; }
    }

    public void Initialize()
    {
        if (CurrentSession() is { Messages.Count: > 0 }) ShowMain();
        else ShowNewChat();
    }

    public void NavigateBack()
    {
        if (HasCommandResult) { CloseCommandResult(); return; }
        if (Page == InteractivePage.SessionPreview)
        {
            _historyTextSelection = false;
            ResetHistorySelection();
            RenderResume(_resumeInlineOffset);
            return;
        }
        if (Page == InteractivePage.History) TerminalInput.DiscardHistoryWheel();
        if (CanGoBack) ShowMain();
    }

    public void ShowCommandResult(string command, InteractiveCommandResult result)
    {
        CloseCommandResult();
        var heading = $"/{TerminalTextLayout.Plain(command)} · {(result.ExitCode == 0 ? "Done" : "Failed")} · exit {result.ExitCode}";
        var lines = result.Lines.Select(TerminalTextLayout.Plain).ToArray();
        if (!UsesFixedLayout)
        {
            Console.WriteLine(heading);
            Console.WriteLine();
            foreach (var line in lines) Console.WriteLine(line);
            return;
        }

        _commandHeading = heading;
        SyncHistoryCopyControl();
        _commandLines = lines;
        _resultWidth = 0;
        _resultExpanded = false;
        _resultViewport.Reset();
        RenderTranscriptViewport();
    }

    private int ResultHeight(ScreenLayout layout) =>
        Math.Min(_resultViewport.TotalLines + 1, Math.Max(1, _resultExpanded
            ? layout.ContentHeight : layout.ContentHeight / 2));

    private void OverlayCommandResult(List<string> rows, ScreenLayout layout)
    {
        if (_resultWidth != layout.ContentWidth)
        {
            _resultWidth = layout.ContentWidth;
            _resultViewport.Reflow(LayoutTranscript(_commandLines, _resultWidth), layout.ContentHeight, false);
        }
        var height = ResultHeight(layout);
        var start = layout.ContentHeight - height;
        if (rows.Count > start) rows.RemoveRange(start, rows.Count - start);
        while (rows.Count < start) rows.Add("");
        var more = _resultViewport.TotalLines > height - 1;
        var controls = _resultExpanded ? "F3 shrink · Esc" : more ? "↑↓ · F3 expand · Esc" : "Esc";
        if (layout.ContentWidth < 48) controls = more || _resultExpanded ? "F3 · Esc" : "Esc";
        var titleWidth = Math.Max(1, layout.ContentWidth - TerminalTextLayout.Measure(controls) - 3);
        rows.Add(Ansi.Accent(ConsoleTheme.Summarize(_commandHeading!, titleWidth)) + Ansi.Muted($"   {controls}"));
        rows.AddRange(_resultViewport.GetVisibleLines(height - 1));
    }

    public bool ToggleCommandResultExpansion()
    {
        if (!HasCommandResult) return false;
        _resultExpanded = !_resultExpanded;
        RenderTranscriptViewport();
        return true;
    }

    public void CloseCommandResult()
    {
        if (!HasCommandResult) return;
        _commandHeading = null;
        SyncHistoryCopyControl();
        _commandLines = [];
        _resultViewport.Reset();
        RenderTranscriptViewport();
    }

    public void Redraw()
    {
        if (Page == InteractivePage.NewChat) PrintNewChat();
        else RenderTranscriptViewport(force: true);
    }

    private void ResetMainResultIfSessionChanged()
    {
        var id = _runtime.GetStatus().CurrentSessionId;
        if (_mainSessionId == id) return;
        _mainSessionId = id;
        _mainResult = [];
        _lastOutcome = _latestReply = null;
        _incompletePreview = [];
        _turnActivity = null;
        _turnContext = [];
        _runStarted = null;
    }

    internal IReadOnlyList<string> MainLines()
    {
        var status = _runtime.GetStatus();
        var session = CurrentSession();
        var width = ConsoleTheme.ContentWidth();
        string Detail(string text) => Ansi.Muted("  " + ConsoleTheme.Summarize(
            string.Join(" ", TerminalTextLayout.Plain(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), Math.Max(1, width - 2)));
        var lines = new List<string>
        {
            Ansi.Bold(Ansi.Accent("Agent / Session")),
            Detail($"Model config  {status.ModelProvider}/{status.ModelName}"),
            Detail(session is null ? "No active session. Start with /new or send a message."
                : $"Session  {session.Title} / {session.Messages.Count} messages"),
        };
        if (width >= 60) lines.Add(Detail("Workspace  " + status.Workspace));
        lines.Add("");
        lines.Add(Ansi.Bold(Ansi.Accent("Quick actions")));
        lines.Add("  " + Ansi.Accent("/new") + Ansi.Muted("      New chat"));
        lines.Add("  " + Ansi.Accent("/session") + Ansi.Muted("  Saved sessions"));
        lines.Add("  " + Ansi.Accent("/history") + Ansi.Muted("  Current messages"));
        lines.Add("");
        lines.Add(Ansi.Bold(Ansi.Accent("Recent activity")));
        lines.Add(Detail(_running ? "Running" : _lastOutcome ?? "No activity in this CLI session."));
        foreach (var activity in _turnActivity?.Events ?? []) lines.Add(Detail(activity));
        if (_latestReply is not null && UsesFixedLayout) lines.Add(Detail("Latest reply  " + _latestReply));
        if (_incompletePreview.Count > 0)
        {
            var excerpt = _incompletePreview.Where(line => !string.IsNullOrWhiteSpace(line)
                && !line.Contains("Assistant  streaming", StringComparison.OrdinalIgnoreCase)).TakeLast(6);
            lines.InsertRange(0, new[] { Ansi.Warning("Cancelled preview · not saved") }
                .Concat(excerpt.Select(TerminalTextLayout.Plain)).Concat(new[] { "" }));
        }
        return lines;
    }

    public void ShowMain()
    {
        _newSessionPending = CurrentSession() is null;
        _pendingSessionTitle = null;
        ResetMainResultIfSessionChanged();
        OpenTranscript("Main", MainLines(), false, page: InteractivePage.Main);
    }

    public bool OpenResume()
    {
        try { _allResumeSessions = _runtime.ListSessions(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            PrintStatus("Could not read saved sessions. No session was changed.");
            return false;
        }
        if (_allResumeSessions.Count == 0) { PrintStatus("No saved sessions. Start a conversation or use /new."); return false; }
        _resumeSessions = [];
        SetResumeQuery("");
        return true;
    }

    internal static bool MatchesResumeQuery(AuximRuntimeSessionSummary session, string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(word =>
            session.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
            || session.Id.Contains(word, StringComparison.OrdinalIgnoreCase)
            || session.UpdatedAt.ToString("yyyy-MM-dd HH:mm").Contains(word, StringComparison.OrdinalIgnoreCase));

    public void SetResumeQuery(string text)
    {
        var selectedId = _resumeSessions.Count == 0 ? null : _resumeSessions[_resumeSelection].Id;
        _resumeQuery = string.Concat(TerminalTextLayout.Plain(text).Select(c => char.IsWhiteSpace(c) ? ' ' : c));
        _resumeSessions = _allResumeSessions.Where(session => MatchesResumeQuery(session, _resumeQuery)).ToArray();
        var selected = _resumeSessions.ToList().FindIndex(session => session.Id == selectedId);
        _resumeSelection = selected >= 0 ? selected : Math.Max(0, _resumeSessions.ToList().FindIndex(session => session.IsCurrent));
        _resumeTop = 0;
        RenderResume();
    }

    public void AppendResumeQuery(string text)
    {
        if (_resumeQuery.Length + text.Length <= 256) SetResumeQuery(_resumeQuery + text);
    }

    public void DeleteResumeQueryElement()
    {
        var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(_resumeQuery);
        if (boundaries.Length > 0) SetResumeQuery(_resumeQuery[..boundaries[^1]]);
    }

    private IReadOnlyList<string> ResumeRows(ScreenLayout layout)
    {
        var rows = new List<string>
        {
            Ansi.Accent(ConsoleTheme.Summarize($"Filter: {_resumeQuery}  [{_resumeSessions.Count}/{_allResumeSessions.Count}]", layout.ContentWidth)),
            Ansi.Muted(ConsoleTheme.Summarize("Type to filter · Ctrl+U clear · Click/↑↓ select · → preview", layout.ContentWidth))
        };
        if (_resumeSessions.Count == 0) { rows.Add("No matching sessions."); return rows; }

        var height = Math.Max(0, layout.ContentHeight - rows.Count);
        if (_resumeSelection < _resumeTop) _resumeTop = _resumeSelection;
        if (_resumeSelection >= _resumeTop + height) _resumeTop = Math.Max(0, _resumeSelection - height + 1);
        for (var i = _resumeTop; i < Math.Min(_resumeTop + height, _resumeSessions.Count); i++)
        {
            var session = _resumeSessions[i];
            var label = $"{(i == _resumeSelection ? ">" : " ")} {(session.IsCurrent ? "(current) " : "")}{CleanSummary(session.Title)}  {session.Id}";
            var line = ConsoleTheme.Summarize(label, layout.ContentWidth);
            rows.Add(i == _resumeSelection ? Ansi.Reverse(line) : Ansi.Muted(line));
        }
        return rows;
    }

    public void SelectResumeAt(TerminalPointer pointer)
    {
        if (Page != InteractivePage.Resume || !UsesFixedLayout || pointer is not { Button: 0, Phase: PointerPhase.Press, Modifiers: 0 }) return;
        var layout = PrepareInputLayout(_inputRows)!.Value;
        var row = pointer.Y - layout.ContentTop - 2;
        if (pointer.X < 1 || pointer.X > layout.ContentWidth || row < 0 || row >= layout.ContentHeight - 2) return;
        var index = _resumeTop + row;
        if (index >= _resumeSessions.Count) return;
        _resumeSelection = index;
        RenderResume();
    }

    public string? QuickActionAt(TerminalPointer pointer)
    {
        if (!UsesFixedLayout || _running || Page != InteractivePage.Main || HasCommandResult
            || pointer is not { Button: 0, Phase: PointerPhase.Press, Modifiers: 0 }
            || PrepareInputLayout(_inputRows) is not { } layout || _paintedLayout != layout) return null;
        var row = pointer.Y - layout.ContentTop;
        if (row < 0 || row >= _paintedRows.Count || pointer.X < 1 || pointer.X > layout.ContentWidth) return null;
        var line = TerminalTextLayout.Plain(_paintedRows[row]).TrimEnd();
        if (line is not ("  /new      New chat" or "  /session  Saved sessions" or "  /history  Current messages" or "  /new /session /history"))
            return null;
        foreach (var command in new[] { "/new", "/session", "/history" })
        {
            var offset = line.IndexOf(command, StringComparison.Ordinal);
            if (offset >= 0 && pointer.X > offset && pointer.X <= offset + command.Length) return command;
        }
        return null;
    }

    private void RenderResume(int inlineOffset = 0)
    {
        if (UsesFixedLayout)
        {
            var changedPage = Page != InteractivePage.Resume;
            Page = InteractivePage.Resume;
            _title = "Sessions";
            _notice = "→ Preview · Enter · Esc · ↑↓ select";
            ApplyMousePolicy();
            RenderTranscriptViewport(force: changedPage);
            return;
        }
        var lines = new List<string> { Ansi.Muted($"Filter: {_resumeQuery} · Up/Down select · Right preview · Enter switch · Esc Main"), "" };
        if (_resumeSessions.Count == 0) lines.Add("No matching sessions.");
        for (var i = 0; i < _resumeSessions.Count; i++)
        {
            var session = _resumeSessions[i];
            var text = $"{(i == _resumeSelection ? ">" : " ")} {i + 1}. {session.Title}{(session.IsCurrent ? " (current)" : "")}  {session.UpdatedAt:yyyy-MM-dd HH:mm}  {session.Id}";
            lines.Add(i == _resumeSelection ? Ansi.Accent(text) : Ansi.Muted(text));
        }
        OpenTranscript("Sessions", lines, false, page: InteractivePage.Resume, initialOffset: inlineOffset);
    }

    private void RevealResumeSelection(int height)
    {
        if (_resumeSessions.Count == 0) return;
        var index = 4 + _resumeSelection;
        var before = LayoutTranscript(_transcriptSource.Take(index).ToArray(), _transcriptWidth).Count;
        var length = LayoutTranscript([_transcriptSource[index]], _transcriptWidth).Count;
        var offset = _transcript.Offset;
        if (before < offset) _transcript.Scroll(before - offset);
        else if (before + length > offset + height)
            _transcript.Scroll(Math.Max(before, before + length - height) - offset);
    }

    public void MoveResumeSelection(int delta)
    {
        if (_resumeSessions.Count == 0) return;
        var next = Math.Clamp(_resumeSelection + delta, 0, _resumeSessions.Count - 1);
        if (next == _resumeSelection) return;
        _resumeSelection = next;
        RenderResume();
    }

    public void OpenSessionPreview()
    {
        if (Page != InteractivePage.Resume || _resumeSessions.Count == 0) return;
        var selected = _resumeSessions[_resumeSelection];
        _resumeInlineOffset = _transcript.Offset;
        var rows = new List<HistoryLine>
        {
            new(Ansi.Bold(CleanSummary(selected.Title)), Decoration: true),
            new(Ansi.Muted(selected.Id), Decoration: true), new("", Decoration: true)
        };
        try
        {
            var session = _runtime.GetSession(selected.Id);
            var turns = GetTurns(session);
            if (session is null) rows.Add(new("Session is no longer available.", Decoration: true));
            else if (turns.Count == 0) rows.Add(new("No messages in this session.", Decoration: true));
            else rows.AddRange(RenderTurnsToLines(turns).Lines);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            rows.Add(new("Could not read this session.", Decoration: true));
        }
        OpenTranscript("Session Preview", new HistoryDocument(rows), startAtBottom: false, page: InteractivePage.SessionPreview);
    }

    public void ScrollSessionPreview(int delta, bool byPage = false)
    {
        if (Page != InteractivePage.SessionPreview) return;
        var height = PrepareInputLayout(_inputRows)?.ContentHeight ?? _lastPageHeight;
        ScrollTranscript(byPage ? delta * Math.Max(1, height - 1) : delta, static () => { });
    }

    public bool ResumeSession(string? id = null)
    {
        if (id is null && _resumeSessions.Count == 0) return false;
        id ??= _resumeSessions[_resumeSelection].Id;
        try
        {
            if (string.IsNullOrWhiteSpace(id) || _runtime.UseSession(id) is null)
            { PrintStatus("Session not found. Choose another saved session."); return false; }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { PrintStatus("Could not switch to this session."); return false; }
        ShowMain();
        return true;
    }

    public bool ResumeNumberOrId(string value)
    {
        if (int.TryParse(value, out var index) && index > 0 && index <= _resumeSessions.Count)
            return ResumeSession(_resumeSessions[index - 1].Id);
        return ResumeSession(value);
    }

    private void RenderTranscriptViewport(Action? redrawPrompt = null, bool force = false)
    {
        if (UsesFixedLayout && !_suspended)
        {
            EnsureFixedLayout(force);
            DrawFixedContent();
            redrawPrompt?.Invoke();
            return;
        }
        var width = ConsoleTheme.ContentWidth();
        var windowHeight = WindowHeight();
        if (width != _transcriptWidth)
        {
            var followBottom = _transcript.Offset >= Math.Max(0, _transcript.TotalLines - _lastPageHeight);
            var physicalRows = LayoutTranscript(_transcriptSource, width);
            _transcriptWidth = width;
            _lastPageHeight = HistoryLayout(width, windowHeight, physicalRows.Count).BodyHeight;
            _transcript.Reflow(physicalRows, _lastPageHeight, followBottom);
        }
        if (Page == InteractivePage.Main && !HasCommandResult)
        {
            // A work surface is not a History viewport. Inline results remain in native scrollback.
            foreach (var line in _transcript.Lines) Console.WriteLine(line);
            redrawPrompt?.Invoke();
            return;
        }
        var layout = HistoryLayout(width, windowHeight, _transcript.TotalLines);
        _lastPageHeight = layout.BodyHeight;
        if (Page == InteractivePage.Resume) RevealResumeSelection(layout.BodyHeight);
        var visible = _transcript.GetVisibleLines(layout.BodyHeight).ToArray();
        // Inline output stays in native scrollback; only alternate mode owns fixed regions.
        foreach (var line in visible) Console.WriteLine(line);
        if (layout.Hints.Count > 0)
        {
            Console.WriteLine();
            if (layout.ShowCounter)
                foreach (var line in ConsoleTheme.FooterLines(width,
                    $"{_transcript.Offset + 1}-{Math.Min(_transcript.TotalLines, _transcript.Offset + visible.Length)} / {_transcript.TotalLines}"))
                    Console.WriteLine(line);
            foreach (var line in Page == InteractivePage.SessionPreview
                ? ConsoleTheme.FooterLines(width, "Left/Esc list · Enter switch · Up/Down scroll · PgUp/PgDn")
                : layout.Hints) Console.WriteLine(line);
            Console.WriteLine();
        }
        redrawPrompt?.Invoke();
    }

    internal static IReadOnlyList<string> LayoutTranscript(IReadOnlyList<string> logicalLines, int width) =>
        logicalLines.SelectMany(line => ConsoleTheme.ConversationRow(line, width)).ToArray();

    internal static (int BodyHeight, IReadOnlyList<string> Hints, bool ShowCounter) HistoryLayout(int width, int height, int total)
    {
        if (height <= 4) return (Math.Max(1, height - 1), [], false);
        var hints = ConsoleTheme.FooterLines(width, height < 10 ? "Esc back"
            : TerminalGlyphs.UnicodeEnabled ? "↑↓ scroll   PgUp/PgDn   Esc back" : "Up/Down   PgUp/PgDn   Esc back");
        if (hints.Count > height - 4) hints = ConsoleTheme.FooterLines(width, "Esc");
        if (hints.Count > height - 4) return (Math.Max(1, height - 1), [], false);
        var counterRows = ConsoleTheme.FooterLines(width, $"{total}-{total} / {total}").Count;
        var showCounter = height >= 8 && hints.Count + counterRows + 4 <= height;
        return (Math.Max(1, height - hints.Count - (showCounter ? counterRows : 0) - 3), hints, showCounter);
    }

    private static IReadOnlyList<string> BuildTranscriptFrame(string title, IReadOnlyList<string> lines) =>
        new[] { Ansi.Bold(title), "" }.Concat(lines).ToArray();

    private static int WindowHeight()
    {
        if (Console.IsOutputRedirected) return int.MaxValue;
        try { return Math.Max(1, Console.WindowHeight); }
        catch (IOException) { return 20; }
    }

    internal static HistoryDocument RenderTurnsToLines(IEnumerable<SessionTurn> turns)
    {
        var lines = new List<HistoryLine>();
        void AddMessage(string role, string? detail, IReadOnlyList<HistoryLine> body)
        {
            lines.Add(new(ConsoleTheme.MessageLabel(role, detail), Decoration: true));
            lines.AddRange(body.Select(line => line with { DisplayIndent = 2 }));
            lines.Add(new(""));
        }
        foreach (var turn in turns)
        {
            AddMessage("user", $"#{turn.Number}", MarkdownConsoleRenderer.RenderTranscript(turn.User.Content));
            AddMessage("assistant", null, turn.Assistant is null
                ? [new(Ansi.Muted("No response yet."), Decoration: true)]
                : MarkdownConsoleRenderer.RenderTranscript(turn.Assistant.Content));
        }
        return new(lines);
    }

    public void PrintRuntimePanel()
    {
        var status = _runtime.GetStatus();
        var session = CurrentSession();
        var screenMode = InteractiveScreen.IsAlternateScreenActive ? "alternate" : "inline";
        ConsoleTheme.Panel("Runtime", [
            ("model", $"{status.ModelProvider}/{status.ModelName}"),
            ("session", session is null ? "Not started" : $"{session.Id} ({session.Messages.Count} messages)"),
            ("workspace", status.Workspace),
            ("mounts", status.MountCount.ToString()),
            ("agent shell", "VAShell policy"),
            ("ui", screenMode),
            ("uptime", FormatDuration(DateTimeOffset.Now - _startedAt)),
        ]);
    }

    public void PrintShortcuts()
    {
        ConsoleTheme.Section("Shortcuts");
        ConsoleTheme.Pair("Mouse wheel", "open and scroll conversation history while idle or working");
        ConsoleTheme.Pair("History / Preview", OperatingSystem.IsMacOS()
            ? "drag to select; press Ctrl+C or click Copy; Cmd+C if the terminal forwards it"
            : "drag to select; click Copy or press Ctrl+C with a selection");
        ConsoleTheme.Pair("PageUp/PageDown", "scroll history without leaving the prompt");
        ConsoleTheme.Pair("Up/Down", "move input cursor vertically; navigate History or Sessions when open");
        ConsoleTheme.Pair("Quick actions", "click to fill Composer; Enter runs the command");
        ConsoleTheme.Pair("Tab", "complete; arrows select, Tab or click inserts");
        ConsoleTheme.Pair("F3 (result)", "expand or shrink the result panel");
        ConsoleTheme.Pair("Ctrl+P/Ctrl+N", "browse input history");
        ConsoleTheme.Pair("Alt+Up/Down", "browse input history");
        ConsoleTheme.Pair("Left/Right", "move cursor");
        ConsoleTheme.Pair("Ctrl+A", "select all input");
        ConsoleTheme.Pair("Shift+arrows", "extend input selection when supported by your terminal");
        ConsoleTheme.Pair("F2 / Ctrl+R (History/Preview)", "toggle native text selection; keyboard scrolling remains available");
        ConsoleTheme.Pair("Home/End", "jump to line boundary");
        ConsoleTheme.Pair("Ctrl+C", "cancel active turn; idle: clear selection, then draft; /exit to quit");
        ConsoleTheme.Pair("Ctrl+J", "newline when CR/LF are distinct; /paste is the fallback");
        ConsoleTheme.Pair("Shift+Enter", "newline only when the terminal sends an explicit modified Enter");
        ConsoleTheme.Pair("Ctrl+D", "exit from an empty prompt");
        ConsoleTheme.Pair("\\", "continue input on the next line");
        ConsoleTheme.Pair("//", "run the rest of the line in the real shell");
        ConsoleTheme.Pair("/history", "open the current conversation history");
        ConsoleTheme.Pair("/jump n", "show history from turn n");
        ConsoleTheme.Pair("Esc", "clear input selection first; otherwise return from History, Sessions or New Chat");
        ConsoleTheme.Pair("/new", "open New Chat; create a session on first send");
        ConsoleTheme.Pair("/session", "filter, preview and switch saved sessions");
    }

    public void PrintStatus(string text)
    {
        if (UsesFixedLayout && !_suspended)
        {
            _notice = text;
            var layout = PrepareInputLayout(_inputRows)!.Value;
            ConsoleTheme.WriteComposerHint(Output, layout, text);
            return;
        }
        ConsoleTheme.Hint(text);
    }

    public void PrintTurnHeader(string role, string status)
    {
        if (UsesFixedLayout) return;
        Console.WriteLine();
        foreach (var line in ConsoleTheme.HeadingLines(role == "assistant" ? "Assistant" : role, status, ConsoleTheme.ContentWidth()))
            Console.WriteLine(line);
    }

    public void PrintTurnFooter(DateTimeOffset started, int toolEvents)
    {
        if (UsesFixedLayout)
        {
            if (Page != InteractivePage.History)
                PrintStatus($"done in {FormatDuration(DateTimeOffset.Now - started)} / {toolEvents} tool events");
            return;
        }
        ConsoleTheme.Hint($"done in {FormatDuration(DateTimeOffset.Now - started)}", $"{toolEvents} tool events");
        Console.WriteLine();
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalSeconds < 1
            ? $"{duration.TotalMilliseconds:N0} ms"
            : $"{duration.TotalSeconds:N1} s";
    }
}

internal sealed record SessionTurn(
    int Number,
    AgentMessage User,
    AgentMessage? Assistant);

internal enum InteractivePage { Main, History, Resume, SessionPreview, NewChat }
