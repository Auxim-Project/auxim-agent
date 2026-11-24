using System.Text;

namespace Auxim.Cli.Interactive;

// Incremental wire decoder. Partial protocols survive reader/foreground changes;
// there is deliberately no timeout that turns a protocol tail into ordinary keys.
internal sealed class TerminalInputDecoder
{
    private enum State { Text, Escape, EscIntermediate, Csi, Ss3, String, Paste, LegacyMouse }
    private State _state;
    private readonly StringBuilder _sequence = new();
    private readonly Queue<TerminalInputEvent> _events = new();
    private readonly byte[] _utf8 = new byte[4];
    private int _utf8Count;
    private int _pasteEnd;
    internal const int MaximumPasteBytes = 64 * 1024;
    private readonly List<byte> _paste = new();
    private bool _discardPaste;
    private static readonly UTF8Encoding PasteEncoding = new(false, true);
    private bool _stringEscape;
    private bool _stringBel;
    private bool _invalid;
    private bool _discardSequenceScroll;
    private bool _discardSequenceWheel;
    private int _legacyMouseBytes;
    private static ReadOnlySpan<byte> PasteEnd => "\u001b[201~"u8;
    public long KeyboardReplyRevision { get; private set; }
    public int KeyboardReplyFlags { get; private set; }
    public bool HasPartialSequence => _state != State.Text || _utf8Count != 0;
    public bool HasEvents => _events.Count != 0;
    public bool IsBareEscape => _state == State.Escape;
    internal void Enqueue(TerminalInputEvent value) => _events.Enqueue(value);
    public TerminalInputEvent Take() => _events.Dequeue();
    public void DiscardEvents() { _events.Clear(); DiscardPastePayload(); }

    public void DiscardPastePayload()
    {
        _paste.Clear();
        if (_state == State.Paste) _discardPaste = true;
    }

    private void AppendPaste(byte value)
    {
        if (_discardPaste) return;
        if (_paste.Count == MaximumPasteBytes) { DiscardPastePayload(); return; }
        _paste.Add(value);
    }

    public void DiscardQueuedNavigation()
    {
        var remaining = _events.Where(e => e.Kind is not (TerminalInputEventKind.MouseWheel or TerminalInputEventKind.Pointer)
            && (e.Kind != TerminalInputEventKind.Key || !IsScroll(e.Key.Key))).ToArray();
        _events.Clear();
        foreach (var value in remaining) _events.Enqueue(value);
        if (_state is State.Escape or State.Csi or State.Ss3) _discardSequenceScroll = true;
    }

    public void DiscardQueuedWheel()
    {
        var count = _events.Count;
        for (var i = 0; i < count; i++)
        {
            var value = _events.Dequeue();
            if (value.Kind != TerminalInputEventKind.MouseWheel) _events.Enqueue(value);
        }
        if (_state is State.Escape or State.Csi) _discardSequenceWheel = true;
    }

    private static bool IsScroll(ConsoleKey key) => key is ConsoleKey.UpArrow or ConsoleKey.DownArrow
        or ConsoleKey.PageUp or ConsoleKey.PageDown;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) FeedByte(value);
    }

    private void Control() => _events.Enqueue(new(TerminalInputEventKind.ControlSequence, default));
    private void Key(ConsoleKey key, char value = '\0', ConsoleModifiers modifiers = 0, int wireModifiers = 0)
    {
        if (_discardSequenceScroll && IsScroll(key)) return;
        _events.Enqueue(new(TerminalInputEventKind.Key,
            new ConsoleKeyInfo(value, key, modifiers.HasFlag(ConsoleModifiers.Shift),
                modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control)), KittyModifiers: wireModifiers));
    }

    // Only the ordinary editor may interpret an idle bare ESC as navigation.
    public void ReleaseEditorEscape()
    {
        if (!IsBareEscape || HasEvents) return;
        _state = State.Text;
        _discardSequenceScroll = _discardSequenceWheel = false;
        Key(ConsoleKey.Escape, '\u001b');
    }

    private void FeedByte(byte value)
    {
        if (_state == State.LegacyMouse)
        {
            // A terminal ignoring SGR mode may send X10's three payload bytes.
            // Quarantine all of them, including digits/Enter, across handoffs.
            if (--_legacyMouseBytes == 0) _state = State.Text;
            return;
        }
        if (_state == State.Paste)
        {
            if (value == PasteEnd[_pasteEnd]) _pasteEnd++;
            else
            {
                for (var i = 0; i < _pasteEnd; i++) AppendPaste(PasteEnd[i]);
                _pasteEnd = value == 0x1b ? 1 : 0;
                if (_pasteEnd == 0) AppendPaste(value);
            }
            if (_pasteEnd == PasteEnd.Length)
            {
                string? text = null;
                if (!_discardPaste)
                    try { text = PasteEncoding.GetString(_paste.ToArray()); }
                    catch (DecoderFallbackException) { }
                _paste.Clear();
                _pasteEnd = 0; _state = State.Text;
                _events.Enqueue(new(TerminalInputEventKind.Paste, default, text));
            }
            return; // Payload is never decoded into key/command events.
        }
        if (_state == State.String)
        {
            if (value == 0x9c || value == 7 && _stringBel || (_stringEscape && value == (byte)'\\'))
            { _state = State.Text; _discardSequenceScroll = _discardSequenceWheel = false; }
            _stringEscape = value == 0x1b;
            return;
        }
        if (_state == State.EscIntermediate)
        {
            if (value == 0x1b) { Control(); _state = State.Escape; }
            else if (value is >= 0x30 and <= 0x7e) { Control(); _state = State.Text; _discardSequenceScroll = _discardSequenceWheel = false; }
            return;
        }
        if (_state is State.Csi or State.Ss3)
        {
            if (value == 0x1b)
            {
                Control(); _state = State.Escape; _sequence.Clear(); _invalid = false;
                _discardSequenceWheel = false; // New report, not the abandoned fragment.
                return;
            }
            if (value is >= 0x40 and <= 0x7e)
            {
                var sequence = _sequence.ToString() + (char)value;
                var ss3 = _state == State.Ss3;
                _state = State.Text;
                if (!_invalid && !ss3 && sequence == "200~")
                {
                    _state = State.Paste;
                    _paste.Clear(); _pasteEnd = 0; _discardPaste = false;
                    _events.Enqueue(new(TerminalInputEventKind.PasteStarted, default));
                }
                else if (!_invalid && !ss3 && sequence == "M")
                {
                    _state = State.LegacyMouse; _legacyMouseBytes = 3; Control();
                }
                else if (_invalid || !DecodeKey(sequence, ss3)) Control();
                _sequence.Clear(); _invalid = false; _discardSequenceScroll = _discardSequenceWheel = false;
            }
            else if (value is >= 0x20 and <= 0x3f && _sequence.Length < 64)
                _sequence.Append((char)value);
            else
            {
                // Keep consuming until the final byte, even after malformed data or overflow.
                if (!_invalid) Control();
                _invalid = true;
            }
            return;
        }
        if (_state == State.Escape)
        {
            _sequence.Clear(); _invalid = false;
            switch (value)
            {
                case (byte)'[': _state = State.Csi; return;
                case (byte)'O': _state = State.Ss3; return;
                case (byte)']': case (byte)'P': case (byte)'X': case (byte)'^': case (byte)'_':
                    _state = State.String; _stringBel = value == (byte)']'; _stringEscape = false; Control(); return;
                case 0x1b:
                    _discardSequenceWheel = false;
                    _events.Enqueue(new(TerminalInputEventKind.EscapeBoundary, default));
                    return;
                default:
                    if (value is >= 0x20 and <= 0x2f) { _state = State.EscIntermediate; return; }
                    _state = State.Text;
                    if (value < 0x80) DecodeAscii(value, ConsoleModifiers.Alt);
                    else Control();
                    _discardSequenceScroll = _discardSequenceWheel = false;
                    return;
            }
        }
        if (_utf8Count > 0 && (value & 0xc0) != 0x80)
        {
            _utf8Count = 0;
            Control();
            FeedByte(value); // An invalid UTF-8 prefix must not hide the following ESC.
            return;
        }
        if (_utf8Count > 0 || value >= 0x80)
        {
            if (_utf8Count == 0 && value is 0x90 or 0x98 or 0x9b or 0x9d or 0x9e or 0x9f)
            {
                _state = value == 0x9b ? State.Csi : State.String;
                _sequence.Clear(); _invalid = false;
                if (_state == State.String) { _stringBel = value == 0x9d; _stringEscape = false; Control(); }
                return;
            }
            _utf8[_utf8Count++] = value;
            var status = Rune.DecodeFromUtf8(_utf8.AsSpan(0, _utf8Count), out var rune, out _);
            if (status == System.Buffers.OperationStatus.NeedMoreData && _utf8Count < 4) return;
            _utf8Count = 0;
            if (status != System.Buffers.OperationStatus.Done) { Control(); return; }
            if (rune.Value is 0x90 or 0x98 or 0x9b or 0x9d or 0x9e or 0x9f)
            {
                _state = rune.Value == 0x9b ? State.Csi : State.String;
                _sequence.Clear(); _invalid = false;
                if (_state == State.String) { _stringBel = rune.Value == 0x9d; _stringEscape = false; Control(); }
            }
            else if (Rune.IsControl(rune)) Control();
            else foreach (var character in rune.ToString()) Key(0, character);
            return;
        }
        if (value == 0x1b) { _state = State.Escape; return; }
        DecodeAscii(value);
    }

    private void DecodeAscii(byte value, ConsoleModifiers modifiers = 0)
    {
        switch (value)
        {
            case 13: Key(ConsoleKey.Enter, '\r', modifiers); return;
            case 10: Key(ConsoleKey.Enter, '\n', modifiers | ConsoleModifiers.Control); return;
            case 9: Key(ConsoleKey.Tab, '\t', modifiers); return;
            case 8: case 127: Key(ConsoleKey.Backspace, (char)value, modifiers); return;
        }
        if (value is >= 1 and <= 26)
            Key(ConsoleKey.A + value - 1, (char)value, modifiers | ConsoleModifiers.Control);
        else if (value is >= (byte)'a' and <= (byte)'z') Key(ConsoleKey.A + value - 'a', (char)value, modifiers);
        else if (value is >= (byte)'A' and <= (byte)'Z') Key(ConsoleKey.A + value - 'A', (char)value, modifiers | ConsoleModifiers.Shift);
        else if (value is >= (byte)'0' and <= (byte)'9') Key(ConsoleKey.D0 + value - '0', (char)value, modifiers);
        else if (value >= 32) Key(0, (char)value, modifiers);
        else Control();
    }

    private bool DecodeKey(string sequence, bool ss3)
    {
        if (!ss3 && sequence.StartsWith('<'))
        {
            var mouse = sequence[1..^1].Split(';');
            if (sequence[^1] is not ('M' or 'm') || mouse.Length != 3
                || !int.TryParse(mouse[0], out var button) || button < 0
                || !int.TryParse(mouse[1], out var x) || x <= 0
                || !int.TryParse(mouse[2], out var y) || y <= 0) return false;
            var code = button & ~28;
            var pointerModifiers = ((button & 4) != 0 ? ConsoleModifiers.Shift : 0)
                | ((button & 8) != 0 ? ConsoleModifiers.Alt : 0)
                | ((button & 16) != 0 ? ConsoleModifiers.Control : 0);
            if (code is 64 or 65 && sequence[^1] == 'M')
            {
                if (!_discardSequenceScroll && !_discardSequenceWheel)
                    _events.Enqueue(new(TerminalInputEventKind.MouseWheel, default, ScrollDelta: (code & 1) == 0 ? -1 : 1,
                        Pointer: new(code, x, y, pointerModifiers, PointerPhase.Press)));
                return true;
            }
            var phase = sequence[^1] == 'm' ? PointerPhase.Release : code is >= 32 and <= 34 ? PointerPhase.Drag : PointerPhase.Press;
            if (!(code is >= 0 and <= 2 || code is >= 32 and <= 34 && phase == PointerPhase.Drag)) return false;
            if (!_discardSequenceScroll)
                _events.Enqueue(new(TerminalInputEventKind.Pointer, default,
                    Pointer: new(code & 3, x, y, pointerModifiers, phase)));
            return true;
        }
        // Narrow, explicit modified-Enter forms only. No keyboard mode is
        // enabled: other enhanced key reports remain quarantined controls.
        if (!ss3 && sequence == "27;2;13~")
        {
            _events.Enqueue(new(_discardSequenceScroll ? TerminalInputEventKind.ControlSequence : TerminalInputEventKind.LineBreak, default));
            return true;
        }
        if (!ss3 && sequence.StartsWith('?') && sequence.EndsWith('u')
            && Decimal(sequence[1..^1], out var flags) && flags is >= 0 and <= 31)
        {
            KeyboardReplyFlags = flags; KeyboardReplyRevision++;
            return true; // Consume even late replies: never a key or approval.
        }
        if (!ss3 && sequence.EndsWith('u')) return DecodeKitty(sequence[..^1]);
        var final = sequence[^1];
        var parameters = sequence[..^1];
        var modifiers = (ConsoleModifiers)0;
        var wireModifiers = 0;
        if (parameters.Contains(';'))
        {
            var parts = parameters.Split(';');
            if (parts.Length != 2 || !Decimal(parts[1], out var encoded) || encoded is < 1 or > 256) return false;
            var bits = encoded - 1;
            if ((bits & 56) != 0) return false;
            wireModifiers = bits;
            modifiers = ((bits & 1) != 0 ? ConsoleModifiers.Shift : 0)
                | ((bits & 2) != 0 ? ConsoleModifiers.Alt : 0)
                | ((bits & 4) != 0 ? ConsoleModifiers.Control : 0);
            parameters = parts[0];
        }
        if (ss3 && parameters.Length == 0)
        {
            if (final is >= 'p' and <= 'y')
            {
                Key(ConsoleKey.NumPad0 + final - 'p', (char)('0' + final - 'p'));
                return true;
            }
            if (final == 'M') { Key(ConsoleKey.Enter, '\r'); return true; }
        }
        ConsoleKey key;
        if ((parameters is "" or "1") && final is 'A' or 'B' or 'C' or 'D' or 'H' or 'F')
            key = final switch { 'A' => ConsoleKey.UpArrow, 'B' => ConsoleKey.DownArrow,
                'C' => ConsoleKey.RightArrow, 'D' => ConsoleKey.LeftArrow, 'H' => ConsoleKey.Home, _ => ConsoleKey.End };
        else if ((parameters is "" or "1") && (final == 'Q' || ss3 && final == 'R'))
            key = final == 'Q' ? ConsoleKey.F2 : ConsoleKey.F3;
        else if (!ss3 && final == '~')
            key = parameters switch { "1" or "7" => ConsoleKey.Home, "4" or "8" => ConsoleKey.End,
                "2" => ConsoleKey.Insert, "3" => ConsoleKey.Delete, "5" => ConsoleKey.PageUp, "6" => ConsoleKey.PageDown, "12" => ConsoleKey.F2, "13" => ConsoleKey.F3, _ => 0 };
        else if (!ss3 && sequence == "Z") { key = ConsoleKey.Tab; modifiers = ConsoleModifiers.Shift; }
        else return false;
        if (key == 0) return false;
        Key(key, modifiers: modifiers, wireModifiers: wireModifiers);
        return true;
    }
    private bool DecodeKitty(string parameters)
    {
        // Flag 1 has press events only, no alternate key codes or associated
        // text. Reject unsupported extensions whole, rather than losing fields.
        var fields = parameters.Split(';');
        if (fields.Length is < 1 or > 2 || !Decimal(fields[0], out var code)) return false;
        var encoded = 1;
        if (fields.Length == 2 && !Decimal(fields[1], out encoded)) return false;
        if (encoded is < 1 or > 256) return false;
        var bits = encoded - 1;
        // Super+C is routed only by the History editor. Never turn other
        // super/hyper/meta keys into unmodified shortcuts.
        var superCopy = code == 99 && (bits & 63) == 8;
        if ((bits & 56) != 0 && !superCopy) return false;
        var modifiers = ((bits & 1) != 0 ? ConsoleModifiers.Shift : 0)
            | ((bits & 2) != 0 ? ConsoleModifiers.Alt : 0)
            | ((bits & 4) != 0 ? ConsoleModifiers.Control : 0);
        ConsoleKey key;
        char character;
        switch (code)
        {
            case 13: key = ConsoleKey.Enter; character = '\r'; break;
            case 27: key = ConsoleKey.Escape; character = '\u001b'; break;
            case 9: key = ConsoleKey.Tab; character = '\t'; break;
            case 127: key = ConsoleKey.Backspace; character = '\u007f'; break;
            default:
                if (code is >= 97 and <= 122 && ((bits & 6) != 0 || superCopy))
                {
                    key = ConsoleKey.A + code - 97;
                    character = (bits & 4) != 0 ? (char)(code - 96) : '\0';
                }
                else return false;
                break;
        }
        var kind = key == ConsoleKey.Enter && modifiers == ConsoleModifiers.Shift
            ? TerminalInputEventKind.LineBreak : TerminalInputEventKind.Key;
        // Crossing a page boundary must not replay a fragmented modified Enter.
        if (_discardSequenceScroll && (IsScroll(key) || kind == TerminalInputEventKind.LineBreak))
        { Control(); return true; }
        _events.Enqueue(new(kind, new ConsoleKeyInfo(character, key,
            modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt),
            modifiers.HasFlag(ConsoleModifiers.Control)), KittyCode: code, KittyModifiers: bits));
        return true;
    }

    private static bool Decimal(string value, out int number)
    {
        number = 0;
        return value.Length > 0 && value.All(c => c is >= '0' and <= '9') && int.TryParse(value, out number);
    }

}
