namespace Auxim.Cli.Interactive;

internal enum InputSubmissionKind { AgentMessage, CliCommand, HostCommand }
internal sealed record InputSubmission(string Text, InputSubmissionKind Kind)
{
    // Only fresh, deliberately typed input is classified. Recalled messages,
    // paste and continuation carry their type independently of their contents.
    public static InputSubmission Fresh(string text) => new(text,
        text.TrimStart().StartsWith("//", StringComparison.Ordinal) ? InputSubmissionKind.HostCommand
        : text.TrimStart().StartsWith('/') ? InputSubmissionKind.CliCommand : InputSubmissionKind.AgentMessage);
}
