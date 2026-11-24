using System.Text.Json;
using Auxim.Core.Approval;
using Auxim.Core.Config;
using Auxim.Core.Resources;
using Auxim.Core.Runtime;
using Auxim.Core.Tools;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class AuximAgentToolExecutionTests
{
    [Fact]
    public async Task RunnerUsesRuntimeToolServiceAndReturnsDenialFeedbackToAgent()
    {
        const string environmentDescription = "runtime-provided VAFS environment";
        var client = new DeniedToolCallAgentClient();
        var runtimeTools = new DenyingRuntimeToolService("user rejected this operation");
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition(
            "protected.write",
            "test",
            "Writes a protected resource.",
            (_, _) => Task.FromResult("should not run")));
        var runner = new AuximAgentRunner(_ => client, () => registry, runtimeTools);
        var runId = AuximRunId.New();
        var home = Path.Combine(
            Path.GetTempPath(),
            "auxim-agent-tool-tests",
            Guid.NewGuid().ToString("N"));

        var result = await runner.RunAsync(new AgentRunRequest(
            runId,
            "test-session",
            "write the file",
            [],
            new AuximConfig
            {
                Model = new ModelConfig { Provider = "test", Name = "test" },
                Agent = new AgentConfig { MaxIterations = 2 },
            },
            home,
            environmentDescription,
            NonInteractiveApprovalHandler.Instance,
            NullEventSink.Instance));

        Assert.Equal("continued without the tool", result.FinalResponse);
        Assert.Equal(runId, runtimeTools.RunId);
        Assert.Equal("call-1", runtimeTools.ToolCallId);
        Assert.Equal("protected.write", runtimeTools.ToolName);
        Assert.Equal("/workspace/file.txt", runtimeTools.Arguments["path"]?.ToString());
        Assert.Equal(home, runtimeTools.HomeDirectory);

        var systemPrompt = Assert.Single(
            client.Requests[0],
            message => message.Role == "system");
        Assert.Contains(environmentDescription, systemPrompt.Content);

        var followUp = Assert.Single(client.Requests.Skip(1));
        var toolResult = Assert.Single(followUp, message => message.Role == "tool");
        using var document = JsonDocument.Parse(toolResult.Content);
        Assert.True(document.RootElement.GetProperty("denied").GetBoolean());
        Assert.Equal("protected.write", document.RootElement.GetProperty("tool").GetString());
        Assert.Equal(
            "user rejected this operation",
            document.RootElement.GetProperty("userFeedback").GetString());
        Assert.False(document.RootElement.TryGetProperty("error", out _));
        Assert.Contains(followUp, message =>
            message.Role == "user"
            && message.Content.Contains(
                "user rejected this operation",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TurnCancellationStopsRemainingToolsAndModelRequests(bool toolThrows)
    {
        using var turn = new CancellationTokenSource();
        var client = new CountingToolClient();
        var tools = new ScriptedTools(token =>
        {
            turn.Cancel();
            if (toolThrows) token.ThrowIfCancellationRequested();
            return Task.FromResult("completed despite cancellation");
        });
        var agent = new AuximAgent(client, new ToolRegistry(), tools);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            agent.RunConversationAsync("run two tools", cancellationToken: turn.Token));
        Assert.Equal(1, tools.Count);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToolTimeoutAndFailureAreReportedWithoutCancellingTheTurn(bool toolTimesOut)
    {
        using var toolTimeout = new CancellationTokenSource();
        toolTimeout.Cancel();
        var client = new CountingToolClient();
        var tools = new ScriptedTools(_ => toolTimesOut
            ? Task.FromCanceled<string>(toolTimeout.Token)
            : Task.FromException<string>(new InvalidOperationException("ordinary tool failure")));
        var agent = new AuximAgent(client, new ToolRegistry(), tools);

        var result = await agent.RunConversationAsync("run two tools");
        Assert.Equal("finished", result.FinalResponse);
        Assert.Equal(2, tools.Count);
        Assert.Equal(2, client.Requests.Count);
        var replies = client.Requests[1].Where(message => message.Role == "tool").ToArray();
        Assert.Equal(2, replies.Length);
        foreach (var reply in replies)
        {
            using var json = JsonDocument.Parse(reply.Content);
            Assert.True(json.RootElement.GetProperty("error").GetBoolean());
            Assert.False(json.RootElement.TryGetProperty("denied", out _));
        }
    }

    [Fact]
    public async Task AModelThatReturnsSuccessAfterCancellationCannotCompleteTheTurn()
    {
        using var turn = new CancellationTokenSource();
        var client = new CountingToolClient(() => turn.Cancel());
        var tools = new ScriptedTools(_ => Task.FromResult("tool output"));
        var agent = new AuximAgent(client, new ToolRegistry(), tools);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            agent.RunConversationAsync("run two tools", cancellationToken: turn.Token));
        Assert.Equal(2, tools.Count);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task AlreadyCancelledTurnMakesNoModelOrToolRequests()
    {
        var client = new CountingToolClient();
        var tools = new ScriptedTools(_ => Task.FromResult(""));
        var agent = new AuximAgent(client, new ToolRegistry(), tools);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            agent.RunConversationAsync("do nothing", cancellationToken: new CancellationToken(true)));
        Assert.Equal(0, tools.Count);
        Assert.Empty(client.Requests);
    }

    private sealed class CountingToolClient(Action? onFinalRequest = null) : IToolCallingAgentClient
    {
        public List<IReadOnlyList<AgentMessage>> Requests { get; } = [];

        public Task<string> CompleteAsync(IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AgentClientResponse> CompleteWithToolsAsync(IReadOnlyList<AgentMessage> messages,
            IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
        {
            Requests.Add(messages.ToArray());
            if (Requests.Count == 1)
                return Task.FromResult(new AgentClientResponse("", [new("first", "echo", "{}"), new("second", "echo", "{}")]));
            onFinalRequest?.Invoke();
            return Task.FromResult(new AgentClientResponse("finished", []));
        }
    }

    private sealed class ScriptedTools(Func<CancellationToken, Task<string>> invoke) : IRuntimeToolService
    {
        public int Count { get; private set; }
        public IReadOnlyList<AuximRuntimeTool> ListTools() => [];
        public IReadOnlyList<ResourceAccess> ResolveResourceAccesses(string name, IReadOnlyDictionary<string, object?> arguments) => [];

        public Task<string> InvokeAsync(AuximRunId runId, string toolCallId, string name,
            IReadOnlyDictionary<string, object?> arguments, string homeDirectory, IApprovalHandler approvalHandler,
            IRuntimeEventSink eventSink, CancellationToken cancellationToken)
        {
            Count++;
            return invoke(cancellationToken);
        }
    }

    private sealed class DeniedToolCallAgentClient : IToolCallingAgentClient
    {
        public List<IReadOnlyList<AgentMessage>> Requests { get; } = [];

        public Task<string> CompleteAsync(
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AgentClientResponse> CompleteWithToolsAsync(
            IReadOnlyList<AgentMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToArray());
            return Task.FromResult(Requests.Count == 1
                ? new AgentClientResponse(
                    "",
                    [new ToolCallRequest(
                        "call-1",
                        "protected_write",
                        "{\"path\":\"/workspace/file.txt\"}")])
                : new AgentClientResponse("continued without the tool", []));
        }
    }

    private sealed class DenyingRuntimeToolService(string reason) : IRuntimeToolService
    {
        public AuximRunId RunId { get; private set; }
        public string ToolCallId { get; private set; } = "";
        public string ToolName { get; private set; } = "";
        public IReadOnlyDictionary<string, object?> Arguments { get; private set; } =
            new Dictionary<string, object?>();
        public string HomeDirectory { get; private set; } = "";

        public IReadOnlyList<AuximRuntimeTool> ListTools() => [];

        public IReadOnlyList<ResourceAccess> ResolveResourceAccesses(
            string name,
            IReadOnlyDictionary<string, object?> arguments) => [];

        public Task<string> InvokeAsync(
            AuximRunId runId,
            string toolCallId,
            string name,
            IReadOnlyDictionary<string, object?> arguments,
            string homeDirectory,
            IApprovalHandler approvalHandler,
            IRuntimeEventSink eventSink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunId = runId;
            ToolCallId = toolCallId;
            ToolName = name;
            Arguments = arguments;
            HomeDirectory = homeDirectory;
            return Task.FromException<string>(new ToolApprovalDeniedException(name, reason));
        }
    }

    private sealed class NullEventSink : IRuntimeEventSink
    {
        public static NullEventSink Instance { get; } = new();

        public ValueTask PublishAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
