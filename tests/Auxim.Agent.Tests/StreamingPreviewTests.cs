using System.Text;
using Auxim.Cli.Interactive;
using Auxim.Core.Approval;
using Auxim.Core.Runtime;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class StreamingPreviewTests
{
    private static readonly AuximRunId RunId = new("preview-test");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstDeltaIsVisibleBeforeTheRunFinishes(bool terminal)
    {
        using var output = new ObservedWriter();
        using var diagnostics = new ObservedWriter();
        var renderer = CreateRenderer(output, diagnostics, terminal, () => 80);
        await using var preview = new StreamingPreview(renderer, _ => { });

        await preview.PublishAsync(Delta("first delta"), default);
        await (terminal ? output : diagnostics).WaitForTextAsync("first delta");
        if (!terminal)
        {
            Assert.Empty(output.Snapshot);
        }

        await preview.FinishAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToolBoundariesFlushTextAndStartANewPreviewSegment(bool terminal)
    {
        using var output = new ObservedWriter();
        var renderer = CreateRenderer(output, output, terminal, () => 80);
        await using var preview = new StreamingPreview(renderer, _ => output.Write("<tool>"));

        await preview.PublishAsync(Delta("before tool"), default);
        await preview.PublishAsync(new RuntimeToolStartedEvent("tool", DateTimeOffset.UtcNow, RunId, "call", "echo", []), default);
        await preview.PublishAsync(Delta("after tool"), default);
        await preview.FinishAsync();

        var text = output.Snapshot;
        Assert.True(text.IndexOf("before tool", StringComparison.Ordinal) < text.IndexOf("<tool>", StringComparison.Ordinal));
        Assert.True(text.IndexOf("<tool>", StringComparison.Ordinal) < text.IndexOf("after tool", StringComparison.Ordinal));
        Assert.DoesNotContain("before toolafter tool", text);
        Assert.Equal(1, preview.ToolEventCount);
    }

    [Fact]
    public async Task DeltaProducerDoesNotWaitForABlockedTerminalWrite()
    {
        using var output = new BlockedWriter();
        var renderer = CreateRenderer(output, output, true, () => 80);
        await using var preview = new StreamingPreview(renderer, _ => { });
        await preview.PublishAsync(Delta("first"), default);
        try
        {
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 1000; i++)
            {
                Assert.True(preview.PublishAsync(Delta("x"), default).IsCompletedSuccessfully);
            }
        }
        finally
        {
            output.Release.Set();
        }

        await preview.FinishAsync();
    }

    [Fact]
    public async Task FrequentDeltasAreCombinedWithoutRepaintingHistory()
    {
        using var output = new ObservedWriter();
        var renderer = CreateRenderer(output, output, false, () => 80);
        await using var preview = new StreamingPreview(renderer, _ => { }, TimeSpan.FromHours(1));
        for (var i = 0; i < 1000; i++)
        {
            await preview.PublishAsync(Delta("x"), default);
        }

        await preview.FinishAsync();

        Assert.Equal("[preview] " + new string('x', 1000) + Environment.NewLine, output.Snapshot);
        Assert.Equal(3, output.Writes); // label, one combined body, newline
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task IncompletePreviewIsMarkedAndCannotLeakIntoTheNextRun(string status)
    {
        using var output = new ObservedWriter();
        var renderer = CreateRenderer(output, output, false, () => 80);
        await using (var preview = new StreamingPreview(renderer, _ => { }))
        {
            await preview.PublishAsync(Delta("partial"), default);
            await preview.FinishAsync(status);
            await preview.PublishAsync(Delta("late stale text"), default);
        }

        await using (var next = new StreamingPreview(renderer, _ => { }))
        {
            await next.PublishAsync(Delta("fresh"), default);
            await next.FinishAsync();
        }

        Assert.Equal($"[preview] partial{Environment.NewLine}[preview {status}; not saved]{Environment.NewLine}[preview] fresh{Environment.NewLine}", output.Snapshot);
    }

    [Fact]
    public async Task EmptyOrAbsentDeltasDoNotChangeNonStreamingOutput()
    {
        using var output = new ObservedWriter();
        var renderer = CreateRenderer(output, output, false, () => 80);
        await using var preview = new StreamingPreview(renderer, _ => { });
        await preview.PublishAsync(Delta(""), default);
        await preview.FinishAsync();
        Assert.Empty(output.Snapshot);
    }

    [Fact]
    public async Task MarkdownFragmentsStayPlainAndRedirectedPreviewHasNoAnsi()
    {
        using var output = new ObservedWriter();
        var renderer = CreateRenderer(output, output, false, () => 80);
        await using var preview = new StreamingPreview(renderer, _ => { });
        foreach (var text in new[] { "`", "``cs\n", "**bol", "d**\n", "```", "\u001b[31m" })
        {
            await preview.PublishAsync(Delta(text), default);
        }

        await preview.FinishAsync();
        Assert.Contains("```cs\n**bold**\n```", output.Snapshot);
        Assert.DoesNotContain('\u001b', output.Snapshot);
        Assert.DoesNotContain("[code]", output.Snapshot);
    }

    [Fact]
    public async Task ApprovalOwnsTheTerminalUntilResolution()
    {
        using var output = new ObservedWriter();
        var renderer = CreateRenderer(output, output, false, () => 80);
        await using var preview = new StreamingPreview(renderer, _ => { }, TimeSpan.FromMilliseconds(5));
        await preview.PublishAsync(Delta("before approval"), default);
        await preview.PublishAsync(new RuntimeApprovalRequestedEvent(
            "request", DateTimeOffset.UtcNow, RunId,
            new ApprovalRequest("request", RunId.Value, "tool", new Dictionary<string, object?>(), [])), default);
        output.Write("<approval prompt>");
        var atPrompt = output.Snapshot;
        await preview.PublishAsync(Delta("after approval"), default);
        await Task.Delay(80);
        Assert.Equal(atPrompt, output.Snapshot);
        await preview.PublishAsync(new RuntimeApprovalResolvedEvent(
            "resolved", DateTimeOffset.UtcNow, RunId, "request", true, false, ""), default);
        await output.WaitForTextAsync("after approval");
        await preview.FinishAsync();
    }

    [Fact]
    public void TerminalPreviewUsesABoundedMultilineRegionAndErasesItBeforeTheFormalAnswer()
    {
        using var output = new ObservedWriter();
        var renderer = CreateRenderer(output, output, true, () => 24);
        renderer.Append("old line\nlong line with 中文 and emoji 🙂 latest");
        Assert.Contains("old line", output.Snapshot);
        Assert.Contains("latest", output.Snapshot);
        Assert.DoesNotContain("\u001b[2J", output.Snapshot, StringComparison.Ordinal);
        var beforeFinish = output.Snapshot.Length;
        renderer.Finish(null);
        Assert.Contains("\u001b[2K", output.Snapshot[beforeFinish..], StringComparison.Ordinal);
        renderer.Reset();
    }

    private static StreamingPreviewRenderer CreateRenderer(
        TextWriter output, TextWriter diagnostics, bool terminal, Func<int> width) => new(
            output, diagnostics, terminal ? PreviewOutputMode.Inline : PreviewOutputMode.Redirected,
            () => (width(), 24), new StreamingTranscript(() => [], "", new TranscriptViewport()));

    private static RuntimeContentDeltaEvent Delta(string text) =>
        new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, RunId, text);

    private sealed class ObservedWriter : TextWriter
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        private TaskCompletionSource _changed = NewSignal();
        public override Encoding Encoding => Encoding.UTF8;
        public int Writes { get; private set; }
        public string Snapshot { get { lock (_gate) { return _text.ToString(); } } }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                Writes++;
                _text.Append(value);
                _changed.TrySetResult();
                _changed = NewSignal();
            }
        }

        public override void WriteLine() => Write(Environment.NewLine);
        public override void WriteLine(string? value) => Write(value + Environment.NewLine);

        public async Task WaitForTextAsync(string text)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_text.ToString().Contains(text, StringComparison.Ordinal))
                    {
                        return;
                    }

                    changed = _changed.Task;
                }

                await changed.WaitAsync(timeout.Token);
            }
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockedWriter : TextWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value)
        {
            Entered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
        }

        protected override void Dispose(bool disposing)
        {
            Release.Dispose();
            base.Dispose(disposing);
        }
    }
}
