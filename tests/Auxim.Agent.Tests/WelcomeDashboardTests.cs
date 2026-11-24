using System.Text.RegularExpressions;
using Auxim.Cli.Interactive;
using Auxim.Core.Runtime;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class WelcomeDashboardTests
{
    [Theory]
    [InlineData(80, 24, 8, 11, 17, 23)]
    [InlineData(120, 30, 11, 14, 20, 29)]
    [InlineData(160, 40, 16, 19, 25, 39)]
    public void WelcomeIsOneCenteredBlockWithShortcutsBelowTheComposer(int width, int height,
        int welcome, int composer, int shortcuts, int metadata)
    {
        var layout = ScreenLayout.Create(width, height, dashboard: true);
        Assert.True(layout.IsWelcomeLayout);
        Assert.Equal(welcome, layout.WelcomeRow);
        Assert.Equal(composer, layout.ComposerTop);
        Assert.Equal(shortcuts, layout.ShortcutsRow);
        Assert.Equal(metadata, layout.MetadataRow);
        var rows = Welcome(layout);
        Assert.Equal(ConsoleTheme.WelcomeTitle, Plain(rows.Single(r => r.Row == welcome).Text).Trim());
        Assert.Equal(ConsoleTheme.WelcomeSubtitle, Plain(rows.Single(r => r.Row == welcome + 1).Text).Trim());
        Assert.Equal("/history   /model   /help", Plain(rows.Single(r => r.Row == shortcuts).Text).Trim());
        foreach (var item in rows.Where(r => r.Row != metadata))
        {
            var text = Plain(item.Text);
            Assert.Equal((layout.Columns - TerminalTextLayout.Measure(text.TrimStart())) / 2, text.Length - text.TrimStart().Length);
        }
        Assert.Contains("local/placeholder", Plain(rows.Single(r => r.Row == metadata).Text));
        Assert.DoesNotContain(rows, r => r.Row >= composer && r.Row < composer + layout.ComposerHeight);
        Assert.InRange((welcome + shortcuts) / 2, height / 2 - 1, height / 2 + 1);
    }

    [Theory]
    [InlineData(32, 24)]
    [InlineData(80, 12)]
    [InlineData(10, 8)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    public void CompactWelcomeNeverOverlapsHeaderComposerOrOtherRows(int width, int height)
    {
        var layout = ScreenLayout.Create(width, height, dashboard: true);
        Assert.False(layout.IsWelcomeLayout);
        var rows = Welcome(layout);
        Assert.Equal(rows.Count, rows.Select(r => r.Row).Distinct().Count());
        Assert.All(rows, row =>
        {
            Assert.InRange(row.Row, layout.ContentTop, height);
            Assert.True(row.Row < layout.ComposerTop);
            Assert.True(TerminalTextLayout.Measure(row.Text) <= layout.Columns);
        });
        Assert.Equal(height - layout.ComposerHeight + 1, layout.ComposerTop);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 30)]
    [InlineData(160, 40)]
    public void TheSameDraftAndCursorProjectIntoCentralAndBottomPositions(int width, int height)
    {
        var center = ScreenLayout.Create(width, height, true);
        var bottom = ScreenLayout.Create(width, height);
        var draft = string.Concat(Enumerable.Repeat("中文 👨‍👩‍👧‍👦 e\u0301 text ", 20));
        var row = InputRow.Create(ConsoleTheme.ComposerPrompt(center.ComposerWidth), draft, draft.Length - 3, center.InputColumns);
        Assert.Equal(center.InputColumns, bottom.InputColumns);
        Assert.Equal(center.ComposerLeft, bottom.ComposerLeft);
        Assert.NotEqual(center.InputRow, bottom.InputRow);
        using var central = new StringWriter();
        using var lower = new StringWriter();
        row.Write(central, center);
        row.Write(lower, bottom);
        Assert.Equal(Regex.Replace(central.ToString(), "\\[\\d+;", "[ROW;"), Regex.Replace(lower.ToString(), "\\[\\d+;", "[ROW;"));
        Assert.EndsWith($"\u001b[{center.InputRow};{center.ComposerInnerLeft + row.CursorColumn}H", central.ToString(), StringComparison.Ordinal);
        Assert.EndsWith($"\u001b[{bottom.InputRow};{bottom.ComposerInnerLeft + row.CursorColumn}H", lower.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WelcomeHeaderDoesNotRepeatThePageOrHeroTitle()
    {
        Assert.Equal("AUXIM", Plain(ConsoleTheme.Header("", "Ready", 24)));
        var header = Plain(ConsoleTheme.Header("", "Ready", 79));
        Assert.StartsWith("AUXIM", header, StringComparison.Ordinal);
        Assert.EndsWith("Ready", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Dashboard", header);
        Assert.DoesNotContain("What would", header);
    }

    private static IReadOnlyList<(int Row, string Text)> Welcome(ScreenLayout layout) => InteractiveView.RenderWelcomeRows(layout,
        new("/home", "local", "placeholder", null, 90, "", "/workspace", 0),
        new("session-test", "Local development", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, []));
    private static string Plain(string text) => TerminalTextLayout.Plain(text);
}
