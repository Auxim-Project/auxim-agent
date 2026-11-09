using System.Globalization;
using System.Text;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class MultilineInputTests
{
    [Theory]
    [InlineData("abcdefg", 7, 10, "> abcdefg", 0, 9)]
    [InlineData("abcdefgh", 8, 10, "> abcdefg|  h", 1, 3)]
    [InlineData("abcdefgh", 7, 10, "> abcdefg|  h", 0, 9)]
    [InlineData("ab\ncd\n", 6, 10, "> ab|  cd|  ", 2, 2)]
    [InlineData("ab\r\ncd", 4, 10, "> ab|  cd", 1, 2)]
    [InlineData("中文🙂e\u0301", 6, 7, "> 中文|  🙂e\u0301", 1, 5)]
    [InlineData("a\tb", 3, 10, "> a   b", 0, 7)]
    [InlineData("abcdef\tz", 8, 10, "> abcdef|      z", 1, 7)]
    public void ProjectionMapsSourceIndicesIntoGraphemeRows(string text, int cursor, int columns, string rows, int row, int column)
    {
        var projection = TerminalTextLayout.LayoutInput(text, cursor, columns, "> ");
        Assert.Equal(rows, string.Join('|', projection.Rows.Select(r => r.Text)));
        Assert.Equal(row, projection.CursorRow);
        Assert.Equal(column, projection.CursorColumn);
        Assert.All(projection.Rows, r => Assert.True(ConsoleTheme.CellWidth(r.Text) < columns));
    }

    [Theory]
    [InlineData("中文你好🙂👨‍👩‍👧‍👦🇺🇸e\u0301", 8)]
    [InlineData("👍🏽❤️1️⃣", 5)]
    [InlineData("longcontinuouswordwithoutbreaks", 12)]
    public void SourceTextAndGraphemeBoundariesSurviveEveryCursorPosition(string text, int columns)
    {
        foreach (var cursor in StringInfo.ParseCombiningCharacters(text).Append(text.Length))
        {
            var projection = TerminalTextLayout.LayoutInput(text, cursor, columns, "> ");
            Assert.InRange(projection.CursorRow, 0, projection.Rows.Count - 1);
            Assert.InRange(projection.CursorColumn, 0, columns - 1);
            Assert.Equal(text, string.Concat(projection.Rows.Select(r => text[r.StartUtf16..r.EndUtf16])));
            Assert.DoesNotContain(projection.Rows, r => r.Text.Contains('�'));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TinyWidthsDoNotLoseTheDraft(int columns)
    {
        var text = "中文🙂very long draft";
        var projection = TerminalTextLayout.LayoutInput(text, text.Length, columns, "  › ");
        Assert.InRange(projection.CursorColumn, 0, Math.Max(0, columns - 1));
        Assert.All(projection.Rows, r => Assert.True(ConsoleTheme.CellWidth(r.Text) <= columns));
        Assert.Equal(text, text[projection.Rows[0].StartUtf16..projection.Rows[^1].EndUtf16]);
    }

    [Theory]
    [InlineData(80, 24, true, 4)]
    [InlineData(120, 30, true, 4)]
    [InlineData(160, 40, false, 4)]
    [InlineData(32, 20, true, 5)]
    [InlineData(80, 14, true, 2)]
    [InlineData(1, 1, true, 1)]
    public void DynamicComposerCapsHeightAndKeepsAllRegionsSeparate(int width, int height, bool dashboard, int visible)
    {
        var layout = ScreenLayout.Create(width, height, dashboard, 1000);
        Assert.Equal(visible, layout.InputRows);
        Assert.InRange(layout.ComposerHeight, 1, 8);
        Assert.Equal(layout.BaseComposerHeight + visible - 1, layout.ComposerHeight);
        Assert.Equal(layout, ScreenLayout.Create(width, height, dashboard, 1001));
        Assert.True(layout.InputRow + layout.InputRows - 1 <= layout.ComposerTop + layout.ComposerHeight - 1);
        if (layout.HintHeight > 0) Assert.True(layout.HintRow >= layout.InputRow + layout.InputRows);
        if (dashboard && layout.IsWelcomeLayout)
        {
            Assert.True(layout.WelcomeRow + 1 < layout.ComposerTop);
            Assert.True(layout.ShortcutsRow > layout.ComposerTop + layout.ComposerHeight - 1);
            Assert.True(layout.MetadataRow > layout.ShortcutsRow);
        }
        else Assert.True(layout.ContentTop + layout.ContentHeight <= layout.ComposerTop - layout.GapHeight);
    }

    [Fact]
    public void InternalScrollFollowsCaretInBothDirectionsWithoutDiscardingRows()
    {
        var text = new string('x', 600);
        var end = TerminalTextLayout.LayoutInput(text, text.Length, 71, "  › ");
        Assert.True(end.Rows.Count > 4);
        var first = end.FollowCursor(0, 4);
        Assert.Equal(end.Rows.Count - 4, first);
        var home = TerminalTextLayout.LayoutInput(text, 0, 71, "  › ");
        Assert.Equal(0, home.FollowCursor(first, 4));
        var layout = ScreenLayout.Create(80, 24, true, end.Rows.Count);
        using var output = new StringWriter();
        end.Write(output, layout, first, false);
        Assert.EndsWith($"\u001b[{layout.InputRow + end.CursorRow - first};{layout.ComposerInnerLeft + end.CursorColumn}H", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[2J", output.ToString());
        Assert.DoesNotContain("\u001b[0J", output.ToString());
        Assert.Equal(600, end.Rows.Sum(r => r.EndUtf16 - r.StartUtf16));
    }

    [Fact]
    public void HistoryRoundTripRestoresCompleteDraftCursorAndInternalOffset()
    {
        var draft = new InputDraft();
        string[] history = ["old one", "old\n中文 👨‍👩‍👧‍👦"];
        draft.Reset(history.Length);
        draft.Buffer.Append(new string('a', 500)); draft.Cursor = 123; draft.FirstVisibleRow = 3;
        Assert.True(draft.Previous(history));
        Assert.Equal(history[1], draft.Buffer.ToString());
        Assert.True(draft.Previous(history));
        draft.Buffer.Append(" changed");
        Assert.True(draft.Next(history));
        Assert.True(draft.Next(history));
        Assert.Equal(new string('a', 500), draft.Buffer.ToString());
        Assert.Equal(123, draft.Cursor);
        Assert.Equal(3, draft.FirstVisibleRow);
        Assert.False(draft.Next(history));
        Assert.Equal("old one", history[0]);
    }

    [Fact]
    public void EscHistoryAppendDoesNotMakeTheDraftLookLikeAnOldHistoryEntry()
    {
        var history = new List<string> { "old" };
        var draft = new InputDraft(); draft.Reset(history.Count);
        draft.Buffer.Append("unsent\n中文 draft"); draft.Cursor = 4; draft.FirstVisibleRow = 1;
        history.Add("/resume"); draft.ResumeHistory(history);
        Assert.True(draft.Previous(history));
        Assert.True(draft.Next(history));
        Assert.Equal("unsent\n中文 draft", draft.Buffer.ToString());
        Assert.Equal(4, draft.Cursor); Assert.Equal(1, draft.FirstVisibleRow);
    }

    [Fact]
    public void EscDuringHistoryBrowsingKeepsTheOriginalDraftSnapshot()
    {
        var history = new List<string> { "old", "selected" };
        var draft = new InputDraft(); draft.Reset(history.Count);
        draft.Buffer.Append("original draft"); draft.Cursor = 5;
        draft.Previous(history);
        history.RemoveAt(0); history.Add("/resume"); draft.ResumeHistory(history);
        Assert.Equal(0, draft.HistoryIndex);
        draft.Next(history); draft.Next(history);
        Assert.Equal("original draft", draft.Buffer.ToString());
        Assert.Equal(5, draft.Cursor);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    [InlineData(-1, 0)]
    [InlineData(100, 4)]
    public void DraftCursorCannotStayInsideACombiningSequenceOrSurrogatePair(int cursor, int expected)
    {
        var draft = new InputDraft();
        draft.Buffer.Append("e\u0301🙂"); draft.Cursor = cursor;
        draft.NormalizeCursor();
        Assert.Equal(expected, draft.Cursor);
        Assert.Equal("e\u0301🙂", draft.Buffer.ToString());
    }

    [Fact]
    public void CompletionReplacesOnlyTheCommandAndKeepsLongMultilineTail()
    {
        var tail = " " + new string('x', 400) + "\n中文 👨‍👩‍👧‍👦";
        var buffer = new StringBuilder("/he" + tail);
        var cursor = 3;
        LineEditor.ReplaceCompletion(buffer, ref cursor, 3, "/help");
        Assert.Equal("/help" + tail, buffer.ToString());
        Assert.Equal(5, cursor);
    }

    [Fact]
    public void StreamRendererNoticesContentGeometryChangesWithoutAResizeOrDelta()
    {
        var layout = ScreenLayout.Create(80, 24);
        using var output = new StringWriter();
        var viewport = new TranscriptViewport();
        var transcript = new StreamingTranscript(() => [], "question", viewport);
        var renderer = new StreamingPreviewRenderer(output, output, PreviewOutputMode.Alternate, () => (80, 24), transcript, () => layout);
        renderer.Append(string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i}")));
        layout = ScreenLayout.Create(80, 24, inputRows: 4);
        output.GetStringBuilder().Clear(); renderer.Refresh();
        Assert.Equal(viewport.TotalLines - layout.ContentHeight, viewport.Offset);
        Assert.EndsWith($"\u001b[{layout.InputRow};{layout.InputStartColumn}H", output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear(); renderer.Refresh(); Assert.Empty(output.ToString());
        renderer.Finish(null);
        foreach (System.Text.RegularExpressions.Match erase in System.Text.RegularExpressions.Regex.Matches(output.ToString(), "\u001b\\[(\\d+);1H\u001b\\[2K"))
            Assert.InRange(int.Parse(erase.Groups[1].Value), layout.ContentTop, layout.ContentTop + layout.ContentHeight - 1);
        Assert.EndsWith($"\u001b[{layout.InputRow};{layout.InputStartColumn}H", output.ToString(), StringComparison.Ordinal);
    }
}
