using System.Text.RegularExpressions;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class SurfaceConsistencyTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 30)]
    [InlineData(160, 40)]
    public void ComposerHasOnePerimeterAndAnOpenInternalDivider(int width, int height)
    {
        var layout = ScreenLayout.Create(width, height, true);
        using var output = new StringWriter();
        ConsoleTheme.WriteComposer(output, layout, false, ConsoleTheme.ComposerHint(layout, false));
        var frame = output.ToString();
        Assert.Equal(4, frame.Count(c => c is '╭' or '╮' or '╰' or '╯'));
        Assert.DoesNotContain('├', frame);
        Assert.DoesNotContain('┤', frame);
        Assert.Contains($"\u001b[{layout.SeparatorRow};{layout.ComposerLeft}H│{new string('─', layout.ComposerInnerWidth)}│", frame);
        foreach (Match row in Regex.Matches(frame, "\u001b\\[(\\d+);(\\d+)H([^\u001b]+)"))
        {
            Assert.InRange(int.Parse(row.Groups[1].Value), layout.ComposerTop, layout.ComposerTop + layout.ComposerHeight - 1);
            Assert.Equal(layout.ComposerLeft, int.Parse(row.Groups[2].Value));
            Assert.Equal(layout.ComposerWidth, TerminalTextLayout.Measure(row.Groups[3].Value));
        }
        Assert.EndsWith($"\u001b[{layout.InputRow};{layout.InputStartColumn}H", frame, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShortcutEntrancesNeverUseBracketCharacters(bool surfaces)
    {
        var rendered = TerminalTextLayout.Plain(ConsoleTheme.WelcomeShortcuts(75, surfaces));
        Assert.DoesNotContain('[', rendered);
        Assert.DoesNotContain(']', rendered);
        Assert.Contains("/history", rendered);
        Assert.Contains("/model", rendered);
        Assert.Contains("/help", rendered);
        if (surfaces) Assert.Equal("  /history       /model       /help  ", rendered);
        else Assert.Equal("/history   /model   /help", rendered);
        Assert.Equal("/history   /model   /help", TerminalTextLayout.Plain(ConsoleTheme.WelcomeShortcuts(31, surfaces)));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(160)]
    public void HeaderRuleAndFooterUseTheSameHorizontalRegionAsComposer(int width)
    {
        var layout = ScreenLayout.Create(width, 30, true);
        var header = ConsoleTheme.AlignToComposer(ConsoleTheme.Header("Dashboard", "Ready", layout.ComposerWidth, true), layout);
        var rule = ConsoleTheme.AlignToComposer(ConsoleTheme.HeaderRule(layout.ComposerWidth), layout);
        var footer = ConsoleTheme.AlignToComposer(ConsoleTheme.DashboardStatus("local/placeholder", "Workspace", 2, layout.ComposerWidth), layout);
        Assert.Equal(layout.ComposerLeft - 1, TerminalTextLayout.Plain(header).IndexOf('◇'));
        Assert.Equal(layout.ComposerLeft - 1, TerminalTextLayout.Plain(rule).IndexOf('─'));
        Assert.Equal(layout.ComposerLeft + layout.ComposerWidth - 1, TerminalTextLayout.Measure(header));
        Assert.Equal(layout.ComposerLeft + layout.ComposerWidth - 1, TerminalTextLayout.Measure(rule));
        Assert.Equal(layout.ComposerLeft + 1, TerminalTextLayout.Plain(footer).IndexOf("Model", StringComparison.Ordinal));
    }
}
