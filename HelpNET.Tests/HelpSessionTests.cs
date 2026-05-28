using HelpNET.Assistants;
using HelpNET.Content;
using HelpNET.Session;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="HelpSession"/> navigation history state machine and
/// conversation lifetime.
/// </summary>
public class HelpSessionTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static Task<IHelpSession> BuildSessionAsync()
        => HelpSessionFactory.CreateAsync(
            TestContentFixture.CreateSource(),
            aiSession: null,
            new HelpSessionOptions(HomeTopicId: "home"), ct: CancellationToken.None);

    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public async Task InitialState_NoCurrentTopic()
    {
        await using var session = await BuildSessionAsync();
        Assert.Null(session.CurrentTopic);
    }

    [Fact]
    public async Task InitialState_EmptyHistory()
    {
        await using var session = await BuildSessionAsync();
        Assert.Empty(session.BackHistory);
        Assert.Empty(session.ForwardHistory);
    }

    [Fact]
    public async Task InitialState_EmptyConversation()
    {
        await using var session = await BuildSessionAsync();
        Assert.Empty(session.Conversation);
    }

    // ── NavigateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateAsync_SetsCurrentTopic()
    {
        await using var session = await BuildSessionAsync();
        await session.NavigateAsync(new HelpNavigationRequest("quickstart.backup"), default);

        Assert.Equal("quickstart.backup", session.CurrentTopic!.Id);
    }

    [Fact]
    public async Task NavigateAsync_RaisesCurrentTopicChanged()
    {
        await using var session = await BuildSessionAsync();
        int raised = 0;
        session.CurrentTopicChanged += (_, _) => raised++;

        await session.NavigateAsync(new HelpNavigationRequest("quickstart.backup"), default);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task NavigateAsync_MissingId_Throws()
    {
        await using var session = await BuildSessionAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => session.NavigateAsync(new HelpNavigationRequest("no.such.topic"), default));
    }

    // ── Back / Forward ────────────────────────────────────────────────────────

    [Fact]
    public async Task Navigate_ThenBack_ReturnsToPrevious()
    {
        await using var session = await BuildSessionAsync();

        await session.NavigateAsync(new HelpNavigationRequest("home"), default);
        await session.NavigateAsync(new HelpNavigationRequest("quickstart.backup"), default);

        var back = await session.BackAsync(default);

        Assert.NotNull(back);
        Assert.Equal("home", back.Id);
        Assert.Equal("home", session.CurrentTopic!.Id);
    }

    [Fact]
    public async Task Back_AtBeginning_ReturnsNull()
    {
        await using var session = await BuildSessionAsync();
        var result = await session.BackAsync(default);
        Assert.Null(result);
    }

    [Fact]
    public async Task Back_ThenForward_RestoresNext()
    {
        await using var session = await BuildSessionAsync();

        await session.NavigateAsync(new HelpNavigationRequest("home"), default);
        await session.NavigateAsync(new HelpNavigationRequest("quickstart.backup"), default);
        await session.BackAsync(default);

        var fwd = await session.ForwardAsync(default);

        Assert.NotNull(fwd);
        Assert.Equal("quickstart.backup", fwd.Id);
        Assert.Equal("quickstart.backup", session.CurrentTopic!.Id);
    }

    [Fact]
    public async Task Forward_AtEnd_ReturnsNull()
    {
        await using var session = await BuildSessionAsync();
        await session.NavigateAsync(new HelpNavigationRequest("home"), default);
        // No back taken, so forward stack is empty.
        var result = await session.ForwardAsync(default);
        Assert.Null(result);
    }

    // ── New branch prunes forward stack ──────────────────────────────────────

    [Fact]
    public async Task NavigateAfterBack_PrunesForwardStack()
    {
        await using var session = await BuildSessionAsync();

        await session.NavigateAsync(new HelpNavigationRequest("home"), default);
        await session.NavigateAsync(new HelpNavigationRequest("quickstart.backup"), default);
        await session.BackAsync(default); // forward = [quickstart.backup]

        // Navigate to a new topic — forward stack should be cleared.
        await session.NavigateAsync(new HelpNavigationRequest("concepts.backup-sets"), default);

        Assert.Empty(session.ForwardHistory);
    }

    // ── HomeAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task HomeAsync_NavigatesToHomeTopic()
    {
        await using var session = await BuildSessionAsync();
        var home = await session.HomeAsync(default);

        Assert.Equal("home", home.Id);
        Assert.Equal("home", session.CurrentTopic!.Id);
    }

    [Fact]
    public async Task HomeAsync_AddsCurrentToBackStack()
    {
        await using var session = await BuildSessionAsync();

        await session.NavigateAsync(new HelpNavigationRequest("quickstart.backup"), default);
        await session.HomeAsync(default);

        Assert.Single(session.BackHistory);
        Assert.Equal("quickstart.backup", session.BackHistory[0].Id);
    }

    // ── SearchAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_ReturnsRelevantHits()
    {
        await using var session = await BuildSessionAsync();
        var hits = await session.SearchAsync("restore files", topK: 5, default);

        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task SearchAsync_TopKRespected()
    {
        await using var session = await BuildSessionAsync();
        var hits = await session.SearchAsync("backup tape restore", topK: 2, default);
        Assert.True(hits.Count <= 2);
    }

    // ── AskAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_AppendsToConversation()
    {
        await using var session = await BuildSessionAsync();

        await session.AskAsync("how do I backup", default);

        Assert.Single(session.Conversation);
        Assert.Equal("how do I backup", session.Conversation[0].Query);
    }

    [Fact]
    public async Task AskAsync_RaisesAnswerReceived()
    {
        await using var session = await BuildSessionAsync();
        int raised = 0;
        session.AnswerReceived += (_, _) => raised++;

        await session.AskAsync("backup tape", default);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AskAsync_MultipleTurns_AllStoredInConversation()
    {
        await using var session = await BuildSessionAsync();

        await session.AskAsync("first question", default);
        await session.AskAsync("second question", default);

        Assert.Equal(2, session.Conversation.Count);
    }

    // ── ClearConversation ─────────────────────────────────────────────────────

    [Fact]
    public async Task ClearConversation_EmptiesConversation()
    {
        await using var session = await BuildSessionAsync();

        await session.AskAsync("question", default);
        session.ClearConversation();

        Assert.Empty(session.Conversation);
    }

    // ── GetWalkthroughsForHost ────────────────────────────────────────────────

    [Fact]
    public async Task GetWalkthroughsForHost_UnknownHost_ReturnsEmpty()
    {
        await using var session = await BuildSessionAsync();
        Assert.Empty(session.GetWalkthroughsForHost("NonExistentWindow"));
    }

    // ── GetTopicForControl ────────────────────────────────────────────────────

    [Fact]
    public async Task GetTopicForControl_MatchingHostAndId_ReturnsTopic()
    {
        await using var session = await BuildSessionAsync();
        var topic = session.GetTopicForControl("RestoreWindow", "dialog.restore");

        Assert.NotNull(topic);
        Assert.Equal("dialog.restore", topic.Id);
    }

    [Fact]
    public async Task GetTopicForControl_WrongHost_ReturnsNull()
    {
        await using var session = await BuildSessionAsync();
        var topic = session.GetTopicForControl("SomeOtherWindow", "dialog.restore");
        Assert.Null(topic);
    }

    [Fact]
    public async Task GetTopicForControl_MissingId_ReturnsNull()
    {
        await using var session = await BuildSessionAsync();
        Assert.Null(session.GetTopicForControl("MainWindow", "no.such.topic"));
    }

    // ── AssistantMode ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AssistantMode_WithNullAiSession_IsLexical()
    {
        await using var session = await BuildSessionAsync();
        Assert.Equal(HelpAssistantMode.Lexical, session.AssistantMode);
    }

    // ── MaxConversationTurns ──────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_ExceedsMaxTurns_OldestTurnsDropped()
    {
        await using var session = await HelpSessionFactory.CreateAsync(
            TestContentFixture.CreateSource(),
            aiSession: null,
            new HelpSessionOptions(MaxConversationTurns: 3), ct: CancellationToken.None);

        for (int i = 0; i < 5; i++)
            await session.AskAsync($"question {i}", default);

        Assert.Equal(3, session.Conversation.Count);
        // The oldest questions (0, 1) should have been dropped.
        Assert.Equal("question 2", session.Conversation[0].Query);
    }
}

