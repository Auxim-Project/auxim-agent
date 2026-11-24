using System.Text;
using Auxim.Cli.Interactive;
using Xunit;
namespace Auxim.Agent.Tests;

public sealed class KittyProtocolTests
{
    private sealed class Terminal
    {
        public readonly TerminalInputDecoder Decoder = new();
        public readonly List<string> Output = [];
        public readonly Stack<int> Stack = new();
        public int Flags = 8;
        public long Now;
        public bool Reply = true;
        public bool RejectFlag;
        public int Queries;
        public bool ThrowOnActivationQuery;
        public TerminalKeyboardProtocol Protocol;
        public Terminal() => Protocol = new(Decoder, Write, Read, () => Now);
        private bool Read(int timeout) { Now += timeout; return false; }
        private void Write(string text)
        {
            Output.Add(text);
            if (text == "\x1b[>1u") { Stack.Push(Flags); Flags = RejectFlag ? 0 : 1; }
            if (text == "\x1b[>0u") { Stack.Push(Flags); Flags = 0; }
            if (text == "\x1b[<u") Flags = Stack.Pop();
            if (text == "\x1b[?u")
            {
                Queries++;
                if (ThrowOnActivationQuery && Queries == 2) throw new IOException("query failed");
                if (Reply) Decoder.Feed(Encoding.ASCII.GetBytes($"\x1b[?{Flags}u"));
            }
        }
    }

    [Theory]
    [InlineData(65, 64)][InlineData(129, 128)]
    public void LockModifiersDoNotBreakFlagOneArrowReports(int encoded, int bits)
    {
        var d = new TerminalInputDecoder(); d.Feed(Encoding.ASCII.GetBytes($"\u001b[1;{encoded}A"));
        var e = d.Take(); Assert.Equal(ConsoleKey.UpArrow, e.Key.Key);
        Assert.Equal((ConsoleModifiers)0, e.Key.Modifiers); Assert.Equal(bits, e.KittyModifiers);
    }

    [Fact]
    public void OwnedEntryIsIdempotentAndRestoresCallerAcrossProtectedReaders()
    {
        var terminal = new Terminal(); var p = terminal.Protocol;
        p.Enable(false); Assert.Equal(1, terminal.Flags); Assert.Equal(1, p.ActiveFlags);
        var count = terminal.Output.Count; p.Enable(false); Assert.Equal(count, terminal.Output.Count);
        p.Compatible(); Assert.Equal(0, terminal.Flags); Assert.Single(terminal.Stack);
        p.Enable(false); Assert.Equal(1, terminal.Flags); Assert.Single(terminal.Stack);
        p.Restore(); p.Restore(); Assert.Equal(8, terminal.Flags); Assert.Empty(terminal.Stack);
        p.Enable(true); Assert.Equal(1, terminal.Flags); p.Restore(); Assert.Equal(8, terminal.Flags);
    }

    [Fact]
    public void UnsupportedAndLateResponsesNeverEnableOrBecomeKeys()
    {
        var terminal = new Terminal { Reply = false }; var p = terminal.Protocol;
        p.Enable(false); Assert.Empty(terminal.Stack); Assert.Equal(8, terminal.Flags);
        Assert.True(terminal.Now >= TerminalKeyboardProtocol.TimeoutMilliseconds);
        terminal.Decoder.Feed("\x1b[?1u"u8); Assert.False(terminal.Decoder.HasEvents);
        terminal.Reply = true; p.Enable(false); Assert.Empty(terminal.Stack);
    }

    [Fact]
    public void ActivationMismatchAndExceptionRestoreTheOriginalStack()
    {
        var rejected = new Terminal { RejectFlag = true };
        rejected.Protocol.Enable(false); Assert.Equal(8, rejected.Flags); Assert.Empty(rejected.Stack);
        var failed = new Terminal { ThrowOnActivationQuery = true };
        Assert.Throws<IOException>(() => failed.Protocol.Enable(false));
        Assert.Equal(8, failed.Flags); Assert.Empty(failed.Stack);
    }

    [Fact]
    public void ScreenSwitchRequiresPopOnTheOriginalScreen()
    {
        var t = new Terminal(); t.Protocol.Enable(false);
        Assert.Throws<InvalidOperationException>(() => t.Protocol.Enable(true));
        Assert.Single(t.Stack); t.Protocol.Restore();
        t.Protocol.Enable(true); Assert.Equal(1, t.Flags); t.Protocol.Restore();
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(31)]
    public void ResponsesAreConsumedAtEveryFragmentBoundary(int flags)
    {
        var bytes = Encoding.ASCII.GetBytes($"\x1b[?{flags}u");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var d = new TerminalInputDecoder(); d.Feed(bytes.AsSpan(0, split)); d.Feed(bytes.AsSpan(split));
            Assert.Equal(1, d.KeyboardReplyRevision); Assert.Equal(flags, d.KeyboardReplyFlags);
            Assert.False(d.HasEvents); Assert.False(d.HasPartialSequence);
        }
    }

    [Theory]
    [InlineData("?32u")][InlineData("?-1u")][InlineData("?1;2u")][InlineData("?1:1u")]
    public void MalformedRepliesAreWholeControls(string wire)
    {
        var d = new TerminalInputDecoder(); d.Feed(Encoding.ASCII.GetBytes("\x1b[" + wire));
        Assert.Equal(0, d.KeyboardReplyRevision);
        Assert.Equal(TerminalInputEventKind.ControlSequence, d.Take().Kind); Assert.False(d.HasEvents);
    }
}
