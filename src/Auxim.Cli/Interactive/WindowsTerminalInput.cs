using System.Runtime.InteropServices;

namespace Auxim.Cli.Interactive;

internal interface ITerminalInputBackend
{
    TerminalInputDecoder Decoder { get; }
    bool EndOfInput { get; }
    long LastBytesAt { get; }
    bool KeyboardRequested => false;
    string KeyboardStatus => "legacy: keyboard protocol unavailable/not queried";
    bool KittyEnabled => false;
    void Activate();
    void SetCtrlCInput(bool enabled);
    bool ReadAvailable(int timeout = 0);
    void Suspend();
    void SetMouseInput(TerminalMouseMode mode) { }
    void SetPasteMode(bool enabled) { }
    void EnableKeyboard(bool alternate) { }
    void CompatibleKeyboard() { }

    bool DiscardPending()
    {
        Decoder.DiscardEvents();
        for (var i = 0; i < 16; i++)
        {
            if (!ReadAvailable()) return true;
            Decoder.DiscardEvents();
        }
        return false;
    }
}

// Native Windows console records use the same foreground reader and events
// as the POSIX decoder. In particular, there is no Console.ReadKey poller.
internal sealed class WindowsTerminalInput(nint input, uint original) : ITerminalInputBackend
{
    private const uint ProcessedInput = 0x0001;
    private const uint LineInput = 0x0002;
    private const uint EchoInput = 0x0004;
    private const uint MouseInput = 0x0010;
    private const uint QuickEdit = 0x0040;
    private const uint ExtendedFlags = 0x0080;
    private const uint VirtualTerminalInput = 0x0200;
    private readonly InputRecord[] _records = new InputRecord[64];
    private bool _active, _rawCtrlC, _mouse, _left;
    public TerminalInputDecoder Decoder { get; } = new();
    public bool EndOfInput { get; private set; }
    public long LastBytesAt { get; private set; }

    public static WindowsTerminalInput? Create()
    {
        if (!OperatingSystem.IsWindows() || !InteractiveScreen.IsAlternateScreenActive || Console.IsInputRedirected)
            return null;
        var input = GetStdHandle(-10);
        return GetConsoleMode(input, out var mode) ? new(input, mode) : null;
    }

    public void Activate()
    {
        if (!_active) ApplyMode();
        _active = true;
    }

    public void SetCtrlCInput(bool enabled)
    {
        if (_rawCtrlC == enabled) return;
        _rawCtrlC = enabled;
        if (_active) ApplyMode();
    }

    public void SetMouseInput(TerminalMouseMode mode)
    {
        var enabled = mode is TerminalMouseMode.Normal or TerminalMouseMode.Drag;
        if (_mouse == enabled) return;
        _mouse = enabled;
        if (!enabled) _left = false;
        if (_active) ApplyMode();
    }

    private void ApplyMode()
    {
        var mode = (original & ~(EchoInput | LineInput | VirtualTerminalInput | MouseInput)) | ExtendedFlags;
        if (_mouse) mode = (mode & ~QuickEdit) | ExtendedFlags | MouseInput;
        if (_rawCtrlC) mode &= ~ProcessedInput;
        if (!SetConsoleMode(input, mode)) throw new IOException("Cannot configure Windows console input.");
    }

    public void Suspend()
    {
        _mouse = _left = _rawCtrlC = false;
        if (_active && !SetConsoleMode(input, original)) throw new IOException("Cannot restore Windows console input.");
        _active = false;
    }

    public bool ReadAvailable(int timeout = 0)
    {
        if (EndOfInput) return false;
        if (!GetNumberOfConsoleInputEvents(input, out var count))
        {
            EndOfInput = true;
            return false;
        }
        if (count == 0) return false;
        if (!ReadConsoleInput(input, _records, Math.Min(count, (uint)_records.Length), out var read))
        {
            EndOfInput = true;
            return false;
        }
        LastBytesAt = Environment.TickCount64;
        for (var i = 0; i < read; i++) Decode(_records[i]);
        return read != 0;
    }

    private void Decode(InputRecord record)
    {
        var modifiers = ((record.ControlState & 16) != 0 ? ConsoleModifiers.Shift : 0)
            | ((record.ControlState & 3) != 0 ? ConsoleModifiers.Alt : 0)
            | ((record.ControlState & 12) != 0 ? ConsoleModifiers.Control : 0);
        if (record.Type == 1 && record.Down != 0 && record.VirtualKey <= 255)
        {
            var key = new ConsoleKeyInfo(record.Character, (ConsoleKey)record.VirtualKey,
                modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control));
            var kind = key is { Key: ConsoleKey.Enter, Modifiers: ConsoleModifiers.Shift } ? TerminalInputEventKind.LineBreak : TerminalInputEventKind.Key;
            for (var i = 0; i < Math.Min((int)record.Repeat, 64); i++) Decoder.Enqueue(new(kind, key));
        }
        else if (record.Type == 2 && _mouse)
        {
            var x = record.X - Console.WindowLeft + 1;
            var y = record.Y - Console.WindowTop + 1;
            if (record.MouseFlags == 4 && (short)(record.Buttons >> 16) is var wheel && wheel != 0)
            {
                Decoder.Enqueue(new(TerminalInputEventKind.MouseWheel, default, ScrollDelta: wheel > 0 ? -1 : 1));
                return;
            }
            var pressed = (record.Buttons & 1) != 0;
            if (record.MouseFlags is 0 or 1 or 2 && (pressed || _left))
            {
                var phase = !pressed ? PointerPhase.Release : _left ? PointerPhase.Drag : PointerPhase.Press;
                Decoder.Enqueue(new(TerminalInputEventKind.Pointer, default, Pointer: new(0, x, y, modifiers, phase)));
            }
            _left = pressed;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int Down;
        [FieldOffset(4)] public short X;
        [FieldOffset(6)] public short Y;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort VirtualKey;
        [FieldOffset(8)] public uint Buttons;
        [FieldOffset(14)] public char Character;
        [FieldOffset(12)] public uint MouseControlState;
        [FieldOffset(16)] public uint KeyControlState;
        [FieldOffset(16)] public uint MouseFlags;
        public readonly uint ControlState => Type == 1 ? KeyControlState : MouseControlState;
    }

    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(nint handle, uint mode);
    [DllImport("kernel32.dll")] private static extern bool GetNumberOfConsoleInputEvents(nint handle, out uint count);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", ExactSpelling = true)]
    private static extern bool ReadConsoleInput(nint handle, [Out] InputRecord[] records, uint count, out uint read);
}
