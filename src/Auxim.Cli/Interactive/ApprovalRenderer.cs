using System.Globalization;
using System.Text;
using System.Text.Json;
using Auxim.Core.Approval;

namespace Auxim.Cli.Interactive;

internal static class ApprovalRenderer
{
    internal static readonly string[] Options = ["Allow once", "Always allow", "Deny and give feedback"];

    public static async Task<ApprovalResponse> PromptAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken,
        InteractiveView? view = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var mouse = TerminalMouse.SuspendAlternateScroll();
        using var inputOwner = TerminalInput.BeginApproval(out var inputFailure);
        if (inputOwner is null) return ApprovalResponse.Deny(inputFailure ?? "Safe terminal input is unavailable.");

        var details = Describe(request);
        var panel = view?.CanEmbedApproval == true ? new ApprovalPanel(view, details) : null;
        var compact = panel is null && view?.UsesFixedLayout == true;
        // Alternate buffers may have no native scrollback. In compact fallback
        // keep every detail reachable with PgUp/PgDn using a plain console pager.
        if (compact) EnsureCompactFits();
        var viewport = new TranscriptViewport();
        var pageHeight = 0;
        (int Width, int Height) compactSize = default;
        var selected = 0;
        try
        {
            if (compact) view!.SuspendLayout(clear: false);
            if (panel is null && !compact) PrintDetails(details);
            Draw();

            while (true)
            {
                var input = await TerminalInput.ReadAsync(TerminalInputPolicy.Approval, cancellationToken,
                    panel is not null ? Draw : compact ? RefreshCompact : null);
                cancellationToken.ThrowIfCancellationRequested();
                if (input.Kind is TerminalInputEventKind.PasteStarted or TerminalInputEventKind.Paste
                    or TerminalInputEventKind.ControlSequence or TerminalInputEventKind.EndOfInput)
                    return ApprovalResponse.Deny("Pasted, ambiguous, or closed terminal input cannot confirm approval. Retry with keyboard input.");
                if (input.Kind != TerminalInputEventKind.Key) continue;

                switch (input.Key.Key)
                {
                    case ConsoleKey.D1 or ConsoleKey.NumPad1: selected = 0; break;
                    case ConsoleKey.D2 or ConsoleKey.NumPad2: selected = 1; break;
                    case ConsoleKey.D3 or ConsoleKey.NumPad3: selected = 2; break;
                    case ConsoleKey.UpArrow: selected = Math.Max(0, selected - 1); break;
                    case ConsoleKey.DownArrow: selected = Math.Min(Options.Length - 1, selected + 1); break;
                    case ConsoleKey.PageUp:
                    case ConsoleKey.PageDown:
                        var direction = input.Key.Key == ConsoleKey.PageUp ? -1 : 1;
                        if (panel is not null) panel.Scroll(direction);
                        else if (!compact || !viewport.ScrollClamped(direction * pageHeight, pageHeight)) continue;
                        break;
                    case ConsoleKey.Enter:
                        if (selected < 2) return ApprovalResponse.Allow(remember: selected == 1);
                        return ReadFeedback(panel, cancellationToken);
                    default: continue;
                }
                Draw();
            }
        }
        finally
        {
            if (panel is not null) view!.EndApprovalPanel();
            else if (compact) view!.RestoreLayout(restoreMouse: false);
        }

        void RefreshCompact()
        {
            EnsureCompactFits();
            if (compactSize != (Console.WindowWidth, Console.WindowHeight)) Draw();
        }

        void Draw()
        {
            if (panel is not null) panel.Draw(selected);
            else if (compact)
            {
                InteractiveScreen.Clear();
                // Wrap before paging. Reserve space for every choice and the
                // active decision even on a narrow terminal.
                compactSize = (Console.WindowWidth, Console.WindowHeight);
                var width = Math.Max(1, compactSize.Width - 1);
                var height = Math.Max(1, compactSize.Height);
                var choice = $"Decision: {selected + 1}. {Options[selected]}";
                var instructions = TerminalTextLayout.Wrap("1/2/3 select · Enter confirms", width);
                var choiceRows = TerminalTextLayout.Wrap(choice, width);
                pageHeight = Math.Max(1, height - instructions.Count - choiceRows.Count - 3);
                viewport.Reflow(details.SelectMany(line => TerminalTextLayout.Wrap(line, width)).ToArray(),
                    pageHeight, followBottom: false);
                Console.WriteLine("SAFETY REVIEW");
                foreach (var line in viewport.GetVisibleLines(pageHeight)) Console.WriteLine(line);
                Console.WriteLine($"PgUp/Dn {viewport.Offset + 1}/{viewport.TotalLines}");
                foreach (var line in instructions.Concat(choiceRows)) Console.WriteLine(line);
            }
            else
            {
                // Keep details in native scrollback. Selection changes never erase it.
                Console.WriteLine($"Decision: {selected + 1}. {Options[selected]}");
            }
        }
    }

    private static void EnsureCompactFits()
    {
        if (Console.WindowHeight < 10 || Console.WindowWidth < 24)
            throw new InvalidOperationException("Terminal is too small to review the approval. Resize and retry.");
    }

    private static ApprovalResponse ReadFeedback(ApprovalPanel? panel, CancellationToken token)
    {
        if (panel is null) Console.Write(Ansi.Warning("Feedback for the model: "));
        var reason = new LineEditor([], _ => [], inputLayout: panel is null ? null : _ => panel.DrawFeedback())
            .ReadLine("", "", inputPolicy: TerminalInputPolicy.Feedback, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        return ApprovalResponse.Deny(string.IsNullOrWhiteSpace(reason) ? "User denied this tool call." : reason.Trim());
    }

    private static void PrintDetails(IReadOnlyList<string> details)
    {
        Console.WriteLine();
        Console.WriteLine(Ansi.Warning("SAFETY REVIEW · Tool approval required"));
        foreach (var line in details) Console.WriteLine(line);
        foreach (var (option, i) in Options.Select((value, index) => (value, index)))
            Console.WriteLine($"  {i + 1}. {option}");
        Console.WriteLine("Up/Down or 1/2/3 selects · Enter confirms · Ctrl+C cancels");
    }

    private static IReadOnlyList<string> Describe(ApprovalRequest request)
    {
        var lines = new List<string>
        {
            $"Tool: {Display(request.ToolName)}",
            $"Request: {Display(request.RequestId)}",
            "Risk: This tool can modify files, run commands, or change state.",
            "Always allow remembers the requested resource permissions for future calls.",
            "Arguments:"
        };
        foreach (var (key, value) in request.Arguments)
        {
            var valueText = value switch
            {
                null => "(null)",
                string { Length: 0 } => "(empty string)",
                string str => str,
                JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? "",
                JsonElement json => json.GetRawText(),
                _ => JsonSerializer.Serialize(value)
            };
            lines.Add($"  {Display(key)}: {Display(valueText)}");
        }

        lines.Add("Resources:");
        foreach (var access in request.ResourceAccesses)
            lines.Add($"  {Display(access.Action.ToString())}  {Display(access.Resource.ToString())}");
        return lines;
    }

    // Approval data is untrusted. Show control and format characters as text
    // rather than permitting terminal commands or invisible direction changes.
    private static string Display(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                result.Append(rune.Value <= 0xffff ? $"\\u{rune.Value:x4}" : $"\\U{rune.Value:x8}");
            else if (rune.Value == '\\') result.Append("\\\\");
            else result.Append(rune.ToString());
        }
        return result.ToString();
    }
}
