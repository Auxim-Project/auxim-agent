using Auxim.Cli.Interactive;
using Auxim.Core.Config;
using Auxim.Core.Runtime;
using Auxim.Core.State;
using Xunit;

namespace Auxim.Agent.Tests;

public sealed class NavigationTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "auxim-navigation-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;
    private readonly IAuximRuntime _runtime;
    private readonly InteractiveView _view;

    public NavigationTests()
    {
        _store = new SessionStore(_home);
        _runtime = new AuximRuntimeService(null!, sessionStoreFactory: () => _store,
            configLoader: () => new AuximConfig(), homeDirectory: () => _home);
        _view = new InteractiveView(_runtime, DateTimeOffset.Now);
    }

    [Fact]
    public void StartupRestoresTheSelectionPointerRatherThanTheNewestModifiedSession()
    {
        var selected = _store.NewSession("Selected earlier");
        _store.AppendTurn(selected, "original question", "original reply");
        var newer = _store.NewSession("Newer but not selected", makeCurrent: false);
        _store.AppendTurn(newer, "other question", "other reply");
        var path = Path.Combine(_home, "sessions", selected.Id + ".json");
        var original = File.ReadAllText(path);
        _view.Initialize();
        Assert.Equal(InteractivePage.Main, _view.Page);
        Assert.False(_view.CanGoBack);
        Assert.Equal(selected.Id, _store.GetCurrentSessionId());
        _view.PrepareConversation();
        Assert.Equal(selected.Id, _store.GetCurrentSessionId());
        Assert.Equal(2, _runtime.ListSessions().Count);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void EitherWheelDirectionOpensHistoryWithoutChangingKeyboardPageDown(int delta)
    {
        var session = _store.NewSession("Existing");
        _store.AppendTurn(session, "question", "reply");
        _view.Initialize();
        _view.ScrollTranscript(10, () => throw new Exception("Main PgDn must remain a no-op"));
        Assert.Equal(InteractivePage.Main, _view.Page);
        var draws = 0;
        _view.ScrollWheel(delta, () => draws++);
        Assert.Equal(InteractivePage.History, _view.Page);
        Assert.Equal(1, draws);
        for (var i = 0; i < 10; i++) _view.ScrollWheel(1, () => draws++);
        Assert.Equal(InteractivePage.History, _view.Page);
        Assert.Equal(1, draws);
    }

    [Fact]
    public void HistoryTextSelectionIsContextualAndIgnoresResidualWheelReports()
    {
        var session = _store.NewSession("Existing"); _store.AppendTurn(session, "question", "reply");
        _view.Initialize(); Assert.False(_view.ToggleHistoryTextSelection());
        _view.ScrollWheel(1, () => { });
        Assert.True(_view.ToggleHistoryTextSelection()); Assert.True(_view.HistoryTextSelection);
        Assert.False(_view.CanPointComposer);
        _view.ScrollWheel(-1, () => throw new Exception("Native selection must ignore old mouse reports"));
        _view.ScrollTranscript(-10, () => { }); Assert.True(_view.HistoryTextSelection);
        _view.NavigateBack(); Assert.False(_view.HistoryTextSelection);
        Assert.Equal(InteractivePage.Main, _view.Page);
        _view.OpenResume(); Assert.False(_view.ToggleHistoryTextSelection()); Assert.False(_view.CanPointComposer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("broken")]
    public void MissingOrInvalidPointerOpensNewChatWithoutChoosingAnotherSession(string pointer)
    {
        var other = _store.NewSession("Not selected");
        _store.AppendTurn(other, "kept", "unchanged");
        File.WriteAllText(Path.Combine(_home, "current_session"), pointer);
        if (pointer == "broken") File.WriteAllText(Path.Combine(_home, "sessions", "broken.json"), "{invalid");
        _view.Initialize();
        Assert.Equal(InteractivePage.NewChat, _view.Page);
        Assert.Equal(pointer, _store.GetCurrentSessionId());
        Assert.Equal("kept", _runtime.GetSession(other.Id)!.Messages[0].Content);
        Assert.Equal(pointer == "broken" ? 2 : 1, Directory.GetFiles(Path.Combine(_home, "sessions")).Length);
    }

    [Fact]
    public void UnreadableResumeListDoesNotChangeTheActiveSession()
    {
        var session = _store.NewSession("Kept");
        File.WriteAllText(Path.Combine(_home, "sessions", "broken.json"), "{invalid");
        Assert.False(_view.OpenResume());
        Assert.Equal(session.Id, _store.GetCurrentSessionId());
        Assert.Equal(InteractivePage.NewChat, _view.Page);
    }

    [Fact]
    public void MainHasRealSectionsAndDoesNotInventActivityFromSavedMessages()
    {
        var session = _store.NewSession("Existing");
        _store.AppendTurn(session, "question", "Tool phantom completed");
        _view.Initialize();
        var text = string.Join("\n", _view.MainLines().Select(TerminalTextLayout.Plain));
        Assert.Contains("Agent / Session", text);
        Assert.Contains("Recent activity", text);
        Assert.Contains("Quick actions", text);
        Assert.Contains("/new", text); Assert.Contains("/session", text); Assert.Contains("/history", text);
        Assert.Contains("No activity in this CLI session.", text);
        Assert.DoesNotContain("phantom", text);
        _view.ScrollTranscript(1, () => throw new Exception("Main downward scroll must not redraw"));
        Assert.Equal(InteractivePage.Main, _view.Page);
    }

    [Fact]
    public void NewChatEscapeReturnsToMainWithoutCreatingOrSwitchingSessions()
    {
        var session = _store.NewSession("Existing");
        _store.AppendTurn(session, "kept", "reply");
        _view.StartNewSession();
        Assert.True(_view.CanGoBack);
        _view.NavigateBack();
        Assert.Equal(InteractivePage.Main, _view.Page);
        Assert.Equal(session.Id, _store.GetCurrentSessionId());
        Assert.Single(_runtime.ListSessions());
    }

    [Fact]
    public void DraftSnapshotRestoresTextCursorScrollAndHistoryBrowseState()
    {
        var history = new List<string> { "old", "selected" };
        var draft = new InputDraft(); draft.Reset(history.Count);
        draft.Buffer.Append("中文🙂" + new string('x', 400));
        draft.Cursor = 4; draft.FirstVisibleRow = 2; draft.RequestedRows = 8;
        draft.Previous(history);
        var saved = draft.Copy();
        draft.Reset(history.Count);
        history.Add("/new");
        draft.Restore(saved); draft.ResumeHistory(history);
        Assert.Equal("selected", draft.Buffer.ToString());
        draft.Next(history); draft.Next(history);
        Assert.StartsWith("中文🙂", draft.Buffer.ToString());
        Assert.Equal(4, draft.Cursor); Assert.Equal(2, draft.FirstVisibleRow);
        Assert.Equal("selected", saved.Buffer.ToString());
    }

    [Fact]
    public void EmptyStartupAndHistoryDoNotCreateAnEmptySavedSession()
    {
        _view.Initialize();
        _view.PrintHistory([]);
        Assert.Empty(_runtime.ListSessions());
        Assert.Equal(InteractivePage.NewChat, _view.Page);
        Assert.False(_view.OpenResume());
    }

    [Fact]
    public void HistoryAndResumeAlwaysReturnToMainInOneStep()
    {
        var session = _store.NewSession("Existing");
        _store.AppendTurn(session, "question", "answer");
        _view.Initialize();
        _view.PrintHistory([]);
        Assert.Equal(InteractivePage.History, _view.Page);
        Assert.True(_view.OpenResume());
        Assert.Equal(InteractivePage.Resume, _view.Page);
        _view.NavigateBack();
        Assert.Equal(InteractivePage.Main, _view.Page);
        _view.NavigateBack();
        Assert.Equal(InteractivePage.Main, _view.Page);
        _view.StartNewSession();
        _view.PrintHistory([]);
        _view.NavigateBack();
        Assert.Equal(InteractivePage.Main, _view.Page);
        _view.PrepareConversation();
        Assert.Equal(session.Id, _store.GetCurrentSessionId());
        Assert.Single(_runtime.ListSessions());
    }

    [Fact]
    public void SessionPreviewPreservesFilterAndActiveSessionAndCanReturnFromAnEmptySession()
    {
        var other = _store.NewSession("Empty 中文");
        var active = _store.NewSession("Active");
        _store.AppendTurn(active, "question", "answer");
        _view.Initialize();

        Assert.True(_view.OpenResume());
        _view.SetResumeQuery("中文");
        _view.OpenSessionPreview();
        Assert.Equal(InteractivePage.SessionPreview, _view.Page);
        Assert.Equal(active.Id, _store.GetCurrentSessionId());
        Assert.Empty(_runtime.GetSession(other.Id)!.Messages);

        _view.NavigateBack();
        Assert.Equal(InteractivePage.Resume, _view.Page);
        Assert.Equal(active.Id, _store.GetCurrentSessionId());
        Assert.True(_view.ResumeSession());
        Assert.Equal(other.Id, _store.GetCurrentSessionId());
    }

    [Fact]
    public void SessionPreviewReadFailureLeavesTheActiveSessionUntouched()
    {
        var unreadable = _store.NewSession("Unreadable");
        var active = _store.NewSession("Active");
        _view.Initialize();
        Assert.True(_view.OpenResume());
        _view.SetResumeQuery("Unreadable");
        File.WriteAllText(Path.Combine(_home, "sessions", unreadable.Id + ".json"), "{invalid");

        _view.OpenSessionPreview();
        Assert.Equal(InteractivePage.SessionPreview, _view.Page);
        Assert.False(_view.ResumeSession());
        Assert.Equal(active.Id, _store.GetCurrentSessionId());
        _view.NavigateBack();
        Assert.Equal(InteractivePage.Resume, _view.Page);
    }

    [Fact]
    public void ResumeCancellationAndInvalidSelectionDoNotSwitchOrCreateSessions()
    {
        var session = _store.NewSession("Existing");
        _view.OpenResume();
        Assert.False(_view.ResumeSession("missing"));
        _view.NavigateBack();
        Assert.Equal(InteractivePage.Main, _view.Page);
        Assert.Equal(session.Id, _store.GetCurrentSessionId());
        Assert.Single(_runtime.ListSessions());
        Assert.True(_view.ResumeSession(session.Id));
        _view.PrepareConversation();
        Assert.Equal(session.Id, _store.GetCurrentSessionId());
        Assert.Equal(InteractivePage.Main, _view.Page);
        Assert.Single(_runtime.ListSessions());
    }

    [Fact]
    public void NewChatIsSavedOnlyOnFirstSendAndDoesNotOverwriteTheOriginalSession()
    {
        var original = _store.NewSession("Original");
        _store.AppendTurn(original, "kept", "unchanged");
        _view.StartNewSession("New topic");
        Assert.Equal(original.Id, _store.GetCurrentSessionId());
        Assert.Single(_runtime.ListSessions());
        Assert.Equal(InteractivePage.NewChat, _view.Page);
        _view.StartNewSession("Final topic");
        Assert.Single(_runtime.ListSessions());
        _view.PrepareConversation();
        var created = _store.GetCurrentSessionId();
        Assert.NotEqual(original.Id, created);
        Assert.Equal("Final topic", _runtime.GetSession(created)!.Title);
        _view.PrepareConversation();
        Assert.Equal(created, _store.GetCurrentSessionId());
        Assert.Equal(2, _runtime.ListSessions().Count);
        Assert.Equal("kept", _runtime.GetSession(original.Id)!.Messages[0].Content);
        Assert.Empty(_runtime.GetSession(created)!.Messages);
    }

    [Fact]
    public void MainScrollEntersHistoryAndRepeatedEdgeEventsDoNotRedrawThePrompt()
    {
        var session = _store.NewSession("Existing");
        _store.AppendTurn(session, "question", "answer");
        _view.Initialize();
        var redraws = 0;
        _view.ScrollTranscript(-1, () => redraws++);
        Assert.Equal(InteractivePage.History, _view.Page);
        Assert.Equal(1, redraws);
        for (var i = 0; i < 10; i++) _view.ScrollTranscript(-1, () => redraws++);
        Assert.Equal(1, redraws);
        Assert.Equal(InteractivePage.History, _view.Page);
        _view.NavigateBack();
        Assert.Equal(InteractivePage.Main, _view.Page);
    }

    [Fact]
    public void FixedContentOnlyWritesChangedRowsAndNeverTouchesComposerOrHeader()
    {
        using var output = new StringWriter();
        var layout = ScreenLayout.Create(80, 24);
        _view.PaintContent(output, layout, ["one", "two"]);
        output.GetStringBuilder().Clear();
        _view.PaintContent(output, layout, ["one", "two"]);
        Assert.Empty(output.ToString());
        _view.PaintContent(output, layout, ["one", "changed"]);
        Assert.Equal($"\u001b[{layout.ContentTop + 1};1H\u001b[2Kchanged", output.ToString());
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task PreviewCleanupKeepsMainWorkingPage(string status)
    {
        _view.PrepareConversation();
        await using var preview = _view.BeginStreamingPreview("question");
        await preview.FinishAsync(status);
        _view.EndRun(status);
        Assert.Equal(InteractivePage.Main, _view.Page);
        Assert.False(_view.CanGoBack);
        Assert.Empty(_runtime.GetOrCreateCurrentSession().Messages);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void EscapeClosesSuggestionsBeforeNavigating(bool suggestions, bool canGoBack, bool navigate) =>
        Assert.Equal(navigate, LineEditor.ShouldNavigateBack(suggestions, canGoBack));

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
