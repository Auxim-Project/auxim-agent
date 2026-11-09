using System.Runtime.InteropServices;

namespace Auxim.Cli.Interactive;

internal enum TerminalInputEventKind { Ignored, Key, EscapeBoundary, PasteStarted, Paste, ControlSequence, EndOfInput, MouseWheel, Pointer, LineBreak }
internal enum PointerPhase { Press, Drag, Release }
internal sealed record TerminalPointer(int Button, int X, int Y, ConsoleModifiers Modifiers, PointerPhase Phase);
internal readonly record struct TerminalInputEvent(TerminalInputEventKind Kind, ConsoleKeyInfo Key, string? Text = null,
    int ScrollDelta = 0, TerminalPointer? Pointer = null, int? KittyCode = null, int KittyModifiers = 0);

internal sealed class TerminalInputPolicy(Func<ConsoleKeyInfo, bool> acceptKey)
{
    public bool AcceptsKey(ConsoleKeyInfo key) => acceptKey(key);
    public static TerminalInputPolicy LineEditor { get; } = new(_ => true);
    public static TerminalInputPolicy Secret { get; } = new(_ => true);
    public static TerminalInputPolicy Feedback { get; } = new(_ => true);
    public static TerminalInputPolicy Approval { get; } = new(key =>
        key.Modifiers == 0 && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow
            or ConsoleKey.D1 or ConsoleKey.D2 or ConsoleKey.D3
            or ConsoleKey.NumPad1 or ConsoleKey.NumPad2 or ConsoleKey.NumPad3
        || key.Key == ConsoleKey.Enter && (key.Modifiers == 0
            || key.KeyChar == '\n' && key.Modifiers == ConsoleModifiers.Control));
}

internal static class TerminalInput
{
    private static readonly object Sync = new();
    private static PosixTerminalInput? _input;
    private static bool _initialized;
    private static bool _approval;
    private static int _suspended;
    private static int _pasteReaders;
    private static long _lastBytesAt;
    private static readonly List<IDisposable> Signals = [];

    public static string KeyboardStatus { get { lock (Sync) return _input?.KeyboardStatus ?? "legacy: raw terminal unavailable"; } }
    public static bool KittyEnabled { get { lock (Sync) return _input?.KittyEnabled == true; } }

    public static bool SupportsMouseReports
    {
        get { lock (Sync) { Initialize(); return _input is not null && Ansi.ControlSequencesEnabled; } }
    }

    static TerminalInput()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP })
                Signals.Add(PosixSignalRegistration.Create(signal, _ =>
                {
                    if (signal == PosixSignal.SIGINT && InteractiveInterrupt.Current is not null) return;
                    Restore();
                }));
    }

    private static void Initialize()
    {
        if (_initialized) return;
        try { _input = PosixTerminalInput.Create(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { _input = null; }
        _initialized = true;
    }

    public static bool KeyAvailable
    {
        get
        {
            lock (Sync)
            {
                Initialize();
                if (_input is null) return Console.KeyAvailable;
                if (_suspended > 0 && !_approval) throw new InvalidOperationException("Foreground command owns terminal input.");
                if (_approval) throw new InvalidOperationException("Approval owns terminal input.");
                Fill();
                ReleaseIdleEditorEscape();
                return _input.Decoder.HasEvents || _input.EndOfInput;
            }
        }
    }

    private static void Fill()
    {
        _input!.Activate();
        _input.SetPasteMode(_approval || _pasteReaders > 0);
        if (!_input.Decoder.HasEvents && _input.ReadAvailable()) _lastBytesAt = Environment.TickCount64;
    }

    private static void ReleaseIdleEditorEscape()
    {
        if (!_approval && _input!.Decoder.IsBareEscape && Environment.TickCount64 - Math.Max(_lastBytesAt, _input!.LastBytesAt) >= 50)
            _input.Decoder.ReleaseEditorEscape();
    }

    private static TerminalInputEvent? TryRead(TerminalInputPolicy policy)
    {
        lock (Sync)
        {
            Initialize();
            if (_suspended > 0 && !_approval) throw new InvalidOperationException("Foreground command owns terminal input.");
            if (_approval && policy != TerminalInputPolicy.Approval && policy != TerminalInputPolicy.Feedback)
                throw new InvalidOperationException("Approval owns terminal input.");
            if (_input is null)
            {
                if (policy != TerminalInputPolicy.LineEditor) throw new IOException("Safe terminal input is unavailable.");
                if (!Console.KeyAvailable) return null;
                var key = Console.ReadKey(true);
                return new(policy.AcceptsKey(key) ? TerminalInputEventKind.Key : TerminalInputEventKind.Ignored, key);
            }
            if (policy == TerminalInputPolicy.Secret) _input.CompatibleKeyboard();
            Fill();
            if (!_approval) ReleaseIdleEditorEscape();
            if (_input.Decoder.HasEvents)
            {
                var value = _input.Decoder.Take();
                if (value.Kind == TerminalInputEventKind.Key && value.Key.Key == ConsoleKey.C
                    && value.Key.Modifiers == ConsoleModifiers.Control && InteractiveInterrupt.HandleSignal())
                    return new(TerminalInputEventKind.ControlSequence, default);
                // CSI-u is accepted only by the ordinary editor. In particular,
                // encoded Enter/digits may never authorize an approval or feed secrets.
                if (value.KittyCode is not null && policy != TerminalInputPolicy.LineEditor)
                    return new(TerminalInputEventKind.ControlSequence, default);
                if (value.Kind == TerminalInputEventKind.LineBreak && policy != TerminalInputPolicy.LineEditor)
                    return new(TerminalInputEventKind.ControlSequence, default);
                if (value.Kind is TerminalInputEventKind.MouseWheel or TerminalInputEventKind.Pointer && policy != TerminalInputPolicy.LineEditor)
                    return new(TerminalInputEventKind.ControlSequence, default);
                if (value.Kind == TerminalInputEventKind.EscapeBoundary)
                    return _approval || policy != TerminalInputPolicy.LineEditor
                        ? new(TerminalInputEventKind.ControlSequence, default)
                        : new(TerminalInputEventKind.Key, new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
                if (value.Kind == TerminalInputEventKind.Key && !policy.AcceptsKey(value.Key))
                    return new(policy == TerminalInputPolicy.Approval && value.Key.Modifiers != 0
                        ? TerminalInputEventKind.ControlSequence : TerminalInputEventKind.Ignored, default);
                return value;
            }
            if (_input.EndOfInput) return new(TerminalInputEventKind.EndOfInput, default);
            if (_approval && _input.Decoder.HasPartialSequence && Environment.TickCount64 - Math.Max(_lastBytesAt, _input!.LastBytesAt) >= 200)
                // Fail the approval closed; retain framing for every subsequent reader.
                return new(TerminalInputEventKind.ControlSequence, default);
            return null;
        }
    }

    public static TerminalInputEvent Read(TerminalInputPolicy policy, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (policy == TerminalInputPolicy.Secret && InteractiveInterrupt.Current?.TakeIdleInterrupt() == true)
                return new(TerminalInputEventKind.EndOfInput, default);
            if (TryRead(policy) is { } value) return value;
            Thread.Sleep(10);
        }
    }

    public static async ValueTask<TerminalInputEvent> ReadAsync(TerminalInputPolicy policy, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (policy == TerminalInputPolicy.Secret && InteractiveInterrupt.Current?.TakeIdleInterrupt() == true)
                return new(TerminalInputEventKind.EndOfInput, default);
            if (TryRead(policy) is { } value) return value;
            await Task.Delay(10, cancellationToken);
        }
    }

    // Called only by the active turn's await loop. Other typing is discarded,
    // while incomplete paste/control framing remains quarantined for all readers.
    public static void PollRunningCancellation()
    {
        lock (Sync)
        {
            if (_approval || _suspended > 0 || _input is null) return;
            if (_input.ReadAvailable()) _lastBytesAt = Environment.TickCount64;
            while (_input.Decoder.HasEvents)
            {
                var value = _input.Decoder.Take();
                if (value.Kind == TerminalInputEventKind.Key && value.Key.Key == ConsoleKey.C
                    && value.Key.Modifiers == ConsoleModifiers.Control) InteractiveInterrupt.HandleSignal();
            }
            _input.Decoder.DiscardPastePayload();
            if (_input.EndOfInput) InteractiveInterrupt.HandleSignal();
        }
    }

    public static void DiscardQueuedNavigation()
    {
        lock (Sync)
        {
            if (_approval) throw new InvalidOperationException("Approval owns terminal input.");
            _input?.Decoder.DiscardQueuedNavigation();
        }
    }

    // A composer owns this scope only while editing. Wizards and secrets do not
    // opt in; approval owns its separate, fail-closed paste detection scope.
    // Kitty remains active during the turn; protected/child scopes override it,
    // and Restore pops the owned entry before leaving the current screen.
    public static IDisposable? BeginPasteInput()
    {
        lock (Sync)
        {
            Initialize();
            if (_input is null || !Ansi.ControlSequencesEnabled) return null;
            if (_approval || _suspended > 0) throw new InvalidOperationException("Another foreground owner holds input.");
            _input.Activate();
            _input.SetPasteMode(true);
            _input.EnableKeyboard(InteractiveScreen.IsAlternateScreenActive);
            _pasteReaders++;
            return new Scope(() =>
            {
                lock (Sync)
                {
                    _pasteReaders--;
                    _input.Decoder.DiscardPastePayload();
                    _input.SetPasteMode(_approval || _pasteReaders > 0 && _suspended == 0);
                }
            });
        }
    }

    public static IDisposable? BeginApproval(out string? failure)
    {
        lock (Sync)
        {
            failure = null;
            Initialize();
            if (_input is null) { failure = "Raw terminal input is unavailable; approval was denied safely."; return null; }
            if (_approval) { failure = "Another approval owns terminal input."; return null; }
            _input.Activate();
            if (!_input.DiscardPending() || _input.Decoder.HasPartialSequence)
            {
                if (_suspended > 0) _input.Suspend();
                failure = "Incomplete or excessive terminal input; approval denied. Finish the paste/sequence or restart the CLI.";
                return null;
            }
            _approval = true;
            try { _input.CompatibleKeyboard(); _input.SetPasteMode(true); }
            catch { _approval = false; _input.Suspend(); throw; }
            return new Scope(() =>
            {
                lock (Sync)
                {
                    try { _input.DiscardPending(); }
                    finally
                    {
                        _approval = false;
                        _input.SetPasteMode(_pasteReaders > 0 && _suspended == 0);
                        if (_suspended > 0) _input.Suspend();
                        else if (_input.KeyboardRequested) _input.EnableKeyboard(InteractiveScreen.IsAlternateScreenActive);
                    }
                }
            });
        }
    }

    // Child processes temporarily own the cooked terminal. All CLI readers use this decoder.
    public static IDisposable Suspend()
    {
        lock (Sync)
        {
            if (_approval) throw new InvalidOperationException("Cannot transfer approval input.");
            _suspended++;
            _input?.Suspend();
        }
        return new Scope(() =>
        {
            lock (Sync)
            {
                if (--_suspended != 0 || _input is null) return;
                _input.Activate();
                _input.DiscardPending();
                _input.SetPasteMode(_pasteReaders > 0);
                if (_input.KeyboardRequested) _input.EnableKeyboard(InteractiveScreen.IsAlternateScreenActive);
            }
        });
    }

    public static void Restore()
    {
        lock (Sync)
        {
            try { _input?.Suspend(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }

    private sealed class Scope(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
