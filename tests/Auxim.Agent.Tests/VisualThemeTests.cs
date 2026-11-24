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
        Assert.True(TerminalTextLayout.Measure(header) <= columns);
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
        Assert.True(TerminalTextLayout.Measure(styled) <= columns);
        if (columns >= 12) Assert.Equal(columns, TerminalTextLayout.Measure(styled));
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

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(90)]
    public void PanelAndCommandGridBudgetUsesTerminalCells(int width)
    {
        const string body = "中文🙂 e\u0301 👨‍👩‍👧‍👦 🇺🇸\tlabel text " +
            "Long unbroken value: abcdefghijklmnopqrstuvwxyz";
        var panel = ConsoleTheme.PanelRows([("模型", body), ("session", body)], width);
        var help = ConsoleTheme.CommandRows([new("/history", body), new("/help", body)], width);
        foreach (var row in panel.Concat(help))
        {
            Assert.InRange(TerminalTextLayout.Measure(row), 0, width);
            Assert.DoesNotContain("�", string.Concat(row.EnumerateRunes()));
        }
    }

    [Theory]
    [InlineData(false, "中文", 1, ".")]
    [InlineData(false, "🙂🙂", 3, "...")]
    [InlineData(false, "e\u0301文", 2, "e\u0301.")]
    [InlineData(true, "中文", 1, ".")]
    [InlineData(true, "🙂🙂", 3, "🙂…")]
    [InlineData(true, "e\u0301文", 2, "e\u0301.")]
    public void SummariesNeverSplitGraphemes(bool unicode, string text, int width, string expected)
    {
        using var glyphs = TerminalGlyphs.UseUnicode(unicode);
        Assert.Equal(expected, ConsoleTheme.Summarize(text, width));
    }

    [Fact]
    public async Task GlyphModesStayIsolatedAcrossOverlappingExecutionContexts()
    {
        using var glyphs = TerminalGlyphs.UseUnicode(false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unicode = Task.Run(async () =>
        {
            using (TerminalGlyphs.UseUnicode(true))
            {
                ready.SetResult();
                await release.Task;
                Assert.Equal("╭", TerminalGlyphs.TopLeft);
            }
            Assert.Equal("+", TerminalGlyphs.TopLeft);
        });

        await ready.Task;
        var parentGlyph = TerminalGlyphs.TopLeft;
        release.SetResult();
        await unicode;
        Assert.Equal("+", parentGlyph);
    }
}
