using System.Text;
using System.Runtime.ExceptionServices;
using Auxim.Core.Runtime;

namespace Auxim.Cli.Interactive;

// Delta producers only touch the pending buffer. The pump owns ordinary preview
// writes; awaited boundaries serialize tools/approvals with that same writer.
internal sealed class StreamingPreview : IAsyncDisposable
{
    private readonly StreamingPreviewRenderer _renderer;
    private readonly Action<RuntimeEvent> _renderTool;
    private readonly Action<RuntimeEvent>? _observe;
    private readonly Func<TerminalInputEvent, bool>? _navigate;
    private readonly Action? _onCompleted;
    private readonly object _pendingLock = new();
    private readonly StringBuilder _pending = new();
    private readonly SemaphoreSlim _output = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private bool _finished;
    private bool _approvalActive;

    public StreamingPreview(
        StreamingPreviewRenderer renderer,
        Action<RuntimeEvent> renderTool,
        TimeSpan? frameInterval = null,
        Action<RuntimeEvent>? observe = null,
        Func<TerminalInputEvent, bool>? navigate = null,
        Action? onCompleted = null)
    {
        _renderer = renderer;
        _renderTool = renderTool;
        _observe = observe;
        _navigate = navigate;
        _onCompleted = onCompleted;
        _pump = PumpAsync(frameInterval ?? TimeSpan.FromMilliseconds(40));
    }

    public IReadOnlyList<string> IncompleteLines { get; private set; } = [];

    public int ToolEventCount { get; private set; }

    public ValueTask PublishAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        if (_pump.IsFaulted) return new ValueTask(_pump);
        if (runtimeEvent is RuntimeContentDeltaEvent delta)
        {
            if (!string.IsNullOrEmpty(delta.Delta))
            {
                lock (_pendingLock)
                {
                    if (!_finished)
                    {
                        _pending.Append(delta.Delta);
                    }
                }
            }

            // Never run terminal output on the SSE producer's continuation.
            return ValueTask.CompletedTask;
        }

        if (_observe is not null && runtimeEvent is RuntimeRunStartedEvent or RuntimeRunCompletedEvent or RuntimeRunFailedEvent)
            return new ValueTask(RenderBoundaryAsync(runtimeEvent));

        return runtimeEvent is RuntimeToolStartedEvent or RuntimeToolCompletedEvent
            or RuntimeApprovalRequestedEvent or RuntimeApprovalResolvedEvent
            ? new ValueTask(RenderBoundaryAsync(runtimeEvent))
            : ValueTask.CompletedTask;
    }

    private async Task PumpAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await _output.WaitAsync(_stop.Token);
                try
                {
                    if (!_approvalActive)
                    {
                        FlushPending();
                        _renderer.Refresh(); // also observe resize while the model is idle
                    }
                }
                finally
                {
                    _output.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task RenderBoundaryAsync(RuntimeEvent runtimeEvent)
    {
        await _output.WaitAsync();
        try
        {
            if (_finished)
            {
                return;
            }

            if (runtimeEvent is RuntimeApprovalResolvedEvent)
            {
                _renderer.Resume();
                _approvalActive = false;
                _observe?.Invoke(runtimeEvent);
                _renderer.Refresh();
                return;
            }

            FlushPending();
            _renderer.EndSegment();
            _observe?.Invoke(runtimeEvent);
            if (runtimeEvent is RuntimeApprovalRequestedEvent)
            {
                // The runtime awaits this boundary before invoking the existing
                // approval handler. No timer write may overlap its prompt.
                if (_observe is not null) _renderer.Refresh();
                _approvalActive = true;
                _renderer.Suspend();
            }
            else if (runtimeEvent is RuntimeToolStartedEvent or RuntimeToolCompletedEvent)
            {
                ToolEventCount++;
                _renderTool(runtimeEvent);
            }
            else if (!_approvalActive) _renderer.Refresh();
        }
        finally
        {
            _output.Release();
        }
    }

    public async ValueTask<bool> NavigateAsync(TerminalInputEvent input)
    {
        await _output.WaitAsync();
        try
        {
            if (_finished || _navigate is null) return false;
            if (_approvalActive)
            {
                if (input.IsCtrlC) InteractiveInterrupt.HandleSignal();
                return false;
            }
            var leftHistory = _navigate(input);
            _renderer.Refresh();
            return leftHistory;
        }
        finally { _output.Release(); }
    }

    private void FlushPending()
    {
        string text;
        lock (_pendingLock)
        {
            if (_pending.Length == 0)
            {
                return;
            }

            text = _pending.ToString();
            _pending.Clear();
        }

        _renderer.Append(text);
    }

    public async Task FinishAsync(string? incompleteStatus = null)
    {
        lock (_pendingLock)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
        }

        _stop.Cancel();
        Exception? failure = null;
        try
        {
            try { await _pump; }
            catch (Exception exception) { failure = exception; }
            await _output.WaitAsync();
            try
            {
                if (failure is null)
                {
                    try { FlushPending(); }
                    catch (Exception exception) { failure = exception; }
                }
                if (incompleteStatus is not null) IncompleteLines = _renderer.Snapshot();
                else if (failure is null) _onCompleted?.Invoke();
                // Even a failed pump must release its physical screen region.
                try { _renderer.Finish(failure is null ? incompleteStatus : "failed"); }
                catch (Exception exception) { failure ??= exception; }
            }
            finally
            {
                _output.Release();
            }
        }
        finally
        {
            // Temporary text never enters the runtime or persistent session.
            lock (_pendingLock)
            {
                _pending.Clear();
            }

            _renderer.Reset();
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FinishAsync("cancelled");
        }
        finally
        {
            _stop.Dispose();
            _output.Dispose();
        }
    }
}
