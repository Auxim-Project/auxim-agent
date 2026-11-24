using Auxim.Cli.Interactive;
using Xunit;

namespace Auxim.Agent.Tests;

[CollectionDefinition("Console capture", DisableParallelization = true)]
public sealed class ConsoleCaptureCollection;

[Collection("Console capture")]
public sealed class InteractiveCommandResultTests
{
    [Theory]
    [InlineData("model set", CommandPresentation.Foreground)]
    [InlineData("model set local", CommandPresentation.Result)]
    [InlineData("model set local local", CommandPresentation.Result)]
    [InlineData("auth set-api-key", CommandPresentation.Foreground)]
    [InlineData("auth set-api-key literal", CommandPresentation.Result)]
    [InlineData("tool run", CommandPresentation.Result)]
    [InlineData("tool run echo", CommandPresentation.Foreground)]
    [InlineData("tool list", CommandPresentation.Result)]
    [InlineData("session", CommandPresentation.Navigation)]
    [InlineData("session search words", CommandPresentation.Result)]
    [InlineData("session help", CommandPresentation.Result)]
    [InlineData("session list", CommandPresentation.Result)]
    [InlineData("session use", CommandPresentation.Result)]
    [InlineData("session use known", CommandPresentation.Result)]
    [InlineData("sessions", CommandPresentation.Result)]
    [InlineData("resume", CommandPresentation.Result)]
    [InlineData("help", CommandPresentation.Result)]
    [InlineData("status", CommandPresentation.Result)]
    [InlineData("doctor", CommandPresentation.Result)]
    public void PresentationDependsOnSubcommandAndArguments(string command, object expected)
        => Assert.Equal((CommandPresentation)expected, InteractiveCommandResult.Classify(InteractiveCommandParser.Tokenize(command)));

    [Fact]
    public void CompletionUsesOnlyTheCurrentCommandContext()
    {
        Assert.Equal(["/session"], InteractiveCommandCatalog.Complete("/ses"));
        Assert.Equal(["/session clear"], InteractiveCommandCatalog.Complete("/SESSION cl"));
        Assert.Empty(InteractiveCommandCatalog.Complete("/SESSION se"));
        Assert.Empty(InteractiveCommandCatalog.Complete("/resu"));
        Assert.Equal(["/session help"], InteractiveCommandCatalog.Complete("/session h"));
        Assert.Empty(InteractiveCommandCatalog.Complete("/session search already-typed"));
        Assert.Empty(InteractiveCommandCatalog.Complete("/session\n"));
    }

    [Fact]
    public void CaptureRetainsOutputOrderAndFailureAndRestoresBothWriters()
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        var result = InteractiveCommandResult.Capture(() =>
        {
            Console.WriteLine("first");
            Console.Error.WriteLine("second");
            Console.WriteLine("\u001b[2Jthird\u001b]0;title\a");
            throw new InvalidOperationException("test failure");
        });
        Assert.Same(stdout, Console.Out);
        Assert.Same(stderr, Console.Error);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("first", result.Lines[0]);
        Assert.Equal("stderr: second", result.Lines[1]);
        Assert.Contains("test failure", result.Lines[^1]);
        Assert.DoesNotContain('\u001b', string.Join('\n', result.Lines));
        Assert.DoesNotContain('\a', string.Join('\n', result.Lines));
    }

    [Fact]
    public void CaptureIsBoundedEvenWhenACommandKeepsWriting()
    {
        var result = InteractiveCommandResult.Capture(() =>
        {
            Console.Write(new string('x', InteractiveCommandResult.MaximumCharacters * 3));
            return 7;
        });
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(InteractiveCommandResult.MaximumCharacters, result.Lines[0].Length);
        Assert.Contains("Output truncated", result.Lines[^1]);
    }
}
