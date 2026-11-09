using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class VisualThemeTests
{
    [Theory]
    [InlineData("truecolor", "xterm-256color", (int)TerminalColorDepth.TrueColor)]
    [InlineData("24BIT", "xterm", (int)TerminalColorDepth.TrueColor)]
    [InlineData(null, "xterm-direct", (int)TerminalColorDepth.TrueColor)]
    [InlineData(null, "xterm-256color", (int)TerminalColorDepth.Indexed)]
    [InlineData(null, "xterm", (int)TerminalColorDepth.Basic)]
    [InlineData(null, "dumb", (int)TerminalColorDepth.Basic)]
    [InlineData(null, null, (int)TerminalColorDepth.Basic)]
    public void ColorCapabilityUsesExplicitSignals(string? colorTerm, string? term, int expected) =>
        Assert.Equal((TerminalColorDepth)expected, Ansi.DetectColorDepth(colorTerm, term));

    [Fact]
    public void BrandAndComposerHaveTrueColorAndConservativeFallbackCodes()
    {
        Assert.Equal("38;2;156;151;219", Ansi.ColorCode(TerminalColorDepth.TrueColor, 156, 151, 219, 140, 95));
        Assert.Equal("38;5;140", Ansi.ColorCode(TerminalColorDepth.Indexed, 156, 151, 219, 140, 95));
        Assert.Equal("95", Ansi.ColorCode(TerminalColorDepth.Basic, 156, 151, 219, 140, 95));
        Assert.Equal("48;2;32;37;48", Ansi.ColorCode(TerminalColorDepth.TrueColor, 32, 37, 48, 235, 40, true));
        Assert.Equal("48;5;235", Ansi.ColorCode(TerminalColorDepth.Indexed, 32, 37, 48, 235, 40, true));
        Assert.Equal("40", Ansi.ColorCode(TerminalColorDepth.Basic, 32, 37, 48, 235, 40, true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    public void HeaderFitsAndOnlyWideWindowsIncludeState(int columns)
    {
        var header = ConsoleTheme.Header("Conversation 历史 👨‍👩‍👧‍👦", "Working", columns);
        Assert.True(ConsoleTheme.CellWidth(header) <= columns);
        if (columns >= 40) Assert.EndsWith("Working", TerminalTextLayout.Plain(header), StringComparison.Ordinal);
        else Assert.DoesNotContain("Working", TerminalTextLayout.Plain(header));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(79)]
    public void ComposerDecorationKeepsTheEditCursorAndOnlyFillsItsRow(int columns)
    {
        var draft = "你好 👨‍👩‍👧‍👦 e\u0301 long draft";
        var prompt = ConsoleTheme.ComposerPrompt(columns);
        var layout = ScreenLayout.Create(columns + 1, 12);
        var row = InputRow.Create(prompt, draft, draft.Length, layout.InputColumns);
        var styled = ConsoleTheme.ComposerLine(row.Text, columns);
        Assert.True(ConsoleTheme.CellWidth(styled) <= columns);
        if (columns >= 12) Assert.Equal(columns, ConsoleTheme.CellWidth(styled));
        Assert.InRange(row.CursorColumn, 0, Math.Max(0, columns - 1));
        Assert.DoesNotContain("auxim >", TerminalTextLayout.Plain(styled));
        // A wider window reveals the complete original draft, including Unicode.
        var wide = InputRow.Create(ConsoleTheme.ComposerPrompt(100), draft, draft.Length, 100);
        Assert.EndsWith(draft, wide.Text, StringComparison.Ordinal);
        using var writer = new StringWriter();
        row.Write(writer, layout);
        Assert.Contains($"\u001b[{layout.InputRow};{row.CursorColumn + layout.ComposerInnerLeft}H", writer.ToString());
        Assert.DoesNotContain("\u001b[0J", writer.ToString());
        Assert.DoesNotContain("\u001b[2J", writer.ToString());
    }

    [Fact]
    public void StreamingStylesDoNotChangePlainContentOrAddMarkdown()
    {
        foreach (var row in new[] { "You", "Assistant  streaming", "Tool  file.write  running  /  1 resources", "Tool  echo  succeeded  /  4 chars", "Tool  echo  failed", "  **unfinished markdown", "  中文 🙂" })
            Assert.Equal(row, TerminalTextLayout.Plain(ConsoleTheme.StreamingRow(row)));
    }
}
