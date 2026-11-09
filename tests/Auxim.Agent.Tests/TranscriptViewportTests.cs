using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class TranscriptViewportTests
{
    private static readonly string[] History = ["one", "two", "three", "four", "five"];

    [Theory]
    [InlineData(false, 0, "one", "two")]
    [InlineData(true, 3, "four", "five")]
    public void OpeningHistorySelectsTheRequestedEnd(
        bool startAtBottom, int expectedOffset, string first, string second)
    {
        var viewport = new TranscriptViewport();

        viewport.Open(History, startAtBottom, pageHeight: 2);

        Assert.True(viewport.IsActive);
        Assert.Equal(expectedOffset, viewport.Offset);
        Assert.Equal([first, second], viewport.GetVisibleLines(2));
    }

    [Fact]
    public void ClampedScrollingReportsNoChangeAtEitherBoundary()
    {
        var viewport = new TranscriptViewport();
        viewport.Open(History, false, 2);
        Assert.False(viewport.ScrollClamped(int.MinValue, 2));
        Assert.True(viewport.ScrollClamped(int.MaxValue, 2));
        Assert.Equal(3, viewport.Offset);
        Assert.False(viewport.ScrollClamped(int.MaxValue, 2));
        Assert.True(viewport.ScrollClamped(-1, 2));
        Assert.Equal(2, viewport.Offset);
    }

    [Fact]
    public void ScrollingStopsAtBothEndsAndCanReverseDirection()
    {
        var viewport = new TranscriptViewport();
        viewport.Open(History, startAtBottom: false, pageHeight: 2);

        viewport.Scroll(-10);
        Assert.Equal(["one", "two"], viewport.GetVisibleLines(2));
        Assert.Equal(0, viewport.Offset);

        viewport.Scroll(10);
        Assert.Equal(["four", "five"], viewport.GetVisibleLines(2));
        Assert.Equal(3, viewport.Offset);

        viewport.Scroll(-1);
        Assert.Equal(["three", "four"], viewport.GetVisibleLines(2));
        Assert.Equal(2, viewport.Offset);
    }

    [Fact]
    public void ResizingClampsThePositionWithoutJumpingBackWhenShrinking()
    {
        var viewport = new TranscriptViewport();
        viewport.Open(History, startAtBottom: true, pageHeight: 2);

        Assert.Equal(["two", "three", "four", "five"], viewport.GetVisibleLines(4));
        Assert.Equal(1, viewport.Offset);
        Assert.Equal(["two", "three"], viewport.GetVisibleLines(2));
        Assert.Equal(1, viewport.Offset);
    }

    [Fact]
    public void ReturningToDashboardDiscardsHistoryStateBeforeReopening()
    {
        var viewport = new TranscriptViewport();
        viewport.Open(History, startAtBottom: true, pageHeight: 2);

        viewport.Reset();

        Assert.False(viewport.IsActive);
        Assert.Empty(viewport.Lines);
        Assert.Equal(0, viewport.Offset);

        viewport.Open(["new session"], startAtBottom: false, pageHeight: 2);
        Assert.True(viewport.IsActive);
        Assert.Equal(["new session"], viewport.GetVisibleLines(2));
        Assert.Equal(0, viewport.Offset);
    }

    [Fact]
    public void SwitchingHistoryViewsReplacesThePreviousContentAndPosition()
    {
        var viewport = new TranscriptViewport();
        viewport.Open(History, startAtBottom: true, pageHeight: 2);

        viewport.Open(["selected turn"], startAtBottom: false, pageHeight: 2);

        Assert.Equal(["selected turn"], viewport.GetVisibleLines(2));
        Assert.Equal(0, viewport.Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedirectedOutputShowsAllLinesAndClearsTheScrollOffset(bool startAtBottom)
    {
        var viewport = new TranscriptViewport();
        viewport.Open(History, startAtBottom, pageHeight: 2);
        viewport.Scroll(10);

        Assert.Equal(History, viewport.GetVisibleLines(int.MaxValue));
        Assert.Equal(0, viewport.Offset);
    }
}
