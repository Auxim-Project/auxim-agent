using System.Globalization;
using System.Text;

namespace Auxim.Cli.Interactive;

// Coordinates are one-based terminal rows. Empty regions have height zero.
internal readonly record struct ScreenLayout(int Width, int Height, bool IsDashboard = false, int RequestedInputRows = 1)
{
    public int Columns => Math.Max(0, Width - 1);
    public int ComposerWidth => Math.Min(100, Math.Max(0, Columns - (Width >= 80 ? 4 : 0)));
    public int ComposerLeft => (Columns - ComposerWidth) / 2 + 1;
    public int HeaderHeight => Height >= 14 && Columns >= 40 ? 2 : Height >= 3 ? 1 : 0;
    public int ContentTop => HeaderHeight + 1;
    public bool HasComposerBorder => Height >= 16 && ComposerWidth >= 40;
    public int ComposerInset => HasComposerBorder ? 1 : 0;
    public int ComposerInnerLeft => ComposerLeft + ComposerInset;
    public int ComposerInnerWidth => Math.Max(0, ComposerWidth - 2 * ComposerInset);
    public int BaseComposerHeight => HasComposerBorder ? 5 : Height >= 10 && ComposerWidth >= 20 ? 4
        : Height >= 6 && ComposerWidth >= 12 ? 3 : Height >= 4 ? 2 : 1;
    public int InputRows
    {
        get
        {
            if (InputColumns <= 0) return 1;
            var available = IsWelcomeLayout ? Height - HeaderHeight - 2 - 5
                : Height - HeaderHeight - GapHeight - 1;
            var maximum = Math.Max(1, Math.Min(8, available) - BaseComposerHeight + 1);
            return Math.Clamp(RequestedInputRows, 1, maximum);
        }
    }
    public int ComposerHeight => BaseComposerHeight + InputRows - 1;
    public bool IsWelcomeLayout => IsDashboard && Columns >= 40 && Height >= 14;
    public int WelcomeRow => IsWelcomeLayout ? ContentTop + Math.Max(0, (Height - HeaderHeight - 2 - (ComposerHeight + 5)) / 2) : ContentTop;
    public int ComposerTop => IsWelcomeLayout ? WelcomeRow + 3 : Height - ComposerHeight + 1;
    public int ShortcutsRow => IsDashboard ? IsWelcomeLayout ? ComposerTop + ComposerHeight + 1
        : ContentHeight > 0 ? ComposerTop - GapHeight - 1 : 0 : 0;
    public int MetadataRow => !IsDashboard ? 0 : IsWelcomeLayout ? Height - 1
        : ShortcutsRow - 2 >= ContentTop + 3 ? ShortcutsRow - 2 : 0;
    public int GapHeight => Height >= 7 ? 1 : 0;
    public int InputRow => ComposerTop + (BaseComposerHeight >= 3 ? 1 : 0);
    public int HintHeight => ComposerHeight >= 2 ? 1 : 0;
    public int SeparatorRow => HasComposerBorder ? InputRow + InputRows : 0;
    public int HintRow => HintHeight == 0 ? 0 : InputRow + InputRows - 1 + (HasComposerBorder ? 2 : 1);
    public int InputRightPadding => ComposerWidth >= 12 ? 2 : 0;
    public int InputColumns => Math.Max(0, ComposerInnerWidth - InputRightPadding);
    public int InputStartColumn => Math.Min(TerminalTextLayout.Measure(ConsoleTheme.ComposerPrompt(ComposerWidth)),
        Math.Max(0, InputColumns - 1)) + ComposerInnerLeft;
    public int ContentHeight => Math.Max(0, ComposerTop - GapHeight - ContentTop);
    public int ContentWidth => Math.Max(1, ConsoleTheme.ReadableWidth(Columns));

    public static ScreenLayout Create(int width, int height, bool dashboard = false, int inputRows = 1)
    {
        var layout = new ScreenLayout(Math.Max(1, width), Math.Max(1, height), dashboard, inputRows);
        return layout with { RequestedInputRows = layout.InputRows };
    }

    public void WriteComposerRow(TextWriter output, int row, string text)
    {
        // The caller supplies a padded row. Never erase a whole terminal line
        // here: the margins belong to the surrounding page, not the composer.
        var fitted = ComposerWidth == 0 ? "" : TerminalTextLayout.WrapStyled(text, ComposerWidth)[0];
        output.Write($"\u001b[{row};{ComposerLeft}H{fitted}");
    }

    public static void WriteRow(TextWriter output, int row, int columns, string text)
    {
        var fitted = columns <= 0 ? "" : TerminalTextLayout.WrapStyled(text, columns)[0];
        output.Write($"\u001b[{row};1H\u001b[2K{fitted}");
    }
}

// A horizontal window into the real draft, never a shortened edit buffer.
internal readonly record struct InputRow(string Text, int CursorColumn)
{
    public static InputRow Create(string prompt, string text, int cursor, int columns)
    {
        if (columns <= 0) return new("", 0);
        var prefix = TerminalTextLayout.Plain(prompt);
        if (TerminalTextLayout.Measure(prefix) + 2 > columns) prefix = columns >= 3 ? "> " : "";
        var prefixWidth = TerminalTextLayout.Measure(prefix);
        var available = columns - prefixWidth;
        var indexes = StringInfo.ParseCombiningCharacters(text);
        var elements = indexes.Select((start, i) => text[start..(i + 1 < indexes.Length ? indexes[i + 1] : text.Length)])
            .Select(e => e.Any(char.IsControl) ? " " : e).ToArray();
        var before = indexes.Count(i => i < cursor);
        var startIndex = 0;
        var used = elements.Take(before).Sum(TerminalTextLayout.CellWidth);
        // Keep a blank cell for a cursor at the end of the draft.
        while (used >= available && startIndex < before)
            used -= TerminalTextLayout.CellWidth(elements[startIndex++]);
        var row = new StringBuilder(prefix);
        var cells = 0;
        for (var i = startIndex; i < elements.Length; i++)
        {
            var width = TerminalTextLayout.CellWidth(elements[i]);
            if (cells + width > available) break;
            row.Append(elements[i]); cells += width;
        }
        return new(row.ToString(), Math.Min(columns - 1, prefixWidth + used));
    }

    public void Write(TextWriter output, ScreenLayout layout, bool placeholder = false)
    {
        // Placeholder text is a projection only; Text and CursorColumn still
        // describe the empty draft and its real insertion point.
        var display = placeholder ? Text + " Message Auxim..." : Text;
        display = layout.InputColumns == 0 ? "" : TerminalTextLayout.Wrap(display, layout.InputColumns)[0];
        layout.WriteComposerRow(output, layout.InputRow,
            ConsoleTheme.FrameComposerLine(ConsoleTheme.ComposerLine(display, layout.ComposerInnerWidth, placeholder), layout));
        output.Write($"\u001b[{layout.InputRow};{Math.Min(CursorColumn, Math.Max(0, layout.InputColumns - 1)) + layout.ComposerInnerLeft}H");
        output.Flush();
    }
}
