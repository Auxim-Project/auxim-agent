using System.Globalization;
using System.Text.RegularExpressions;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class ScreenLayoutTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(32, 10)]
    [InlineData(4, 4)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(1, 3)]
    public void RegionsNeverOverlapOrLeaveTheWindow(int width, int height)
    {
        var layout = ScreenLayout.Create(width, height);
        Assert.InRange(layout.InputRow, 1, height);
        Assert.Equal(width - 1, layout.Columns);
        var occupied = Enumerable.Range(layout.ComposerTop, layout.ComposerHeight).ToList();
        occupied.AddRange(Enumerable.Range(1, layout.HeaderHeight));
        occupied.AddRange(Enumerable.Range(layout.ComposerTop - layout.GapHeight, layout.GapHeight));
        occupied.AddRange(Enumerable.Range(layout.ContentTop, layout.ContentHeight));
        Assert.Equal(height, occupied.Count);
        Assert.Equal(height, occupied.Distinct().Count());
        Assert.All(occupied, row => Assert.InRange(row, 1, height));
    }

    [Theory]
    [InlineData("plain long draft abcdefghijklmnopqrstuvwxyz", 8)]
    [InlineData("中文输入🙂👨‍👩‍👧‍👦🇺🇸e\u0301结束", 12)]
    [InlineData("中文🙂", 1)]
    [InlineData("中文🙂", 0)]
    [InlineData("line one\nline two\tend", 10)]
    public void HorizontalInputKeepsCursorVisibleWithoutMutatingDraft(string text, int columns)
    {
        var original = text;
        foreach (var cursor in StringInfo.ParseCombiningCharacters(text).Append(text.Length))
        {
            var row = InputRow.Create("auxim > ", text, cursor, columns);
            var cells = Elements(row.Text).Sum(TerminalTextLayout.CellWidth);
            Assert.True(cells <= columns);
            Assert.InRange(row.CursorColumn, 0, Math.Max(0, columns - 1));
            Assert.DoesNotContain('\n', row.Text);
            Assert.DoesNotContain('\t', row.Text);
        }
        Assert.Equal(original, text);
        var wide = InputRow.Create("auxim > ", text, text.Length, 200);
        Assert.Equal("auxim > " + text.Replace('\n', ' ').Replace('\t', ' '), wide.Text);
    }

    [Fact]
    public void InputPaintOnlyErasesItsAssignedRow()
    {
        var layout = ScreenLayout.Create(20, 8);
        using var output = new StringWriter();
        InputRow.Create("auxim > ", "很长的输入🙂 test", 9, layout.InputColumns).Write(output, layout);
        var frame = output.ToString();
        Assert.DoesNotContain("\u001b[0J", frame);
        Assert.DoesNotContain("\u001b[2J", frame);
        Assert.Contains($"\u001b[{layout.InputRow};{layout.ComposerLeft}H", frame);
        Assert.DoesNotContain("\u001b[2K", frame);
        foreach (Match match in Regex.Matches(frame, "\u001b\\[(\\d+);(\\d+)H"))
        {
            Assert.Equal(layout.InputRow, int.Parse(match.Groups[1].Value));
            Assert.InRange(int.Parse(match.Groups[2].Value), 1, layout.Width);
        }
    }
    private static IEnumerable<string> Elements(string text) =>
        StringInfo.ParseCombiningCharacters(text).Select(i => StringInfo.GetNextTextElement(text, i));
}
