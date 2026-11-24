using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class InputSelectionTests
{
    [Theory]
    [InlineData("中文🙂é\r\n\tcode")]
    [InlineData("/new")]
    [InlineData("")]
    public void SelectAllReplacesEntireSourceWithoutChangingItUntilInsertion(string text)
    {
        var draft = new InputDraft(); draft.InsertPaste(text); draft.SelectAll();
        Assert.Equal(text, draft.Buffer.ToString());
        Assert.Equal(0, draft.SelectionStart); Assert.Equal(text.Length, draft.SelectionEnd);
        draft.InsertText("X");
        Assert.Equal("X", draft.Buffer.ToString()); Assert.Equal(1, draft.Cursor);
        Assert.False(draft.HasSelection);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 5)]
    public void OrdinaryHorizontalMovementCollapsesWithoutEditing(int direction, int expected)
    {
        var draft = new InputDraft(); draft.Buffer.Append("abcdef");
        draft.SelectionAnchor = 1; draft.Cursor = 5;
        Assert.True(draft.CollapseSelection(direction));
        Assert.Equal(expected, draft.Cursor); Assert.Null(draft.SelectionAnchor);
        Assert.Equal("abcdef", draft.Buffer.ToString());
    }

    [Fact]
    public void PasteReplacesSelectionOnceAndDraftSnapshotsPreserveAllState()
    {
        var draft = new InputDraft(); draft.Reset(1); draft.InsertPaste("before中文🙂after");
        draft.SelectionAnchor = 6; draft.Cursor = 10; draft.FirstVisibleRow = 3; draft.PreferredColumn = 7;
        var copy = draft.Copy();
        Assert.True(draft.Previous(["history"])); Assert.False(draft.HasSelection);
        Assert.True(draft.Next(["history"]));
        Assert.Equal(copy.SelectionAnchor, draft.SelectionAnchor);
        Assert.Equal(copy.Cursor, draft.Cursor); Assert.Equal(copy.FirstVisibleRow, draft.FirstVisibleRow);
        Assert.Equal(copy.PreferredColumn, draft.PreferredColumn);
        draft.InsertPaste("replacement\r\n🙂");
        Assert.Equal("beforereplacement\r\n🙂after", draft.Buffer.ToString());
        Assert.False(draft.HasSelection); Assert.True(draft.ContainsPaste);
        draft.Restore(copy); Assert.True(draft.DeleteSelection());
        Assert.Equal("beforeafter", draft.Buffer.ToString());
    }

    [Fact]
    public void VerticalExtensionKeepsAnchorAndExpectedColumnAcrossShortRows()
    {
        var draft = new InputDraft(); draft.Buffer.Append("abcdef\nx\n123456"); draft.Cursor = 5;
        draft.ExtendSelection(true);
        var projection = TerminalTextLayout.LayoutInput(draft.Buffer.ToString(), draft.Cursor, 20, "> ");
        Assert.True(draft.MoveVertical(projection, 1));
        projection = TerminalTextLayout.LayoutInput(draft.Buffer.ToString(), draft.Cursor, 20, "> ");
        Assert.True(draft.MoveVertical(projection, 1));
        Assert.Equal(5, draft.SelectionAnchor); Assert.Equal(14, draft.Cursor);
        Assert.Equal(7, draft.PreferredColumn);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(32)]
    [InlineData(80)]
    public void SelectedVisibleCellsExcludePromptAndPreservePlainUnicodeAndTabExpansion(int width)
    {
        const string text = "中文🙂é\tZ\r\nlong text";
        var projection = TerminalTextLayout.LayoutInput(text, text.Length, width, "> ");
        for (var row = 0; row < projection.Rows.Count; row++)
        {
            var styled = projection.SelectedRow(row, 0, text.Length, true);
            Assert.StartsWith(row == 0 ? "> " : "  ", styled);
            Assert.Contains("\u001b[7m", styled, StringComparison.Ordinal);
            Assert.Equal(projection.Rows[row].Text.TrimEnd(), TerminalTextLayout.Plain(styled).TrimEnd());
            Assert.DoesNotContain('\u001b', projection.SelectedRow(row, 0, text.Length, false));
        }
    }
}
