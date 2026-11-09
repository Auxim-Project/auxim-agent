using Auxim.Core.Runtime;
using System.Text.Json;

namespace Auxim.Cli.Interactive;

// Owns session presentation and view transitions; terminal/input lifecycles stay
// in InteractiveScreen and LineEditor, and command execution stays in the shell.
internal sealed class InteractiveView(IAuximRuntime runtime, DateTimeOffset startedAt)
{
    private readonly IAuximRuntime _runtime = runtime;
    private readonly DateTimeOffset _startedAt = startedAt;
    private readonly TranscriptViewport _transcript = new();
    private StreamingTranscript? _activeTranscript;
    private IReadOnlyList<string> _transcriptSource = [];
    private int _transcriptWidth;
    private int _lastPageHeight;

    private ScreenLayout? _layout;
    private int _inputRows = 1;
    private bool _suspended;
    private bool _running;
    private volatile bool _cancelling;
    private bool _paintedCancelling;
    private IReadOnlyList<string> _incompletePreview = [];
    public void MarkCancelling() => _cancelling = true;
    public void RetainIncompletePreview(IReadOnlyList<string> lines) => _incompletePreview = lines;
    private string _title = "New Chat";
    public InteractivePage Page { get; private set; } = InteractivePage.NewChat;
    public bool CanGoBack => HasCommandResult || Page is InteractivePage.History or InteractivePage.Resume or InteractivePage.NewChat;
    public bool HasCommandResult => _commandReturn is not null;
    private (string Title, IReadOnlyList<string> Source, int Offset)? _commandReturn;
    private IReadOnlyList<string> _commandSummary = [];
    private bool _newSessionPending = true;
    private string? _pendingSessionTitle;
    private string? _mainSessionId;
    private IReadOnlyList<string> _mainResult = [];
    private readonly Queue<string> _recentTools = new();
    private DateTimeOffset? _runStarted;
    private string? _lastOutcome;
    private string? _latestReply;
    private int _observedToolEvents;
    private IReadOnlyList<string> _paintedRows = [];
    private ScreenLayout? _paintedLayout;
    private IReadOnlyList<AuximRuntimeSessionSummary> _resumeSessions = [];
    private int _resumeSelection;
    private bool _historyTextSelection;
    public bool HistoryTextSelection => Page == InteractivePage.History && !HasCommandResult && _historyTextSelection;
    public bool CanPointComposer => !HasCommandResult && (Page is InteractivePage.Main or InteractivePage.NewChat or InteractivePage.History)
        && !HistoryTextSelection && !_running && !_suspended;

    private string HistoryMouseHint => ConsoleTheme.ContentWidth() < 50
        ? HistoryTextSelection ? "Select text · Ctrl+R mouse · Esc" : "Ctrl+R select text · Esc Main"
        : HistoryTextSelection ? "Text selection · drag/copy · Ctrl+R mouse · Esc Main"
            : "Ctrl+R select text · arrows/PgUp/PgDn scroll · Esc Main";

    public void ApplyMousePolicy()
    {
        if (!UsesFixedLayout) return;
        TerminalMouse.SetMode(_running || _suspended || HistoryTextSelection ? TerminalMouseMode.Off : TerminalMouseMode.Normal);
    }

    public bool ToggleHistoryTextSelection()
    {
        if (Page != InteractivePage.History || HasCommandResult) return false;
        _historyTextSelection = !_historyTextSelection;
        _notice = HistoryMouseHint;
        ApplyMousePolicy();
        if (PrepareInputLayout(_inputRows) is { } layout) ConsoleTheme.WriteComposerHint(Console.Out, layout, _notice);
        else ConsoleTheme.Hint(_notice);
        return true;
    }

    // New Chat is an intent: opening it never creates or overwrites a saved session.
    private AuximRuntimeSession NewChatSummary() =>
        new("", string.IsNullOrWhiteSpace(_pendingSessionTitle) ? "New conversation" : _pendingSessionTitle, default, default, false, []);
    private string _notice = "";
    public bool UsesFixedLayout => InteractiveScreen.IsAlternateScreenActive;

    // Called only by the current foreground owner (editor or preview pump).
    public ScreenLayout? PrepareInputLayout(int requestedRows = 1)
    {
        if (!UsesFixedLayout || _suspended) return null;
        _inputRows = requestedRows;
        EnsureFixedLayout();
        return _layout;
    }

    private ScreenLayout PrepareRunningLayout()
    {
        EnsureFixedLayout();
        if (_paintedCancelling != _cancelling && _layout is { } layout)
        {
            _paintedCancelling = _cancelling;
            if (layout.HeaderHeight > 0)
                ScreenLayout.WriteRow(Console.Out, 1, layout.Columns, ConsoleTheme.AlignToComposer(
                    ConsoleTheme.Header("Main", "Cancelling", layout.ComposerWidth, decorated: true), layout));
            ConsoleTheme.WriteComposerHint(Console.Out, layout, "Cancelling... waiting for current operation; effects are not undone.");
            Console.Out.Write($"\u001b[{layout.InputRow};{layout.InputStartColumn}H");
        }
        return _layout!.Value;
    }

    private void EnsureFixedLayout(bool force = false)
    {
        var size = TerminalSize();
        var next = ScreenLayout.Create(size.Width, size.Height, dashboard: !_running && !HasCommandResult && Page == InteractivePage.NewChat, inputRows: _running ? 1 : _inputRows);
        if (!force && _layout == next) return;
        var previous = _layout;
        var heightOnly = !force && previous is { } old && old.Width == next.Width && old.Height == next.Height
            && old.IsDashboard == next.IsDashboard;
        _layout = next;
        _paintedLayout = null;
        if (heightOnly && previous is { } prior)
        {
            var first = next.IsDashboard ? Math.Min(prior.WelcomeRow, next.WelcomeRow) : Math.Min(prior.ComposerTop, next.ComposerTop);
            var last = next.IsDashboard ? Math.Max(prior.ShortcutsRow, next.ShortcutsRow) : next.Height;
            for (var row = first; row <= last; row++) ScreenLayout.WriteRow(Console.Out, row, next.Columns, "");
        }
        else
        {
            // Size/page changes invalidate physical coordinates once. Ordinary
            // input and capped internal scrolling never repaint this surface.
            for (var row = 1; row <= next.Height; row++) ScreenLayout.WriteRow(Console.Out, row, next.Columns, "");
            if (next.HeaderHeight > 0)
                ScreenLayout.WriteRow(Console.Out, 1, next.Columns, ConsoleTheme.AlignToComposer(ConsoleTheme.Header(next.IsDashboard ? "New Chat" : _running ? "Main" : _title,
                    _running ? _cancelling ? "Cancelling" : "Running" : "Ready", next.ComposerWidth, decorated: true), next));
            if (next.HeaderHeight == 2)
                ScreenLayout.WriteRow(Console.Out, 2, next.Columns, ConsoleTheme.AlignToComposer(ConsoleTheme.HeaderRule(next.ComposerWidth), next));
        }
        if (next.IsDashboard)
        {
            foreach (var (row, text) in RenderWelcomeRows(next, _runtime.GetStatus(), NewChatSummary()))
                ScreenLayout.WriteRow(Console.Out, row, next.Columns, text);
        }
        else if (!_running) DrawFixedContent();
        ConsoleTheme.WriteComposer(Console.Out, next, _running,
            _running ? ConsoleTheme.RunningHint : _notice.Length > 0 ? _notice : ConsoleTheme.ComposerHint(next, CanGoBack));
    }

    private void DrawFixedContent()
    {
        var layout = _layout!.Value;
        if (layout.IsDashboard) return;
        IReadOnlyList<string> rows;
        if (IsTranscript)
        {
            var follow = _transcript.Offset >= Math.Max(0, _transcript.TotalLines - _lastPageHeight);
            if (_transcriptWidth != layout.ContentWidth)
            {
                _transcriptWidth = layout.ContentWidth;
                _transcript.Reflow(LayoutTranscript(_transcriptSource, _transcriptWidth), layout.ContentHeight, follow);
            }
            else if (follow && _lastPageHeight != layout.ContentHeight)
                _transcript.Reflow(_transcript.Lines, layout.ContentHeight, true);
            _lastPageHeight = layout.ContentHeight;
            if (Page == InteractivePage.Resume) RevealResumeSelection(layout.ContentHeight);
            rows = _transcript.GetVisibleLines(layout.ContentHeight).ToArray();
        }
        else
        {
            rows = [];
        }
        var visible = rows.Take(layout.ContentHeight).ToArray();
        PaintContent(Console.Out, layout, visible);
    }

    // Only visible rows are cached. Idle/edge scrolls emit no terminal output.
    internal void PaintContent(TextWriter output, ScreenLayout layout, IReadOnlyList<string> visible)
    {
        for (var i = 0; i < layout.ContentHeight; i++)
        {
            var line = i < visible.Count ? visible[i] : "";
            if (_paintedLayout == layout && i < _paintedRows.Count && _paintedRows[i] == line) continue;
            ScreenLayout.WriteRow(output, layout.ContentTop + i, layout.Columns, line);
        }
        _paintedLayout = layout;
        _paintedRows = Enumerable.Range(0, layout.ContentHeight)
            .Select(i => i < visible.Count ? visible[i] : "").ToArray();
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

    public void SuspendLayout()
    {
        if (!UsesFixedLayout) return;
        _suspended = true;
        InteractiveScreen.SetHistoryScrollActive(false);
        InteractiveScreen.Clear();
    }

    public void RestoreLayout()
    {
        if (!UsesFixedLayout) return;
        _suspended = false;
        EnsureFixedLayout(force: true);
        ApplyMousePolicy();
    }

    public void EndRun(string? status = null)
    {
        _running = false;
        _lastOutcome = status ?? "Completed";
        if (_runStarted is { } start)
            _lastOutcome += $" in {FormatDuration(DateTimeOffset.Now - start)} / {_observedToolEvents} tool events";
        _notice = status ?? "";
        SetTranscriptSource(MainLines(), startAtBottom: false);
        if (!UsesFixedLayout) return;
        _suspended = false;
        _inputRows = 1;
        if (status is not null) EnsureFixedLayout(force: true);
    }

    public bool IsTranscript => _transcript.IsActive;

    public StreamingPreview BeginStreamingPreview(string userInput)
    {
        var mode = !Ansi.ControlSequencesEnabled ? PreviewOutputMode.Redirected
            : InteractiveScreen.IsAlternateScreenActive ? PreviewOutputMode.Alternate : PreviewOutputMode.Inline;
        _activeTranscript = new StreamingTranscript(() => RenderTurnsToLines(GetCurrentTurns()), userInput, _transcript);
        _notice = "";
        _runStarted = DateTimeOffset.Now;
        _lastOutcome = null;
        _latestReply = null;
        _recentTools.Clear();
        _observedToolEvents = 0;
        Page = InteractivePage.Main;
        _title = "Main";
        _cancelling = _paintedCancelling = false;
        _incompletePreview = [];
        _running = true;
        if (UsesFixedLayout)
        {
            _inputRows = 1;
            _suspended = false;
            EnsureFixedLayout(force: true);
        }
        var renderer = new StreamingPreviewRenderer(Console.Out, Console.Error, mode, TerminalSize, _activeTranscript,
            UsesFixedLayout ? PrepareRunningLayout : null,
            UsesFixedLayout ? SuspendLayout : null,
            UsesFixedLayout ? RestoreLayout : null);
        return new StreamingPreview(renderer, runtimeEvent =>
        {
            var activity = DescribeToolEvent(runtimeEvent);
            _observedToolEvents++;
            _recentTools.Enqueue(runtimeEvent is RuntimeToolStartedEvent started ? $"Started {started.ToolName}" : activity);
            while (_recentTools.Count > 2) _recentTools.Dequeue();
            renderer.AddTool(activity);
            if (mode == PreviewOutputMode.Redirected) PrintToolEvent(runtimeEvent);
        });
    }

    private static (int Width, int Height) TerminalSize()
    {
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
        ConsoleTheme.Hint("mouse wheel scrolls history; Up/Down edits input", "Ctrl+P/Ctrl+N input history", "Shift+drag selects text while mouse tracking is active", "Ctrl+D exit");
        ConsoleTheme.Hint("Left/Right cursor", "Tab completion", "line \\ continues input", "/paste ends with .");
        ConsoleTheme.Hint("Enter sends; Ctrl+J inserts a newline when CR/LF are distinct", "otherwise use /paste");
        ConsoleTheme.Hint("Shift+Enter requires explicit modified-Enter encoding from your terminal; no universal support");
        ConsoleTheme.Hint("Ctrl+C cancels the active turn; idle clears selection, then draft; /exit quits");
        ConsoleTheme.Hint("Ctrl+A selects all input", "Shift+arrows extend selection", "Esc clears selection before navigation");
        ConsoleTheme.Hint("History: Ctrl+R toggles native text selection; use your terminal's Copy command");
        ConsoleTheme.Hint("/history opens history", "/show <n> opens one turn", "/jump <n> replays from a turn", "Esc returns to Main", "/resume selects a saved session");
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

    public void PrintHistory(IReadOnlyList<string> args)
    {
        var turns = GetCurrentTurns();
        if (turns.Count == 0)
        {
            PrintStatus("No turns in the current session.");
            return;
        }

        var count = ParsePositiveInt(args.FirstOrDefault(), defaultValue: Math.Min(20, turns.Count));
        var start = Math.Max(0, turns.Count - count);
        OpenTranscript(
            $"Conversation History ({turns.Count} {(turns.Count == 1 ? "turn" : "turns")})",
            RenderTurnsToLines(turns.Skip(start)),
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

    private IReadOnlyList<SessionTurn> GetCurrentTurns()
    {
        var session = CurrentSession();
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

    private void OpenTranscript(string title, IReadOnlyList<string> lines, bool startAtBottom, Action? redrawPrompt = null, InteractivePage page = InteractivePage.History)
    {
        if (page != InteractivePage.History || Page != InteractivePage.History) _historyTextSelection = false;
        Page = page;
        _notice = page == InteractivePage.History ? HistoryMouseHint : "";
        _title = title;
        _running = false;
        SetTranscriptSource(lines, startAtBottom);
        ApplyMousePolicy();
        RenderTranscriptViewport(redrawPrompt, force: true);
    }

    private void SetTranscriptSource(IReadOnlyList<string> lines, bool startAtBottom)
    {
        _transcriptSource = UsesFixedLayout ? lines : Page == InteractivePage.Main
            ? new[] { ConsoleTheme.Header("Main", "Ready", ConsoleTheme.ContentWidth()), "" }.Concat(lines).ToArray()
            : BuildTranscriptFrame(_title, lines);
        _transcriptWidth = ConsoleTheme.ContentWidth();
        var physicalRows = LayoutTranscript(_transcriptSource, _transcriptWidth);
        var size = TerminalSize();
        _lastPageHeight = UsesFixedLayout ? ScreenLayout.Create(size.Width, size.Height, inputRows: _inputRows).ContentHeight
            : HistoryLayout(_transcriptWidth, WindowHeight(), physicalRows.Count).BodyHeight;
        _transcript.Open(physicalRows, startAtBottom, _lastPageHeight);
    }

    public void ScrollWheel(int delta, Action redrawPrompt)
    {
        if (!HistoryTextSelection) ScrollTranscript(delta, redrawPrompt, wheel: true);
    }

    public void ScrollTranscript(int delta, Action redrawPrompt) => ScrollTranscript(delta, redrawPrompt, wheel: false);

    private void ScrollTranscript(int delta, Action redrawPrompt, bool wheel)
    {
        if (HasCommandResult)
        {
            var resultHeight = PrepareInputLayout(_inputRows)?.ContentHeight ?? _lastPageHeight;
            if (_transcript.ScrollClamped(delta, resultHeight)) RenderTranscriptViewport(redrawPrompt);
            return;
        }
        if (Page == InteractivePage.Main)
        {
            if (delta == 0 || !wheel && delta > 0) return;
            var turns = GetCurrentTurns();
            if (turns.Count == 0) return;
            // A wheel gesture enters History once; subsequent gestures only move its viewport.
            Page = InteractivePage.History;
            _historyTextSelection = false;
            _notice = HistoryMouseHint;
            _title = "History";
            SetTranscriptSource(RenderTurnsToLines(turns), startAtBottom: true);
            _transcript.ScrollClamped(delta, _lastPageHeight);
            RenderTranscriptViewport(redrawPrompt, force: true);
            return;
        }
        if (Page != InteractivePage.History) return;
        var layout = PrepareInputLayout(_inputRows);
        var height = layout?.ContentHeight ?? _lastPageHeight;
        if (!_transcript.ScrollClamped(delta, height)) return;
        RenderTranscriptViewport(redrawPrompt);
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
        if (CanGoBack) ShowMain();
    }

    public void ShowCommandResult(string command, InteractiveCommandResult result)
    {
        CloseCommandResult();
        var heading = $"/{TerminalTextLayout.Plain(command)} · {(result.ExitCode == 0 ? "Done" : "Failed")} · exit {result.ExitCode}";
        var lines = new[] { heading, "" }.Concat(result.Lines.Select(TerminalTextLayout.Plain)).ToArray();
        // Inline remains ordinary terminal scrollback, with no fixed-region controls.
        if (!UsesFixedLayout)
        {
            foreach (var line in lines) Console.WriteLine(line);
            return;
        }
        var size = TerminalSize();
        var resultLayout = ScreenLayout.Create(size.Width, size.Height, inputRows: _inputRows);
        var width = resultLayout.ContentWidth;
        if (LayoutTranscript(lines, width).Count <= Math.Min(6, resultLayout.ContentHeight - 1))
        {
            ResetMainResultIfSessionChanged();
            _commandSummary = lines;
            Page = InteractivePage.Main;
            _title = "Main";
            _notice = heading;
            SetTranscriptSource(MainLines(), false);
            RenderTranscriptViewport();
            ConsoleTheme.WriteComposerHint(Console.Out, _layout!.Value, heading);
            return;
        }
        _commandReturn = (_title, _transcriptSource, _transcript.Offset);
        _title = "Command result";
        _notice = "Up/Down scroll · Esc close";
        SetTranscriptSource(lines, false);
        RenderTranscriptViewport();
        ConsoleTheme.WriteComposerHint(Console.Out, _layout!.Value, _notice);
    }

    public void CloseCommandResult()
    {
        if (_commandReturn is not { } saved) return;
        _commandReturn = null;
        _title = saved.Title;
        _notice = Page == InteractivePage.History ? HistoryMouseHint : "";
        SetTranscriptSource(saved.Source, false);
        _transcript.Scroll(saved.Offset);
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
        _commandSummary = [];
        _lastOutcome = _latestReply = null;
        _incompletePreview = [];
        _recentTools.Clear();
        _observedToolEvents = 0;
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
        lines.Add("  " + Ansi.Accent("/resume") + Ansi.Muted("   Saved sessions"));
        lines.Add("  " + Ansi.Accent("/history") + Ansi.Muted("  Current messages"));
        lines.Add("");
        lines.Add(Ansi.Bold(Ansi.Accent("Recent activity")));
        lines.Add(Detail(_running ? "Running" : _lastOutcome ?? "No activity in this CLI session."));
        foreach (var activity in _recentTools) lines.Add(Detail(activity));
        if (_latestReply is not null && UsesFixedLayout) lines.Add(Detail("Latest reply  " + _latestReply));
        if (_incompletePreview.Count > 0)
        {
            var excerpt = _incompletePreview.Where(line => !string.IsNullOrWhiteSpace(line)
                && !line.Contains("Assistant  streaming", StringComparison.OrdinalIgnoreCase)).TakeLast(6);
            lines.InsertRange(0, new[] { Ansi.Warning("Cancelled preview · not saved") }
                .Concat(excerpt.Select(TerminalTextLayout.Plain)).Concat(new[] { "" }));
        }
        if (_commandSummary.Count > 0)
        {
            lines.InsertRange(0, new[] { Ansi.Bold(Ansi.Accent("Command result")) }
                .Concat(_commandSummary).Concat(new[] { "" }));
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
        try { _resumeSessions = _runtime.ListSessions(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            PrintStatus("Could not read saved sessions. No session was changed.");
            return false;
        }
        if (_resumeSessions.Count == 0) { PrintStatus("No saved sessions. Start a conversation or use /new."); return false; }
        _resumeSelection = Math.Max(0, _resumeSessions.ToList().FindIndex(session => session.IsCurrent));
        RenderResume();
        return true;
    }

    private void RenderResume()
    {
        var lines = new List<string> { Ansi.Muted("Up/Down select · Enter open · Esc back"), "" };
        for (var i = 0; i < _resumeSessions.Count; i++)
        {
            var session = _resumeSessions[i];
            var text = $"{(i == _resumeSelection ? ">" : " ")} {i + 1}. {session.Title}{(session.IsCurrent ? " (current)" : "")}  {session.UpdatedAt:yyyy-MM-dd HH:mm}  {session.Id}";
            lines.Add(i == _resumeSelection ? Ansi.Accent(text) : Ansi.Muted(text));
        }
        OpenTranscript("Resume", lines, false, page: InteractivePage.Resume);
    }

    private void RevealResumeSelection(int height)
    {
        var index = (UsesFixedLayout ? 2 : 4) + _resumeSelection;
        var before = LayoutTranscript(_transcriptSource.Take(index).ToArray(), _transcriptWidth).Count;
        var length = LayoutTranscript([_transcriptSource[index]], _transcriptWidth).Count;
        var offset = _transcript.Offset;
        if (before < offset) _transcript.Scroll(before - offset);
        else if (before + length > offset + height)
            _transcript.Scroll(Math.Max(before, before + length - height) - offset);
    }

    public void MoveResumeSelection(int delta)
    {
        var next = Math.Clamp(_resumeSelection + delta, 0, _resumeSessions.Count - 1);
        if (next == _resumeSelection) return;
        _resumeSelection = next;
        RenderResume();
    }

    public bool ResumeSession(string? id = null)
    {
        id ??= _resumeSessions.Count > 0 ? _resumeSessions[_resumeSelection].Id : null;
        if (string.IsNullOrWhiteSpace(id) || _runtime.UseSession(id) is null)
        { PrintStatus("Session not found. Use /resume to select a saved session."); return false; }
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
            foreach (var line in layout.Hints) Console.WriteLine(line);
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

    internal static IReadOnlyList<string> RenderTurnsToLines(IEnumerable<SessionTurn> turns)
    {
        var lines = new List<string>();
        foreach (var turn in turns)
        {
            lines.AddRange(ConsoleTheme.MessageLines("user", MarkdownConsoleRenderer.RenderToLines(turn.User.Content), $"#{turn.Number}"));
            lines.AddRange(ConsoleTheme.MessageLines("assistant", turn.Assistant is null
                ? [Ansi.Muted("No response yet.")]
                : MarkdownConsoleRenderer.RenderToLines(turn.Assistant.Content)));
        }
        return lines;
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
        ConsoleTheme.Pair("Mouse wheel", "open and scroll conversation history");
        ConsoleTheme.Pair("Shift+drag", "select text while terminal mouse tracking is active");
        ConsoleTheme.Pair("PageUp/PageDown", "scroll history without leaving the prompt");
        ConsoleTheme.Pair("Up/Down", "move input cursor vertically; navigate History/Resume when open");
        ConsoleTheme.Pair("Ctrl+P/Ctrl+N", "browse input history");
        ConsoleTheme.Pair("Alt+Up/Down", "browse input history");
        ConsoleTheme.Pair("Left/Right", "move cursor");
        ConsoleTheme.Pair("Ctrl+A", "select all input");
        ConsoleTheme.Pair("Shift+arrows", "extend input selection when supported by your terminal");
        ConsoleTheme.Pair("Ctrl+R (History)", "toggle native text selection; keyboard scrolling remains available");
        ConsoleTheme.Pair("Home/End", "jump to line boundary");
        ConsoleTheme.Pair("Tab", "complete slash commands");
        ConsoleTheme.Pair("Ctrl+C", "cancel active turn; idle: clear selection, then draft; /exit to quit");
        ConsoleTheme.Pair("Ctrl+J", "newline when CR/LF are distinct; /paste is the fallback");
        ConsoleTheme.Pair("Shift+Enter", "newline only when the terminal sends an explicit modified Enter");
        ConsoleTheme.Pair("Ctrl+D", "exit from an empty prompt");
        ConsoleTheme.Pair("\\", "continue input on the next line");
        ConsoleTheme.Pair("//", "run the rest of the line in the real shell");
        ConsoleTheme.Pair("/history", "list turn numbers in the current session");
        ConsoleTheme.Pair("/jump n", "clear and replay conversation from turn n");
        ConsoleTheme.Pair("Esc", "clear input selection first; otherwise return from History, Resume or New Chat");
        ConsoleTheme.Pair("/new", "open New Chat; create a session on first send");
        ConsoleTheme.Pair("/resume", "browse and select saved sessions");
    }

    public void PrintStatus(string text)
    {
        if (UsesFixedLayout && !_suspended)
        {
            _notice = text;
            var layout = PrepareInputLayout(_inputRows)!.Value;
            ConsoleTheme.WriteComposerHint(Console.Out, layout, text);
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

internal enum InteractivePage { Main, History, Resume, NewChat }
