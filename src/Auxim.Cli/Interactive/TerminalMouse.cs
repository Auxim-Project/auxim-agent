namespace Auxim.Cli.Interactive;

internal enum TerminalMouseMode { Off, Normal, Drag, NativeSelection }

internal static class TerminalMouse
{
    private static readonly object Sync = new();
    private static bool _shuttingDown;
    internal const string DisableReports = "\u001b[?9l\u001b[?1000l\u001b[?1002l\u001b[?1003l"
        + "\u001b[?1005l\u001b[?1006l\u001b[?1007l\u001b[?1015l\u001b[?1016l";
    public static TerminalMouseMode Mode { get; private set; }
    public static long Revision { get; private set; }

    public static void Reset()
    {
        lock (Sync)
        {
            if (_shuttingDown) return;
            if (Ansi.ControlSequencesEnabled)
                WriteAndFlush(DisableReports);
            Mode = TerminalMouseMode.Off;
            Revision++;
        }
    }

    public static void SetMode(TerminalMouseMode mode)
    {
        if (!Ansi.ControlSequencesEnabled || (mode is TerminalMouseMode.Normal or TerminalMouseMode.Drag) && !TerminalInput.SupportsMouseReports)
            mode = TerminalMouseMode.Off;
        lock (Sync)
        {
            if (_shuttingDown || Mode == mode) return;
            if (Ansi.ControlSequencesEnabled)
            {
                // Release reports before native selection. Never enable mode
                // 1007, which converts wheel motion into indistinguishable keys.
                WriteAndFlush(DisableReports + (mode switch
                {
                    TerminalMouseMode.Normal => "\u001b[?1006h\u001b[?1000h",
                    TerminalMouseMode.Drag => "\u001b[?1006h\u001b[?1002h",
                    _ => ""
                }));
            }
            TerminalInput.SetNativeMouseInput(mode);
            Mode = mode;
            Revision++;
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (_shuttingDown) return;
            SetMode(TerminalMouseMode.Off);
            _shuttingDown = true;
        }
    }

    public static void EnableAlternateScroll() => SetMode(TerminalMouseMode.Normal);
    public static void DisableAlternateScroll() => SetMode(TerminalMouseMode.Off);

    public static IDisposable SuspendAlternateScroll()
    {
        // A press is never resumed after another foreground owner: its release
        // might have been consumed there. Restore reports, never a held drag.
        var restore = Mode == TerminalMouseMode.Drag ? TerminalMouseMode.Normal : Mode;
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
