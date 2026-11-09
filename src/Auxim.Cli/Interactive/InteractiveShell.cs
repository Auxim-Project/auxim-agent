using Auxim.Core.Runtime;
using Auxim.Cli.Services;

namespace Auxim.Cli.Interactive;

internal static class InteractiveShell
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Now;
    private static InteractiveView _view = null!;
    private static IAuximRuntime _runtime = null!;

    public static async Task<int> RunAsync(IAuximRuntime runtime)
    {
        _runtime = runtime;
        _view = new InteractiveView(runtime, StartedAt);
        using var interrupts = InteractiveInterrupt.Enter();
        using var screen = InteractiveScreen.Enter();
        try
        {
            var history = InteractiveHistory.Load(runtime);
            var editor = new LineEditor(
                history.Entries,
                InteractiveCommandCatalog.Complete,
                _view.ScrollTranscript,
                _view.PrepareInputLayout,
                () => _view.Page == InteractivePage.NewChat,
                () => _view.Page is InteractivePage.History or InteractivePage.Resume || _view.HasCommandResult,
                _view.ScrollWheel,
                () => _view.CanPointComposer,
                () => _view.HistoryTextSelection,
                _view.ToggleHistoryTextSelection,
                history.KindAt);
            _view.Initialize();

            while (true)
            {
                var submission = ReadUserInput(editor);
                if (submission is null)
                {
                    Console.WriteLine();
                    history.Save();
                    return 0;
                }

                var input = submission.Kind == InputSubmissionKind.AgentMessage ? submission.Text : submission.Text.Trim();
                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                if (submission.Kind == InputSubmissionKind.HostCommand)
                {
                    history.Add(submission);
                    history.Save();
                    await RunForegroundAsync(async () =>
                    {
                        using (TerminalInput.Suspend())
                            await RunShellEscapeAsync(input[2..].TrimStart());
                        return 0;
                    });
                    continue;
                }

                if (submission.Kind == InputSubmissionKind.CliCommand)
                {
                    history.Add(submission);
                    var parts = SplitCommand(input[1..]);
                    var presentation = InteractiveCommandResult.Classify(parts);
                    var exitCode = 0;
                    try
                    {
                        if (presentation == CommandPresentation.Result)
                        {
                            // These branches complete synchronously: no input, tools or approval.
                            var result = InteractiveCommandResult.Capture(() => RunSlashCommandAsync(input).GetAwaiter().GetResult());
                            ApplySessionTransition(parts, result.ExitCode);
                            _view.ShowCommandResult(parts.FirstOrDefault() ?? "command", result);
                            exitCode = result.ExitCode;
                        }
                        else if (presentation == CommandPresentation.Foreground && parts[0].Equals("tool", StringComparison.OrdinalIgnoreCase)
                            && parts[1] == "run")
                            exitCode = await RunDirectToolAsync(parts.Skip(1).ToArray(), interrupts);
                        else if (presentation == CommandPresentation.Foreground)
                            exitCode = await RunForegroundAsync(() => RunSlashCommandAsync(input));
                        else exitCode = await RunSlashCommandAsync(input);
                    }
                    catch (Exception exception)
                    {
                        _view.ShowCommandResult(parts.FirstOrDefault() ?? "command",
                            new InteractiveCommandResult(1, [$"Error: {exception.Message}"]));
                    }
                    history.Save();
                    if (exitCode == ExitRequested)
                    {
                        return 0;
                    }

                    continue;
                }

                _view.PrepareConversation();
                history.Add(submission);
                history.Save();
                using var turn = new CancellationTokenSource();
                interrupts.BeginTurn(turn);
                var started = DateTimeOffset.Now;
                await using var preview = _view.BeginStreamingPreview(input);
                try
                {
                    _view.PrintTurnHeader("assistant", $"thinking  {started:HH:mm:ss}");
                    var runner = new ChatRunner(
                        _runtime,
                        new DelegateRuntimeEventSink(preview.PublishAsync));
                    var result = await interrupts.AwaitTurnAsync(runner.RunAsync(input, turn.Token), _view.MarkCancelling);
                    turn.Token.ThrowIfCancellationRequested();
                    await preview.FinishAsync();
                    _view.EndRun();
                    _view.AppendAssistantResponse(result.FinalResponse);
                    _view.PrintTurnFooter(started, preview.ToolEventCount);
                }
                catch (OperationCanceledException)
                {
                    await preview.FinishAsync("cancelled");
                    _view.RetainIncompletePreview(preview.IncompleteLines);
                    _view.EndRun("Cancelled current turn. Completed tool effects are not undone.");
                    if (!_view.UsesFixedLayout)
                    {
                        Console.WriteLine();
                        Console.WriteLine("[cancelled preview; not saved]");
                        foreach (var line in preview.IncompleteLines) Console.WriteLine(TerminalTextLayout.Plain(line));
                        Console.WriteLine(Ansi.Warning("Cancelled current turn. Completed tool effects are not undone."));
                        Console.WriteLine();
                    }
                }
                catch (Exception exception)
                {
                    await preview.FinishAsync("failed");
                    _view.EndRun($"Error: {exception.Message}");
                    if (!_view.UsesFixedLayout)
                    {
                        Console.WriteLine();
                        Console.WriteLine(Ansi.Error($"Error: {exception.Message}"));
                        Console.WriteLine();
                    }
                }
                finally
                {
                    interrupts.EndTurn();
                }
            }
        }
        finally
        {
            interrupts.EndTurn();
        }
    }

    private const int ExitRequested = 42;

    private static void ApplySessionTransition(IReadOnlyList<string> parts, int code)
    {
        if (code != 0 || parts.FirstOrDefault()?.ToLowerInvariant() != "session") return;
        if (parts.Skip(1).FirstOrDefault() is "use" or "new") _view.ShowMain();
        else if (parts.Skip(1).FirstOrDefault() == "clear") _view.ShowNewChat();
    }

    private static async Task<int> RunDirectToolAsync(IReadOnlyList<string> args, InteractiveInterrupt interrupts)
    {
        using var turn = new CancellationTokenSource();
        interrupts.BeginTurn(turn);
        var completedAfterCancellation = false;
        try
        {
            _view.ShowMain();
            var code = await RunForegroundAsync(async () =>
            {
                ConsoleTheme.Hint("Running tool. Ctrl+C requests cancellation; completed effects are not undone.");
                var result = await interrupts.AwaitTurnAsync(
                    CommandHandlers.HandleToolWithCancellation(args, _runtime, turn.Token));
                completedAfterCancellation = turn.IsCancellationRequested;
                if (completedAfterCancellation)
                    ConsoleTheme.Hint("Tool completed after a cancellation request. Effects were not rolled back.");
                return result;
            });
            _view.ShowMain();
            _view.PrintStatus(completedAfterCancellation
                ? "Tool completed after a cancellation request. Effects were not rolled back." : "Tool completed.");
            return code;
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            _view.ShowMain();
            _view.ShowCommandResult("tool run", new(130,
                ["Tool wait cancelled. Effects are not undone; child processes may continue."]));
            return 130;
        }
        catch (Exception exception)
        {
            _view.ShowMain();
            _view.ShowCommandResult("tool run", new(1, [$"Tool failed: {exception.Message}"]));
            return 1;
        }
        finally { interrupts.EndTurn(); }
    }

    private static async Task<int> RunForegroundAsync(Func<Task<int>> action)
    {
        if (!_view.UsesFixedLayout) return await action();
        _view.SuspendLayout();
        try
        {
            var result = await action();
            ConsoleTheme.Hint("Press Enter to return to Auxim.");
            while (true)
            {
                var input = TerminalInput.Read(TerminalInputPolicy.LineEditor);
                if (input.Kind == TerminalInputEventKind.EndOfInput
                    || input.Kind == TerminalInputEventKind.Key && input.Key.Key == ConsoleKey.Enter) break;
            }
            return result;
        }
        finally { _view.RestoreLayout(); }
    }

    private static async Task<int> RunSlashCommandAsync(string input)
    {
        var parts = SplitCommand(input[1..]);
        if (parts.Count == 0)
        {
            return 0;
        }

        var command = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();
        switch (command)
        {
            case "exit":
            case "quit":
            case "q":
                return ExitRequested;
            case "help":
            case "?":
                _view.PrintHelp();
                return 0;
            case "new":
                _view.StartNewSession(string.Join(' ', args));
                return 0;
            case "context":
            case "usage":
                _view.PrintContext();
                return 0;
            case "history":
                _view.PrintHistory(args);
                return 0;
            case "show":
                _view.ShowTurn(args);
                return 0;
            case "jump":
            case "goto":
                _view.JumpToTurn(args);
                return 0;
            case "tail":
                _view.TailTurns(args);
                return 0;
            case "resume":
                if (args.Length > 0) return _view.ResumeSession(args[0]) ? 0 : 1;
                BrowseSessions();
                return 0;
            case "status":
                _view.PrintRuntimePanel();
                return 0;
            case "shortcuts":
                _view.PrintShortcuts();
                return 0;
            case "paste":
                Console.WriteLine("/paste is only available from the main prompt.");
                return 0;
            case "model":
                return CommandHandlers.HandleModel(args, _runtime);
            case "auth":
                return CommandHandlers.HandleAuth(args, _runtime);
            case "config":
                return CommandHandlers.HandleConfig(args, _runtime);
            case "session":
                if (args.FirstOrDefault() == "list" || args.FirstOrDefault() == "use" && args.Length == 1)
                {
                    BrowseSessions();
                    return 0;
                }
                return CommandHandlers.HandleSession(args, _runtime);
            case "sessions":
                BrowseSessions();
                return 0;
            case "tools":
                return await CommandHandlers.HandleTool(["list"], _runtime);
            case "tool":
                return await CommandHandlers.HandleTool(args, _runtime);
            case "approval":
                return CommandHandlers.HandleApproval(args, _runtime);
            case "sandbox":
                return CommandHandlers.HandleSandbox(args, _runtime);
            case "doctor":
                return CommandHandlers.HandleDoctor(_runtime);
            case "clear":
            case "redraw":
                _view.Redraw();
                return 0;
            case "welcome":
                _view.ShowNewChat();
                return 0;
            default:
                Console.WriteLine(Ansi.Warning($"Unknown command: /{command}"));
                ConsoleTheme.Hint("Type /help for available commands.");
                return 1;
        }
    }

    private static void BrowseSessions()
    {
        if (!_view.OpenResume()) return;
        if (Console.IsInputRedirected)
        {
            Console.Write("Session number or id (empty to go back): ");
            var selection = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(selection)) _view.NavigateBack();
            else if (!_view.ResumeNumberOrId(selection.Trim())) _view.NavigateBack();
            return;
        }
        while (_view.Page == InteractivePage.Resume)
        {
            // Same serial input ownership as the editor and approval prompt.
            if (!TerminalInput.KeyAvailable)
            {
                _view.PrepareInputLayout();
                Thread.Sleep(25);
                continue;
            }
            var input = TerminalInput.Read(TerminalInputPolicy.LineEditor);
            if (input.Kind == TerminalInputEventKind.EndOfInput) { _view.NavigateBack(); return; }
            if (input.Kind != TerminalInputEventKind.Key) continue;
            switch (input.Key.Key)
            {
                case ConsoleKey.UpArrow: _view.MoveResumeSelection(-1); break;
                case ConsoleKey.DownArrow: _view.MoveResumeSelection(1); break;
                case ConsoleKey.Enter: _view.ResumeSession(); break;
                case ConsoleKey.Escape:
                    _view.NavigateBack();
                    TerminalInput.DiscardQueuedNavigation();
                    break;
            }
        }
    }

    private static InputSubmission? ReadUserInput(LineEditor editor)
    {
        _view.ApplyMousePolicy();
        string? input;
        try
        {
            input = editor.ReadLine(
                Prompt(),
                PromptVisible(),
                canNavigateBack: () => _view.CanGoBack,
                scrollNavigation: true,
                navigateBack: _view.NavigateBack,
                acceptPaste: true);
        }
        finally
        {
            // Do not leave mouse reports or navigation keys queued while a model,
            // tool, approval prompt, or child process owns the foreground.
            InteractiveScreen.SetHistoryScrollActive(active: false);
        }

        if (input is null)
        {
            return null;
        }

        _view.CloseCommandResult();

        var submission = editor.Submission ?? throw new InvalidOperationException("Missing input submission type.");
        if (editor.SubmittedPaste || (submission.Kind != InputSubmissionKind.CliCommand || input.Trim() != "/paste") && !input.EndsWith('\\')) return submission;
        _view.SuspendLayout();
        try { return new(ReadContinuedInput(editor, input), InputSubmissionKind.AgentMessage); }
        finally { _view.RestoreLayout(); }
    }

    private static string ReadContinuedInput(LineEditor editor, string input)
    {
        if (input.Trim() == "/paste")
        {
            return ReadPasteBlock(editor);
        }

        if (!input.EndsWith('\\'))
        {
            return input;
        }

        var lines = new List<string> { input[..^1] };
        while (true)
        {
            var continuation = editor.ReadLine(ContinuationPrompt(), ContinuationPromptVisible(), acceptPaste: true);
            if (continuation is null)
            {
                return string.Join(Environment.NewLine, lines);
            }

            if (continuation.EndsWith('\\'))
            {
                lines.Add(continuation[..^1]);
                continue;
            }

            lines.Add(continuation);
            return string.Join(Environment.NewLine, lines);
        }
    }

    private static string ReadPasteBlock(LineEditor editor)
    {
        ConsoleTheme.Section("Paste");
        ConsoleTheme.Hint("Finish with a single . on its own line.");
        var lines = new List<string>();
        while (true)
        {
            var line = editor.ReadLine(ContinuationPrompt(), ContinuationPromptVisible(), acceptPaste: true);
            if (line is null || line == ".")
            {
                break;
            }

            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Prompt() => $"{Ansi.Accent("auxim")}{Ansi.Muted(" >")} ";

    private static string PromptVisible() => "auxim > ";

    private static string ContinuationPrompt() => $"{Ansi.Muted("     >")} ";

    private static string ContinuationPromptVisible() => "     > ";

    private static async Task RunShellEscapeAsync(string command)
    {
        using var turn = new CancellationTokenSource();
        InteractiveInterrupt.Current!.BeginTurn(turn);
        var started = DateTimeOffset.Now;
        try
        {
            Console.WriteLine();
            Console.WriteLine($"{ConsoleTheme.Badge("shell")} {Ansi.Muted(command)} {Ansi.Muted(started.ToString("HH:mm:ss"))}");
            ConsoleTheme.Rule();
            var exitCode = await _runtime.RunHostCommandAsync(command, turn.Token);
            if (exitCode != 0)
            {
                Console.WriteLine($"{ConsoleTheme.Badge("shell")} {Ansi.Warning($"exit code {exitCode}")}");
            }
            ConsoleTheme.Rule();
            ConsoleTheme.Hint($"done in {InteractiveView.FormatDuration(DateTimeOffset.Now - started)}");
            Console.WriteLine();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine(Ansi.Warning("Cancelled shell command."));
            Console.WriteLine();
        }
        catch (Exception exception)
        {
            Console.WriteLine(Ansi.Error($"Shell error: {exception.Message}"));
            Console.WriteLine();
        }
        finally
        {
            InteractiveInterrupt.Current!.EndTurn();
            TerminalMouse.Reset();
        }
    }

    private static IReadOnlyList<string> SplitCommand(string input)
    {
        return InteractiveCommandParser.Tokenize(input);
    }
}
