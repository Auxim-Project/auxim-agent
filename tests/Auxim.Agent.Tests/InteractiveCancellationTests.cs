using System.Text;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class InteractiveCancellationTests
{
    [Fact]
    public void InterruptsAreIdempotentAndDoNotCancelTheNextTurn()
    {
        using var owner = new InteractiveInterrupt();
        owner.Request(); Assert.True(owner.TakeIdleInterrupt()); Assert.False(owner.TakeIdleInterrupt());
        using var turn = new CancellationTokenSource();
        var cancellations = 0;
        using var callback = turn.Token.Register(() => cancellations++);
        owner.BeginTurn(turn);
        Assert.Equal(InteractiveExecutionState.Running, owner.State);
        for (var i = 0; i < 10; i++) owner.Request();
        Assert.Equal(InteractiveExecutionState.Cancelling, owner.State);
        Assert.True(turn.IsCancellationRequested); Assert.Equal(1, cancellations);
        owner.EndTurn(); turn.Dispose(); owner.Request();
        using var next = new CancellationTokenSource(); owner.BeginTurn(next);
        Assert.False(next.IsCancellationRequested); Assert.False(owner.TakeIdleInterrupt());
    }

    [Fact]
    public async Task CancellationDoesNotAbandonAnOperationThatIgnoresItsToken()
    {
        using var owner = new InteractiveInterrupt(); using var turn = new CancellationTokenSource();
        owner.BeginTurn(turn);
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Request();
        var waiting = owner.AwaitTurnAsync(pending.Task, () => announced.SetResult());
        await announced.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(waiting.IsCompleted);
        Assert.Equal(InteractiveExecutionState.Cancelling, owner.State);
        pending.SetResult(7); Assert.Equal(7, await waiting);
        Assert.True(turn.IsCancellationRequested);
    }

    [Fact]
    public async Task ForegroundFailureStillAwaitsTheCancelledOperation()
    {
        using var owner = new InteractiveInterrupt(); using var turn = new CancellationTokenSource();
        owner.BeginTurn(turn); owner.Request();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = owner.AwaitTurnAsync(pending.Task, () => throw new IOException("input failure"));
        Assert.False(waiting.IsCompleted);
        pending.SetResult(0);
        await Assert.ThrowsAsync<IOException>(() => waiting);
        Assert.True(turn.IsCancellationRequested);
    }

    [Theory]
    [InlineData("\u001b[13;2u")]
    [InlineData("\u001b[27;2;13~")]
    public void ExplicitShiftEnterIsOneNonApprovalEventAcrossEverySplit(string wire)
    {
        var bytes = Encoding.UTF8.GetBytes(wire);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var decoder = new TerminalInputDecoder(); decoder.Feed(bytes.AsSpan(0, split)); decoder.Feed(bytes.AsSpan(split));
            Assert.Equal(TerminalInputEventKind.LineBreak, decoder.Take().Kind);
            Assert.False(decoder.HasEvents); Assert.False(decoder.HasPartialSequence);
        }
    }

    [Fact]
    public void CrLfAndUnsupportedEnhancedKeysRemainDistinct()
    {
        var decoder = new TerminalInputDecoder(); decoder.Feed("\r\n\u001b[1114112;5u"u8);
        var cr = decoder.Take(); var lf = decoder.Take();
        Assert.Equal(ConsoleKey.Enter, cr.Key.Key); Assert.Equal((ConsoleModifiers)0, cr.Key.Modifiers);
        Assert.Equal('\r', cr.Key.KeyChar); Assert.Equal('\n', lf.Key.KeyChar);
        Assert.Equal(ConsoleModifiers.Control, lf.Key.Modifiers);
        Assert.Equal(TerminalInputEventKind.ControlSequence, decoder.Take().Kind);
        Assert.False(decoder.HasEvents);
    }

    [Fact]
    public async Task CancelledPreviewRetainsTextWithoutAddingItToSavedHistory()
    {
        var viewport = new TranscriptViewport();
        var saved = new[] { "saved history" };
        var transcript = new StreamingTranscript(() => saved, "question", viewport);
        await using var preview = new StreamingPreview(new StreamingPreviewRenderer(new StringWriter(),
            new StringWriter(), PreviewOutputMode.Redirected, () => (80, 24), transcript), _ => { });
        // Buffer a delta so cancellation must flush before taking the snapshot.
        await preview.PublishAsync(new Auxim.Core.Runtime.RuntimeContentDeltaEvent("e", DateTimeOffset.Now,
            Auxim.Core.Runtime.AuximRunId.New(), "partial 中文🙂"), default);
        await preview.FinishAsync("cancelled");
        Assert.Contains(preview.IncompleteLines, line => line.Contains("partial 中文🙂"));
        Assert.Equal(new[] { "saved history" }, saved);
        Assert.Empty(transcript);
    }
}
