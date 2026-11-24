namespace Auxim.Cli.Interactive;

internal enum TerminalColorDepth { Basic, Indexed, TrueColor }

internal static class Ansi
{
    public static bool Enabled =>
        ControlSequencesEnabled
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    public static bool ControlSequencesEnabled =>
        !Console.IsOutputRedirected
        && !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase)
        && (!OperatingSystem.IsWindows() || WindowsVirtualTerminalLikelyEnabled());

    public static string Bold(string text) => Wrap("1", text);

    public static string Accent(string text) => Color(text, 156, 151, 219, 140, 95);
    public static string Text(string text) => Color(text, 240, 240, 243, 255, 37);
    public static bool SupportsSurfaces => Enabled && ColorDepth != TerminalColorDepth.Basic;
    public static string FocusBoundary(string text) => Enabled ? "\u001b[49m" + Accent(text) : text;
    public static string Edge(string text) => Color(text, 65, 72, 87, 239, 90);
    public static string Composer(string text) => Background(text, 32, 37, 48, 235, 40);

    internal static TerminalColorDepth DetectColorDepth(string? colorTerm, string? term) =>
        string.Equals(colorTerm, "truecolor", StringComparison.OrdinalIgnoreCase)
        || string.Equals(colorTerm, "24bit", StringComparison.OrdinalIgnoreCase)
        || (term?.Contains("direct", StringComparison.OrdinalIgnoreCase) ?? false)
            ? TerminalColorDepth.TrueColor
            : (term?.Contains("256color", StringComparison.OrdinalIgnoreCase) ?? false)
                ? TerminalColorDepth.Indexed : TerminalColorDepth.Basic;

    private static TerminalColorDepth ColorDepth => DetectColorDepth(
        Environment.GetEnvironmentVariable("COLORTERM"), Environment.GetEnvironmentVariable("TERM"));

    internal static string ColorCode(TerminalColorDepth depth, int r, int g, int b, int index, int basic, bool background = false) =>
        depth switch
        {
            TerminalColorDepth.TrueColor => $"{(background ? 48 : 38)};2;{r};{g};{b}",
            TerminalColorDepth.Indexed => $"{(background ? 48 : 38)};5;{index}",
            _ => basic.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    private static string Color(string text, int r, int g, int b, int index, int basic) =>
        Wrap(ColorCode(ColorDepth, r, g, b, index, basic), text);
    private static string Background(string text, int r, int g, int b, int index, int basic) =>
        Wrap(ColorCode(ColorDepth, r, g, b, index, basic, true), text);

    public static string Success(string text) => Color(text, 152, 192, 163, 151, 32);

    public static string Warning(string text) => Color(text, 212, 180, 131, 180, 33);

    public static string Error(string text) => Color(text, 219, 150, 150, 174, 31);

    public static string Muted(string text) => Color(text, 154, 159, 171, 247, 90);

    public static string Reverse(string text) => Wrap("7", text);

    private static string Wrap(string code, string text)
    {
        return Enabled ? $"\u001b[{code}m{text.Replace("\u001b[0m", $"\u001b[0m\u001b[{code}m", StringComparison.Ordinal)}\u001b[0m" : text;
    }

    private static bool WindowsVirtualTerminalLikelyEnabled()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_SESSION"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERM_PROGRAM"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSYSTEM"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANSICON"))
            || string.Equals(Environment.GetEnvironmentVariable("ConEmuANSI"), "ON", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("ConEmuANSI"), "1", StringComparison.OrdinalIgnoreCase);
    }
}
