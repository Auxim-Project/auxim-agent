namespace Auxim.Cli.Interactive;

internal enum ComposerInputAction { Ignore, InsertNewline, Submit }
internal static class ComposerInputActions
{
    public static ComposerInputAction Decide(TerminalInputEvent input)
    {
        if (input.Kind == TerminalInputEventKind.LineBreak) return ComposerInputAction.InsertNewline;
        if (input.Kind != TerminalInputEventKind.Key) return ComposerInputAction.Ignore;
        var key = input.Key;
        if (key.Key == ConsoleKey.J && key.Modifiers == ConsoleModifiers.Control
            || key.Key == ConsoleKey.Enter && (key.Modifiers == ConsoleModifiers.Shift
                || key.KeyChar == '\n' && key.Modifiers == ConsoleModifiers.Control))
            return ComposerInputAction.InsertNewline;
        return key.Key == ConsoleKey.Enter ? ComposerInputAction.Submit : ComposerInputAction.Ignore;
    }
}
