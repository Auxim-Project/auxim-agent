namespace Auxim.Cli.Interactive;

internal enum TerminalMouseMode { Off, Normal, Drag }

internal static class TerminalMouse
{
    private static readonly object Sync = new();
    public static TerminalMouseMode Mode { get; private set; }
    public static long Revision { get; private set; }

    public static void Reset()
    {
        lock (Sync)
        {
            if (Ansi.ControlSequencesEnabled)
                WriteAndFlush("\u001b[?1007l\u001b[?1006l\u001b[?1003l\u001b[?1002l\u001b[?1000l");
            Mode = TerminalMouseMode.Off;
            Revision++;
        }
    }

    public static void SetMode(TerminalMouseMode mode)
    {
        if (!Ansi.ControlSequencesEnabled || mode != TerminalMouseMode.Off && !TerminalInput.SupportsMouseReports)
            mode = TerminalMouseMode.Off;
        lock (Sync)
        {
            if (Mode == mode) return;
            if (Ansi.ControlSequencesEnabled)
            {
                // Never use 1007: wheel and physical arrows must remain distinct.
                WriteAndFlush("\u001b[?1007l\u001b[?1003l\u001b[?1002l\u001b[?1000l");
                WriteAndFlush(mode == TerminalMouseMode.Off ? "\u001b[?1006l"
                    : "\u001b[?1005l\u001b[?1015l\u001b[?1006h" + (mode == TerminalMouseMode.Drag ? "\u001b[?1002h" : "\u001b[?1000h"));
            }
            Mode = mode;
            Revision++;
        }
    }

    public static void EnableAlternateScroll() => SetMode(TerminalMouseMode.Normal);
    public static void DisableAlternateScroll() => SetMode(TerminalMouseMode.Off);

    public static IDisposable SuspendAlternateScroll()
    {
        // A press is never resumed after another foreground owner: its release
        // might have been consumed there. Restore reports, never a held drag.
        var restore = Mode == TerminalMouseMode.Off ? TerminalMouseMode.Off : TerminalMouseMode.Normal;
        SetMode(TerminalMouseMode.Off);
        return new Suspension(restore);
    }

    private static void WriteAndFlush(string text)
    {
        try { Console.Out.Write(text); Console.Out.Flush(); }
        catch { /* The terminal may already be closed during cleanup. */ }
    }

    private sealed class Suspension(TerminalMouseMode restore) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SetMode(restore);
        }
    }
}
