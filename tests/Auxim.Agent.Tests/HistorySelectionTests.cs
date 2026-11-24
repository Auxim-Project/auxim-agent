using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class HistorySelectionTests
{
    [Fact]
    public void ReverseRangeCopiesLogicalTextRatherThanWrappedCellsOrDecorations()
    {
        var lines = MarkdownConsoleRenderer.RenderTranscript(
            "## Header\nword **中文🙂** e\u0301\n```sh\n  printf '\\t'\t\n```");
        var rows = lines.SelectMany((line, index) => (line with { DisplayIndent = 2 }).Wrap(index, 7)).ToArray();
        var selection = new HistorySelection();
        var start = rows.First(row => row.Line == 1);
        var end = rows.Last(row => row.Line == 4);
        selection.Press(new(0, 0, 0, 0, PointerPhase.Press), end.Hit(int.MaxValue), 0);
        selection.Move(new(0, 0, 0, 0, PointerPhase.Drag), start.Hit(0));
        Assert.Equal("Header\nword 中文🙂 e\u0301\n  printf '\\t'\t", selection.Copy(index => lines[index]));

        var narrower = lines.SelectMany((line, index) => line.Wrap(index, 2)).ToArray();
        var range = selection.Range;
        Assert.Contains(narrower, row => row.Highlight(range.Start, range.End).Contains("\u001b[7m"));
        Assert.Equal("Header\nword 中文🙂 e\u0301\n  printf '\\t'\t", selection.Copy(index => lines[index]));
    }

    [Fact]
    public void EdgeDragTicksWithoutInputAndStopsOnRelease()
    {
        var selection = new HistorySelection();
        var frame = ScreenLayout.Create(80, 24);
        var start = new HistoryPosition(2, 1);
        var end = new HistoryPosition(3, 4);
        selection.Press(new(0, 4, 5, 0, PointerPhase.Press), (start, start), 0);
        selection.Move(new(0, 4, frame.ContentTop - 1, 0, PointerPhase.Drag), (end, end));
        Assert.Equal(0, selection.ScrollDirection(frame, 50));
        Assert.Equal(-1, selection.ScrollDirection(frame, 100));
        Assert.Equal(-1, selection.ScrollDirection(frame, 200));
        selection.Move(new(0, 4, frame.ContentTop + frame.ContentHeight, 0, PointerPhase.Drag), (end, end));
        Assert.Equal(1, selection.ScrollDirection(frame, 300));
        selection.Move(new(0, 4, frame.ContentTop + frame.ContentHeight, 0, PointerPhase.Release), (end, end));
        Assert.Equal(0, selection.ScrollDirection(frame, 400));
        Assert.True(selection.HasRange);
    }

    [Fact]
    public void PartialLiveSelectionKeepsOnlyIdenticalSavedTextOnReplacement()
    {
        var selection = new HistorySelection();
        var start = new HistoryPosition(0, 0);
        var end = new HistoryPosition(0, 2);
        selection.Press(new(0, 0, 0, 0, PointerPhase.Press), (start, end), 0);
        selection.Move(new(0, 0, 0, 0, PointerPhase.Drag), (start, end));
        Assert.Equal("中文", selection.Copy(_ => new("中文🙂")));
        Assert.True(selection.CanKeepPrefix([new("中文")], [new("中文"), new("new response")]));
        Assert.False(selection.CanKeepPrefix([new("中文")], [new("其他")]));
        Assert.False(selection.CanKeepPrefix([], [new("中文")]));
    }

    [Fact]
    public void KittyAcceptsOnlyExplicitSuperCopyAndKeepsControlCopyDistinct()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("\u001b[99;9u\u001b[99;5u\u001b[13;9u\u001b[99;13u\u0003"u8);
        Assert.True(decoder.Take().IsSuperC);
        Assert.True(decoder.Take().IsCtrlC);
        Assert.Equal(TerminalInputEventKind.ControlSequence, decoder.Take().Kind);
        Assert.Equal(TerminalInputEventKind.ControlSequence, decoder.Take().Kind);
        Assert.True(decoder.Take().IsCtrlC);
    }

    [Fact]
    public async Task ProcessClipboardDeliversExactUtf8AndReportsFailureMissingAndTimeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "auxim-copy-" + Guid.NewGuid().ToString("N"));
        const string selected = "中文🙂\n  exact\t";
        try
        {
            var copied = await Clipboard.CopyProcessAsync("/bin/sh", ["-c", "cat > \"$1\"", "test-copy", path],
                selected, TimeSpan.FromSeconds(3));
            Assert.True(copied.Success, copied.Status);
            Assert.Equal(selected, File.ReadAllText(path));
            var rejected = await Clipboard.CopyProcessAsync("/bin/sh", ["-c", "exit 1"], selected, TimeSpan.FromSeconds(3));
            Assert.False(rejected.Success);
            var missing = await Clipboard.CopyProcessAsync(path + "-missing", [], selected, TimeSpan.FromSeconds(3));
            Assert.Contains("not found", missing.Status);
            var timeout = await Clipboard.CopyProcessAsync("/bin/sleep", ["5"], selected, TimeSpan.FromMilliseconds(120));
            Assert.Contains("timed out", timeout.Status);
        }
        finally { File.Delete(path); }
    }
}
