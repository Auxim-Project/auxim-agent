using System.Globalization;
using System.Text;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class ComposerPointerTests
{
    private static TerminalPointer Point(InputCaretStop stop, ScreenLayout frame, int first, PointerPhase phase) =>
        new(0, frame.ComposerInnerLeft + stop.Column, frame.InputRow + stop.Row - first, 0, phase);

    [Theory]
    [InlineData(80)]
    [InlineData(32)]
    [InlineData(12)]
    public void ClicksMapToGraphemeStopsIncludingTabsWrappingAndVisibleScroll(int width)
    {
        const string text = "A中文🙂é\tZ\r\nsecond line with wrapping\n👨‍👩‍👧‍👦 tail";
        var frame = ScreenLayout.Create(width, 24, inputRows: 8);
        var projection = TerminalTextLayout.LayoutInput(text, 0, frame.InputColumns, ConsoleTheme.ComposerPrompt(frame.ComposerWidth));
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
        foreach (var stop in projection.CaretStops!)
        {
            var draft = new InputDraft(); draft.InsertPaste(text);
            draft.FirstVisibleRow = Math.Clamp(stop.Row, 0, Math.Max(0, projection.Rows.Count - frame.InputRows));
            var gesture = new ComposerPointerSelection();
            Assert.True(gesture.Handle(Point(stop, frame, draft.FirstVisibleRow, PointerPhase.Press), draft, frame, 0));
            Assert.Contains(draft.Cursor, boundaries);
            Assert.Equal(stop.Index, draft.Cursor);
            Assert.Equal(text, draft.Buffer.ToString());
            gesture.Handle(Point(stop, frame, draft.FirstVisibleRow, PointerPhase.Release), draft, frame, 10);
            Assert.False(gesture.IsDragging);
        }
    }

    [Fact]
    public void DragReleaseSelectsSourceRangeAndOutsidePressLeavesDraftUntouched()
    {
        var draft = new InputDraft(); draft.InsertPaste("abcdef");
        var frame = ScreenLayout.Create(80, 24, inputRows: 2);
        var projection = TerminalTextLayout.LayoutInput("abcdef", 0, frame.InputColumns, ConsoleTheme.ComposerPrompt(frame.ComposerWidth));
        var gesture = new ComposerPointerSelection();
        var start = projection.CaretStops!.Single(stop => stop.Index == 1);
        var end = projection.CaretStops!.Single(stop => stop.Index == 5);
        gesture.Handle(Point(start, frame, 0, PointerPhase.Press), draft, frame, 0);
        gesture.Handle(Point(end, frame, 0, PointerPhase.Drag), draft, frame, 10);
        Assert.Equal(1, draft.SelectionStart); Assert.Equal(5, draft.SelectionEnd);
        gesture.Handle(Point(end, frame, 0, PointerPhase.Release), draft, frame, 20);
        Assert.False(gesture.IsDragging);
        Assert.False(gesture.Handle(new(0, 1, 1, 0, PointerPhase.Press), draft, frame, 30));
        Assert.Equal(5, draft.Cursor); Assert.Equal(1, draft.SelectionAnchor);
        draft.InsertText("Q"); Assert.Equal("aQf", draft.Buffer.ToString());
    }

    [Fact]
    public void ReleaseLossTimeoutResizeAndExplicitCancelDoNotResumeAGesture()
    {
        var draft = new InputDraft(); draft.InsertPaste("abcdef");
        var frame = ScreenLayout.Create(80, 24);
        var gesture = new ComposerPointerSelection();
        var press = new TerminalPointer(0, frame.InputStartColumn + 1, frame.InputRow, 0, PointerPhase.Press);
        gesture.Handle(press, draft, frame, 0);
        gesture.Expire(frame, ComposerPointerSelection.IdleTimeoutMilliseconds);
        Assert.False(gesture.IsDragging);
        Assert.False(gesture.Handle(press with { Phase = PointerPhase.Drag }, draft, frame, 2100));
        gesture.Handle(press, draft, frame, 2200);
        gesture.Expire(ScreenLayout.Create(40, 20), 2210);
        Assert.False(gesture.IsDragging);
        gesture.Handle(press, draft, frame, 2300); gesture.Cancel();
        Assert.False(gesture.Handle(press with { Phase = PointerPhase.Release }, draft, frame, 2310));
    }

    [Fact]
    public void DragOutsideVisibleRowsScrollsBoundedlyAndNeverTruncatesSource()
    {
        var text = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"line {i:D2}"));
        var draft = new InputDraft(); draft.InsertPaste(text); draft.FirstVisibleRow = 0;
        var frame = ScreenLayout.Create(80, 24, inputRows: 30);
        var gesture = new ComposerPointerSelection();
        gesture.Handle(new(0, frame.InputStartColumn, frame.InputRow, 0, PointerPhase.Press), draft, frame, 0);
        for (var i = 1; i < 60; i++)
            gesture.Handle(new(0, int.MaxValue, int.MaxValue, 0, PointerPhase.Drag), draft, frame, i * 10);
        Assert.InRange(draft.FirstVisibleRow, 1, 30 - frame.InputRows);
        Assert.Equal(text, draft.Buffer.ToString());
        Assert.InRange(draft.Cursor, 0, text.Length);
    }

    [Theory]
    [InlineData("\u001b[<0;12;7M", 0, 0)]
    [InlineData("\u001b[<32;15;9M", 0, 1)]
    [InlineData("\u001b[<0;15;9m", 0, 2)]
    [InlineData("\u001b[<20;12;7M", 6, 0)]
    public void SgrPointerPhasesAndModifiersSurviveEverySplit(string wire, int modifiers, int phase)
    {
        var bytes = Encoding.UTF8.GetBytes(wire);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var decoder = new TerminalInputDecoder(); decoder.Feed(bytes.AsSpan(0, split));
            decoder.Feed(bytes.AsSpan(split));
            var input = decoder.Take();
            Assert.Equal(TerminalInputEventKind.Pointer, input.Kind);
            Assert.Equal((PointerPhase)phase, input.Pointer!.Phase);
            Assert.Equal((ConsoleModifiers)modifiers, input.Pointer.Modifiers);
            Assert.Equal(0, input.Pointer.Button);
            Assert.False(TerminalInputPolicy.Approval.AcceptsKey(input.Key));
            Assert.False(decoder.HasPartialSequence); Assert.False(decoder.HasEvents);
        }
    }
}
