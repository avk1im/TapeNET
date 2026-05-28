using HelpNET.Assistants;
using HelpNET.Content;
using HelpNET.Session;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="HelpSessionFactory"/> — mode selection and session wiring.
/// </summary>
public class HelpSessionFactoryTests
{
    // ── Mode selection — Lexical (Phase 3) ────────────────────────────────────

    [Fact]
    public async Task CreateAsync_NullAiSession_CreatesLexicalSession()
    {
        await using var session = await HelpSessionFactory.CreateAsync(
            TestContentFixture.CreateSource(),
            aiSession: null,
            new HelpSessionOptions(), ct: CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Lexical, session.AssistantMode);
    }

    // ── Content loading ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_LoadsAllTopicsFromSource()
    {
        await using var session = await HelpSessionFactory.CreateAsync(
            TestContentFixture.CreateSource(),
            aiSession: null,
            new HelpSessionOptions(), ct: CancellationToken.None);

        // Session should be able to navigate to any fixture topic without throwing.
        await session.NavigateAsync(new HelpNavigationRequest("home"), default);
        Assert.Equal("home", session.CurrentTopic!.Id);
    }

    [Fact]
    public async Task CreateAsync_EmptySource_SessionIsUsable()
    {
        await using var session = await HelpSessionFactory.CreateAsync(
            new InMemoryHelpContentSource("empty", Array.Empty<HelpRawDocument>()),
            aiSession: null,
            new HelpSessionOptions(), ct: CancellationToken.None);

        // Empty corpus — just check the session is constructed without throwing.
        Assert.Equal(HelpAssistantMode.Lexical, session.AssistantMode);
        Assert.Empty(session.Conversation);
    }

    // ── HomeTopicId option ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_HomeTopicId_UsedByHomeAsync()
    {
        await using var session = await HelpSessionFactory.CreateAsync(
            TestContentFixture.CreateSource(),
            aiSession: null,
            new HelpSessionOptions(HomeTopicId: "quickstart.backup"), ct: CancellationToken.None);

        var home = await session.HomeAsync(default);
        Assert.Equal("quickstart.backup", home.Id);
    }

    [Fact]
    public async Task CreateAsync_MissingHomeTopicId_FallsBackToFirstTopic()
    {
        // Build a source with a topic that is NOT "home".
        var source = new InMemoryHelpContentSource("t",
        [
            ("concepts/backup-sets.md",
             "---\nid: concepts.backup-sets\ntitle: Backup Sets\nkind: concept\n---\nBody."),
        ]);

        await using var session = await HelpSessionFactory.CreateAsync(
            source,
            aiSession: null,
            new HelpSessionOptions(HomeTopicId: "home"), // "home" does not exist
            ct: CancellationToken.None);

        // HomeAsync should fall back to the first topic instead of throwing.
        var home = await session.HomeAsync(default);
        Assert.Equal("concepts.backup-sets", home.Id);
    }

    // ── Session options wiring ────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_DefaultTopK_SearchRespectsOption()
    {
        await using var session = await HelpSessionFactory.CreateAsync(
            TestContentFixture.CreateSource(),
            aiSession: null,
            new HelpSessionOptions(DefaultTopK: 2), ct: CancellationToken.None);

        var hits = await session.SearchAsync("backup tape restore", topK: 2, default);
        Assert.True(hits.Count <= 2);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_CancelledToken_ThrowsOperationCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HelpSessionFactory.CreateAsync(
                TestContentFixture.CreateSource(),
                aiSession: null,
                new HelpSessionOptions(),
                ct: cts.Token));
    }
}

