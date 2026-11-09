using System.Text;

namespace Auxim.Cli.Interactive;

internal enum CommandPresentation { Navigation, Result, Foreground }

internal sealed record InteractiveCommandResult(int ExitCode, IReadOnlyList<string> Lines)
{
    internal const int MaximumCharacters = 32 * 1024;

    public static CommandPresentation Classify(IReadOnlyList<string> parts)
    {
        var command = parts.FirstOrDefault()?.ToLowerInvariant();
        var subcommand = parts.Skip(1).FirstOrDefault();
        if (command is "exit" or "quit" or "q" or "history" or "show" or "jump" or "goto"
            or "tail" or "resume" or "sessions" or "new" or "clear" or "redraw" or "welcome"
            || command == "session" && (subcommand == "list" || subcommand == "use" && parts.Count == 2))
            return CommandPresentation.Navigation;
        if (command == "model" && subcommand == "set" && parts.Count == 2
            || command == "auth" && subcommand == "set-api-key" && (parts.Count < 3 || string.IsNullOrWhiteSpace(parts[2]))
            || command == "tool" && subcommand == "run" && parts.Count >= 3)
            return CommandPresentation.Foreground;
        return CommandPresentation.Result;
    }

    // Only called for the synchronous, non-interactive branches classified above.
    // Both channels share a bounded sink so their write order is retained.
    public static InteractiveCommandResult Capture(Func<int> action)
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        var output = new BoundedWriter();
        var error = new ChannelWriter(output, true);
        int code;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            try { code = action(); }
            catch (OperationCanceledException) { code = 1; error.WriteLine("Command cancelled."); }
            catch (Exception exception) { code = 1; error.WriteLine($"Error: {exception.Message}"); }
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        var text = TerminalTextLayout.Plain(output.Text).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.TrimEnd('\n').Split('\n').ToList();
        if (output.Truncated) lines.Add("[Output truncated at 32768 characters. Use the non-interactive command for full output.]");
        return new(code, lines);
    }

    private sealed class BoundedWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        public override Encoding Encoding => Encoding.UTF8;
        public bool Truncated { get; private set; }
        public string Text => _text.ToString();
        public override void Write(char value)
        {
            if (_text.Length < MaximumCharacters) _text.Append(value);
            else Truncated = true;
        }
    }

    private sealed class ChannelWriter(BoundedWriter target, bool error) : TextWriter
    {
        private bool _lineStart = true;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            if (_lineStart && error) foreach (var c in "stderr: ") target.Write(c);
            target.Write(value);
            _lineStart = value == '\n';
        }
    }
}
