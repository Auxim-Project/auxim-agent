using System.Globalization;
using System.Text;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class VerticalCursorTests
{
    private static InputProjection Project(InputDraft draft, int width = 80) =>
        TerminalTextLayout.LayoutInput(draft.Buffer.ToString(), draft.Cursor, width, "> ", draft.ForwardAffinity);

    [Fact]
    public void LongShortLongKeepsPreferredCellColumnInBothDirections()
    {
        var draft = new InputDraft(); draft.Buffer.Append("abcdef\nx\n123456"); draft.Cursor = 5;
        Assert.True(draft.MoveVertical(Project(draft), 1));
        Assert.Equal(8, draft.Cursor); Assert.Equal(7, draft.PreferredColumn);
        Assert.Equal(3, Project(draft).CursorColumn);
        Assert.True(draft.MoveVertical(Project(draft), 1));
        Assert.Equal(14, draft.Cursor); Assert.Equal(7, Project(draft).CursorColumn);
        Assert.True(draft.MoveVertical(Project(draft), -1));
        Assert.True(draft.MoveVertical(Project(draft), -1));
        Assert.Equal(5, draft.Cursor);
    }

    [Theory]
    [InlineData("中文🙂é\n\tX\n123456789")]
    [InlineData("👨‍👩‍👧‍👦🇺🇸👍🏽\r\nx\r\n中文text")]
    [InlineData("a\tb\n短\nlong again")]
    [InlineData("abcdefghi0123456789中文🙂éabcdefghi")]
    public void AllVerticalTargetsAreActualGraphemeStopsAndRoundTripThroughProjection(string text)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
        foreach (var width in new[] { 3, 7, 12, 32, 80, 120, 160 })
        foreach (var index in boundaries)
        {
            var draft = new InputDraft(); draft.Buffer.Append(text); draft.Cursor = index;
            for (var i = 0; i < 30; i++)
            {
                var before = Project(draft, width);
                if (!draft.MoveVertical(before, 1)) break;
                var after = Project(draft, width);
                Assert.Equal(before.CursorRow + 1, after.CursorRow);
                Assert.Contains(draft.Cursor, boundaries);
                Assert.InRange(after.CursorColumn, 0, width - 1);
            }
        }
    }

    [Fact]
    public void SoftWrapHasTwoCaretPositionsAtTheSameSourceIndex()
    {
        const string text = "abcdefghijklmno";
        var draft = new InputDraft(); draft.Buffer.Append(text);
        Assert.True(draft.MoveVertical(Project(draft, 10), 1));
        Assert.Equal(7, draft.Cursor); Assert.True(draft.ForwardAffinity);
        Assert.Equal(1, Project(draft, 10).CursorRow);
        Assert.Equal(2, Project(draft, 10).CursorColumn);
        draft.ResetPreferredColumn();
        Assert.Equal(0, Project(draft, 10).CursorRow);
        Assert.Equal(9, Project(draft, 10).CursorColumn);
    }

    [Fact]
    public void InternalScrollFollowsVerticalCaretAndSnapshotsKeepItsPreference()
    {
        var draft = new InputDraft(); draft.Reset(1);
        draft.InsertPaste(string.Join('\n', Enumerable.Range(0, 20).Select(i => $"line {i:D2} 中文🙂")));
        draft.Cursor = 4;
        for (var i = 0; i < 15; i++)
        {
            Assert.True(draft.MoveVertical(Project(draft), 1));
            draft.FirstVisibleRow = Project(draft).FollowCursor(draft.FirstVisibleRow, 4);
        }
        Assert.Equal(12, draft.FirstVisibleRow);
        var saved = draft.Copy();
        Assert.True(draft.Previous(["history"])); Assert.Null(draft.PreferredColumn);
        Assert.True(draft.Next(["history"]));
        Assert.Equal(saved.PreferredColumn, draft.PreferredColumn);
        Assert.Equal(saved.Cursor, draft.Cursor);
        Assert.Equal(saved.FirstVisibleRow, draft.FirstVisibleRow);
        draft.InsertPaste("x"); Assert.Null(draft.PreferredColumn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(80)]
    public void SingleLineOrTinyComposerNeverNavigatesOutsideInput(int width)
    {
        var draft = new InputDraft(); draft.Buffer.Append("a");
        Assert.False(draft.MoveVertical(Project(draft, width), -1));
        Assert.False(draft.MoveVertical(Project(draft, width), 1));
        Assert.Equal(0, draft.Cursor);
    }

    [Fact]
    public void WheelAndPhysicalArrowsRemainDifferentAtEveryFragmentBoundary()
    {
        var bytes = Encoding.UTF8.GetBytes("\u001b[A\u001b[<64;12;7M\u001b[B\u001b[<65;12;7M");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var decoder = new TerminalInputDecoder(); var events = new List<TerminalInputEvent>();
            decoder.Feed(bytes.AsSpan(0, split));
            while (decoder.HasEvents) events.Add(decoder.Take());
            decoder.Feed(bytes.AsSpan(split));
            while (decoder.HasEvents) events.Add(decoder.Take());
            Assert.Equal(new[] { TerminalInputEventKind.Key, TerminalInputEventKind.MouseWheel,
                TerminalInputEventKind.Key, TerminalInputEventKind.MouseWheel }, events.Select(e => e.Kind));
            Assert.Equal(ConsoleKey.UpArrow, events[0].Key.Key);
            Assert.Equal(-1, events[1].ScrollDelta);
            Assert.Equal(ConsoleKey.DownArrow, events[2].Key.Key);
            Assert.Equal(1, events[3].ScrollDelta);
        }
    }

    [Theory]
    [InlineData("\u001b[<64;0;1M")]
    [InlineData("\u001b[<65;1;1m")]
    [InlineData("\u001b[<999999999999;1;1M")]
    public void OtherMouseReportsCannotBecomeKeyboardOrWheelInput(string wire)
    {
        var decoder = new TerminalInputDecoder(); decoder.Feed(Encoding.UTF8.GetBytes(wire));
        Assert.Equal(TerminalInputEventKind.ControlSequence, decoder.Take().Kind);
        Assert.False(decoder.HasEvents);
    }

    [Fact]
    public void LegacyMousePayloadIsQuarantinedAcrossEverySplitAndOwnershipChange()
    {
        var bytes = "\u001b[M 2\r"u8.ToArray();
        for (var split = 0; split <= bytes.Length; split++)
        {
            var decoder = new TerminalInputDecoder();
            decoder.Feed(bytes.AsSpan(0, split));
            while (decoder.HasEvents) Assert.Equal(TerminalInputEventKind.ControlSequence, decoder.Take().Kind);
            decoder.DiscardEvents();
            decoder.Feed(bytes.AsSpan(split));
            while (decoder.HasEvents) Assert.Equal(TerminalInputEventKind.ControlSequence, decoder.Take().Kind);
            Assert.False(decoder.HasPartialSequence);
        }
    }
}
