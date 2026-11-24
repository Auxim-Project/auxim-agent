using System.Diagnostics;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class InteractiveViewTests
{
    [Fact]
    public async Task HistoryAndResumeAreDistinctFromMain()
    {
        var output = await RunCliAsync([
            "first turn", "second turn", "/history", "/show 1",
            "/session", "1", "/history", "/jump 2", "/tail 1", "/exit",
        ]);

        Assert.Equal(1, Count(output, "AUXIM / New Chat"));
        Assert.Contains("Sessions", output);
        Assert.Equal(2, Count(output, "Conversation History (2 turns)"));
        Assert.Contains("Turn 1", output);
        Assert.Equal(2, Count(output, "History from turn 2"));
        Assert.Contains("Auxim received: first turn", output);
        Assert.Contains("Auxim received: second turn", output);
    }

    [Fact]
    public async Task EmptyHistoryAndNewChatAliasesLeaveThePromptUsable()
    {
        var output = await RunCliAsync([
            "/history", "/show 1", "/jump 1", "/clear", "/redraw", "/welcome",
            "after returning", "/history", "/show 1", "/exit",
        ]);

        Assert.Contains("No turns in the current session.", output);
        Assert.Equal(2, Count(output, "Current session has no turns."));
        Assert.Equal(4, Count(output, "AUXIM / New Chat"));
        Assert.Contains("Conversation History (1 turn)", output);
        Assert.Contains("Turn 1", output);
        Assert.Contains("Auxim received: after returning", output);
    }

    private static int Count(string output, string text) =>
        output.Split(text, StringSplitOptions.None).Length - 1;

    [Fact]
    public async Task CommandResultsDoNotBecomeMessagesAndFailuresLeaveTheShellUsable()
    {
        var output = await RunCliAsync([
            "/status", "/model show", "/help", "/doctor", "/config set",
            "/session use missing", "only actual message", "/history", "/exit",
        ]);
        Assert.Contains("Auxim Doctor", output);
        Assert.Contains("Provider:", output);
        Assert.Contains("Usage:", output);
        Assert.Contains("stderr: Usage: /session [clear|help]", output);
        Assert.Contains("Failed", output);
        Assert.Contains("exit 1", output);
        Assert.Contains("Conversation History (1 turn)", output);
        Assert.DoesNotContain("Press Enter to return", output);
        Assert.DoesNotContain('\u001b', output);
    }

    private static async Task<string> RunCliAsync(IReadOnlyList<string> commands)
    {
        var home = Path.Combine(Path.GetTempPath(), "auxim-cli-view-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = home,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(TranscriptViewport).Assembly.Location);
        start.Environment["AUXIM_HOME"] = home;
        start.Environment["AUXIM_PROVIDER"] = "local";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";

        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(string.Join(Environment.NewLine, commands) + Environment.NewLine);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stderr);
            return await stdout;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            Directory.Delete(home, recursive: true);
        }
    }
}
