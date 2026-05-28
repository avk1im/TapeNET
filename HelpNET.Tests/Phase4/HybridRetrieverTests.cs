using HelpNET.Embeddings;
using HelpNET.Indexing;
using HelpNET.Retrieval;
using Xunit;

namespace HelpNET.Tests.Phase4;

/// <summary>
/// Tests for <see cref="HybridRetriever"/> — score blending, candidate merging,
/// and result ordering.
/// </summary>
public class HybridRetrieverTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static async Task<HybridRetriever> BuildRetrieverAsync(
        float lexicalWeight = HybridRetriever.DefaultLexicalWeight)
    {
        var (bundle, store) = await BundleBuilder.BuildFromFixtureAsync();
        var gen             = new FakeEmbeddingGenerator(BundleBuilder.TestDim);
        var embIndex        = HelpEmbeddingIndex.Build(bundle, gen, store);
        var bm25            = BM25HelpIndex.Build(store.All);
        return new HybridRetriever(bm25, embIndex, store, lexicalWeight);
    }

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public async Task Constructor_InvalidLexicalWeight_Throws()
    {
        var (bundle, store) = await BundleBuilder.BuildFromFixtureAsync();
        var gen             = new FakeEmbeddingGenerator();
        var embIndex        = HelpEmbeddingIndex.Build(bundle, gen, store);
        var bm25            = BM25HelpIndex.Build(store.All);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HybridRetriever(bm25, embIndex, store, lexicalWeight: 1.5f));
    }

    [Fact]
    public async Task Constructor_NegativeLexicalWeight_Throws()
    {
        var (bundle, store) = await BundleBuilder.BuildFromFixtureAsync();
        var gen             = new FakeEmbeddingGenerator();
        var embIndex        = HelpEmbeddingIndex.Build(bundle, gen, store);
        var bm25            = BM25HelpIndex.Build(store.All);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HybridRetriever(bm25, embIndex, store, lexicalWeight: -0.1f));
    }

    // ── RetrieveAsync: basic behaviour ───────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_ValidQuery_ReturnsResults()
    {
        var retriever = await BuildRetrieverAsync();
        var results   = await retriever.RetrieveAsync("backup tape", topK: 5);
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyQuery_ReturnsEmpty()
    {
        var retriever = await BuildRetrieverAsync();
        var results   = await retriever.RetrieveAsync("", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task RetrieveAsync_ZeroTopK_ReturnsEmpty()
    {
        var retriever = await BuildRetrieverAsync();
        var results   = await retriever.RetrieveAsync("tape", topK: 0);
        Assert.Empty(results);
    }

    [Fact]
    public async Task RetrieveAsync_TopKRespected()
    {
        var retriever = await BuildRetrieverAsync();
        var results   = await retriever.RetrieveAsync("backup restore tape", topK: 2);
        Assert.True(results.Count <= 2);
    }

    [Fact]
    public async Task RetrieveAsync_ScoresDescending()
    {
        var retriever = await BuildRetrieverAsync();
        var results   = await retriever.RetrieveAsync("backup tape restore", topK: 10);

        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i - 1].Score >= results[i].Score,
                $"Scores not descending at index {i}");
    }

    [Fact]
    public async Task RetrieveAsync_NoDuplicateTopics()
    {
        var retriever = await BuildRetrieverAsync();
        var results   = await retriever.RetrieveAsync("tape backup sets restore", topK: 10);

        var topicIds = results.Select(r => r.Topic.Id).ToList();
        Assert.Equal(topicIds.Distinct(StringComparer.OrdinalIgnoreCase).Count(), topicIds.Count);
    }

    // ── Weight sensitivity ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0f)]  // Pure semantic
    [InlineData(0.5f)]  // Equal blend
    [InlineData(1.0f)]  // Pure lexical
    public async Task RetrieveAsync_VariousWeights_DoesNotThrow(float weight)
    {
        var retriever = await BuildRetrieverAsync(lexicalWeight: weight);
        // Should not throw regardless of weight extreme.
        var results = await retriever.RetrieveAsync("backup", topK: 3);
        Assert.NotNull(results);
    }
}
