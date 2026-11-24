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
            + new string(' ', Math.Max(2, available - CellWidth(left) - CellWidth(right))) + right);
    }

    public static string AlignToComposer(string line, ScreenLayout layout) =>
        new string(' ', layout.ComposerLeft - 1) + line;

    public static string CenterLine(string line, ScreenLayout layout) =>
        new string(' ', Math.Max(0, (layout.Columns - CellWidth(line)) / 2)) + line;

    public static string RunningHint => "Working   Ctrl+C cancel   Wheel History";
    public static string StatusHint(string text) => text.StartsWith("Error:", StringComparison.Ordinal) ? Ansi.Error(text)
        : text.StartsWith("Cancelled", StringComparison.Ordinal) ? Ansi.Warning(text) : Ansi.Muted(text);

    public static string Header(string page, string state, int columns, bool decorated = false)
    {
        if (columns <= 0) return "";
        var prefix = decorated && columns >= 16 ? TerminalGlyphs.UnicodeEnabled ? "◇ " : "* " : "";
        var brandName = prefix + "AUXIM";
        var left = page.Length == 0 ? brandName : brandName + " / " + page;
        var right = columns >= 40 ? state : "";
        var labelWidth = Math.Max(1, columns - (right.Length > 0 ? right.Length + 3 : 0));
        var label = Summarize(left, labelWidth);
        var brand = label.StartsWith(brandName, StringComparison.Ordinal)
            ? Ansi.Bold(Ansi.Accent(brandName)) + Ansi.Muted(label[brandName.Length..]) : Ansi.Accent(label);
        return brand + (right.Length > 0
            ? new string(' ', Math.Max(1, columns - CellWidth(label) - right.Length)) + Ansi.Muted(right) : "");
    }

    public static string HeaderRule(int columns) => Ansi.Edge(TerminalGlyphs.HorizontalLine(columns));

    public static int CellWidth(string text)
    {
        var plain = TerminalTextLayout.Plain(text);
        return System.Globalization.StringInfo.ParseCombiningCharacters(plain)
            .Sum(i => TerminalTextLayout.CellWidth(System.Globalization.StringInfo.GetNextTextElement(plain, i)));
    }

    public static string ComposerLine(string text, int columns, bool placeholder = false)
    {
        var padding = new string(' ', Math.Max(0, columns - CellWidth(text)));
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
                + new string(' ', Math.Max(0, layout.ComposerInnerWidth - CellWidth(line)))), layout));
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
        if (TerminalTextLayout.Wrap(text, cells).Count == 1) return text;
        var suffix = cells < 3 ? "." : TerminalGlyphs.Ellipsis;
        var suffixWidth = CellWidth(suffix);
        if (suffixWidth >= cells) return new string('.', cells);
        return TerminalTextLayout.Wrap(text, cells - suffixWidth)[0] + suffix;
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
        var prefix = label.PadRight(9) + " ";
        return TerminalTextLayout.WrapStyled(value, width - prefix.Length)
            .Select((line, index) => (index == 0 ? Ansi.Muted(prefix) : new string(' ', prefix.Length)) + line).ToArray();
    }

    public static string MessageLabel(string role, string? detail = null) =>
        (role == "user" ? Ansi.Bold(Ansi.Text("You")) : Ansi.Bold(Ansi.Accent("Assistant"))) + (detail is null ? "" : Ansi.Muted($"  {detail}"));

    public static IReadOnlyList<string> MessageLines(string role, IEnumerable<string> body, string? detail = null) =>
        new[] { MessageLabel(role, detail) }.Concat(body.Select(line => "  " + line)).Append("").ToArray();

    public static IReadOnlyList<string> ConversationRow(string logicalLine, int width, bool styled = true)
    {
        var indent = logicalLine.StartsWith("  ", StringComparison.Ordinal) && width > 2 ? "  " : "";
        var content = indent.Length > 0 ? logicalLine[2..] : logicalLine;
        var rows = styled ? TerminalTextLayout.WrapStyled(content, width - indent.Length)
            : TerminalTextLayout.Wrap(content, width - indent.Length);
        return rows.Select(row => styled ? Ansi.Text(indent + row) : indent + row).ToArray();
    }

    public static IReadOnlyList<string> FooterLines(int width, params string[] hints) =>
        TerminalTextLayout.WrapStyled(Ansi.Muted(string.Join("   ", hints)), width);

    public static void Title(string title, string subtitle)
    {
        WriteWrapped($"{Ansi.Bold(Ansi.Accent(title))} {Ansi.Muted(subtitle)}", ConsoleWidth());
        Console.WriteLine(RuleText());
    }

    public static void Banner(string title, string subtitle)
    {
        Console.WriteLine();
        var width = Math.Min(ConsoleWidth(), MaxWidth);
        Console.WriteLine(Ansi.Accent($"{TerminalGlyphs.TopLeft}{TerminalGlyphs.HorizontalLine(width - 2)}{TerminalGlyphs.TopRight}"));
        WriteBoxLines($"{Ansi.Bold(title.ToUpperInvariant())}  {Ansi.Muted(subtitle)}", width, Ansi.Accent);
        WriteBoxLines(Ansi.Muted("portable local agent for workspace automation"), width, Ansi.Accent);
        Console.WriteLine(Ansi.Accent($"{TerminalGlyphs.BottomLeft}{TerminalGlyphs.HorizontalLine(width - 2)}{TerminalGlyphs.BottomRight}"));
    }

    public static void StatusBar(params (string Label, string Value)[] items)
    {
        Console.WriteLine();
        var width = ConsoleWidth();
        var line = $"  {string.Join("  ", items.Select(item => Chip(item.Label, item.Value)))}";
        if (VisibleLength(line) <= width)
        {
            Console.WriteLine(line);
            return;
        }

        foreach (var item in items)
        {
            var prefix = $"  {item.Label.PadRight(8)} ";
            var valueWidth = Math.Max(8, width - prefix.Length);
            var valueLines = WrapVisible(item.Value, valueWidth);
            Console.WriteLine($"{Ansi.Muted(prefix)}{Ansi.SoftBackground($" {valueLines[0]} ")}");
            foreach (var continuation in valueLines.Skip(1))
            {
                Console.WriteLine($"{new string(' ', prefix.Length)}{Ansi.SoftBackground($" {continuation} ")}");
            }
        }
    }

    public static void Section(string title)
    {
        Console.WriteLine();
        foreach (var line in HeadingLines(title, null, ContentWidth())) Console.WriteLine(line);
    }

    public static void Pair(string label, string value)
    {
        WriteWrapped($"{Ansi.Muted(label.PadRight(12))} {value}", ConsoleWidth(), "             ");
    }

    public static void Hint(params string[] hints)
    {
        WriteWrapped(Ansi.Muted(string.Join("  ", hints)), ConsoleWidth());
    }

    public static void Panel(string title, IEnumerable<(string Label, string Value)> rows)
    {
        Console.WriteLine();
        foreach (var line in HeadingLines(title, null, ContentWidth())) Console.WriteLine(line);
        var width = Math.Min(ConsoleWidth(), MaxWidth);
        var columns = width >= 88 ? 2 : 1;
        var materialized = rows.ToArray();
        var split = columns == 2 ? (materialized.Length + 1) / 2 : materialized.Length;
        var columnWidth = columns == 2 ? 39 : Math.Max(12, width - 2);
        for (var index = 0; index < split; index++)
        {
            var left = FormatPanelRows(materialized[index], columnWidth);
            if (columns == 1)
            {
                foreach (var line in left)
                {
                    Console.WriteLine(line);
                }

                continue;
            }

            var right = index + split < materialized.Length
                ? FormatPanelRows(materialized[index + split], columnWidth)
                : [PadVisible("", columnWidth)];
            var rowHeight = Math.Max(left.Count, right.Count);
            for (var rowIndex = 0; rowIndex < rowHeight; rowIndex++)
            {
                var leftLine = rowIndex < left.Count ? left[rowIndex] : PadVisible("", columnWidth);
                var rightLine = rowIndex < right.Count ? right[rowIndex] : PadVisible("", columnWidth);
                Console.WriteLine($"{leftLine}  {rightLine}");
            }
        }
    }

    public static void ActionList(IEnumerable<InteractiveCommand> commands)
    {
        var width = Math.Max(20, Math.Min(ConsoleWidth() - 2, MaxWidth));
        var columns = width >= 88 ? 2 : 1;
        var rows = commands.ToArray();
        var split = columns == 2 ? (rows.Length + 1) / 2 : rows.Length;
        var innerWidth = width - 4;

        Console.WriteLine($"  {Ansi.Muted($"{TerminalGlyphs.TopLeft}{TerminalGlyphs.HorizontalLine(innerWidth)}{TerminalGlyphs.TopRight}")}");
        for (var index = 0; index < split; index++)
        {
            var cellWidth = columns == 2 ? 39 : Math.Max(8, innerWidth - 2);
            var left = FormatActionLines(rows[index], index + 1, cellWidth);
            if (columns == 1)
            {
                foreach (var line in left)
                {
                    Console.WriteLine($"  {Ansi.Muted(TerminalGlyphs.Vertical)} {line}{Ansi.Muted($" {TerminalGlyphs.Vertical}")}");
                }

                continue;
            }

            var right = index + split < rows.Length
                ? FormatActionLines(rows[index + split], index + split + 1, cellWidth)
                : [PadVisible("", cellWidth)];
            var rowHeight = Math.Max(left.Count, right.Count);
            for (var rowIndex = 0; rowIndex < rowHeight; rowIndex++)
            {
                var leftLine = rowIndex < left.Count ? left[rowIndex] : PadVisible("", cellWidth);
                var rightLine = rowIndex < right.Count ? right[rowIndex] : PadVisible("", cellWidth);
                Console.WriteLine($"  {Ansi.Muted(TerminalGlyphs.Vertical)} {leftLine} {Ansi.Muted(TerminalGlyphs.Vertical)} {rightLine} {Ansi.Muted(TerminalGlyphs.Vertical)}");
            }
        }
        Console.WriteLine($"  {Ansi.Muted($"{TerminalGlyphs.BottomLeft}{TerminalGlyphs.HorizontalLine(innerWidth)}{TerminalGlyphs.BottomRight}")}");
    }

    public static string Pill(string text) => Ansi.Reverse($" {text} ");

    public static string Chip(string label, string value)
    {
        return $"{Ansi.Muted(label)} {Ansi.SoftBackground($" {value} ")}";
    }

    private static IReadOnlyList<string> FormatActionLines(InteractiveCommand command, int number, int width)
    {
        var index = Ansi.Muted(number.ToString("00"));
        var commandLabel = Ansi.Accent(command.Name);
        var firstPrefix = $"{index}  ";
        var first = $"{firstPrefix}{commandLabel}";
        var lines = new List<string> { PadVisible(first, width) };
        var descriptionWidth = Math.Max(8, width - 4);
        foreach (var line in WrapVisible(Ansi.Muted(command.Description), descriptionWidth))
        {
            lines.Add(PadVisible($"    {line}", width));
        }

        return lines;
    }

    private static IReadOnlyList<string> FormatPanelRows((string Label, string Value) row, int width)
    {
        var prefix = $"  {Ansi.Muted(row.Label.PadRight(12))} ";
        var valueWidth = Math.Max(8, width - 15);
        var valueLines = WrapVisible(row.Value, valueWidth).ToArray();
        if (valueLines.Length == 0)
        {
            return [PadVisible(prefix, width)];
        }

        var lines = new List<string>
        {
            PadVisible(prefix + valueLines[0], width),
        };
        foreach (var line in valueLines.Skip(1))
        {
            lines.Add(PadVisible(new string(' ', 15) + line, width));
        }

        return lines;
    }

    public static void CommandGrid(IEnumerable<InteractiveCommand> commands)
    {
        var width = Math.Min(ConsoleWidth(), MaxWidth);
        var columns = width >= 88 ? 2 : 1;
        var rows = commands
            .Select(command => $"{Ansi.Accent(command.Name.PadRight(16))} {command.Description}")
            .ToArray();

        if (columns == 1)
        {
            foreach (var row in rows)
            {
                WriteWrapped($"  {row}", width, "  ");
            }

            return;
        }

        var split = (rows.Length + 1) / 2;
        for (var index = 0; index < split; index++)
        {
            var left = StripAnsi(rows[index]);
            var leftRendered = rows[index];
            var rightRendered = index + split < rows.Length ? rows[index + split] : "";
            WriteWrapped($"  {leftRendered}{new string(' ', Math.Max(2, 44 - left.Length))}{rightRendered}", width, "  ");
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
            return Math.Max(Console.WindowWidth - 1, 20);
        }
        catch (IOException)
        {
            return 80;
        }
    }

    public static string FitVisible(string text, int width)
    {
        if (width <= 0)
        {
            return "";
        }

        var visible = StripAnsi(text);
        if (visible.Length <= width)
        {
            return text;
        }

        if (width == 1)
        {
            return visible[..1];
        }

        var suffix = TerminalGlyphs.Ellipsis;
        var prefixLength = Math.Max(0, width - suffix.Length);
        return visible[..Math.Min(visible.Length, prefixLength)] + suffix;
    }

    public static IReadOnlyList<string> WrapVisible(string text, int width)
    {
        if (width <= 0)
        {
            return [""];
        }

        if (VisibleLength(text) <= width)
        {
            return [text];
        }

        var visible = StripAnsi(text);
        var lines = new List<string>();
        var remaining = visible;
        while (remaining.Length > width)
        {
            var split = FindWrapPoint(remaining, width);
            lines.Add(remaining[..split].TrimEnd());
            remaining = remaining[split..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            lines.Add(remaining);
        }

        return lines.Count == 0 ? [""] : lines;
    }

    private static void WriteWrapped(string text, int width, string continuationPrefix = "")
    {
        var lines = WrapVisible(text, width);
        for (var index = 0; index < lines.Count; index++)
        {
            if (index == 0 || continuationPrefix.Length == 0)
            {
                Console.WriteLine(lines[index]);
                continue;
            }

            WriteWrapped(continuationPrefix + lines[index], width);
        }
    }

    private static string PadVisible(string text, int width)
    {
        text = FitVisible(text, width);
        var visible = StripAnsi(text);
        return visible.Length >= width ? text : text + new string(' ', width - visible.Length);
    }

    private static string BoxLine(string content, int width)
    {
        var available = width - 4;
        return $"{Ansi.Accent(TerminalGlyphs.Vertical)} {PadVisible(content, available)} {Ansi.Accent(TerminalGlyphs.Vertical)}";
    }

    private static void WriteBoxLines(string content, int width, Func<string, string> border)
    {
        foreach (var line in WrapVisible(content, width - 4))
        {
            Console.WriteLine($"{border(TerminalGlyphs.Vertical)} {PadVisible(line, width - 4)} {border(TerminalGlyphs.Vertical)}");
        }
    }

    private static int FindWrapPoint(string text, int width)
    {
        var max = Math.Min(width, text.Length);
        for (var index = max; index > 0; index--)
        {
            if (char.IsWhiteSpace(text[index - 1]))
            {
                return index;
            }
        }

        return max;
    }

    private static int VisibleLength(string text) => StripAnsi(text).Length;

    private static string StripAnsi(string text)
    {
        var result = new List<char>();
        var inEscape = false;
        foreach (var character in text)
        {
            if (character == '\u001b')
            {
                inEscape = true;
                continue;
            }

            if (inEscape)
            {
                if (char.IsLetter(character))
                {
                    inEscape = false;
                }

                continue;
            }

            result.Add(character);
        }

        return new string(result.ToArray());
    }
}
