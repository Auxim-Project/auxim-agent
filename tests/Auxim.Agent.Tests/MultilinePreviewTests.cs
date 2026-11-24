using System.Globalization;
using System.Text;
using Auxim.Cli.Interactive;
using Auxim.Core.Approval;
using Auxim.Core.Config;
using Auxim.Core.Runtime;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class MultilinePreviewTests : IDisposable
{
    private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("auxim-preview-tests-");

    public void Dispose() => _home.Delete(recursive: true);

    [Fact]
    public void GrowingTranscriptFollowsTheBottomWithoutRepeatedScreenClears()
    {
        using var screen = new Screen(30, 10);
        var viewport = new TranscriptViewport();
        var reads = 0;
        var transcript = new StreamingTranscript(() => { reads++; return ["saved history"]; }, "question", viewport);
        var renderer = Renderer(screen, transcript);
        renderer.Append("first\nsecond");
        Assert.Contains("first", screen.Visible);
        Assert.Contains("second", screen.Visible);
        var clears = screen.Clears;
        for (var i = 0; i < 40; i++) renderer.Append($"\nline {i}");

        Assert.Contains("line 39", screen.Visible);
        Assert.DoesNotContain("line 0\n", screen.Visible);
        Assert.Equal(viewport.TotalLines - 6, viewport.Offset);
        Assert.Equal(clears, screen.Clears);
        Assert.Equal(1, reads);
        var writes = screen.Writes;
        renderer.Refresh();
        Assert.Equal(writes, screen.Writes);
        renderer.Finish(null);
        renderer.Reset();
        Assert.DoesNotContain("line 39", screen.Visible);
        Assert.False(viewport.IsActive);
        Assert.Empty(viewport.Lines);
    }

    [Theory]
    [InlineData("中文你好", 4, "中文|你好")]
    [InlineData("🙂👨‍👩‍👧‍👦🇺🇸e\u0301", 4, "🙂👨‍👩‍👧‍👦|🇺🇸e\u0301")]
    [InlineData("👍🏽❤️1️⃣", 4, "👍🏽❤️|1️⃣")]
    [InlineData("a\tb", 4, "a   |b")]
    [InlineData("a\n", 4, "a|")]
    public void PhysicalRowsRespectGraphemeBoundaries(string text, int columns, string expected)
    {
        var rows = TerminalTextLayout.Wrap(text, columns);
        Assert.Equal(expected, string.Join('|', rows));
        foreach (var row in rows)
        {
            var elements = StringInfo.GetTextElementEnumerator(row);
            var cells = 0;
            while (elements.MoveNext()) cells += TerminalTextLayout.CellWidth(elements.GetTextElement());
            Assert.True(cells <= columns);
        }
    }

    [Fact]
    public void CrossDeltaCombiningEmojiAndCrLfAreReassembledBeforeLayout()
    {
        var viewport = new TranscriptViewport();
        var transcript = new StreamingTranscript(() => [], "question", viewport);
        transcript.Append("e"); transcript.Prepare(40, 10);
        transcript.Append("\u0301 👨"); transcript.Prepare(40, 10);
        transcript.Append("‍👩‍👧‍👦\r"); transcript.Prepare(40, 10);
        transcript.Append("\nlast ");
        transcript.Append("\ud83d"); transcript.Prepare(40, 10);
        Assert.DoesNotContain("�", string.Join('\n', transcript));
        transcript.Append("\ude42"); transcript.Prepare(40, 10);
        Assert.Equal(["You", "  question", "", "Assistant  streaming", "  e\u0301 👨‍👩‍👧‍👦", "  last 🙂"], transcript.ToArray());
    }

    [Fact]
    public void ResizingWithoutADeltaReflowsOriginalTextAndErasesOldRows()
    {
        using var screen = new Screen(40, 12);
        var transcript = new StreamingTranscript(() => ["saved 中文 history"], "prompt", new TranscriptViewport());
        var renderer = Renderer(screen, transcript);
        renderer.Append("a long unbroken message 中文 🙂 with its ending");
        screen.Resize(18, 8);
        renderer.Refresh();
        Assert.Contains("ending", screen.Visible.Replace("\n", "").Replace(" ", "")); // a narrow physical row may split the word
        screen.Resize(70, 14);
        renderer.Refresh();
        Assert.Contains("a long unbroken message 中文 🙂 with its ending", screen.Visible);
        screen.Resize(1, 1);
        renderer.Refresh();
        screen.Resize(70, 14);
        renderer.Refresh();
        Assert.Contains("ending", screen.Visible);
        renderer.Finish(null); renderer.Reset();
        Assert.DoesNotContain("ending", screen.Visible);
    }

    [Fact]
    public void TemporaryTailDoesNotOverwriteThePreviousTranscriptOrItsPosition()
    {
        var viewport = new TranscriptViewport();
        viewport.Open(["old one", "old two", "old three"], false, 2);
        viewport.Scroll(1);
        Assert.Equal(["old two", "old three"], viewport.GetVisibleLines(2));
        var transient = new StreamingTranscript(() => ["history"], "prompt", viewport);
        transient.Append("temporary body"); transient.Prepare(80, 3);
        Assert.Contains("  temporary body", viewport.GetVisibleLines(3));
        transient.Reset();
        Assert.Equal(1, viewport.Offset);
        Assert.Equal(["old two", "old three"], viewport.GetVisibleLines(2));
    }

    [Fact]
    public async Task ToolAndApprovalBoundariesRetainOrderedContentAndRebuildAfterHandoff()
    {
        using var screen = new Screen(80, 22);
        var transcript = new StreamingTranscript(() => [], "prompt", new TranscriptViewport());
        var renderer = Renderer(screen, transcript);
        await using var preview = new StreamingPreview(renderer, _ => renderer.AddTool("[tool:echo] start"));
        var run = new AuximRunId("test");
        await preview.PublishAsync(Delta("before tool"), default);
        await preview.PublishAsync(new RuntimeToolStartedEvent("tool", DateTimeOffset.UtcNow, run, "call", "echo", []), default);
        Assert.Contains("before tool", screen.Visible);
        Assert.Contains("[tool:echo] start", screen.Visible);
        await preview.PublishAsync(new RuntimeApprovalRequestedEvent("request", DateTimeOffset.UtcNow, run,
            new ApprovalRequest("request", run.Value, "echo", new Dictionary<string, object?>(), [])), default);
        screen.Write("APPROVAL OWNS SCREEN");
        var atApproval = screen.Visible;
        await preview.PublishAsync(Delta("after tool"), default);
        await Task.Delay(100);
        Assert.Equal(atApproval, screen.Visible);
        screen.Write("\u001b[2J\u001b[H");
        await preview.PublishAsync(new RuntimeApprovalResolvedEvent("resolved", DateTimeOffset.UtcNow, run, "request", true, false, ""), default);
        await WaitUntilAsync(() => screen.Visible.Contains("after tool", StringComparison.Ordinal));
        Assert.DoesNotContain("APPROVAL OWNS SCREEN", screen.Visible);
        Assert.Contains("[tool:echo] start", screen.Visible);
        await preview.FinishAsync();
        Assert.DoesNotContain("after tool", screen.Visible);
        screen.WriteLine("formal answer");
        Assert.Equal(1, screen.Visible.Split("formal answer").Length - 1);
    }

    [Fact]
    public async Task AFailedPaintStillCleansItsRegionAndAllowsTheNextRun()
    {
        using var screen = new Screen(60, 12);
        var viewport = new TranscriptViewport();
        var transcript = new StreamingTranscript(() => [], "prompt", viewport);
        var renderer = Renderer(screen, transcript);
        await using (var preview = new StreamingPreview(renderer, _ => { }))
        {
            await preview.PublishAsync(Delta("partial text"), default);
            await WaitUntilAsync(() => screen.Visible.Contains("partial text", StringComparison.Ordinal));
            screen.ThrowOnNextWrite = true;
            await preview.PublishAsync(Delta(" paint failure"), default);
            await screen.FailedWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IOException>(() => preview.FinishAsync());
        }
        Assert.DoesNotContain("partial text", screen.Visible);
        Assert.False(viewport.IsActive);
        await using (var next = new StreamingPreview(renderer, _ => { }))
        {
            await next.PublishAsync(Delta("fresh next run"), default);
            await WaitUntilAsync(() => screen.Visible.Contains("fresh next run", StringComparison.Ordinal));
            Assert.DoesNotContain("paint failure", screen.Visible);
            await next.FinishAsync("cancelled");
        }
        Assert.False(viewport.IsActive);
    }

    [Fact]
    public async Task WorkspaceHistoryFollowsTheBottomAndKeepsComposerAndCursorIntact()
    {
        using var screen = new Screen(80, 24);
        var view = Workspace(screen);
        view.ShowMain();
        Assert.Equal(4, view.PrepareInputLayout(4)!.Value.InputRows);

        await using var preview = view.BeginStreamingPreview("question");
        await preview.PublishAsync(Delta("first live text"), default);
        await WaitUntilAsync(() => screen.Visible.Contains("first live text", StringComparison.Ordinal));
        await OpenLiveHistory(preview);
        var erased = screen.ClearedRows.Length;
        await preview.PublishAsync(Delta(string.Concat(Enumerable.Range(0, 40).Select(i => $"\nbody row {i:00}"))), default);
        await WaitUntilAsync(() => screen.Visible.Contains("body row 39", StringComparison.Ordinal));
        var layout = ScreenLayout.Create(80, 24);
        Assert.DoesNotContain("body row 00", screen.Visible);
        Assert.Contains("AUXIM / History", screen.Visible);
        Assert.Contains("Working...", screen.Visible);
        Assert.Contains("Follow live", screen.Visible);
        Assert.Equal((layout.InputRow, layout.InputStartColumn), screen.Cursor);
        Assert.NotEmpty(screen.ClearedRows.Skip(erased));
        Assert.All(screen.ClearedRows.Skip(erased), row =>
            Assert.InRange(row, layout.ContentTop, layout.ContentTop + layout.ContentHeight - 1));
        var writes = screen.Writes;
        await Task.Delay(100);
        Assert.Equal(writes, screen.Writes);
        await preview.FinishAsync();
        view.EndRun(response: "formal answer");
        Assert.DoesNotContain("body row 39", screen.Visible);
        Assert.Equal((layout.InputRow, layout.InputStartColumn), screen.Cursor);
        Assert.Equal(0, screen.Clears);
    }

    [Fact]
    public async Task WorkspaceObservesResizeWithoutADeltaButPausesPaintingDuringApproval()
    {
        using var screen = new Screen(80, 24);
        var view = Workspace(screen);
        await using var preview = view.BeginStreamingPreview("question");
        screen.Resize(60, 16);
        await WaitUntilAsync(() => screen.Visible.Contains("Working...", StringComparison.Ordinal));
        Assert.Equal((ScreenLayout.Create(60, 16).InputRow, ScreenLayout.Create(60, 16).InputStartColumn), screen.Cursor);

        await OpenLiveHistory(preview);
        await preview.PublishAsync(Delta("before approval"), default);
        var run = new AuximRunId("test");
        var requested = new RuntimeApprovalRequestedEvent("request", DateTimeOffset.UtcNow, run,
            new ApprovalRequest("request", run.Value, "echo", new Dictionary<string, object?>(), []));
        await preview.PublishAsync(requested, default);
        Assert.Contains("before approval", screen.Visible);
        view.PaintApproval(["APPROVAL OWNS SCREEN"], ScreenLayout.Create(60, 16), feedback: false, layoutChanged: true);
        var writes = screen.Writes;
        screen.Resize(18, 8);
        await preview.PublishAsync(Delta("after approval 中文🙂"), default);
        await Task.Delay(100);
        Assert.Equal(writes, screen.Writes);

        view.EndApprovalPanel();
        await preview.PublishAsync(new RuntimeApprovalResolvedEvent("resolved", DateTimeOffset.UtcNow, run, "request", false, false, "no"), default);
        await WaitUntilAsync(() => screen.Visible.Contains("中文🙂", StringComparison.Ordinal));
        Assert.DoesNotContain("APPROVAL OWNS SCREEN", screen.Visible);

        foreach (var (width, height) in new[] { (1, 1), (3, 3), (80, 24) })
        {
            var before = screen.Writes;
            screen.Resize(width, height);
            var layout = ScreenLayout.Create(width, height);
            await WaitUntilAsync(() => screen.Writes > before
                && screen.Cursor == (layout.InputRow, layout.InputStartColumn));
        }
        await WaitUntilAsync(() => screen.Visible.Contains("after approval 中文🙂", StringComparison.Ordinal));
        await preview.FinishAsync();
        view.EndRun();
        Assert.Equal(0, screen.Clears);
    }

    [Fact]
    public async Task WorkspaceRecoversFromPaintFailureAndDoesNotReuseItsTemporaryTranscript()
    {
        using var screen = new Screen(80, 24);
        var view = Workspace(screen);
        await using (var preview = view.BeginStreamingPreview("first question"))
        {
            await OpenLiveHistory(preview);
            await preview.PublishAsync(Delta("partial text"), default);
            await WaitUntilAsync(() => screen.Visible.Contains("partial text", StringComparison.Ordinal));
            screen.ThrowOnNextWrite = true;
            await preview.PublishAsync(Delta(" paint failure"), default);
            await screen.FailedWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IOException>(() => preview.FinishAsync());
            view.EndRun("Failed");
        }
        Assert.DoesNotContain("partial text", screen.Visible);
        await using var next = view.BeginStreamingPreview("new question");
        await OpenLiveHistory(next);
        await next.PublishAsync(Delta("fresh content"), default);
        await WaitUntilAsync(() => screen.Visible.Contains("fresh content", StringComparison.Ordinal));
        Assert.DoesNotContain("paint failure", screen.Visible);
        await next.FinishAsync("cancelled");
        view.EndRun("Cancelled");
        Assert.DoesNotContain("fresh content", screen.Visible);
    }

    private InteractiveView Workspace(Screen screen) =>
        new(new AuximRuntimeService(null!, configLoader: () => new AuximConfig(),
            homeDirectory: () => _home.FullName), DateTimeOffset.Now,
            output: screen, size: () => (screen.Width, screen.Height), fixedLayout: true);

    private static async Task OpenLiveHistory(StreamingPreview preview) =>
        await preview.NavigateAsync(new(TerminalInputEventKind.MouseWheel, default, ScrollDelta: 1));

    private static StreamingPreviewRenderer Renderer(Screen screen, StreamingTranscript transcript) =>
        new(screen, screen, PreviewOutputMode.Inline, () => (screen.Width, screen.Height), transcript);

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task IncompleteRunRestoresThePreviousViewportAndLeavesNoTemporaryRows(string status)
    {
        using var screen = new Screen(60, 12);
        var viewport = new TranscriptViewport();
        viewport.Open(["saved one", "saved two", "saved three"], true, 2);
        var transcript = new StreamingTranscript(() => ["saved history"], "prompt", viewport);
        var renderer = Renderer(screen, transcript);
        await using (var preview = new StreamingPreview(renderer, _ => { }))
        {
            await preview.PublishAsync(Delta("partial one\npartial two"), default);
            await WaitUntilAsync(() => screen.Visible.Contains("partial two", StringComparison.Ordinal));
            await preview.FinishAsync(status);
        }
        Assert.DoesNotContain("partial", screen.Visible);
        Assert.Equal(["saved one", "saved two", "saved three"], viewport.Lines);
        Assert.Equal(1, viewport.Offset);
        Assert.Empty(transcript);
        await using (var next = new StreamingPreview(renderer, _ => { }))
        {
            await next.PublishAsync(Delta("fresh content"), default);
            await WaitUntilAsync(() => screen.Visible.Contains("fresh content", StringComparison.Ordinal));
            Assert.DoesNotContain("partial", screen.Visible);
            await next.FinishAsync();
        }
    }

    private static RuntimeContentDeltaEvent Delta(string text) => new("delta", DateTimeOffset.UtcNow, new AuximRunId("test"), text);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    // Only interprets the cursor/erase operations emitted by our renderer. Tests
    // assert the visible grid, not character counts in an ANSI output recording.
    private sealed class Screen(int width, int height) : TextWriter
    {
        private readonly object _gate = new();
        private readonly List<int> _clearedRows = [];
        private string?[,] _cells = new string?[height, width];
        private int _row, _column;
        public int Width { get; private set; } = width;
        public int Height { get; private set; } = height;
        public int Clears { get; private set; }
        public int Writes { get; private set; }
        public int[] ClearedRows { get { lock (_gate) return _clearedRows.ToArray(); } }
        public (int Row, int Column) Cursor { get { lock (_gate) return (_row + 1, _column + 1); } }
        public bool ThrowOnNextWrite { get; set; }
        public TaskCompletionSource FailedWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Encoding Encoding => Encoding.UTF8;
        public string Visible
        {
            get
            {
                lock (_gate)
                    return string.Join('\n', Enumerable.Range(0, Height).Select(r =>
                        string.Concat(Enumerable.Range(0, Width).Select(c => _cells[r, c] ?? " ")).TrimEnd()));
            }
        }

        public void Resize(int newWidth, int newHeight)
        {
            lock (_gate)
            {
                Width = newWidth; Height = newHeight;
                _cells = new string?[Height, Width];
                _row = Math.Min(_row, Height - 1); _column = Math.Min(_column, Width - 1);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            lock (_gate)
            {
                Writes++;
                if (ThrowOnNextWrite)
                {
                    ThrowOnNextWrite = false; FailedWrite.TrySetResult();
                    throw new IOException("simulated paint failure");
                }
                for (var i = 0; i < value.Length;)
                {
                    if (value[i] == '\u001b')
                    {
                        var end = i + 2;
                        while (end < value.Length && !(value[end] is >= '@' and <= '~')) end++;
                        var numbers = value[(i + 2)..end].Split(';').Select(n => int.TryParse(n, out var number) ? number : 1).ToArray();
                        var n = numbers[0];
                        switch (value[end])
                        {
                            case 'H': _row = Math.Clamp(n - 1, 0, Height - 1); _column = Math.Clamp((numbers.Length > 1 ? numbers[1] : 1) - 1, 0, Width - 1); break;
                            case 'A': _row = Math.Max(0, _row - n); break;
                            case 'B': _row = Math.Min(Height - 1, _row + n); break;
                            case 'K':
                                _clearedRows.Add(_row + 1);
                                for (var c = n == 2 ? 0 : _column; c < Width; c++) _cells[_row, c] = null;
                                break;
                            case 'J': _cells = new string?[Height, Width]; Clears++; break;
                        }
                        i = end + 1;
                    }
                    else if (value[i] == '\r') { _column = 0; i++; }
                    else if (value[i] == '\n') { AdvanceLine(); i++; }
                    else
                    {
                        var element = StringInfo.GetNextTextElement(value, i);
                        var cells = TerminalTextLayout.CellWidth(element);
                        if (_column + cells > Width) AdvanceLine();
                        if (cells > 0)
                        {
                            _cells[_row, _column] = element;
                            for (var c = 1; c < cells && _column + c < Width; c++) _cells[_row, _column + c] = "";
                        }
                        _column += cells; i += element.Length;
                    }
                }
            }
        }
        public override void WriteLine(string? value) => Write(value + "\n");
        private void AdvanceLine()
        {
            _column = 0;
            if (++_row < Height) return;
            for (var r = 1; r < Height; r++) for (var c = 0; c < Width; c++) _cells[r - 1, c] = _cells[r, c];
            _row = Height - 1;
            for (var c = 0; c < Width; c++) _cells[_row, c] = null;
        }
    }
}
