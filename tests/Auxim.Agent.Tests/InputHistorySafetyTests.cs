using Auxim.Cli.Interactive;
using Xunit;
namespace Auxim.Agent.Tests;

public sealed class InputHistorySafetyTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "auxim-history-" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_home, "history");
    private string Versioned => Legacy + ".cli-v1.jsonl";
    public InputHistorySafetyTests() => Directory.CreateDirectory(_home);
    public void Dispose() => Directory.Delete(_home, true);

    [Fact]
    public void MultilineMessagesAndCommandsRoundtripWithoutPrefixInference()
    {
        var history = new InteractiveHistory(Legacy);
        history.Add(new("//printf test\r\n中文🙂\n/new", InputSubmissionKind.AgentMessage));
        history.Add(InputSubmission.Fresh("/status")); history.Save();
        var loaded = new InteractiveHistory(Legacy);
        Assert.Equal(history.Entries, loaded.Entries);
        Assert.Equal(InputSubmissionKind.AgentMessage, loaded.KindAt(0));
        Assert.Equal(InputSubmissionKind.CliCommand, loaded.KindAt(1));
        Assert.Equal(2, File.ReadAllLines(Versioned).Length);
        Assert.False(File.Exists(Legacy));
    }

    [Theory]
    [InlineData("/auth set-api-key very-sensitive")]
    [InlineData("/auth set-api-key 'quoted secret'")]
    [InlineData("/tool run echo api_key=secret")]
    [InlineData("//curl -H 'Authorization: Bearer secret' url")]
    [InlineData("//curl -u user:secret url")]
    [InlineData("//curl -d '{\"api_key\":\"secret\"}' url")]
    [InlineData("/model set provider model https://secret@host/v1")]
    public void SensitiveCommandsNeverReachDisk(string text)
    {
        var history = new InteractiveHistory(Legacy); history.Add(InputSubmission.Fresh(text)); history.Save();
        Assert.Empty(history.Entries); Assert.False(File.Exists(Versioned));
    }

    [Fact]
    public void LegacyBytesArePreservedAndAmbiguousInputsBecomeMessages()
    {
        const string old = "/status\n//printf test\n/auth set-api-key legacy-secret\n";
        File.WriteAllText(Legacy, old);
        var history = new InteractiveHistory(Legacy); history.Add(InputSubmission.Fresh("hello")); history.Save();
        Assert.Equal(old, File.ReadAllText(Legacy)); Assert.Equal(3, history.Entries.Count);
        Assert.Equal(InputSubmissionKind.AgentMessage, history.KindAt(0));
        Assert.DoesNotContain("legacy-secret", File.ReadAllText(Versioned));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Legacy));
    }

    [Theory]
    [InlineData("broken SYNTHETIC_SECRET")]
    [InlineData("{\"version\":1,\"kind\":\"CliCommand\",\"text\":\"/auth set-api-key SYNTHETIC_SECRET\"")]
    [InlineData("{\"version\":99,\"kind\":\"AgentMessage\",\"text\":\"SYNTHETIC_SECRET\"}")]
    [InlineData("{\"version\":1,\"kind\":\"CliCommand\",\"kind\":\"AgentMessage\",\"text\":\"SYNTHETIC_SECRET\"}")]
    [InlineData("{\"version\":1,\"kind\":\"CliCommand\",\"text\":[\"SYNTHETIC_SECRET\"]}")]
    public void UnsupportedOrDamagedHistoryIsNeitherRecalledNorOverwritten(string damaged)
    {
        var wire = InteractiveHistory.Encode(InputSubmission.Fresh("/status")) + "\n" + damaged + "\n"
            + InteractiveHistory.Encode(InputSubmission.Fresh("valid message")) + "\n";
        File.WriteAllText(Versioned, wire);
        var history = new InteractiveHistory(Legacy);
        Assert.Equal(["/status", "valid message"], history.Entries);
        Assert.Equal(InputSubmissionKind.CliCommand, history.KindAt(0));
        Assert.Equal(InputSubmissionKind.AgentMessage, history.KindAt(1));
        history.Add(InputSubmission.Fresh("new message")); history.Save();
        Assert.Equal(wire, File.ReadAllText(Versioned));
    }

    [Fact]
    public void InvalidUtf8CannotProduceAReplayableRecord()
    {
        var invalid = System.Text.Encoding.UTF8.GetBytes(
            InteractiveHistory.Encode(InputSubmission.Fresh("/status")) + "\n"
            + "{\"version\":1,\"kind\":\"HostCommand\",\"text\":\"//printf ");
        byte[] wire = [.. invalid, 0xff, (byte)'"', (byte)'}', (byte)'\n'];
        File.WriteAllBytes(Versioned, wire);
        var history = new InteractiveHistory(Legacy);
        Assert.DoesNotContain(history.Entries, entry => entry.Contains("printf", StringComparison.Ordinal));
        history.Add(InputSubmission.Fresh("new message")); history.Save();
        Assert.Equal(wire, File.ReadAllBytes(Versioned));
    }

    [Fact]
    public void AStaleWriterMergesOnlyNewEntriesAndDeduplicatesAgainstTheLatestTail()
    {
        var first = new InteractiveHistory(Legacy);
        var second = new InteractiveHistory(Legacy);
        first.Add(InputSubmission.Fresh("/status")); first.Save();
        second.Add(InputSubmission.Fresh("/status")); second.Save();
        second.Add(InputSubmission.Fresh("/context")); second.Save();
        first.Add(InputSubmission.Fresh("/status")); first.Save();

        Assert.Equal(["/status", "/context", "/status"], new InteractiveHistory(Legacy).Entries);
    }

    [Fact]
    public void MergedHistoryKeepsTheLastTwoHundredEntries()
    {
        var first = new InteractiveHistory(Legacy);
        var second = new InteractiveHistory(Legacy);
        for (var i = 1; i <= 200; i++) first.Add(InputSubmission.Fresh($"old {i}"));
        first.Save();
        second.Add(InputSubmission.Fresh("new")); second.Save();
        var merged = new InteractiveHistory(Legacy).Entries;
        Assert.Equal(200, merged.Count);
        Assert.Equal("old 2", merged[0]);
        Assert.Equal("new", merged[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataThatBecomesUnsafeAfterStartupBlocksTheMerge(bool validShape)
    {
        var history = new InteractiveHistory(Legacy);
        var wire = validShape ? InteractiveHistory.Encode(InputSubmission.Fresh("/auth set-api-key SYNTHETIC_SECRET"))
            : "{\"version\":1,\"text\":\"SYNTHETIC_SECRET";
        File.WriteAllText(Versioned, wire);
        history.Add(InputSubmission.Fresh("new message")); history.Save();
        Assert.Equal(wire, File.ReadAllText(Versioned));
        Assert.Equal(["new message"], history.Entries);
        Assert.Empty(Directory.GetFiles(_home, "*.tmp"));
    }

    [Fact]
    public void ExistingFilePermissionsAreExplicitlyTightened()
    {
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(Versioned, InteractiveHistory.Encode(InputSubmission.Fresh("hello")) + "\n");
        File.SetUnixFileMode(Versioned, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var history = new InteractiveHistory(Legacy);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Versioned));
        history.Save(); Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Versioned));
    }

    [Fact]
    public void SaveFailureLeavesExistingDataAndCleansItsTemporaryFile()
    {
        Directory.CreateDirectory(Versioned);
        var marker = Path.Combine(Versioned, "keep"); File.WriteAllText(marker, "original");
        var history = new InteractiveHistory(Legacy); history.Add(InputSubmission.Fresh("hello"));
        history.Save();
        Assert.Equal("original", File.ReadAllText(marker));
        Assert.Empty(Directory.GetFiles(_home, "*.tmp"));
    }

    [Fact]
    public void LinkedHistoryIsNotFollowedOrReplaced()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_home, "target"); File.WriteAllText(target, "preserved");
        File.CreateSymbolicLink(Legacy, target);
        var history = new InteractiveHistory(Legacy); history.Add(InputSubmission.Fresh("hello")); history.Save();
        Assert.Equal("preserved", File.ReadAllText(target)); Assert.False(File.Exists(Versioned));
        Assert.NotNull(new FileInfo(Legacy).LinkTarget);
    }

    [Fact]
    public void ExistingSensitiveVersionedHistoryRequiresExplicitReview()
    {
        var wire = InteractiveHistory.Encode(InputSubmission.Fresh("/auth set-api-key old-secret"));
        File.WriteAllText(Versioned, wire);
        var history = new InteractiveHistory(Legacy); history.Add(InputSubmission.Fresh("hello")); history.Save();
        Assert.Equal(wire, File.ReadAllText(Versioned)); Assert.DoesNotContain(history.Entries, text => text.Contains("old-secret"));
    }

    [Fact]
    public void DraftCopiesAndHistorySnapshotKeepMessageOriginAfterEditing()
    {
        var draft = new InputDraft(); draft.InsertPaste("//message");
        draft.SelectAll(); draft.InsertText("/new"); Assert.Equal(InputSubmissionKind.AgentMessage, draft.SubmissionKind);
        var saved = draft.Copy(); draft.Reset(1); draft.Restore(saved);
        draft.HistoryIndex = 1; draft.Previous(new[] { "/status" }); draft.Next(new[] { "/status" });
        Assert.Equal(InputSubmissionKind.AgentMessage, draft.SubmissionKind);
        draft.SelectAll(); draft.DeleteSelection(); Assert.Null(draft.SubmissionKind); Assert.False(draft.ContainsPaste);
    }
}
