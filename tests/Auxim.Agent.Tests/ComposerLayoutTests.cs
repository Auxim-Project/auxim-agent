using System.Text.RegularExpressions;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class ComposerLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandardComposerHasBorderedInputAndSeparateHintLayer(bool unicode)
    {
        using var glyphs = TerminalGlyphs.UseUnicode(unicode);
        var layout = ScreenLayout.Create(80, 24);
        Assert.Equal(5, layout.ComposerHeight);
        Assert.Equal(20, layout.ComposerTop);
        Assert.Equal(21, layout.InputRow);
        Assert.Equal(23, layout.HintRow);
        Assert.Equal(16, layout.ContentHeight);
        Assert.Equal(1, layout.GapHeight);
        Assert.Equal(2, layout.InputRightPadding);
        Assert.Equal(71, layout.InputColumns);
        Assert.Equal(75, layout.ComposerWidth);
        Assert.Equal(3, layout.ComposerLeft);
        using var writer = new StringWriter();
        ConsoleTheme.WriteComposer(writer, layout, false, ConsoleTheme.IdleHint);
        var frame = TerminalFrame.WithoutStyles(writer.ToString());
        Assert.Contains("Message Auxim...", frame);
        Assert.Contains("/ Commands", frame);
        Assert.Contains("Enter Send", frame);
        var line = new string(unicode ? '─' : '-', 73);
        Assert.Contains(unicode ? $"\u001b[20;3H╭{line}╮" : $"\u001b[20;3H+{line}+", frame);
        Assert.Contains(unicode ? $"\u001b[24;3H╰{line}╯" : $"\u001b[24;3H+{line}+", frame);
        Assert.Contains(unicode ? $"\u001b[22;3H│{line}│" : $"\u001b[22;3H|{line}|", frame);
        Assert.EndsWith("\u001b[21;8H", frame, StringComparison.Ordinal);
        AssertAllErasesInsideComposer(frame, layout);
    }

    [Theory]
    [InlineData(80, 24, 5)]
    [InlineData(32, 10, 4)]
    [InlineData(16, 10, 3)]
    [InlineData(80, 8, 3)]
    [InlineData(8, 8, 2)]
    [InlineData(80, 4, 2)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    public void CompactLayoutsDoNotOverlapInputHintsOrContent(int width, int height, int composerHeight)
    {
        var layout = ScreenLayout.Create(width, height);
        Assert.Equal(composerHeight, layout.ComposerHeight);
        Assert.True(layout.ContentTop + layout.ContentHeight <= layout.ComposerTop - layout.GapHeight);
        Assert.InRange(layout.InputRow, layout.ComposerTop, height);
        if (layout.HintHeight > 0)
        {
            Assert.True(layout.InputRow < layout.HintRow);
            Assert.InRange(layout.HintRow, layout.ComposerTop, height);
        }
        using var writer = new StringWriter();
        ConsoleTheme.WriteComposer(writer, layout, false, ConsoleTheme.IdleHint);
        AssertAllErasesInsideComposer(writer.ToString(), layout);
    }

    [Theory]
    [InlineData(false, "  > ")]
    [InlineData(true, "  › ")]
    public void PlaceholderNeverBecomesDraftAndCursorStaysAtInsertionPoint(bool unicode, string expectedPrompt)
    {
        using var glyphs = TerminalGlyphs.UseUnicode(unicode);
        var layout = ScreenLayout.Create(80, 24);
        var row = InputRow.Create(ConsoleTheme.ComposerPrompt(layout.ComposerWidth), "", 0, layout.InputColumns);
        var original = row.Text;
        using var writer = new StringWriter();
        row.Write(writer, layout, placeholder: true);
        Assert.Contains(expectedPrompt + " Message Auxim...", TerminalFrame.WithoutStyles(writer.ToString()));
        Assert.Equal(original, row.Text);
        Assert.DoesNotContain("Message", row.Text);
        Assert.Equal(4, row.CursorColumn);
        Assert.EndsWith("\u001b[21;8H", writer.ToString(), StringComparison.Ordinal);
        writer.GetStringBuilder().Clear();
        InputRow.Create(ConsoleTheme.ComposerPrompt(layout.ComposerWidth), "Test", 4, layout.InputColumns).Write(writer, layout);
        Assert.Contains(expectedPrompt + "Test", TerminalFrame.WithoutStyles(writer.ToString()));
        Assert.DoesNotContain("Message Auxim", writer.ToString());
        Assert.EndsWith("\u001b[21;12H", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BusyComposerKeepsSameGeometryWithoutPlaceholder()
    {
        var layout = ScreenLayout.Create(80, 24);
        using var writer = new StringWriter();
        ConsoleTheme.WriteComposer(writer, layout, true, ConsoleTheme.RunningHint);
        Assert.Contains("Working...", writer.ToString());
        Assert.Contains("Ctrl+C cancel", writer.ToString());
        Assert.DoesNotContain("Message Auxim", writer.ToString());
        Assert.EndsWith("\u001b[21;8H", writer.ToString(), StringComparison.Ordinal);
        AssertAllErasesInsideComposer(writer.ToString(), layout);
    }

    [Theory]
    [InlineData("中文输入", 24)]
    [InlineData("🙂👨‍👩‍👧‍👦🇺🇸e\u0301", 16)]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz", 24)]
    public void LongInputReservesRightPaddingAndRecoversAfterResize(string text, int width)
    {
        var draft = string.Concat(Enumerable.Repeat(text, 10));
        var layout = ScreenLayout.Create(width, 24);
        var row = InputRow.Create(ConsoleTheme.ComposerPrompt(layout.ComposerWidth), draft, draft.Length, layout.InputColumns);
        Assert.True(TerminalTextLayout.Measure(row.Text) <= layout.InputColumns);
        Assert.StartsWith("  ", row.Text, StringComparison.Ordinal);
        Assert.InRange(row.CursorColumn, 4, layout.InputColumns - 1);
        using var writer = new StringWriter();
        row.Write(writer, layout);
        AssertAllErasesInsideComposer(writer.ToString(), layout);
        var recovered = InputRow.Create(ConsoleTheme.ComposerPrompt(2000), draft, draft.Length, 2000);
        Assert.EndsWith(draft, recovered.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, '|')]
    [InlineData(true, '│')]
    public void SuggestionsAndStatusOnlyPaintTheInternalHintRow(bool unicode, char border)
    {
        using var glyphs = TerminalGlyphs.UseUnicode(unicode);
        var layout = ScreenLayout.Create(80, 24);
        using var writer = new StringWriter();
        ConsoleTheme.WriteComposerHint(writer, layout, "/help  /history  /model");
        Assert.Contains($"\u001b[23;3H{border}  /help", TerminalFrame.WithoutStyles(writer.ToString()));
        Assert.Single(Regex.Matches(writer.ToString(), "\\[23;3H"));
        AssertAllErasesInsideComposer(writer.ToString(), layout);
    }

    private static void AssertAllErasesInsideComposer(string frame, ScreenLayout layout)
    {
        Assert.DoesNotContain("\u001b[0J", frame);
        Assert.DoesNotContain("\u001b[2J", frame);
        foreach (Match match in Regex.Matches(frame, "\u001b\\[(\\d+);(\\d+)H"))
        {
            Assert.InRange(int.Parse(match.Groups[1].Value), layout.ComposerTop, layout.Height);
            Assert.InRange(int.Parse(match.Groups[2].Value), layout.ComposerLeft,
                layout.ComposerLeft + Math.Max(0, layout.ComposerWidth - 1));
        }
    }
}
