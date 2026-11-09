using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class StreamingCliTests
{
    [Fact]
    public async Task LivePreviewPrecedesCompletionAndOnlyTheFinalAnswerIsSavedAndPrinted()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cli = new CliSession("hello\n/exit\n");
        var server = cli.ServeAsync([
            new Response(Text("planning"),
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call\",\"function\":{\"name\":\"echo\",\"arguments\":\"{\\\"text\\\":\\\"tool result\\\"}\"}}]}}]}\n\ndata: [DONE]\n\n",
                release.Task),
            new Response(Text("**authoritative answer**"), "data: [DONE]\n\n", Task.CompletedTask),
        ]);
        try
        {
            var early = await cli.ReadPreviewUntilAsync("planning");
            Assert.False(cli.Process.HasExited);
            Assert.Empty(cli.SavedMessages());
            release.TrySetResult();
            var (output, remainingPreview) = await cli.FinishAsync();
            await server;

            Assert.Equal(1, Count(output, "authoritative answer"));
            Assert.DoesNotContain("planning", output);
            Assert.DoesNotContain("**authoritative answer**", output); // formal Markdown renderer
            Assert.Contains("Tool  echo", output);
            Assert.Contains("**authoritative answer**", early + remainingPreview);
            Assert.DoesNotContain('\u001b', output + early + remainingPreview);
            Assert.Equal(["hello", "**authoritative answer**"], cli.SavedMessages());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task FailedStreamIsNotSavedAndTheFollowingRunStartsCleanly()
    {
        await using var cli = new CliSession("failed prompt\nnext prompt\n/exit\n");
        var server = cli.ServeAsync([
            new Response(Text("unfinished"), "data: {invalid json}\n\n", Task.CompletedTask),
            new Response(Text("recovered answer"), "data: [DONE]\n\n", Task.CompletedTask),
        ]);
        var (output, preview) = await cli.FinishAsync();
        await server;

        Assert.Contains("Error:", output);
        Assert.Contains("[preview failed; not saved]", preview);
        Assert.Equal(1, Count(output, "recovered answer"));
        Assert.DoesNotContain("unfinished", output);
        Assert.Equal(["next prompt", "recovered answer"], cli.SavedMessages());
    }

    private static string Text(string content) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content } } } }) + "\n\n";

    private static int Count(string output, string text) => output.Split(text, StringSplitOptions.None).Length - 1;
    private sealed record Response(string First, string Tail, Task Release);

    private sealed class CliSession : IAsyncDisposable
    {
        private readonly string _home = Path.Combine(Path.GetTempPath(), "auxim-stream-tests", Guid.NewGuid().ToString("N"));
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        private readonly Task<string> _output;
        public Process Process { get; }

        public CliSession(string input)
        {
            Directory.CreateDirectory(_home);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = _home,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(typeof(TranscriptViewport).Assembly.Location);
            start.Environment["AUXIM_HOME"] = _home;
            start.Environment["AUXIM_PROVIDER"] = "openai-compatible";
            start.Environment["AUXIM_MODEL"] = "test-model";
            start.Environment["AUXIM_BASE_URL"] = $"http://127.0.0.1:{port}/v1";
            start.Environment["AUXIM_API_KEY"] = "test-key";
            start.Environment["TERM"] = "dumb";
            start.Environment["NO_COLOR"] = "1";
            Process = System.Diagnostics.Process.Start(start)!;
            _output = Process.StandardOutput.ReadToEndAsync(_timeout.Token);
            Process.StandardInput.Write(input);
            Process.StandardInput.Close();
        }

        public async Task ServeAsync(IReadOnlyList<Response> responses)
        {
            foreach (var response in responses)
            {
                using var client = await _listener.AcceptTcpClientAsync(_timeout.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var length = 0;
                while (await reader.ReadLineAsync(_timeout.Token) is { Length: > 0 } line)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        length = int.Parse(line["Content-Length:".Length..].Trim());
                    }
                }

                var body = new char[length];
                await reader.ReadBlockAsync(body.AsMemory(), _timeout.Token);
                var size = Encoding.UTF8.GetByteCount(response.First + response.Tail);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {size}\r\nConnection: close\r\n\r\n"), _timeout.Token);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response.First), _timeout.Token);
                await stream.FlushAsync(_timeout.Token);
                await response.Release.WaitAsync(_timeout.Token);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response.Tail), _timeout.Token);
            }
        }

        public async Task<string> ReadPreviewUntilAsync(string text)
        {
            var result = new StringBuilder();
            var character = new char[1];
            while (!result.ToString().Contains(text, StringComparison.Ordinal))
            {
                Assert.Equal(1, await Process.StandardError.ReadAsync(character, _timeout.Token));
                result.Append(character[0]);
            }

            return result.ToString();
        }

        public async Task<(string Output, string Preview)> FinishAsync()
        {
            var preview = Process.StandardError.ReadToEndAsync(_timeout.Token);
            await Process.WaitForExitAsync(_timeout.Token);
            var error = await preview;
            Assert.True(Process.ExitCode == 0, error);
            return (await _output, error);
        }

        public string[] SavedMessages() => Directory.GetFiles(_home, "session-*.json", SearchOption.AllDirectories)
            .SelectMany(path =>
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.GetProperty("messages").EnumerateArray()
                    .Select(message => message.GetProperty("content").GetString()!).ToArray();
            }).ToArray();

        public async ValueTask DisposeAsync()
        {
            _timeout.Cancel();
            _listener.Stop();
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync();
            }

            Process.Dispose();
            _timeout.Dispose();
            Directory.Delete(_home, recursive: true);
        }
    }
}
