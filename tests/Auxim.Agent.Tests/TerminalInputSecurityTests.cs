using System.Text;
using Auxim.Cli.Interactive;
using Auxim.Core.Approval;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class TerminalInputSecurityTests
{
    private static List<TerminalInputEvent> Drain(TerminalInputDecoder decoder)
    {
        var events = new List<TerminalInputEvent>();
        while (decoder.HasEvents) events.Add(decoder.Take());
        return events;
    }

    [Theory]
    [InlineData("\u001b[200~hello\r\u001b[201~")]
    [InlineData("\u001b[13;2u")]
    [InlineData("\u001b[27;2;13~")]
    [InlineData("\u001b[13;\r2u")]
    [InlineData("\u001b]2;2\r\u001b\\")]
    [InlineData("\u001bP2\r\u001b\\")]
    [InlineData("\u001b(2")]
    [InlineData("\u009b13;2u")]
    public void ProtocolBytesNeverBecomeApprovalKeysAtAnySplit(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var decoder = new TerminalInputDecoder();
            decoder.Feed(bytes.AsSpan(0, split));
            var events = Drain(decoder);
            decoder.Feed(bytes.AsSpan(split));
            events.AddRange(Drain(decoder));
            Assert.DoesNotContain(events, e => e.Kind == TerminalInputEventKind.Key);
            Assert.False(decoder.HasPartialSequence);
        }
    }

    [Fact]
    public void DcsDoesNotTerminateAtAnEmbeddedBellAndNestedEscapeDoesNotReleasePaste()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("\u001bP\a2\r"u8);
        Assert.True(decoder.HasPartialSequence);
        Assert.DoesNotContain(Drain(decoder), e => e.Kind == TerminalInputEventKind.Key);
        decoder.Feed("\u001b\\"u8);
        Assert.False(decoder.HasPartialSequence);
        decoder.Feed("\u001b[13;\u001b[200~2\r"u8);
        Assert.True(decoder.HasPartialSequence);
        Assert.DoesNotContain(Drain(decoder), e => e.Kind == TerminalInputEventKind.Key);
    }

    [Fact]
    public void PasteIsACompleteEventAndItsPayloadIsNeverRetainedAsKeys()
    {
        var decoder = new TerminalInputDecoder();
        foreach (var value in Encoding.UTF8.GetBytes("\u001b[200~中文🙂\r2\n\u001b[A\u001b[201~"))
            decoder.Feed([value]);
        Assert.Equal([TerminalInputEventKind.PasteStarted, TerminalInputEventKind.Paste], Drain(decoder).Select(e => e.Kind));
        Assert.False(decoder.HasPartialSequence);
    }

    [Theory]
    [InlineData("\u001b[200~")]
    [InlineData("\u001b[13;")]
    [InlineData("\u001b]")]
    public void HandoffsDoNotResetIncompleteProtocolState(string prefix)
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed(Encoding.UTF8.GetBytes(prefix));
        decoder.DiscardEvents();
        Assert.True(decoder.HasPartialSequence);
        decoder.Feed("2\r3\n"u8);
        Assert.DoesNotContain(Drain(decoder), e => e.Kind == TerminalInputEventKind.Key);
        Assert.True(decoder.HasPartialSequence);
    }

    [Fact]
    public void ResidualEndMarkerIsNotAnOptionAndQueuedDecisionsCanBeDiscarded()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("\u001b[201~\r"u8);
        Assert.Equal([TerminalInputEventKind.ControlSequence, TerminalInputEventKind.Key], Drain(decoder).Select(e => e.Kind));
        decoder.Feed("2\r"u8);
        decoder.DiscardEvents();
        Assert.Empty(Drain(decoder));
    }

    [Fact]
    public void OversizedCsiStaysQuarantinedUntilItsFinalByte()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed(Encoding.UTF8.GetBytes("\u001b[" + new string('2', 100000) + "\r"));
        Assert.DoesNotContain(Drain(decoder), e => e.Kind == TerminalInputEventKind.Key);
        Assert.True(decoder.HasPartialSequence);
        decoder.Feed("u"u8);
        Assert.DoesNotContain(Drain(decoder), e => e.Kind == TerminalInputEventKind.Key);
        Assert.False(decoder.HasPartialSequence);
    }

    [Fact]
    public void BrokenUtf8DoesNotHideTheFollowingPasteBoundary()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed([0xc2]);
        decoder.Feed("\u001b[200~2\r\u001b[201~"u8);
        Assert.DoesNotContain(Drain(decoder), e => e.Kind == TerminalInputEventKind.Key);
        Assert.False(decoder.HasPartialSequence);
    }

    [Fact]
    public void NormalKeysAndHistoryShortcutsRemainAvailable()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("123\r\u001b[A\u001b[B\u001b[H\u001b[F\u001b[3~\u001b[5~\u001b[6~\u001b[1;3A\u0010\u000e\t"u8);
        var keys = Drain(decoder).Select(e => e.Key).ToArray();
        Assert.Equal(new[] { ConsoleKey.D1, ConsoleKey.D2, ConsoleKey.D3, ConsoleKey.Enter,
            ConsoleKey.UpArrow, ConsoleKey.DownArrow, ConsoleKey.Home, ConsoleKey.End,
            ConsoleKey.Delete, ConsoleKey.PageUp, ConsoleKey.PageDown, ConsoleKey.UpArrow,
            ConsoleKey.P, ConsoleKey.N, ConsoleKey.Tab }, keys.Select(k => k.Key));
        Assert.Equal(ConsoleModifiers.Alt, keys[11].Modifiers);
        Assert.Equal(ConsoleModifiers.Control, keys[12].Modifiers);
        Assert.All(keys.Take(6), key => Assert.True(TerminalInputPolicy.Approval.AcceptsKey(key)));
        Assert.False(TerminalInputPolicy.Approval.AcceptsKey(keys[11]));
    }

    [Fact]
    public void ApplicationKeypadDigitsAndEnterRemainValidApprovalInput()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("\u001bOq\u001bOr\u001bOs\u001bOM"u8);
        var keys = Drain(decoder).Select(e => e.Key).ToArray();
        Assert.Equal(new[] { ConsoleKey.NumPad1, ConsoleKey.NumPad2, ConsoleKey.NumPad3, ConsoleKey.Enter }, keys.Select(k => k.Key));
        Assert.All(keys, key => Assert.True(TerminalInputPolicy.Approval.AcceptsKey(key)));
    }

    [Fact]
    public void UnicodeDraftCharactersRoundTripWithoutLosingUtf16Units()
    {
        const string text = "中文🙂é";
        var decoder = new TerminalInputDecoder();
        foreach (var value in Encoding.UTF8.GetBytes(text)) decoder.Feed([value]);
        Assert.Equal(text, new string(Drain(decoder).Select(e => e.Key.KeyChar).ToArray()));
    }

    [Fact]
    public void EscapeAdjacentToAnArrowHasAnExplicitBoundaryRatherThanSwallowingEscape()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("\u001b\u001b[A"u8);
        Assert.Equal(new[] { TerminalInputEventKind.EscapeBoundary, TerminalInputEventKind.Key },
            Drain(decoder).Select(e => e.Kind));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldPageScrollIsDiscardedWithoutDiscardingOrResettingProtocols(bool fragmented)
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed(fragmented ? "\u001b\u001b["u8 : "\u001b\u001b[A"u8);
        Assert.Equal(TerminalInputEventKind.EscapeBoundary, decoder.Take().Kind);
        decoder.DiscardQueuedNavigation();
        if (fragmented) decoder.Feed("A"u8);
        Assert.Empty(Drain(decoder));
        decoder.Feed("\u001b[A"u8);
        Assert.Equal(ConsoleKey.UpArrow, Assert.Single(Drain(decoder)).Key.Key);
        decoder.Feed("\u001b[13;2"u8);
        decoder.DiscardQueuedNavigation();
        decoder.ReleaseEditorEscape();
        Assert.Empty(Drain(decoder));
        decoder.Feed("u"u8);
        Assert.Equal(TerminalInputEventKind.ControlSequence, Assert.Single(Drain(decoder)).Kind);
    }

    [Fact]
    public void HistoryExitDropsQueuedAndFragmentedWheelOnly()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("\u001b[<64;2;2M\u001b[Bx\u001b[<65;2;"u8);
        decoder.DiscardQueuedWheel();
        decoder.Feed("2M\u001b[<65;2;2M"u8);
        var events = Drain(decoder);
        Assert.Equal(3, events.Count);
        Assert.Equal(ConsoleKey.DownArrow, events[0].Key.Key);
        Assert.Equal('x', events[1].Key.KeyChar);
        Assert.Equal(TerminalInputEventKind.MouseWheel, events[2].Kind);

        decoder.Feed("\u001b[<65;2;"u8);
        decoder.DiscardQueuedWheel();
        decoder.Feed("\u001b[<64;2;2M"u8);
        Assert.Equal(new[] { TerminalInputEventKind.ControlSequence, TerminalInputEventKind.MouseWheel },
            Drain(decoder).Select(value => value.Kind));
    }

    [Fact]
    public void AnEscapeAfterAQueuedSubmissionSurvivesTheApprovalHandoff()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("message\r\u001b"u8);
        decoder.ReleaseEditorEscape();
        Assert.True(decoder.HasPartialSequence);
        decoder.DiscardEvents();
        decoder.Feed("[200~2\r\u001b[201~"u8);
        Assert.DoesNotContain(Drain(decoder), e => e.Kind == TerminalInputEventKind.Key);
        Assert.False(decoder.HasPartialSequence);
    }

    [Fact]
    public void OnlyTheEditorMayReleaseAnIdleEscape()
    {
        var decoder = new TerminalInputDecoder();
        decoder.Feed("\u001b"u8);
        Assert.True(decoder.HasPartialSequence);
        Assert.Empty(Drain(decoder));
        decoder.ReleaseEditorEscape();
        Assert.Equal(ConsoleKey.Escape, Assert.Single(Drain(decoder)).Key.Key);
        Assert.False(decoder.HasPartialSequence);
    }

    [Fact]
    public async Task RedirectedInputFailsClosedWithoutReadingDecisions()
    {
        if (!Console.IsInputRedirected) return;
        var request = new ApprovalRequest("test", "test", "test", new Dictionary<string, object?>(), []);
        var result = await new CliApprovalHandler().RequestAsync(request, default);
        Assert.False(result.Approved);
        Assert.False(result.Remember);
    }
}
