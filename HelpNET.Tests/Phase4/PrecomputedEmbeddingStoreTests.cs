using System.Text.Json;
using HelpNET.Content;
using HelpNET.Embeddings;
using HelpNET.Indexing;
using Xunit;

namespace HelpNET.Tests.Phase4;

/// <summary>
/// Tests for <see cref="PrecomputedEmbeddingStore"/> — bundle loading, validation,
/// and vector access.
/// </summary>
public class PrecomputedEmbeddingStoreTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (HelpEmbeddingBundle Bundle, List<HelpChunk> Chunks) BuildMinimalBundle(
        int    chunkCount = 3,
        int    dim        = BundleBuilder.TestDim,
        string modelId    = BundleBuilder.TestModelId)
    {
        var chunks = Enumerable.Range(0, chunkCount)
            .Select(i => new HelpChunk($"topic.{i}", $"Heading {i}", $"Text for chunk {i}", i))
            .ToList();
        var bundle = BundleBuilder.Build(chunks, dim, modelId);
        return (bundle, chunks);
    }

    // ── Load: happy path ──────────────────────────────────────────────────────

    [Fact]
    public void Load_ValidBundle_ReturnsStoreWithCorrectMetadata()
    {
        var (bundle, _) = BuildMinimalBundle(chunkCount: 3, dim: 4);

        var store = PrecomputedEmbeddingStore.Load(bundle);

        Assert.Equal(BundleBuilder.TestModelId, store.ModelId);
        Assert.Equal(4, store.Dimension);
        Assert.Equal(3, store.ChunkIndex.Count);
    }

    [Fact]
    public void Load_ValidBundle_VectorsHaveCorrectLength()
    {
        var (bundle, _) = BuildMinimalBundle(chunkCount: 5, dim: 4);

        var store = PrecomputedEmbeddingStore.Load(bundle);

        for (int i = 0; i < 5; i++)
        {
            var vec = store.GetVector(i);
            Assert.Equal(4, vec.Length);
        }
    }

    [Fact]
    public void Load_ValidBundle_ChunkIndexEntriesMatchInput()
    {
        var (bundle, chunks) = BuildMinimalBundle(chunkCount: 3);

        var store = PrecomputedEmbeddingStore.Load(bundle);

        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i].TopicId, store.ChunkIndex[i].TopicId);
            Assert.Equal(chunks[i].Heading, store.ChunkIndex[i].Heading);
            Assert.Equal(chunks[i].Index,   store.ChunkIndex[i].ChunkIndex);
        }
    }

    // ── Load: validation failures ─────────────────────────────────────────────

    [Fact]
    public void Load_ModelIdMismatch_Throws()
    {
        var (bundle, _) = BuildMinimalBundle();

        Assert.Throws<InvalidOperationException>(
            () => PrecomputedEmbeddingStore.Load(bundle, expectedModelId: "wrong-model"));
    }

    [Fact]
    public void Load_DimensionMismatch_Throws()
    {
        var (bundle, _) = BuildMinimalBundle(dim: 4);

        Assert.Throws<InvalidOperationException>(
            () => PrecomputedEmbeddingStore.Load(bundle, expectedDimension: 8));
    }

    [Fact]
    public void Load_BlobLengthNotMultipleOfDim_Throws()
    {
        // Craft a bundle with a blob that has an odd byte count.
        var index = JsonSerializer.Serialize(new[]
        {
            new { topicId = "t1", heading = "h", chunkIndex = 0 }
        });
        var badBundle = new HelpEmbeddingBundle(
            BundleBuilder.TestModelId,
            4,
            "hash",
            new ReadOnlyMemory<byte>(new byte[7]), // 7 bytes is not a multiple of 4×4=16
            index);

        Assert.Throws<InvalidOperationException>(
            () => PrecomputedEmbeddingStore.Load(badBundle));
    }

    [Fact]
    public void Load_ChunkIndexCountMismatch_Throws()
    {
        var (bundle, _) = BuildMinimalBundle(chunkCount: 3, dim: 4);

        // Build a chunk index JSON that claims only 2 entries but blob has 3.
        var shortIndex = JsonSerializer.Serialize(new[]
        {
            new { topicId = "t1", heading = "h", chunkIndex = 0 },
            new { topicId = "t2", heading = "h", chunkIndex = 0 }
        });
        var mismatchBundle = bundle with { ChunkIndexJson = shortIndex };

        Assert.Throws<InvalidOperationException>(
            () => PrecomputedEmbeddingStore.Load(mismatchBundle));
    }

    // ── GetVector ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetVector_SameChunkTwice_ReturnsSameValues()
    {
        var (bundle, _) = BuildMinimalBundle(chunkCount: 4, dim: 4);
        var store = PrecomputedEmbeddingStore.Load(bundle);

        var v1 = store.GetVector(2).ToArray();
        var v2 = store.GetVector(2).ToArray();

        Assert.Equal(v1, v2);
    }

    [Fact]
    public void GetVector_DifferentChunks_ReturnsDifferentVectors()
    {
        var (bundle, _) = BuildMinimalBundle(chunkCount: 4, dim: 4);
        var store = PrecomputedEmbeddingStore.Load(bundle);

        var v0 = store.GetVector(0).ToArray();
        var v1 = store.GetVector(1).ToArray();

        // Two different chunk texts (via FakeEmbeddingGenerator) should produce
        // different vectors — this would only collide on a hash collision, which
        // is astronomically unlikely with the 4-D test vectors.
        Assert.NotEqual(v0, v1);
    }
}
