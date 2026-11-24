using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Auxim.Cli.Interactive;

// One synchronous stdin owner. No Console reader may run while this backend is active.
internal sealed class PosixTerminalInput : ITerminalInputBackend, IDisposable
{
    private const int Interrupted = 4;
    private readonly byte[] _original = new byte[512]; // Opaque native termios, including platform padding.
    private readonly byte[] _raw = new byte[512];
    private readonly byte[] _bytes = new byte[4096];
    // Internal command capture may temporarily replace Console.Out. Protocol
    // cleanup must still reach the terminal, including during signal handling.
    private readonly TextWriter _terminalOutput = Console.Out;
    private bool _active;
    private bool _pasteMode;
    public TerminalInputDecoder Decoder { get; } = new();
    private TerminalKeyboardProtocol? _keyboard;
    public bool KeyboardRequested { get; private set; }
    public string KeyboardStatus => _keyboard?.Status ?? "legacy: keyboard protocol unavailable/not queried";
    public bool KittyEnabled => _keyboard?.ActiveFlags == 1;
    public void EnableKeyboard(bool alternate)
    {
        if (!Ansi.ControlSequencesEnabled || Console.IsOutputRedirected) return;
        KeyboardRequested = true;
        _keyboard ??= new(Decoder, text => { _terminalOutput.Write(text); _terminalOutput.Flush(); }, ReadAvailable);
        _keyboard.Enable(alternate);
    }
    public void CompatibleKeyboard() => _keyboard?.Compatible();
    public long LastBytesAt { get; private set; }
    public bool EndOfInput { get; private set; }

    private PosixTerminalInput()
    {
        if (TcGetAttr(0, _original) != 0) throw new IOException("Cannot read terminal input settings.");
        _original.CopyTo(_raw, 0);
        MakeRaw(_raw);
        // VMIN=0, VTIME=0: read cannot block if a signal flushes input after poll.
        // Darwin c_cc starts at 32 (VMIN=16); Linux/generic at 17 (VMIN=6).
        _raw[OperatingSystem.IsMacOS() ? 48 : 23] = 0;
        _raw[OperatingSystem.IsMacOS() ? 49 : 22] = 0;
        // Darwin tcflag_t is unsigned long; Linux tcflag_t is unsigned int.
        // Preserve output processing and signal delivery (not cfmakeraw's default).
        var size = OperatingSystem.IsMacOS() ? 8 : 4;
        Array.Copy(_original, size, _raw, size, size);
        if (OperatingSystem.IsMacOS())
            BinaryPrimitives.WriteUInt64LittleEndian(_raw.AsSpan(24),
                BinaryPrimitives.ReadUInt64LittleEndian(_raw.AsSpan(24))
                | (BinaryPrimitives.ReadUInt64LittleEndian(_original.AsSpan(24)) & 0x80UL));
        else
            BinaryPrimitives.WriteUInt32LittleEndian(_raw.AsSpan(12),
                BinaryPrimitives.ReadUInt32LittleEndian(_raw.AsSpan(12))
                | (BinaryPrimitives.ReadUInt32LittleEndian(_original.AsSpan(12)) & 1U));
        Activate();
    }

    public void SetCtrlCInput(bool enabled)
    {
        var darwin = OperatingSystem.IsMacOS();
        var index = darwin ? 32 + 8 : 17; // c_cc[VINTR]: Darwin index 8, Linux index 0.
        var interrupt = _original[index];
        // Bypass tty translation of keyboard ETX only. Leave ISIG and any
        // user-configured non-ETX interrupt character intact. A process-directed
        // SIGINT still reaches the ordinary cancellation handler.
        var next = enabled && interrupt == 3 ? (darwin ? (byte)0xff : (byte)0) : interrupt;
        var previous = _raw[index];
        if (previous == next) return;
        _raw[index] = next;
        if (_active && TcSetAttr(0, 0, _raw) != 0)
        {
            _raw[index] = previous;
            throw new IOException("Cannot change terminal interrupt character.");
        }
    }

    public static PosixTerminalInput? Create()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) || !BitConverter.IsLittleEndian
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
            || IsATty(0) != 1) return null;
        return new PosixTerminalInput();
    }

    public void Activate()
    {
        if (_active) return;
        if (TcSetAttr(0, 0, _raw) != 0) throw new IOException("Cannot enter terminal input mode.");
        _active = true;

    }

    public void SetPasteMode(bool enabled)
    {
        if (_pasteMode == enabled || !Ansi.ControlSequencesEnabled || Console.IsOutputRedirected) return;
        _terminalOutput.Write(enabled ? "\u001b[?2004h" : "\u001b[?2004l");
        _terminalOutput.Flush();
        _pasteMode = enabled;
    }

    public bool ReadAvailable(int timeout = 0)
    {
        if (EndOfInput) return false;
        var descriptor = new PollDescriptor { File = 0, Events = 1 };
        var ready = Poll(ref descriptor, 1, timeout);
        if (ready < 0 && Marshal.GetLastPInvokeError() == Interrupted) return false;
        if (ready < 0) throw new IOException("Cannot poll terminal input.");
        if (ready == 0) return false;
        var count = Read(0, _bytes, (nuint)_bytes.Length);
        if (count < 0 && Marshal.GetLastPInvokeError() == Interrupted) return false;
        if (count <= 0 && (descriptor.Returned & 0x10) != 0) // POLLHUP
        { EndOfInput = true; return false; }
        if (count < 0) throw new IOException("Cannot read terminal input.");
        if (count == 0) return false;
        LastBytesAt = Environment.TickCount64;
        Decoder.Feed(_bytes.AsSpan(0, (int)count));
        return true;
    }

    public void Suspend()
    {
        try { _keyboard?.Restore(); SetPasteMode(false); }
        finally
        {
            if (_active)
            {
                if (TcSetAttr(0, 0, _original) != 0) throw new IOException("Cannot restore terminal input settings.");
                _active = false;
            }
            SetCtrlCInput(false);
        }
    }
    public void Dispose() => Suspend();

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor { public int File; public short Events; public short Returned; }
    [DllImport("libc", EntryPoint = "isatty")] private static extern int IsATty(int file);
    [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)] private static extern int TcGetAttr(int file, [Out] byte[] settings);
    [DllImport("libc", EntryPoint = "tcsetattr", SetLastError = true)] private static extern int TcSetAttr(int file, int action, byte[] settings);
    [DllImport("libc", EntryPoint = "cfmakeraw")] private static extern void MakeRaw([In, Out] byte[] settings);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollDescriptor descriptors, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)] private static extern nint Read(int file, [Out] byte[] buffer, nuint count);
}
