namespace Auxim.Cli.Interactive;

internal static class InteractiveCommandCatalog
{
    public static readonly IReadOnlyList<InteractiveCommand> Commands =
    [
        new("/help", "Show this help"),
        new("/exit", "Quit"),
        new("/new", "Open New Chat (saved on first send)"),
        new("/session", "Browse saved sessions", ["clear"]),
        new("/model", "Show or configure model", ["show", "set"]),
        new("/auth", "Manage API keys", ["status", "path", "set-api-key"]),
        new("/config", "Show or edit config", ["show", "path", "set"]),
        new("/tools", "List tools"),
        new("/tool", "Run a tool directly", ["list", "run"]),
        new("/sandbox", "Show or edit VAFS mappings", ["show", "workspace", "mount", "unmount"]),
        new("/approval", "Show or clear approvals", ["list", "revoke", "clear"]),
        new("/doctor", "Show diagnostics"),
        new("/context", "Show current session context stats"),
        new("/history", "Open the conversation history"),
        new("/show", "Show a single turn by number"),
        new("/jump", "Replay history from a turn number"),
        new("/tail", "Replay recent turns"),
        new("/status", "Show interface and runtime status"),
        new("/shortcuts", "Show keyboard shortcuts"),
        new("/paste", "Enter multiline input"),
        new("/clear", "Clear screen"),
        new("/redraw", "Redraw the interface"),
        new("/welcome", "Show New Chat"),
        new("//", "Run a command in your real shell"),
    ];

    public static InteractiveCommand? Find(string command) =>
        Commands.FirstOrDefault(item => item.Name.Equals(command, StringComparison.OrdinalIgnoreCase));

    public static string[] Complete(string input)
    {
        if (!input.StartsWith('/') || input.IndexOfAny(['\r', '\n']) >= 0) return [];

        var split = input.IndexOfAny([' ', '\t']);
        if (split < 0)
            return Commands.Select(command => command.Name)
                .Where(name => name.StartsWith(input, StringComparison.OrdinalIgnoreCase)).ToArray();

        var command = Find(input[..split]);
        var prefix = input[(split + 1)..].TrimStart(' ', '\t');
        if (command?.Subcommands is not { } subcommands || prefix.IndexOfAny([' ', '\t']) >= 0) return [];
        return subcommands.Append("help").Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(name => $"{command.Name} {name}").ToArray();
    }
}

internal sealed record InteractiveCommand(string Name, string Description, IReadOnlyList<string>? Subcommands = null);
