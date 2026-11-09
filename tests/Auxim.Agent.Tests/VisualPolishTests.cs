using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class VisualPolishTests
{
    [Theory]
    [InlineData(80, 75, 3)]
    [InlineData(120, 100, 10)]
    [InlineData(160, 100, 30)]
    [InlineData(32, 31, 1)]
    [InlineData(4, 3, 1)]
    [InlineData(1, 0, 1)]
    public void ComposerUsesOneCenteredCoordinateSpace(int width, int expectedWidth, int expectedLeft)
    {
        var layout = ScreenLayout.Create(width, 24);
        Assert.Equal(expectedWidth, layout.ComposerWidth);
        Assert.Equal(expectedLeft, layout.ComposerLeft);
        Assert.InRange((layout.Columns - layout.ComposerWidth) - 2 * (layout.ComposerLeft - 1), 0, 1);
        Assert.Equal(layout.ComposerInnerWidth - layout.InputRightPadding, layout.InputColumns);
        var draft = string.Concat(Enumerable.Repeat("中文 👨‍👩‍👧‍👦e\u0301 long text ", 30));
        var row = InputRow.Create(ConsoleTheme.ComposerPrompt(layout.ComposerWidth), draft, draft.Length, layout.InputColumns);
        Assert.True(ConsoleTheme.CellWidth(row.Text) <= layout.InputColumns);
        using var writer = new StringWriter();
        row.Write(writer, layout);
        Assert.StartsWith($"\u001b[{layout.InputRow};{expectedLeft}H", writer.ToString(), StringComparison.Ordinal);
        Assert.EndsWith($"\u001b[{layout.InputRow};{layout.ComposerInnerLeft + row.CursorColumn}H", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[2K", writer.ToString());
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(120, true)]
    [InlineData(160, true)]
    [InlineData(32, false)]
    public void HintsKeepWholeShortcutsAndOnlyShowBackInHistory(int width, bool showsBack)
    {
        var layout = ScreenLayout.Create(width, 24);
        var dashboard = ConsoleTheme.ComposerHint(layout, false);
        var history = ConsoleTheme.ComposerHint(layout, true);
        Assert.Contains("/ Commands", dashboard);
        Assert.Contains("Enter Send", dashboard);
        Assert.DoesNotContain("Esc Back", dashboard);
        Assert.Equal(showsBack, history.Contains("Esc Back", StringComparison.Ordinal));
        Assert.True(history.Length <= layout.InputColumns - 2);
    }

    [Theory]
    [InlineData(80, 24, true)]
    [InlineData(120, 24, true)]
    [InlineData(160, 40, true)]
    [InlineData(160, 12, false)]
    [InlineData(32, 24, false)]
    public void OnlySpaciousDashboardGetsCenteredAndUpperMiddlePlacement(int width, int height, bool centered)
    {
        var layout = ScreenLayout.Create(width, height, dashboard: true);
        Assert.Equal(centered, layout.IsWelcomeLayout);
        var bottom = ScreenLayout.Create(width, height);
        Assert.False(bottom.IsDashboard);
        Assert.Equal(height - bottom.ComposerHeight + 1, bottom.ComposerTop);
        if (centered)
        {
            Assert.True(layout.WelcomeRow < layout.ComposerTop);
            Assert.True(layout.ShortcutsRow > layout.ComposerTop + layout.ComposerHeight - 1);
        }
        // Transcript projection is independent of Dashboard placement.
        var transcript = InteractiveView.LayoutTranscript(["You", "  你好"], layout.ContentWidth);
        Assert.Equal("You", TerminalTextLayout.Plain(transcript[0]));
        Assert.StartsWith("  你好", TerminalTextLayout.Plain(transcript[1]), StringComparison.Ordinal);
    }
}
