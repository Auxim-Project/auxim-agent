namespace Auxim.Cli.Interactive;

internal static class TerminalInputDiagnostic
{
    public static int Run()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        { Console.Error.WriteLine("Input diagnostic requires a real interactive terminal."); return 1; }
        using var interrupts = InteractiveInterrupt.Enter();
        try
        {
            using var composer = TerminalInput.BeginPasteInput();
            TerminalInput.SetHistoryCopyControl(true);
            Console.WriteLine(TerminalInput.KeyboardStatus);
            Console.WriteLine($"Auxim active flags={(TerminalInput.KittyEnabled ? 1 : 0)}");
            Console.WriteLine("Press physical Enter, Shift+Enter, Ctrl+J. Ctrl+D exits. No message text, paste content or secrets are printed or saved.");
            Console.WriteLine("Copy key test: press Ctrl+C and Cmd/Super+C. A forwarded key is reported without copying. No event means the host may have intercepted it.");
            Console.WriteLine("Legacy terminals may send identical Enter/Shift+Enter bytes: then both report Submit. Use Ctrl+J or /paste in Auxim.");
            while (true)
            {
                if (interrupts.TakeIdleInterrupt()) Console.WriteLine("Signal / console interrupt -> idle cancellation (process stays alive)");
                if (!TerminalInput.KeyAvailable) { Thread.Sleep(25); continue; }
                var input = TerminalInput.Read(TerminalInputPolicy.HistoryEditor);
                if (input.Kind == TerminalInputEventKind.EndOfInput) return 0;
                if (input.Kind == TerminalInputEventKind.Key && input.Key.Key == ConsoleKey.D
                    && input.Key.Modifiers == ConsoleModifiers.Control) return 0;
                if (input.IsCtrlC || input.IsSuperC)
                {
                    var key = input.IsCtrlC ? "Ctrl+C" : "Super+C";
                    Console.WriteLine($"{(input.KittyCode is null ? "legacy/native" : "CSI-u")} {key} -> copy key delivered (no clipboard write)");
                    continue;
                }
                var action = ComposerInputActions.Decide(input);
                // Do not print arbitrary key codes: those reveal typed text.
                if (action != ComposerInputAction.Ignore)
                    Console.WriteLine($"{(input.KittyCode is null ? "legacy" : "CSI-u")} key={input.Key.Key} modifiers={input.Key.Modifiers} event={input.Kind} -> {action}");
                else if (input.Kind == TerminalInputEventKind.ControlSequence)
                    Console.WriteLine("Control sequence -> Ignore");
            }
        }
        finally { TerminalInput.Restore(); }
    }
}
