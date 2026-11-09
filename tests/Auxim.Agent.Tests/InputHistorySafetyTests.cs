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
        Assert.Empty(history.Entries); Assert.Equal("", File.ReadAllText(Versioned));
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
    [InlineData("broken json")]
    [InlineData("{\"version\":99,\"kind\":\"AgentMessage\",\"text\":\"//test\"}")]
    public void UnsupportedOrDamagedHistoryIsNotOverwritten(string text)
    {
        File.WriteAllText(Versioned, text);
        var history = new InteractiveHistory(Legacy); history.Add(InputSubmission.Fresh("new message")); history.Save();
        Assert.Equal(text, File.ReadAllText(Versioned)); Assert.Equal(text, history.Entries[0]);
        Assert.Equal(InputSubmissionKind.AgentMessage, history.KindAt(0));
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
