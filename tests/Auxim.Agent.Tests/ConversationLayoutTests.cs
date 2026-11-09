using System.Globalization;
using Auxim.Cli.Interactive;
using Auxim.Core.Runtime;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class ConversationLayoutTests
{
    [Fact]
    public void DashboardIsCompactAndPrioritizesStateOverTheActionCatalog()
    {
        var lines = InteractiveView.RenderDashboardLines(Status(), Session([]), "inline", 80);
        var text = Plain(lines);
        Assert.Contains("Welcome to Auxim.", text);
        Assert.DoesNotContain("Dashboard", text);
        Assert.DoesNotContain("AUXIM", text);
        Assert.Contains("local/placeholder", text);
        Assert.Contains("0 messages", text);
        Assert.Contains("Ready for your first message.", text);
        Assert.Contains("/help commands", text);
        Assert.Contains("/history conversation", text);
        Assert.Contains("/model setup", text);
        Assert.DoesNotContain("Actions", text);
        Assert.DoesNotContain("// git status", text);
        Assert.DoesNotContain("╭", text);
        Assert.True(lines.Count <= 14);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    public void NarrowDashboardWrapsUnicodeAndBoundsLongSessionMetadata(int width)
    {
        var session = Session([new AgentMessage("user", "你好")]) with
        {
            Title = string.Concat(Enumerable.Repeat("项目讨论 👨‍👩‍👧‍👦 e\u0301 ", 100)),
        };
        var lines = InteractiveView.RenderDashboardLines(Status(), session, "alternate", width);
        Assert.Contains("Continue this conversation.", TerminalTextLayout.Plain(string.Concat(lines)));
        Assert.Contains("项目", Plain(lines));
        Assert.True(lines.Count < 30);
        AssertRowsFit(lines, width);
        Assert.DoesNotContain("�", Plain(lines));
    }

    [Fact]
    public void HistoryUsesRoleHeadingsAndWhitespaceInsteadOfRepeatedTurnFrames()
    {
        var logical = InteractiveView.RenderTurnsToLines([
            new SessionTurn(7, new AgentMessage("user", "你好，帮我检查代码。"), new AgentMessage("assistant", "## Findings\nA **clear** answer.\n```cs\nreturn true;\n```")),
            new SessionTurn(8, new AgentMessage("user", "继续"), null),
        ]);
        var text = Plain(logical);
        Assert.Contains("You  #7", text);
        Assert.Contains("Assistant", text);
        Assert.Contains("  你好", text);
        Assert.Contains("No response yet.", text);
        Assert.Contains("return true;", text);
        Assert.DoesNotContain("[turn:", text);
        Assert.DoesNotContain(logical, line => TerminalTextLayout.Plain(line).Trim().Length > 3
            && TerminalTextLayout.Plain(line).Trim().All(c => c == '-'));
    }

    [Theory]
    [InlineData(12, 10)]
    [InlineData(24, 16)]
    [InlineData(40, 20)]
    [InlineData(80, 24)]
    public void LongUnicodeHistoryIsPaginatedByPhysicalRowsAndKeepsFooterSpace(int width, int height)
    {
        var response = string.Concat(Enumerable.Repeat("中文 🙂 👨‍👩‍👧‍👦 e\u0301 long text ", 100));
        var logical = InteractiveView.RenderTurnsToLines([
            new SessionTurn(1, new AgentMessage("user", "请详细解释"), new AgentMessage("assistant", response)),
        ]);
        var rows = InteractiveView.LayoutTranscript(logical, width);
        AssertRowsFit(rows, width);
        var layout = InteractiveView.HistoryLayout(width, height, rows.Count);
        var viewport = new TranscriptViewport();
        viewport.Open(rows, true, layout.BodyHeight);
        Assert.Equal(layout.BodyHeight, viewport.GetVisibleLines(layout.BodyHeight).Count());
        var counter = layout.ShowCounter ? ConsoleTheme.FooterLines(width, $"{rows.Count}-{rows.Count} / {rows.Count}").Count : 0;
        Assert.True(layout.BodyHeight + layout.Hints.Count + counter + 3 <= height);
        var bottom = viewport.Offset;
        viewport.Scroll(-1);
        _ = viewport.GetVisibleLines(layout.BodyHeight).ToArray();
        Assert.Equal(bottom - 1, viewport.Offset);
    }

    [Fact]
    public void LargeHistoryKeepsTurnNumbersAndTailClippingWithoutRepeatedBorders()
    {
        var turns = Enumerable.Range(1, 500).Select(n => new SessionTurn(n,
            new AgentMessage("user", $"question {n}"), new AgentMessage("assistant", $"answer {n}")));
        var rows = InteractiveView.LayoutTranscript(InteractiveView.RenderTurnsToLines(turns), 40);
        var viewport = new TranscriptViewport();
        viewport.Open(rows, true, 12);
        var tail = Plain(viewport.GetVisibleLines(12));
        Assert.Contains("#500", tail);
        Assert.Contains("answer 500", tail);
        Assert.DoesNotContain("question 1\n", tail);
        var offset = viewport.Offset;
        viewport.Reflow(rows, 12, false);
        Assert.Equal(offset, viewport.Offset);
        viewport.Scroll(-10);
        Assert.Equal(12, viewport.GetVisibleLines(12).Count());
    }

    [Fact]
    public void StyledPhysicalWrappingPreservesStylesWithoutCountingEscapeBytesAsCells()
    {
        var rows = TerminalTextLayout.WrapStyled("\u001b[1m中文e\u0301🙂abcdef\u001b[0m", 6);
        AssertRowsFit(rows, 6);
        Assert.Equal("中文e\u0301🙂abcdef", string.Concat(rows.Select(TerminalTextLayout.Plain)));
        Assert.All(rows, row => Assert.EndsWith("\u001b[0m", row, StringComparison.Ordinal));
        Assert.All(rows, row => Assert.StartsWith("\u001b[1m", row, StringComparison.Ordinal));
    }

    [Fact]
    public void StreamingAndSavedMessagesUseTheSameNamesAndBodyIndentation()
    {
        var viewport = new TranscriptViewport();
        var streaming = new StreamingTranscript(() => [], "你好", viewport);
        streaming.Append("第一行\n第二行");
        streaming.Prepare(30, 20);
        var text = Plain(streaming);
        Assert.Contains("You\n  你好", text);
        Assert.Contains("Assistant  streaming\n  第一行\n  第二行", text);
        Assert.DoesNotContain("[assistant preview]", text);
        streaming.Reset();
        Assert.False(viewport.IsActive);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(4, 6)]
    [InlineData(12, 8)]
    [InlineData(24, 4)]
    public void VerySmallViewportsReduceHintsInsteadOfOverflowing(int width, int height)
    {
        var layout = InteractiveView.HistoryLayout(width, height, 10000);
        var footer = layout.Hints.Count == 0 ? 1 : layout.Hints.Count + 3
            + (layout.ShowCounter ? ConsoleTheme.FooterLines(width, "10000-10000 / 10000").Count : 0);
        Assert.True(layout.BodyHeight + footer <= height);
    }

    [Fact]
    public void EmptyConversationDoesNotInventMessageBlocks()
    {
        Assert.Empty(InteractiveView.RenderTurnsToLines([]));
    }

    private static AuximRuntimeStatus Status() => new("/home", "local", "placeholder", null, 90, "", "/workspace", 0);
    private static AuximRuntimeSession Session(IReadOnlyList<AgentMessage> messages) =>
        new("session-12345678", "Untitled session", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, messages);
    private static string Plain(IEnumerable<string> rows) => string.Join('\n', rows.Select(TerminalTextLayout.Plain));
    private static void AssertRowsFit(IEnumerable<string> rows, int columns)
    {
        foreach (var row in rows)
        {
            var elements = StringInfo.GetTextElementEnumerator(TerminalTextLayout.Plain(row));
            var cells = 0;
            while (elements.MoveNext()) cells += TerminalTextLayout.CellWidth(elements.GetTextElement());
            Assert.True(cells <= columns, $"{cells} cells exceeds {columns}: {row}");
        }
    }
}
