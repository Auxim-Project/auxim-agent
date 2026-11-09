using System.Runtime.InteropServices;

namespace Auxim.Cli.Interactive;

internal enum InteractiveExecutionState { Idle, Running, Cancelling }

// Signal callbacks only request cancellation. Rendering and draft changes stay
// with their foreground owners; the existing runtime task is always awaited.
internal sealed class InteractiveInterrupt : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _turn;
    private int _idleInterrupt;
    private PosixSignalRegistration? _signal;
    private bool _attached;
    private static InteractiveInterrupt? _current;
    public static InteractiveInterrupt? Current => Volatile.Read(ref _current);
    private InteractiveExecutionState _state;
    public InteractiveExecutionState State { get { lock (_sync) return _state; } }

    public static InteractiveInterrupt Enter()
    {
        var owner = new InteractiveInterrupt();
        Volatile.Write(ref _current, owner);
        owner._attached = true;
        Console.CancelKeyPress += owner.OnCancel;
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            owner._signal = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                owner.Request();
            });
        return owner;
    }

    public static bool HandleSignal()
    {
        var owner = Current;
        if (owner is null) return false;
        owner.Request();
        return true;
    }

    private void OnCancel(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        // POSIX registration owns the request there. Console may report the
        // same SIGINT too; don't turn one idle interrupt into two draft edits.
        if (_signal is null) Request();
    }

    public void BeginTurn(CancellationTokenSource turn)
    {
        lock (_sync)
        {
            _turn = turn;
            _state = InteractiveExecutionState.Running;
            Interlocked.Exchange(ref _idleInterrupt, 0);
        }
    }

    public void EndTurn()
    {
        lock (_sync) { _turn = null; _state = InteractiveExecutionState.Idle; }
    }

    public void Request()
    {
        lock (_sync)
        {
            if (State == InteractiveExecutionState.Idle) Interlocked.Exchange(ref _idleInterrupt, 1);
            else if (State == InteractiveExecutionState.Running)
            {
                _state = InteractiveExecutionState.Cancelling;
                _turn!.Cancel();
            }
        }
    }

    public bool TakeIdleInterrupt() => Interlocked.Exchange(ref _idleInterrupt, 0) != 0;

    public async Task<T> AwaitTurnAsync<T>(Task<T> operation, Action? cancelling = null)
    {
        var announced = false;
        try
        {
            while (!operation.IsCompleted)
            {
                // Approval has exclusive input ownership. No competing read is
                // made there, nor while a child owns the cooked terminal.
                TerminalInput.PollRunningCancellation();
                if (!announced && State == InteractiveExecutionState.Cancelling)
                {
                    cancelling?.Invoke();
                    announced = true;
                }
                await Task.WhenAny(operation, Task.Delay(25));
            }
            return await operation;
        }
        catch
        {
            // An input/terminal failure must not leave an orphaned agent task
            // running while the shell resumes editing or disposes its token.
            Request();
            try { await operation; } catch { /* Preserve the original failure. */ }
            throw;
        }
    }

    public void Dispose()
    {
        EndTurn();
        if (!_attached) return;
        _signal?.Dispose();
        Console.CancelKeyPress -= OnCancel;
        Volatile.Write(ref _current, null);
    }
}
