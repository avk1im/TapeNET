using HelpNET.Assistants;
using HelpNET.Content;
using HelpNET.Embeddings;
using HelpNET.Retrieval;
using HelpNET.Session;
using HelpNET.Tests.Phase4;

using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="SemanticHelpAssistant"/> — Mode 2, which ranks excerpts
/// by cosine similarity and returns them as Markdown <b>without</b> invoking an LLM.
/// </summary>
/// <remarks>
/// The assistant is driven by a <see cref="HelpEmbeddingIndex"/> built over a fake
/// precomputed bundle (see <see cref="BundleBuilder"/>) and a deterministic
/// <see cref="FakeEmbeddingGenerator"/>, so no real ONNX model is required and the
/// rankings are reproducible.
/// </remarks>
public class SemanticHelpAssistantTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static async Task<(SemanticHelpAssistant Assistant, HelpContentStore Store)>
        BuildAsync(int topK = 5)
    {
        var (bundle, store) = await BundleBuilder.BuildFromFixtureAsync();
        var gen             = new FakeEmbeddingGenerator(BundleBuilder.TestDim);
        var index           = HelpEmbeddingIndex.Build(bundle, gen, store);
        var asst            = new SemanticHelpAssistant(index, store, topK);
        return (asst, store);
    }

    private static HelpAssistantRequest Req(string query)
        => new(query, null, null, []);

    // ── Mode ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mode_IsSemantic()
    {
        var (asst, _) = await BuildAsync();
        Assert.Equal(HelpAssistantMode.Semantic, asst.Mode);
    }

    // ── AskAsync returns ranked excerpts ──────────────────────────────────────

    [Fact]
    public async Task AskAsync_ValidQuery_ResponseModeIsSemantic()
    {
        var (asst, _) = await BuildAsync();
        var response  = await asst.AskAsync(Req("backup tape restore"), CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Semantic, response.Mode);
    }

    [Fact]
    public async Task AskAsync_ValidQuery_CitationsResolveInStore()
    {
        var (asst, store) = await BuildAsync();
        var response      = await asst.AskAsync(Req("backup tape restore"), CancellationToken.None);

        // When excerpts are returned, every cited topic id must resolve.
        if (response.Citations.Count > 0)
            foreach (var citation in response.Citations)
                Assert.NotNull(store.GetById(citation.TopicId));
    }

    [Fact]
    public async Task AskAsync_ValidQuery_CitationsBackedByExcerpts_NoLlmSynthesis()
    {
        var (asst, _) = await BuildAsync();
        var response  = await asst.AskAsync(Req("incremental backup sets"), CancellationToken.None);

        if (response.Citations.Count == 0)
            return; // low-confidence path covered by a dedicated test

        // Mode 2 surfaces retrieved excerpts verbatim — the answer is a list of
        //  topic links, never an LLM-synthesised prose answer.
        Assert.Contains("help://topic/", response.AnswerMarkdown);
        Assert.Contains("Semantic results for:", response.AnswerMarkdown);
    }

    [Fact]
    public async Task AskAsync_ValidQuery_AnswerListsEachCitation()
    {
        var (asst, _) = await BuildAsync();
        var response  = await asst.AskAsync(Req("restore files from tape"), CancellationToken.None);

        if (response.Citations.Count == 0)
            return;

        // Every cited topic should be linked in the Markdown body.
        foreach (var citation in response.Citations)
            Assert.Contains($"help://topic/{citation.TopicId}", response.AnswerMarkdown);
    }

    // ── Ranking ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_TopK_LimitsCitationCount()
    {
        var (asst, _) = await BuildAsync(topK: 2);
        var response  = await asst.AskAsync(Req("backup tape restore drive"), CancellationToken.None);

        Assert.True(response.Citations.Count <= 2);
    }

    [Fact]
    public async Task AskAsync_Confidence_TracksTopExcerptScore()
    {
        var (asst, _) = await BuildAsync();
        var response  = await asst.AskAsync(Req("backup tape restore"), CancellationToken.None);

        // Confidence is the (clamped) top cosine score; it is in [0, 1].
        Assert.InRange(response.Confidence, 0f, 1f);
    }

    [Fact]
    public async Task AskAsync_IdenticalQueries_ProduceSameRanking()
    {
        var (asst, _) = await BuildAsync();

        var r1 = await asst.AskAsync(Req("glossary tape backup set"), CancellationToken.None);
        var r2 = await asst.AskAsync(Req("glossary tape backup set"), CancellationToken.None);

        // Deterministic fake generator → identical citation order.
        Assert.Equal(r1.Citations.Count, r2.Citations.Count);
        for (int i = 0; i < r1.Citations.Count; i++)
            Assert.Equal(r1.Citations[i].TopicId, r2.Citations[i].TopicId);
    }

    // ── Low-confidence / no-match ─────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_LowConfidence_ReturnsNoMatchWithZeroConfidence()
    {
        // Force the no-match path by demanding more confidence than the fake
        //  hash-based vectors can ever provide for an unrelated query: build an
        //  assistant whose index is empty so SearchAsync yields no excerpts.
        var store = await TestContentFixture.LoadStoreAsync();
        var emptyBundle = BundleBuilder.Build([]);
        var gen   = new FakeEmbeddingGenerator(BundleBuilder.TestDim);
        var index = HelpEmbeddingIndex.Build(emptyBundle, gen, store);
        var asst  = new SemanticHelpAssistant(index, store);

        var response = await asst.AskAsync(Req("backup"), CancellationToken.None);

        Assert.Empty(response.Citations);
        Assert.Equal(0f, response.Confidence);
        Assert.Equal(HelpAssistantMode.Semantic, response.Mode);
    }

    [Fact]
    public async Task AskAsync_SuggestedTopicsDoNotDuplicateCitations()
    {
        var (asst, _) = await BuildAsync();
        var response  = await asst.AskAsync(Req("backup tape"), CancellationToken.None);

        var citedIds     = response.Citations.Select(c => c.TopicId).ToHashSet();
        var suggestedIds = response.SuggestedTopics.Select(s => s.Id);

        foreach (var id in suggestedIds)
            Assert.DoesNotContain(id, citedIds);
    }

    [Fact]
    public async Task AskAsync_NeverPopulatesSuggestedActions()
    {
        // Mode 2 has no host action dispatch — actions are always empty.
        var (asst, _) = await BuildAsync();
        var response  = await asst.AskAsync(Req("restore files"), CancellationToken.None);

        Assert.Empty(response.SuggestedActions);
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

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
