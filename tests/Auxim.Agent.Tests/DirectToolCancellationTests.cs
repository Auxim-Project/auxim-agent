using Auxim.Cli;
using Auxim.Core.Approval;
using Auxim.Core.Resources;
using Auxim.Core.Runtime;
using Xunit;
namespace Auxim.Agent.Tests;

public sealed class DirectToolCancellationTests
{
    private sealed class Tools : IRuntimeToolService
    {
        public CancellationToken Received;
        public IReadOnlyDictionary<string, object?> Arguments = null!;
        public IApprovalHandler Approval = null!;
        public Func<CancellationToken, Task<string>> Execute = _ => Task.FromResult("result");
        public IReadOnlyList<AuximRuntimeTool> ListTools() => [];
        public IReadOnlyList<ResourceAccess> ResolveResourceAccesses(string name, IReadOnlyDictionary<string, object?> args) => [];
        public Task<string> InvokeAsync(AuximRunId runId, string toolCallId, string name,
            IReadOnlyDictionary<string, object?> arguments, string homeDirectory, IApprovalHandler approvalHandler,
            IRuntimeEventSink eventSink, CancellationToken cancellationToken)
        {
            Received = cancellationToken; Arguments = arguments; Approval = approvalHandler;
            return Execute(cancellationToken);
        }
    }
    private static IAuximRuntime Runtime(Tools tools) => new AuximRuntimeService(null!, tools, homeDirectory: () => Path.GetTempPath());

    [Fact]
    public async Task TokenReachesTheActualToolAndCooperativeCancellationPropagates()
    {
        var tools = new Tools { Execute = async token => { await Task.Delay(Timeout.Infinite, token); return "unreachable"; } };
        using var turn = new CancellationTokenSource();
        var running = CommandHandlers.HandleToolWithCancellation(["run", "test", "text=a=b"], Runtime(tools), turn.Token);
        Assert.Equal(turn.Token, tools.Received); Assert.Equal("a=b", tools.Arguments["text"]);
        Assert.IsType<Auxim.Cli.Interactive.CliApprovalHandler>(tools.Approval);
        turn.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task IgnoringCancellationIsNotReportedAsSuccessfulCancellation()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new Tools { Execute = _ => pending.Task };
        using var turn = new CancellationTokenSource();
        var running = CommandHandlers.HandleToolWithCancellation(["run", "test"], Runtime(tools), turn.Token);
        turn.Cancel(); Assert.False(running.IsCompleted);
        pending.SetResult("completed with effects"); Assert.Equal(0, await running);
    }

    [Fact]
    public async Task PublicEntryKeepsTheNonInteractiveUncancelledToken()
    {
        var tools = new Tools(); Assert.Equal(0, await CommandHandlers.HandleTool(["run", "test"], Runtime(tools)));
        Assert.False(tools.Received.CanBeCanceled);
    }

    [Fact]
    public async Task ToolFailureRemainsAnExceptionForTheInteractiveBoundaryToReport()
    {
        var tools = new Tools { Execute = _ => Task.FromException<string>(new IOException("tool failed")) };
        await Assert.ThrowsAsync<IOException>(() => CommandHandlers.HandleToolWithCancellation(["run", "test"], Runtime(tools), default));
    }
}
