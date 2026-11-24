using System.Text.RegularExpressions;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class TerminalComponentTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 30)]
    [InlineData(160, 40)]
    public void BorderedComposerHasTwoLayersAndOneSharedCoordinateSpace(int width, int height)
    {
        var layout = ScreenLayout.Create(width, height, true);
        Assert.True(layout.HasComposerBorder);
        Assert.Equal(5, layout.ComposerHeight);
        Assert.Equal(2, layout.HeaderHeight);
        Assert.Equal(layout.ComposerWidth - 2, layout.ComposerInnerWidth);
        Assert.Equal(layout.ComposerLeft + 1, layout.ComposerInnerLeft);
        Assert.Equal(layout.ComposerInnerLeft + 4, layout.InputStartColumn);
        Assert.Equal(layout.InputRow + 1, layout.SeparatorRow);
        Assert.Equal(layout.SeparatorRow + 1, layout.HintRow);
        using var output = new StringWriter();
        ConsoleTheme.WriteComposer(output, layout, false, ConsoleTheme.ComposerHint(layout, false));
        var rows = PaintedRows(output.ToString());
        Assert.All(rows, row => Assert.Equal(layout.ComposerWidth, TerminalTextLayout.Measure(row.Text)));
        Assert.All(rows, row => Assert.Equal(layout.ComposerLeft, row.Column));
        Assert.StartsWith("╭", rows.First().Text, StringComparison.Ordinal);
        Assert.EndsWith("╯", rows.Single(r => r.Row == layout.ComposerTop + 4).Text, StringComparison.Ordinal);
        Assert.Contains(rows, r => r.Row == layout.SeparatorRow && r.Text.StartsWith("│─", StringComparison.Ordinal) && r.Text.EndsWith("─│", StringComparison.Ordinal));
        var input = rows.Last(r => r.Row == layout.InputRow).Text;
        Assert.StartsWith("│  ›  Message Auxim...", input, StringComparison.Ordinal);
        Assert.EndsWith("│", input, StringComparison.Ordinal);
        Assert.Equal(' ', input[layout.InputStartColumn - layout.ComposerLeft]);
        Assert.Equal('M', input[layout.InputStartColumn - layout.ComposerLeft + 1]);
        Assert.DoesNotContain("\u001b[2J", output.ToString());
        Assert.DoesNotContain("\u001b[2K", output.ToString());
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 30)]
    [InlineData(160, 40)]
    public void UnicodeLongInputCannotOverwriteEitherBorder(int width, int height)
    {
        var layout = ScreenLayout.Create(width, height);
        var text = string.Concat(Enumerable.Repeat("中文 👨‍👩‍👧‍👦 e\u0301 🙂 long input ", 50));
        foreach (var cursor in new[] { 0, 2, text.Length - 4, text.Length })
        {
            var input = InputRow.Create(ConsoleTheme.ComposerPrompt(layout.ComposerWidth), text, cursor, layout.InputColumns);
            using var output = new StringWriter();
            input.Write(output, layout);
            var row = Assert.Single(PaintedRows(output.ToString()));
            Assert.StartsWith("│  › ", row.Text, StringComparison.Ordinal);
            Assert.EndsWith("  │", row.Text, StringComparison.Ordinal);
            Assert.Equal(layout.ComposerWidth, TerminalTextLayout.Measure(row.Text));
            Assert.EndsWith($"\u001b[{layout.InputRow};{layout.ComposerInnerLeft + input.CursorColumn}H", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(32, 20)]
    [InlineData(80, 10)]
    [InlineData(4, 4)]
    public void CompactComposerRemovesDecorationsWithoutSpendingEditCells(int width, int height)
    {
        var layout = ScreenLayout.Create(width, height, true);
        Assert.False(layout.HasComposerBorder);
        Assert.Equal(layout.ComposerWidth, layout.ComposerInnerWidth);
        Assert.Equal(layout.ComposerLeft, layout.ComposerInnerLeft);
        using var output = new StringWriter();
        ConsoleTheme.WriteComposer(output, layout, false, ConsoleTheme.ComposerHint(layout, false));
        Assert.DoesNotContain("╭", output.ToString());
        Assert.DoesNotContain("├", output.ToString());
        Assert.DoesNotContain("│", output.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(79)]
    [InlineData(119)]
    public void DecoratedHeaderAndRuleAlwaysFit(int columns)
    {
        var header = TerminalTextLayout.Plain(ConsoleTheme.Header("Conversation", "Running", columns, true));
        Assert.True(TerminalTextLayout.Measure(header) <= columns);
        Assert.Equal(columns, TerminalTextLayout.Measure(ConsoleTheme.HeaderRule(columns)));
        if (columns >= 40)
        {
            Assert.StartsWith("◇ AUXIM /", header, StringComparison.Ordinal);
            Assert.EndsWith("Running", header, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DashboardUsesQuietTagsAndSplitFooterWithoutAddingActions()
    {
        Assert.Equal("/history   /model   /help", TerminalTextLayout.Plain(ConsoleTheme.WelcomeShortcuts(75)));
        Assert.Equal("/history   /model   /help", TerminalTextLayout.Plain(ConsoleTheme.WelcomeShortcuts(31)));
        var footer = TerminalTextLayout.Plain(ConsoleTheme.DashboardStatus("local/placeholder", "Workspace review", 2, 119));
        Assert.Equal(2, footer.IndexOf("Model", StringComparison.Ordinal));
        Assert.True(footer.IndexOf("Workspace", StringComparison.Ordinal) > 60);
        Assert.Equal(117, TerminalTextLayout.Measure(footer));
        Assert.EndsWith("2 messages", footer, StringComparison.Ordinal);
    }

    private static IReadOnlyList<(int Row, int Column, string Text)> PaintedRows(string frame)
    {
        var matches = Regex.Matches(frame, "\u001b\\[(\\d+);(\\d+)H([^\u001b]*)");
        return matches.Where(m => m.Groups[3].Value.Length > 0).Select(m => (
            int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Value)).ToArray();
    }
}
