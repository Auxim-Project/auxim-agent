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

        return runes.Sum(RuneWidth);
    }

    public static int Measure(string text)
    {
        var width = 0;
        var max = 0;
        var elements = StringInfo.GetTextElementEnumerator(Plain(text));
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element is "\r\n" or "\r" or "\n")
            {
                max = Math.Max(max, width);
                width = 0;
            }
            else width += element == "\t" ? 4 - width % 4 : CellWidth(element);
        }
        return Math.Max(max, width);
    }

    public static string TakeCells(string text, int columns)
    {
        var output = new StringBuilder();
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(Plain(text));
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element is "\r\n" or "\r" or "\n" || element.Length == 1 && char.IsSurrogate(element[0])) break;
            var width = element == "\t" ? 4 - used % 4 : CellWidth(element);
            if (used + width > columns) break;
            if (element == "\t") output.Append(' ', width);
            else output.Append(element);
            used += width;
        }
        return output.ToString();
    }

    public static string Pad(string text, int columns) =>
        text + new string(' ', Math.Max(0, columns - Measure(text)));

    // Keeps source indices intact: display substitutions and soft wraps never
    // enter the draft. Reserve one cell so an end-of-row caret cannot auto-wrap.
    public static InputProjection LayoutInput(string text, int cursor, int columns, string prompt, bool forwardAffinity = false)
    {
        if (columns <= 0) return new([new InputVisualRow(0, text.Length, "")], 0, 0);
        prompt = Plain(prompt);
        if (TerminalTextLayout.Measure(prompt) + 2 > columns) prompt = columns >= 3 ? "> " : "";
        var prefix = TerminalTextLayout.Measure(prompt);
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

    private static int RuneWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format
            or UnicodeCategory.Control)
        {
            return 0;
        }

        return IsWide(rune.Value) ? 2 : 1;
    }

    private static bool IsWide(int value)
    {
        return value is >= 0x1100 and <= 0x115f
            or >= 0x231a and <= 0x231b
            or >= 0x2329 and <= 0x232a
            or >= 0x23e9 and <= 0x23ec
            or >= 0x23f0 and <= 0x23f0
            or >= 0x23f3 and <= 0x23f3
            or >= 0x25fd and <= 0x25fe
            or >= 0x2614 and <= 0x2615
            or >= 0x2648 and <= 0x2653
            or >= 0x267f and <= 0x267f
            or >= 0x2693 and <= 0x2693
            or >= 0x26a1 and <= 0x26a1
            or >= 0x26aa and <= 0x26ab
            or >= 0x26bd and <= 0x26be
            or >= 0x26c4 and <= 0x26c5
            or >= 0x26ce and <= 0x26ce
            or >= 0x26d4 and <= 0x26d4
            or >= 0x26ea and <= 0x26ea
            or >= 0x26f2 and <= 0x26f3
            or >= 0x26f5 and <= 0x26f5
            or >= 0x26fa and <= 0x26fa
            or >= 0x26fd and <= 0x26fd
            or >= 0x2705 and <= 0x2705
            or >= 0x270a and <= 0x270b
            or >= 0x2728 and <= 0x2728
            or >= 0x274c and <= 0x274c
            or >= 0x274e and <= 0x274e
            or >= 0x2753 and <= 0x2755
            or >= 0x2757 and <= 0x2757
            or >= 0x2795 and <= 0x2797
            or >= 0x27b0 and <= 0x27b0
            or >= 0x27bf and <= 0x27bf
            or >= 0x2b1b and <= 0x2b1c
            or >= 0x2b50 and <= 0x2b50
            or >= 0x2b55 and <= 0x2b55
            or >= 0x2e80 and <= 0xa4cf
            or >= 0xac00 and <= 0xd7a3
            or >= 0xf900 and <= 0xfaff
            or >= 0xfe10 and <= 0xfe19
            or >= 0xfe30 and <= 0xfe6f
            or >= 0xff00 and <= 0xff60
            or >= 0xffe0 and <= 0xffe6
            or >= 0x1f300 and <= 0x1f64f
            or >= 0x1f900 and <= 0x1f9ff
            or >= 0x20000 and <= 0x3fffd;
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
        var width = TerminalTextLayout.Measure(text);
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
