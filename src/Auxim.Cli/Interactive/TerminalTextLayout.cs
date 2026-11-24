using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Auxim.Cli.Interactive;

// Plain preview rows use terminal cells, not UTF-16 string lengths. Formal
// Markdown remains the responsibility of MarkdownConsoleRenderer.
internal static class TerminalTextLayout
{
    private static readonly Regex ControlSequence = new("\u001b\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);

    public static string Plain(string text) => new(ControlSequence.Replace(text, "")
        .Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());

    public static int CellWidth(string element)
    {
        var runes = element.EnumerateRunes().ToArray();
        if (runes.Any(r => r.Value is 0x200d or 0xfe0f or 0x20e3 or >= 0x1f3fb and <= 0x1f3ff)
            || runes.Count(r => r.Value is >= 0x1f1e6 and <= 0x1f1ff) == 2
            || runes.Any(r => r.Value is >= 0x1f680 and <= 0x1faff))
        {
            return 2;
        }

        return LineEditor.DisplayWidth(element);
    }

    // Keeps source indices intact: display substitutions and soft wraps never
    // enter the draft. Reserve one cell so an end-of-row caret cannot auto-wrap.
    public static InputProjection LayoutInput(string text, int cursor, int columns, string prompt, bool forwardAffinity = false)
    {
        if (columns <= 0) return new([new InputVisualRow(0, text.Length, "")], 0, 0);
        prompt = Plain(prompt);
        if (ConsoleTheme.CellWidth(prompt) + 2 > columns) prompt = columns >= 3 ? "> " : "";
        var prefix = ConsoleTheme.CellWidth(prompt);
        var budget = Math.Max(0, columns - prefix - 1);
        if (budget == 0) return new([new InputVisualRow(0, text.Length, prompt)], 0, Math.Min(prefix, columns - 1));
        cursor = Math.Clamp(cursor, 0, text.Length);
        var rows = new List<InputVisualRow>();
        var stops = new List<InputCaretStop>();
        var spans = new List<InputVisualSpan>();
        var row = new StringBuilder();
        var used = 0;
        var start = 0;
        var caretRow = 0;
        var caretColumn = prefix;
        var mapped = false;
        void Caret(int index)
        {
            stops.Add(new(index, rows.Count, prefix + used, false));
            if (!mapped && cursor <= index)
            {
                caretRow = rows.Count; caretColumn = prefix + used; mapped = true;
            }
        }
        void Break(int end, int next)
        {
            rows.Add(new(start, end, (rows.Count == 0 ? prompt : new string(' ', prefix)) + row));
            row.Clear(); used = 0; start = next;
        }
        for (var index = 0; index < text.Length;)
        {
            var element = StringInfo.GetNextTextElement(text, index);
            var end = index + element.Length;
            if (element is "\r\n" or "\r" or "\n")
            {
                spans.Add(new(index, end, rows.Count, prefix + used, 1));
                Caret(index); Break(index, end); index = end; continue;
            }
            var display = element == "\t" ? new string(' ', 4 - used % 4)
                : element.Any(char.IsControl) || (element.Length == 1 && char.IsHighSurrogate(element[0])) ? "" : element;
            var width = element == "\t" ? display.Length : CellWidth(display);
            if (width > budget) { display = "?"; width = 1; }
            // At a soft-wrap boundary the insertion point belongs to the end
            // of the preceding row; the reserved cell makes that position safe.
            Caret(index);
            if (used > 0 && used + width > budget)
            {
                Break(index, index);
                stops.Add(new(index, rows.Count, prefix, true));
                if (cursor == index && forwardAffinity)
                {
                    caretRow = rows.Count; caretColumn = prefix; mapped = true;
                }
                if (element == "\t")
                {
                    display = budget >= 4 ? "    " : "?";
                    width = display.Length;
                }
            }
            spans.Add(new(index, end, rows.Count, prefix + used, width));
            row.Append(display); used += width; index = end;
        }
        Caret(text.Length);
        Break(text.Length, text.Length);
        return new(rows, caretRow, caretColumn, stops, spans);
    }

    public static IReadOnlyList<string> Wrap(string text, int columns) => WrapCore(Plain(text), columns, false);

    public static IReadOnlyList<string> WrapStyled(string text, int columns) => WrapCore(text, columns, true);

    internal static IReadOnlyList<string> WrapTranscript(string text, int columns,
        Action<int, int, int, int, int> span) => WrapCore(text, columns, true, span);

    internal static string HighlightCells(string styled, int start, int end)
    {
        var output = new StringBuilder();
        var column = 0;
        var active = false;
        for (var index = 0; index < styled.Length;)
        {
            var match = styled[index] == '\u001b' ? ControlSequence.Match(styled, index) : null;
            if (match is { Success: true } && match.Index == index)
            {
                output.Append(match.Value);
                if (active) output.Append("\u001b[7m");
                index += match.Length;
                continue;
            }
            var element = StringInfo.GetNextTextElement(styled, index);
            var width = CellWidth(element);
            var selected = column < end && column + width > start;
            if (active != selected) output.Append(selected ? "\u001b[7m" : "\u001b[27m");
            active = selected;
            output.Append(element);
            column += width;
            index += element.Length;
        }
        if (active) output.Append("\u001b[27m");
        return output.ToString();
    }

    private static IReadOnlyList<string> WrapCore(string text, int columns, bool styled,
        Action<int, int, int, int, int>? span = null)
    {
        columns = Math.Max(1, columns);
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
        var rows = new List<string>();
        var row = new StringBuilder();
        var used = 0;
        var style = "";
        var plainIndex = 0;
        void BreakRow()
        {
            rows.Add(row + (style.Length == 0 ? "" : "\u001b[0m"));
            row.Clear(); row.Append(style); used = 0;
        }
        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '\u001b')
            {
                var match = ControlSequence.Match(text, index);
                if (match.Success && match.Index == index)
                {
                    if (styled && match.Value.EndsWith('m'))
                    {
                        row.Append(match.Value);
                        style = match.Value is "\u001b[0m" or "\u001b[m" ? "" : style + match.Value;
                    }
                    index += match.Length;
                }
                else index++;
                continue;
            }
            var element = StringInfo.GetNextTextElement(text, index);
            var length = element.Length;
            index += length;
            if (element is "\r\n" or "\r" or "\n")
            {
                BreakRow();
                continue;
            }

            if (element == "\t")
            {
                var spaces = 4 - used % 4;
                for (var i = 0; i < spaces; i++)
                {
                    if (used == columns)
                    {
                        BreakRow();
                    }

                    span?.Invoke(rows.Count, used, 1, plainIndex, plainIndex + 1);
                    row.Append(' '); used++;
                }

                plainIndex++;
                continue;
            }

            var width = CellWidth(element);
            if (element.Any(c => char.IsControl(c))) continue;
            if (width > columns)
            {
                element = "?";
                width = 1;
            }

            if (used > 0 && used + width > columns)
            {
                BreakRow();
            }

            span?.Invoke(rows.Count, used, width, plainIndex, plainIndex + length);
            row.Append(element);
            used += width;
            plainIndex += length;
        }

        rows.Add(row + (style.Length == 0 ? "" : "\u001b[0m"));
        return rows;
    }
}

internal sealed record InputVisualRow(int StartUtf16, int EndUtf16, string Text);
internal sealed record InputCaretStop(int Index, int Row, int Column, bool ForwardAffinity);
internal sealed record InputVisualSpan(int Start, int End, int Row, int Column, int Width);
internal sealed record InputProjection(IReadOnlyList<InputVisualRow> Rows, int CursorRow, int CursorColumn,
    IReadOnlyList<InputCaretStop>? CaretStops = null, IReadOnlyList<InputVisualSpan>? Spans = null)
{
    internal string SelectedRow(int row, int start, int end, bool controls)
    {
        var text = Rows[row].Text;
        if (!controls || start == end || Spans is null) return text;
        var selected = Spans.Where(span => span.Row == row && span.Start < end && span.End > start && span.Width > 0).ToArray();
        if (selected.Length == 0) return text;
        // A selected explicit newline gets one visible cell at the row end.
        var width = ConsoleTheme.CellWidth(text);
        if (selected.Any(span => span.Column >= width)) text += " ";
        var output = new StringBuilder(); var column = 0; var active = false;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var highlight = selected.Any(span => column >= span.Column && column < span.Column + span.Width);
            if (highlight != active) output.Append(highlight ? "\u001b[7m" : "\u001b[27m");
            output.Append(element); active = highlight; column += TerminalTextLayout.CellWidth(element);
        }
        if (active) output.Append("\u001b[27m");
        return output.ToString();
    }
    public InputCaretStop? VerticalTarget(int delta, int preferredColumn)
    {
        var row = CursorRow + delta;
        if (row < 0 || row >= Rows.Count || CaretStops is null) return null;
        // Nearest terminal cell, always at a grapheme boundary. At a soft wrap
        // the same source index has two legal visual positions (caret affinity).
        return HitTest(row, preferredColumn);
    }

    public InputCaretStop? HitTest(int row, int column) => CaretStops?
            .Where(stop => stop.Row == row)
            .OrderBy(stop => Math.Abs((long)stop.Column - column))
            .ThenBy(stop => stop.Column).FirstOrDefault();
    public int FollowCursor(int first, int height)
    {
        first = Math.Clamp(first, 0, Math.Max(0, Rows.Count - height));
        if (CursorRow < first) return CursorRow;
        return CursorRow >= first + height ? CursorRow - height + 1 : first;
    }

    public void Write(TextWriter output, ScreenLayout layout, int first, bool empty, int selectionStart = 0, int selectionEnd = 0)
    {
        for (var i = 0; i < layout.InputRows; i++)
        {
            var text = first + i < Rows.Count ? Rows[first + i].Text : "";
            var placeholder = empty && i == 0;
            if (placeholder) text += " Message Auxim...";
            if (first + i < Rows.Count && selectionStart != selectionEnd)
                text = SelectedRow(first + i, selectionStart, selectionEnd, Ansi.ControlSequencesEnabled);
            text = layout.InputColumns <= 0 ? "" : TerminalTextLayout.WrapStyled(text, layout.InputColumns)[0];
            layout.WriteComposerRow(output, layout.InputRow + i,
                ConsoleTheme.FrameComposerLine(ConsoleTheme.ComposerLine(text, layout.ComposerInnerWidth, placeholder), layout));
        }
        var row = layout.InputRow + Math.Clamp(CursorRow - first, 0, layout.InputRows - 1);
        var column = layout.ComposerInnerLeft + Math.Clamp(CursorColumn, 0, Math.Max(0, layout.InputColumns - 1));
        output.Write($"\u001b[{row};{column}H"); output.Flush();
    }
}
