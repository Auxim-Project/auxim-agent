namespace Auxim.Cli.Interactive;

internal static class ConsoleTheme
{
    private const int MaxWidth = 96;

    public static int ReadableWidth(int available) => Math.Max(1, Math.Min(available, MaxWidth));
    public static int ContentWidth() => Console.IsOutputRedirected ? 80 : ReadableWidth(LineEditor.TerminalWidth() - 1);

    public static string ComposerPrompt(int columns) => columns >= 12 ? TerminalGlyphs.UnicodeEnabled ? "  › " : "  > " : columns >= 4 ? "> " : "";
    public static string IdleHint => "/ Commands   Enter Send";
    public static string ComposerHint(ScreenLayout layout, bool history)
    {
        var available = layout.InputColumns - (layout.ComposerWidth >= 12 ? 2 : 0);
        var full = IdleHint + (history ? "   Esc Back" : "");
        var newline = TerminalInput.KittyEnabled ? "   Shift+Enter Newline" : "   Ctrl+J Newline";
        if ((full + newline).Length <= available) return full + newline;
        if (full.Length <= available) return full;
        if (IdleHint.Length <= available) return IdleHint;
        if (available >= 10) return "/ Commands";
        return available >= 4 ? "Send" : "";
    }

    public static string WelcomeTitle => "What would you like to do?";
    public static string WelcomeSubtitle => "Ask a question or give Auxim a task.";
    public static string WelcomeShortcuts(int columns, bool? surfaces = null)
    {
        if (columns >= 40 && (surfaces ?? Ansi.SupportsSurfaces))
            return string.Join("   ", new[] { "/history", "/model", "/help" }
                .Select(command => Ansi.Composer(Ansi.Accent("  " + command + "  "))));
        var shortcuts = columns >= 28 ? "/history   /model   /help" : columns >= 18 ? "/history /model" : "/help";
        return Ansi.Accent(shortcuts);
    }

    public static string DashboardStatus(string model, string session, int messages, int columns)
    {
        if (columns < 60) return Ansi.Muted(Summarize($"{model} / {session} / {messages} messages", columns));
        const int padding = 2;
        var available = Math.Max(0, columns - padding * 2);
        var left = Summarize("Model  " + model, available / 2 - 2);
        var right = Summarize($"{session}  /  {messages} messages", available / 2 - 2);
        return Ansi.Muted(new string(' ', padding) + left
            + new string(' ', Math.Max(2, available - TerminalTextLayout.Measure(left) - TerminalTextLayout.Measure(right))) + right);
    }

    public static string AlignToComposer(string line, ScreenLayout layout) =>
        new string(' ', layout.ComposerLeft - 1) + line;

    public static string CenterLine(string line, ScreenLayout layout) =>
        new string(' ', Math.Max(0, (layout.Columns - TerminalTextLayout.Measure(line)) / 2)) + line;

    public static string RunningHint => "Working   Ctrl+C cancel   Wheel History";
    public static string StatusHint(string text) => text.StartsWith("Error:", StringComparison.Ordinal) ? Ansi.Error(text)
        : text.StartsWith("Cancelled", StringComparison.Ordinal) ? Ansi.Warning(text) : Ansi.Muted(text);

    public static string Header(string page, string state, int columns, bool decorated = false)
    {
        if (columns <= 0) return "";
        var prefix = decorated && columns >= 16 ? TerminalGlyphs.UnicodeEnabled ? "◇ " : "* " : "";
        var brandName = prefix + "AUXIM";
        var left = page.Length == 0 ? brandName : brandName + " / " + page;
        var right = columns >= 40 ? Summarize(state, columns / 3) : "";
        var stateWidth = TerminalTextLayout.Measure(right);
        var labelWidth = Math.Max(1, columns - (stateWidth > 0 ? stateWidth + 3 : 0));
        var label = Summarize(left, labelWidth);
        var brand = label.StartsWith(brandName, StringComparison.Ordinal)
            ? Ansi.Bold(Ansi.Accent(brandName)) + Ansi.Muted(label[brandName.Length..]) : Ansi.Accent(label);
        return brand + (stateWidth > 0
            ? new string(' ', Math.Max(1, columns - TerminalTextLayout.Measure(label) - stateWidth)) + Ansi.Muted(right) : "");
    }

    public static string HeaderRule(int columns) => Ansi.Edge(TerminalGlyphs.HorizontalLine(columns));

    public static string ComposerLine(string text, int columns, bool placeholder = false)
    {
        var padding = new string(' ', Math.Max(0, columns - TerminalTextLayout.Measure(text)));
        var marker = ComposerPrompt(columns);
        var styled = marker.Length > 0 && text.StartsWith(marker, StringComparison.Ordinal)
            ? Ansi.Accent(marker) + (placeholder ? Ansi.Muted(text[marker.Length..]) : Ansi.Text(text[marker.Length..]))
            : placeholder ? Ansi.Muted(text) : Ansi.Text(text);
        return Ansi.Composer(styled + padding);
    }

    public static string FrameComposerLine(string content, ScreenLayout layout) => layout.HasComposerBorder
        ? Ansi.FocusBoundary(TerminalGlyphs.Vertical) + content + Ansi.FocusBoundary(TerminalGlyphs.Vertical) : content;

    public static string ComposerRule(ScreenLayout layout, string left, string right) =>
        Ansi.FocusBoundary(left + TerminalGlyphs.HorizontalLine(layout.ComposerInnerWidth) + right);

    public static string ComposerSeparator(ScreenLayout layout) => FrameComposerLine(
        Ansi.Composer(Ansi.Edge(TerminalGlyphs.HorizontalLine(layout.ComposerInnerWidth))), layout);

    public static void WriteComposerHint(TextWriter output, ScreenLayout layout, string hint)
    {
        if (layout.HintHeight == 0) return;
        var indent = layout.ComposerWidth >= 12 ? "  " : "";
        var available = Math.Max(0, layout.InputColumns - indent.Length);
        var text = Summarize(hint, available);
        var line = indent + text;
        layout.WriteComposerRow(output, layout.HintRow,
            FrameComposerLine(Ansi.Composer(indent + StatusHint(text)
                + new string(' ', Math.Max(0, layout.ComposerInnerWidth - TerminalTextLayout.Measure(line)))), layout));
    }

    public static void WriteComposer(TextWriter output, ScreenLayout layout, bool busy, string hint)
    {
        // Only the region owned by the composer gets a background. The gap
        // above it deliberately uses the terminal's normal background.
        for (var i = 0; i < layout.ComposerHeight; i++)
        {
            var row = layout.ComposerTop + i;
            var line = layout.HasComposerBorder && i == 0 ? ComposerRule(layout, TerminalGlyphs.TopLeft, TerminalGlyphs.TopRight)
                : layout.HasComposerBorder && i == layout.ComposerHeight - 1 ? ComposerRule(layout, TerminalGlyphs.BottomLeft, TerminalGlyphs.BottomRight)
                : row == layout.SeparatorRow ? ComposerSeparator(layout)
                : FrameComposerLine(Ansi.Composer(new string(' ', layout.ComposerInnerWidth)), layout);
            layout.WriteComposerRow(output, row, line);
        }
        WriteComposerHint(output, layout, hint);
        InputRow.Create(ComposerPrompt(layout.ComposerWidth), busy ? "Working..." : "", 0, layout.InputColumns)
            .Write(output, layout, placeholder: !busy);
    }

    public static string StreamingRow(string row)
    {
        if (row == "You") return MessageLabel("user");
        if (row == "Assistant  streaming") return MessageLabel("assistant", "streaming");
        if (row.StartsWith("Tool  ", StringComparison.Ordinal))
        {
            var parts = row.Split("  ", 3, StringSplitOptions.None);
            if (parts.Length == 3)
            {
                var detail = parts[2];
                var state = detail.StartsWith("succeeded", StringComparison.Ordinal) ? Ansi.Success(detail)
                    : detail.StartsWith("failed", StringComparison.Ordinal) ? Ansi.Error(detail) : Ansi.Muted(detail);
                return Ansi.Muted("Tool  ") + Ansi.Accent(parts[1]) + "  " + state;
            }
        }
        return row.StartsWith("[tool:", StringComparison.Ordinal) ? Ansi.Muted(row) : Ansi.Text(row);
    }

    public static string Summarize(string text, int cells)
    {
        if (cells <= 0) return "";
        text = TerminalTextLayout.Plain(text).ReplaceLineEndings(" ").Trim();
        if (TerminalTextLayout.Measure(text) <= cells) return text;
        var suffix = cells < 3 ? "." : TerminalGlyphs.Ellipsis;
        var suffixWidth = TerminalTextLayout.Measure(suffix);
        if (suffixWidth >= cells) return new string('.', cells);
        return TerminalTextLayout.TakeCells(text, cells - suffixWidth) + suffix;
    }

    public static IReadOnlyList<string> HeadingLines(string title, string? detail, int width) =>
        TerminalTextLayout.WrapStyled(Ansi.Bold(Ansi.Text(title)) + (string.IsNullOrEmpty(detail) ? "" : Ansi.Muted($"   {detail}")), width);

    public static IReadOnlyList<string> MetadataLines(string label, string value, int width)
    {
        value = TerminalTextLayout.Plain(value).ReplaceLineEndings(" ");
        if (width < 28)
        {
            if (label.Length == 0) return ConversationRow("  " + value, width);
            return HeadingLines(label, null, width).Concat(ConversationRow("  " + value, width)).ToArray();
        }
        var prefix = TerminalTextLayout.Pad(label, 9) + " ";
        return LabelledRows(Ansi.Muted(prefix), value, width).ToArray();
    }

    public static string MessageLabel(string role, string? detail = null) =>
        (role == "user" ? Ansi.Bold(Ansi.Text("You")) : Ansi.Bold(Ansi.Accent("Assistant"))) + (detail is null ? "" : Ansi.Muted($"  {detail}"));

    public static IReadOnlyList<string> ConversationRow(string logicalLine, int width)
    {
        var indent = logicalLine.StartsWith("  ", StringComparison.Ordinal) && width > 2 ? "  " : "";
        var content = indent.Length > 0 ? logicalLine[2..] : logicalLine;
        return TerminalTextLayout.WrapStyled(content, width - indent.Length)
            .Select(row => Ansi.Text(indent + row)).ToArray();
    }

    public static IReadOnlyList<string> FooterLines(int width, params string[] hints) =>
        TerminalTextLayout.WrapStyled(Ansi.Muted(string.Join("   ", hints)), width);

    public static void Section(string title)
    {
        Console.WriteLine();
        foreach (var line in HeadingLines(title, null, ContentWidth())) Console.WriteLine(line);
    }

    public static void Pair(string label, string value)
    {
        foreach (var line in LabelledRows(Ansi.Muted(TerminalTextLayout.Pad(label, 12) + " "), value, ConsoleWidth()))
            Console.WriteLine(line);
    }

    public static void Hint(params string[] hints)
    {
        foreach (var line in TerminalTextLayout.WrapStyled(Ansi.Muted(string.Join("  ", hints)), ConsoleWidth()))
            Console.WriteLine(line);
    }

    public static void Panel(string title, IEnumerable<(string Label, string Value)> rows)
    {
        Section(title);
        foreach (var line in PanelRows(rows, Math.Min(ConsoleWidth(), MaxWidth))) Console.WriteLine(line);
    }

    internal static IEnumerable<string> PanelRows(IEnumerable<(string Label, string Value)> rows, int width) =>
        Columns(rows.ToArray(), width, (row, columns) =>
            LabelledRows(Ansi.Muted(TerminalTextLayout.Pad(row.Label, 12) + " "), row.Value, columns).ToArray());

    public static void CommandGrid(IEnumerable<InteractiveCommand> commands)
    {
        foreach (var line in CommandRows(commands, Math.Min(ConsoleWidth(), MaxWidth))) Console.WriteLine(line);
    }

    internal static IEnumerable<string> CommandRows(IEnumerable<InteractiveCommand> commands, int width) =>
        Columns(commands.ToArray(), width, (command, columns) =>
            LabelledRows(Ansi.Accent(TerminalTextLayout.Pad(command.Name, 16) + " "), command.Description, columns).ToArray());

    private static IEnumerable<string> LabelledRows(string prefix, string value, int width)
    {
        var indent = TerminalTextLayout.Measure(prefix);
        if (indent >= width - 2)
        {
            foreach (var line in TerminalTextLayout.Wrap(TerminalTextLayout.Plain(prefix).TrimEnd(), width)) yield return line;
            foreach (var line in TerminalTextLayout.WrapStyled(value, width)) yield return line;
            yield break;
        }
        var lines = TerminalTextLayout.WrapStyled(value, width - indent);
        for (var i = 0; i < lines.Count; i++)
            yield return (i == 0 ? prefix : new string(' ', indent)) + lines[i];
    }

    private static IEnumerable<string> Columns<T>(IReadOnlyList<T> items, int width, Func<T, int, string[]> format)
    {
        if (width < 88)
        {
            foreach (var item in items)
                foreach (var line in format(item, width)) yield return line;
            yield break;
        }

        var split = (items.Count + 1) / 2;
        var leftWidth = (width - 2) / 2;
        for (var i = 0; i < split; i++)
        {
            var left = format(items[i], leftWidth);
            var right = i + split < items.Count ? format(items[i + split], width - leftWidth - 2) : [];
            for (var row = 0; row < Math.Max(left.Length, right.Length); row++)
                yield return row >= right.Length ? left[row]
                    : TerminalTextLayout.Pad(row < left.Length ? left[row] : "", leftWidth) + "  " + right[row];
        }
    }

    public static void Rule()
    {
        Console.WriteLine(RuleText());
    }

    public static string RuleText()
    {
        return Ansi.Muted(new string('-', Math.Min(ConsoleWidth(), MaxWidth)));
    }

    public static string Badge(string text)
    {
        return $"{Ansi.Muted("[")}{Ansi.Accent(text)}{Ansi.Muted("]")}";
    }

    public static int ConsoleWidth()
    {
        if (Console.IsOutputRedirected)
        {
            return 80;
        }

        try
        {
            return Math.Max(Console.WindowWidth - 1, 1);
        }
        catch (IOException)
        {
            return 80;
        }
    }
}
