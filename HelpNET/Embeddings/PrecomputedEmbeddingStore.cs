using System.Text.Json;
using System.Text.Json.Serialization;
using HelpNET.Content;
using HelpNET.Indexing;

namespace HelpNET.Embeddings;

/// <summary>
/// Maps a chunk's position in the precomputed embedding blob back to its topic
/// and heading metadata.  One entry per chunk, stored as a JSON array in
/// <see cref="HelpEmbeddingBundle.ChunkIndexJson"/>.
/// </summary>
/// <param name="TopicId">Id of the topic this chunk belongs to.</param>
/// <param name="Heading">
/// Nearest preceding heading within the topic, or the topic title.
/// </param>
/// <param name="ChunkIndex">
/// Zero-based ordinal of this chunk within its topic (matches <see cref="HelpChunk.Index"/>).
/// </param>
internal sealed record ChunkIndexEntry(
    [property: JsonPropertyName("topicId")]    string TopicId,
    [property: JsonPropertyName("heading")]    string Heading,
    [property: JsonPropertyName("chunkIndex")] int    ChunkIndex);

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Loads a <see cref="HelpEmbeddingBundle"/> and exposes its embedding matrix
/// as a flat array of L2-normalised float vectors, with chunk metadata resolved
/// into <see cref="ChunkIndexEntry"/> records.
/// </summary>
/// <remarks>
/// The <see cref="HelpEmbeddingBundle.EmbeddingBlob"/> is stored as a packed
/// little-endian <c>float[]</c>: <c>dim</c> floats per chunk, chunk-major order.
/// The dimension is validated against <see cref="HelpEmbeddingBundle.Dimension"/>
/// on load; a hash mismatch is also surfaced as an exception.
/// </remarks>
internal sealed class PrecomputedEmbeddingStore
{
    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>Model id of the embedding model that produced this store.</summary>
    internal string ModelId { get; }

    /// <summary>Embedding vector dimension.</summary>
    internal int Dimension { get; }

    /// <summary>
    /// Flat embedding matrix.  Row <c>i</c> starts at offset <c>i * Dimension</c>.
    /// </summary>
    internal float[] Embeddings { get; }

    /// <summary>Chunk metadata, one entry per embedding row.</summary>
    internal IReadOnlyList<ChunkIndexEntry> ChunkIndex { get; }

    // ── Construction ─────────────────────────────────────────────────────────

    private PrecomputedEmbeddingStore(
        string                       modelId,
        int                          dimension,
        float[]                      embeddings,
        IReadOnlyList<ChunkIndexEntry> chunkIndex)
    {
        ModelId    = modelId;
        Dimension  = dimension;
        Embeddings = embeddings;
        ChunkIndex = chunkIndex;
    }

    /// <summary>
    /// Loads a <see cref="PrecomputedEmbeddingStore"/> from a bundle, validating
    /// dimension and optionally the model hash.
    /// </summary>
    /// <param name="bundle">The bundle as loaded from <see cref="IHelpContentSource"/>.</param>
    /// <param name="expectedModelId">
    /// When not <c>null</c>, compared to <see cref="HelpEmbeddingBundle.ModelId"/>;
    /// throws <see cref="InvalidOperationException"/> on mismatch.
    /// </param>
    /// <param name="expectedDimension">
    /// When not zero, compared to <see cref="HelpEmbeddingBundle.Dimension"/>;
    /// throws <see cref="InvalidOperationException"/> on mismatch.
    /// </param>
    internal static PrecomputedEmbeddingStore Load(
        HelpEmbeddingBundle bundle,
        string?             expectedModelId   = null,
        int                 expectedDimension = 0)
    {
        // ── Validate model id ─────────────────────────────────────────────────
        if (expectedModelId is not null &&
            !string.Equals(bundle.ModelId, expectedModelId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Embedding bundle model id mismatch: expected '{expectedModelId}', " +
                $"bundle contains '{bundle.ModelId}'.");
        }

        // ── Validate dimension ────────────────────────────────────────────────
        if (expectedDimension > 0 && bundle.Dimension != expectedDimension)
        {
            throw new InvalidOperationException(
                $"Embedding bundle dimension mismatch: expected {expectedDimension}, " +
                $"bundle declares {bundle.Dimension}.");
        }

        // ── Decode float blob ─────────────────────────────────────────────────
        var blob = bundle.EmbeddingBlob.Span;
        if (blob.Length % (bundle.Dimension * sizeof(float)) != 0)
        {
            throw new InvalidOperationException(
                $"Embedding blob length {blob.Length} is not a multiple of " +
                $"dimension×4 ({bundle.Dimension * sizeof(float)}).");
        }

        int chunkCount = blob.Length / (bundle.Dimension * sizeof(float));
        var embeddings = new float[chunkCount * bundle.Dimension];

        for (int i = 0; i < embeddings.Length; i++)
        {
            embeddings[i] = BitConverter.ToSingle(blob.Slice(i * sizeof(float), sizeof(float)));
        }

        // ── Parse chunk index JSON ─────────────────────────────────────────────
        var chunkIndex = JsonSerializer.Deserialize<List<ChunkIndexEntry>>(
                             bundle.ChunkIndexJson)
                         ?? throw new InvalidOperationException(
                             "Embedding bundle ChunkIndexJson is null or empty.");

        if (chunkIndex.Count != chunkCount)
        {
            throw new InvalidOperationException(
                $"Chunk index entry count ({chunkIndex.Count}) does not match " +
                $"embedding row count ({chunkCount}).");
        }

        return new PrecomputedEmbeddingStore(
            bundle.ModelId, bundle.Dimension, embeddings, chunkIndex);
    }

    /// <summary>
    /// Returns a memory view over the embedding vector for row <paramref name="index"/>.
    /// </summary>
    internal ReadOnlySpan<float> GetVector(int index)
        => Embeddings.AsSpan(index * Dimension, Dimension);
}
