namespace Auxim.Cli.Interactive;

// One owned stack entry on the current screen. All I/O is performed by the
// existing synchronous stdin owner, before handing input to another consumer.
internal sealed class TerminalKeyboardProtocol(
    TerminalInputDecoder decoder, Action<string> write, Func<int, bool> read,
    Func<long>? clock = null)
{
    internal const int TimeoutMilliseconds = 200;
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private bool? _screen;
    private bool _attempted;
    private bool _supported;
    private bool _pushed;
    public int ActiveFlags { get; private set; }
    public string Status { get; private set; } = "not queried";

    public void Enable(bool alternate)
    {
        if (_screen != alternate)
        {
            if (_pushed) throw new InvalidOperationException("Keyboard stack must be restored before switching screens.");
            _screen = alternate; _attempted = _supported = false;
        }
        if (_pushed && ActiveFlags == 1) return;
        if (!_attempted)
        {
            _attempted = true;
            var flags = Query();
            if (flags is null) { Status = "legacy: no valid Kitty response (timeout/unsupported)"; return; }
            _supported = true;
        }
        if (!_supported) return;
        Restore();
        // Push replaces flags only in our own entry; pop restores prior flags.
        _pushed = true;
        try
        {
            write("\u001b[>1u");
            if (Query() != 1)
            {
                Restore(); _supported = false;
                Status = "legacy: flag 1 activation was not confirmed";
                return;
            }
            ActiveFlags = 1;
            Status = "Kitty supported; active flags=1";
        }
        catch { Restore(); throw; }
    }

    public void Compatible()
    {
        if (!_supported || _pushed && ActiveFlags == 0) return;
        Restore();
        // Protected readers require legacy keys even if the caller originally
        // had Kitty enabled. Save that caller state instead of overwriting it.
        _pushed = true;
        try { write("\u001b[>0u"); ActiveFlags = 0; Status = "Kitty suspended; protected reader flags=0"; }
        catch { Restore(); throw; }
    }

    public void Restore()
    {
        if (!_pushed) return;
        _pushed = false; ActiveFlags = 0;
        write("\u001b[<u");
        Status = "Kitty stack restored to caller";
    }

    private int? Query()
    {
        // Consume already-arrived replies before establishing this query's
        // boundary. Replies are never placed in the ordinary key queue.
        for (var i = 0; i < 16 && read(0); i++) { }
        var revision = decoder.KeyboardReplyRevision;
        write("\u001b[?u");
        var deadline = _clock() + TimeoutMilliseconds;
        while (_clock() < deadline)
        {
            read(10);
            if (decoder.KeyboardReplyRevision != revision) return decoder.KeyboardReplyFlags;
        }
        return null;
    }
}
