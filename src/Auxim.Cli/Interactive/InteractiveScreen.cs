using System.Runtime.InteropServices;

namespace Auxim.Cli.Interactive;

internal sealed class InteractiveScreen : IDisposable
{
    private static readonly object ScreenSync = new();
    private static volatile bool _alternateScreen;
    private static bool _closing;
    private bool _disposed;

    static InteractiveScreen()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FullCleanup();

        RegisterPosixSignal(PosixSignal.SIGTERM, 15);
        RegisterPosixSignal(PosixSignal.SIGHUP, 1);
        RegisterPosixSignal(PosixSignal.SIGINT, 2);
    }

    public static InteractiveScreen Enter()
    {
        var useAlternate = ShouldUseAlternateScreen();
        if (useAlternate) TerminalInput.Restore(); // Pop before changing the screen stack.
        lock (ScreenSync)
        {
            if (!_closing && useAlternate && WriteAndFlush("\u001b[?1049h\u001b[?25h"))
            {
                _alternateScreen = true;
                WriteAndFlush("\u001b]0;Auxim\u0007");
                Clear();
            }
            if (!_closing) TerminalMouse.Reset();
        }
        return new InteractiveScreen();
    }

    public static bool IsAlternateScreenActive => _alternateScreen;

    public static void SetHistoryScrollActive(bool active)
    {
        if (!IsAlternateScreenActive)
        {
            return;
        }

        if (active)
        {
            TerminalMouse.EnableAlternateScroll();
        }
        else
        {
            TerminalMouse.DisableAlternateScroll();
        }
    }

    public static void Clear()
    {
        if (Ansi.ControlSequencesEnabled)
        {
            WriteAndFlush("\u001b[2J\u001b[H");
        }
        else
        {
            Console.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FullCleanup();
    }

    private static void FullCleanup()
    {
        TerminalInput.Restore(shuttingDown: true);
        lock (ScreenSync)
        {
            if (_closing) return;
            _closing = true;
            TerminalMouse.Shutdown();
            if (!_alternateScreen) return;
            WriteAndFlush("\u001b[?25h\u001b[?1049l");
            _alternateScreen = false;
        }
    }

    private static void RegisterPosixSignal(PosixSignal signal, int signum)
    {
        try
        {
            PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                if (signal == PosixSignal.SIGINT && InteractiveInterrupt.Current is not null) return;
                FullCleanup();
                Console.ResetColor();
                Environment.Exit(128 + signum);
            });
        }
        catch (NotSupportedException)
        {
            // Some platforms, such as Windows, do not support these signals.
        }
    }

    private static bool WriteAndFlush(string text)
    {
        try
        {
            Console.Out.Write(text);
            Console.Out.Flush();
            return true;
        }
        catch
        {
            // stdout may already be closed during shutdown.
            return false;
        }
    }

    private static bool ShouldUseAlternateScreen()
    {
        if (!Ansi.ControlSequencesEnabled || Console.IsInputRedirected)
        {
            return false;
        }

        var disabled = Environment.GetEnvironmentVariable("AUXIM_NO_ALT_SCREEN");
        return !string.Equals(disabled, "1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(disabled, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
