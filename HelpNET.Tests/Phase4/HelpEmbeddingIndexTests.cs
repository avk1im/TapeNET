using HelpNET.Embeddings;
using HelpNET.Retrieval;
using Xunit;

namespace HelpNET.Tests.Phase4;

/// <summary>
/// Tests for <see cref="HelpEmbeddingIndex"/> — semantic search over a
/// fake precomputed bundle using <see cref="FakeEmbeddingGenerator"/>.
/// </summary>
public class HelpEmbeddingIndexTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<HelpEmbeddingIndex> BuildIndexAsync()
    {
        var (bundle, store) = await BundleBuilder.BuildFromFixtureAsync();
        var gen             = new FakeEmbeddingGenerator(BundleBuilder.TestDim);
        return HelpEmbeddingIndex.Build(bundle, gen, store);
    }

    // ── Basic behaviour ───────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty()
    {
        var index = await BuildIndexAsync();
        var results = await index.SearchAsync("", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WhitespaceQuery_ReturnsEmpty()
    {
        var index = await BuildIndexAsync();
        var results = await index.SearchAsync("   ", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ZeroTopK_ReturnsEmpty()
    {
        var index = await BuildIndexAsync();
        var results = await index.SearchAsync("backup", topK: 0);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ValidQuery_ReturnsAtMostTopKResults()
    {
        var index = await BuildIndexAsync();
        var results = await index.SearchAsync("backup tape restore", topK: 3);
        Assert.True(results.Count <= 3);
    }

    [Fact]
    public async Task SearchAsync_ValidQuery_AllResultsHaveNonNullTopic()
    {
        var index = await BuildIndexAsync();
        var results = await index.SearchAsync("tape drive", topK: 5);
        Assert.All(results, r => Assert.NotNull(r.Topic));
    }

    [Fact]
    public async Task SearchAsync_ValidQuery_SnippetsAreNonEmpty()
    {
        var index = await BuildIndexAsync();
        var results = await index.SearchAsync("incremental backup", topK: 5);
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Snippet)));
    }

    [Fact]
    public async Task SearchAsync_ValidQuery_ScoresAreBetweenMinusOneAndOne()
    {
        var index = await BuildIndexAsync();
        var results = await index.SearchAsync("restore files", topK: 5);
        Assert.All(results, r =>
        {
            Assert.True(r.Score >= -1.001f, $"Score {r.Score} below -1");
            Assert.True(r.Score <=  1.001f, $"Score {r.Score} above +1");
        });
    }

    [Fact]
    public async Task SearchAsync_ValidQuery_ResultsInDescendingScoreOrder()
    {
        var index = await BuildIndexAsync();
        var results = await index.SearchAsync("backup", topK: 10);
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i - 1].Score >= results[i].Score,
                $"Scores not descending at index {i}");
    }

    // ── Identical query produces identical top result ─────────────────────────

    [Fact]
    public async Task SearchAsync_IdenticalQueries_ProduceSameTopResult()
    {
        var index = await BuildIndexAsync();

        var r1 = await index.SearchAsync("glossary tape", topK: 3);
        var r2 = await index.SearchAsync("glossary tape", topK: 3);

        // With a deterministic fake generator the results must be identical.
        Assert.Equal(r1.Count, r2.Count);
        for (int i = 0; i < r1.Count; i++)
            Assert.Equal(r1[i].Topic.Id, r2[i].Topic.Id);
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_CancelledToken_DoesNotReturnInvalidResults()
    {
        // The fake generator is synchronous and does not observe the token,
        // so cancellation may not throw.  We only assert that if results ARE
        // returned they are well-formed (no null topics).
        var index = await BuildIndexAsync();
        var cts   = new CancellationTokenSource();
        cts.Cancel();

        IReadOnlyList<HelpExcerpt>? results = null;
        try
        {
            results = await index.SearchAsync("tape", topK: 3, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is acceptable behaviour.
            return;
        }

        // If no exception, results should still be structurally valid.
        Assert.NotNull(results);
        Assert.All(results, r => Assert.NotNull(r.Topic));
    }
}
