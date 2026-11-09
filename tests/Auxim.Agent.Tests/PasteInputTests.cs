using System.Text;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class PasteInputTests
{
    private static List<TerminalInputEvent> Drain(TerminalInputDecoder decoder)
    {
        var result = new List<TerminalInputEvent>();
        while (decoder.HasEvents) result.Add(decoder.Take());
        return result;
    }

    [Theory]
    [InlineData("")]
    [InlineData("中文🙂é\r\n```csharp\nvar a = 1;\n```\n")]
    [InlineData("/new\r\n//echo unsafe\t\u001b[A\u001b[13;2u\u001b]0;title\a")]
    [InlineData("a\u001b[20x\u001b\u001b[201x\r\nb")]
    public void EverySplitProducesOneExactPasteAndNoPayloadKeys(string text)
    {
        var bytes = Encoding.UTF8.GetBytes("\u001b[200~" + text + "\u001b[201~");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var decoder = new TerminalInputDecoder();
            decoder.Feed(bytes.AsSpan(0, split));
            var events = Drain(decoder);
            decoder.Feed(bytes.AsSpan(split)); events.AddRange(Drain(decoder));
            Assert.Equal(new[] { TerminalInputEventKind.PasteStarted, TerminalInputEventKind.Paste }, events.Select(e => e.Kind));
            Assert.Equal(text, events[1].Text);
            Assert.False(decoder.HasPartialSequence);
            decoder.Feed("\r"u8);
            Assert.Equal(ConsoleKey.Enter, Assert.Single(Drain(decoder)).Key.Key);
        }
    }

    [Theory]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    [InlineData(1000000, false)]
    public void PasteLimitRejectsTheWholePayloadAndDrainsUntilEnd(int size, bool accepted)
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("\u001b[200~"u8);
        for (var i = 0; i < size; i++) decoder.Feed("x"u8);
        Assert.Equal(TerminalInputEventKind.PasteStarted, Assert.Single(Drain(decoder)).Kind);
        Assert.True(decoder.HasPartialSequence);
        decoder.Feed("\u001b[201~"u8);
        var paste = Assert.Single(Drain(decoder));
        Assert.Equal(TerminalInputEventKind.Paste, paste.Kind);
        Assert.Equal(accepted, paste.Text is not null);
        if (accepted) Assert.Equal(size, paste.Text!.Length);
        decoder.Feed("\u001b[200~next\u001b[201~"u8);
        Assert.Equal("next", Drain(decoder)[1].Text);
    }

    [Fact]
    public void AbandonedPasteCannotLeakToAnotherOwnerOrFollowingDraft()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("\u001b[200~part\u001b[20"u8);
        decoder.DiscardEvents();
        Assert.True(decoder.HasPartialSequence);
        decoder.Feed("1~"u8);
        Assert.Null(Assert.Single(Drain(decoder)).Text);
        decoder.Feed("\u001b[200~clean\u001b[201~"u8);
        Assert.Equal("clean", Drain(decoder)[1].Text);
    }

    [Fact]
    public void InvalidUtf8NeverBecomesPartialTextOrApprovalKeys()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("\u001b[200~"u8); decoder.Feed([0xff, 13, 50]); decoder.Feed("\u001b[201~"u8);
        var events = Drain(decoder);
        Assert.Null(events[1].Text);
        Assert.DoesNotContain(events, e => e.Kind == TerminalInputEventKind.Key);
    }

    [Fact]
    public void BulkInsertionKeepsOriginalTextCaretAndDraftHistorySnapshot()
    {
        const string text = "中文🙂é\r\ncode\n/new\t\u001b[A";
        var draft = new InputDraft(); draft.Reset(1);
        draft.Buffer.Append("prefixSUFFIX"); draft.Cursor = 6;
        draft.InsertPaste(text);
        Assert.Equal("prefix" + text + "SUFFIX", draft.Buffer.ToString());
        Assert.Equal(6 + text.Length, draft.Cursor);
        draft.FirstVisibleRow = 3;
        Assert.True(draft.Previous(["older"]));
        Assert.True(draft.Next(["older"]));
        Assert.True(draft.ContainsPaste);
        Assert.Equal(3, draft.FirstVisibleRow);
        var saved = draft.Copy(); draft.Reset(0); draft.Restore(saved);
        Assert.Equal(saved.Buffer.ToString(), draft.Buffer.ToString());
        Assert.Equal(saved.Cursor, draft.Cursor);
        Assert.True(draft.ContainsPaste);
        foreach (var width in new[] { 1, 32, 80, 120, 160 })
        {
            var projection = TerminalTextLayout.LayoutInput(draft.Buffer.ToString(), draft.Cursor, width, "> ");
            Assert.All(projection.Rows, row => Assert.DoesNotContain('\u001b', row.Text));
        }
    }

    [Fact]
    public void FormalMarkdownCannotExecuteEchoedPasteControls()
    {
        var rendered = string.Join('\n', MarkdownConsoleRenderer.RenderToLines("text\u001b[2Jend\u001b]0;title\a"));
        Assert.DoesNotContain("\u001b[2J", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('\a', rendered);
    }
}
