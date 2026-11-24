using System.Text;
using Auxim.Cli.Interactive;
using Xunit;
namespace Auxim.Agent.Tests;
public sealed class KittyKeyTests
{
    [Theory]
    [InlineData("13u", ConsoleKey.Enter, 0)]
    [InlineData("99;5u", ConsoleKey.C, 4)]
    [InlineData("106;5u", ConsoleKey.J, 4)]
    [InlineData("97;5u", ConsoleKey.A, 4)]
    [InlineData("112;5u", ConsoleKey.P, 4)]
    [InlineData("110;5u", ConsoleKey.N, 4)]
    [InlineData("27u", ConsoleKey.Escape, 0)]
    [InlineData("9;2u", ConsoleKey.Tab, 2)]
    [InlineData("127u", ConsoleKey.Backspace, 0)]
    public void SplitKeysKeepModifiersAndWireIdentity(string wire, ConsoleKey key, int modifiers)
    {
        var bytes = Encoding.ASCII.GetBytes("\x1b[" + wire);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var d = new TerminalInputDecoder(); d.Feed(bytes.AsSpan(0, split)); d.Feed(bytes.AsSpan(split));
            var e = d.Take(); Assert.Equal(TerminalInputEventKind.Key, e.Kind);
            Assert.Equal(key, e.Key.Key); Assert.Equal((ConsoleModifiers)modifiers, e.Key.Modifiers);
            Assert.NotNull(e.KittyCode); Assert.False(d.HasEvents); Assert.False(d.HasPartialSequence);
        }
    }
    [Theory]
    [InlineData("99;0u")][InlineData("99;+5u")][InlineData("99;257u")]
    [InlineData("100;9u")][InlineData("99;5:3u")][InlineData("13;2;13u")]
    [InlineData("?99u")][InlineData("999999999999999999u")]
    public void UnsupportedFieldsAreNeverKeys(string wire)
    {
        var d = new TerminalInputDecoder(); d.Feed(Encoding.ASCII.GetBytes("\x1b[" + wire));
        Assert.Equal(TerminalInputEventKind.ControlSequence, d.Take().Kind); Assert.False(d.HasEvents);
    }
    [Fact]
    public void ArrowsUseTheirSpecifiedLegacyFunctionalForm()
    {
        var d = new TerminalInputDecoder(); d.Feed("\x1b[1;2A\x1b[1;3B\x1b[3;2~"u8);
        Assert.Equal(ConsoleModifiers.Shift, d.Take().Key.Modifiers);
        Assert.Equal(ConsoleModifiers.Alt, d.Take().Key.Modifiers);
        Assert.Equal(ConsoleKey.Delete, d.Take().Key.Key);
    }
}
