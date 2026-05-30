using HelpNET.Assistants;
using HelpNET.Content;
using HelpNET.Indexing;
using HelpNET.Session;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="LexicalHelpAssistant"/>.
/// </summary>
public class LexicalHelpAssistantTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static async Task<(LexicalHelpAssistant Assistant, HelpContentStore Store)>
        BuildAsync()
    {
        var store   = await TestContentFixture.LoadStoreAsync();
        var bm25    = BM25HelpIndex.Build(store.All);
        var intent  = new IntentMatcher(store.All);
        var asst    = new LexicalHelpAssistant(bm25, intent, store, topK: 5);
        return (asst, store);
    }

    private static HelpAssistantRequest Req(string query)
        => new(query, null, null, []);

    // ── Mode ──────────────────────────────────────────────────────────────────
    /*
        [Fact]
        public void Mode_IsLexical()
        {
            var asst = new LexicalHelpAssistant(
                BM25HelpIndex.Build([]), new IntentMatcher([]),
                HelpContentStore.LoadAsync(
                    new InMemoryHelpContentSource("t", Array.Empty<HelpRawDocument>())).GetAwaiter().GetResult(),
                5);
            Assert.Equal(HelpAssistantMode.Lexical, asst.Mode);
        }
    */
    [Fact]
    public async Task Mode_IsLexical()
    {
        var asst = new LexicalHelpAssistant(
            BM25HelpIndex.Build([]), new IntentMatcher([]),
            await HelpContentStore.LoadAsync(
                new InMemoryHelpContentSource("t", Array.Empty<HelpRawDocument>())),
            5);
        Assert.Equal(HelpAssistantMode.Lexical, asst.Mode);
    }

    // ── AskAsync returns top excerpts ─────────────────────────────────────────

    [Fact]
    public async Task AskAsync_KnownQuery_ReturnsCitations()
    {
        var (asst, _) = await BuildAsync();
        var response  = await asst.AskAsync(Req("how do I restore files"), CancellationToken.None);

        Assert.NotEmpty(response.Citations);
        Assert.Equal(HelpAssistantMode.Lexical, response.Mode);
        Assert.True(response.Confidence > 0f);
    }

    [Fact]
    public async Task AskAsync_KnownQuery_AnswerContainsMarkdownLinks()
    {
        var (asst, _) = await BuildAsync();
        var response  = await asst.AskAsync(Req("restore files backup"), CancellationToken.None);

        // The Markdown answer should contain at least one help://topic/ link.
        Assert.Contains("help://topic/", response.AnswerMarkdown);
    }

    [Fact]
    public async Task AskAsync_KnownQuery_ResponseModeIsLexical()
    {
        var (asst, _) = await BuildAsync();
        var response  = await asst.AskAsync(Req("tape backup"), CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Lexical, response.Mode);
    }

    [Fact]
    public async Task AskAsync_NoMatch_ReturnsNoCitationsAndLowConfidence()
    {
        var (asst, _) = await BuildAsync();
        // A query with no matching tokens.
        var response  = await asst.AskAsync(
            Req("xyzzy plugh wumpus grznblx"), CancellationToken.None);

        Assert.Empty(response.Citations);
        Assert.Equal(0f, response.Confidence);
    }

    [Fact]
    public async Task AskAsync_CitationsMatchTopTopics()
    {
        var (asst, store) = await BuildAsync();
        var response      = await asst.AskAsync(Req("restore recover files"), CancellationToken.None);

        // All cited topic ids should resolve in the store.
        foreach (var citation in response.Citations)
            Assert.NotNull(store.GetById(citation.TopicId));
    }

    [Fact]
    public async Task AskAsync_SuggestedTopicsDoNotDuplicateCitations()
    {
        var (asst, _) = await BuildAsync();
        var response  = await asst.AskAsync(Req("backup tape"), CancellationToken.None);

        var citedIds    = response.Citations.Select(c => c.TopicId).ToHashSet();
        var suggestedIds = response.SuggestedTopics.Select(s => s.Id);

        foreach (var id in suggestedIds)
            Assert.DoesNotContain(id, citedIds);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_CancelledToken_Throws()
    {
        var (asst, _) = await BuildAsync();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => asst.AskAsync(Req("anything"), cts.Token));
    }
}
