using HelpNET.Content;
using HelpNET.Indexing;
using HelpNET.Retrieval;
using Microsoft.Extensions.AI;

namespace HelpNET.Embeddings;

/// <summary>
/// Semantic search index that embeds queries at runtime and searches against
/// a precomputed (or lazily built) embedding store.
/// </summary>
/// <remarks>
/// Construction is performed via the async factory <see cref="BuildAsync"/> so
/// that expensive embedding generation is off the UI thread.
/// </remarks>
public sealed class HelpEmbeddingIndex : IHelpEmbeddingIndex
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly PrecomputedEmbeddingStore                     _store;
    private readonly HelpContentStore                              _contentStore;

    // ── Construction ─────────────────────────────────────────────────────────

    private HelpEmbeddingIndex(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        PrecomputedEmbeddingStore                     store,
        HelpContentStore                              contentStore)
    {
        _generator    = generator;
        _store        = store;
        _contentStore = contentStore;
    }

    /// <summary>
    /// Builds a <see cref="HelpEmbeddingIndex"/> from a precomputed bundle and
    /// a runtime embedding generator.
    /// </summary>
    /// <param name="bundle">
    /// Precomputed embedding bundle loaded by <see cref="IHelpContentSource"/>.
    /// </param>
    /// <param name="generator">
    /// Runtime embedding generator (ONNX or provider-supplied) whose model id
    /// must match <paramref name="bundle"/>'s <c>ModelId</c>.
    /// </param>
    /// <param name="contentStore">
    /// Content store used to resolve topic titles and related topics for results.
    /// </param>
    /// <param name="expectedModelId">
    /// When supplied, validated against the bundle's model id.
    /// </param>
    /// <param name="expectedDimension">
    /// When non-zero, validated against the bundle's declared dimension.
    /// </param>
    public static HelpEmbeddingIndex Build(
        HelpEmbeddingBundle                           bundle,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        HelpContentStore                              contentStore,
        string?                                       expectedModelId   = null,
        int                                           expectedDimension = 0)
    {
        var store = PrecomputedEmbeddingStore.Load(bundle, expectedModelId, expectedDimension);
        return new HelpEmbeddingIndex(generator, store, contentStore);
    }

    // ── IHelpEmbeddingIndex ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HelpExcerpt>> SearchAsync(
        string            query,
        int               topK,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return [];

        // Embed the query.
        var generated = await _generator.GenerateAsync([query], null, ct)
                                        .ConfigureAwait(false);
        // Copy to a plain array: Span cannot be held across the await boundary in C# 12.
        float[] queryVector = generated[0].Vector.ToArray();

        // Search the precomputed matrix.
        var hits = CosineSearch.Search(_store, queryVector, topK);

        // Resolve chunk metadata → HelpExcerpt.
        var results = new List<HelpExcerpt>(hits.Count);
        foreach (var (rowIndex, score) in hits)
        {
            var entry = _store.ChunkIndex[rowIndex];
            var topic = _contentStore.GetById(entry.TopicId);
            if (topic is null) continue;

            // Extract a short excerpt from the topic's plain text for the snippet.
            string snippet = ExtractSnippet(topic, entry.Heading);

            results.Add(new HelpExcerpt(
                topic,
                entry.Heading,
                snippet,
                score));
        }

        return results;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns up to ~200 characters of plain text from the topic, preferring
    /// text near the given heading.
    /// </summary>
    private static string ExtractSnippet(HelpTopic topic, string heading)
    {
        const int MaxLen = 200;

        // Try to start from the heading's position in the plain text.
        int start = 0;
        if (!string.IsNullOrEmpty(heading) && heading != topic.Title)
        {
            int idx = topic.PlainText.IndexOf(
                heading, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                start = idx;
        }

        string raw = topic.PlainText.Length <= start + MaxLen
            ? topic.PlainText[start..]
            : topic.PlainText.Substring(start, MaxLen);

        // Trim to a word boundary.
        int lastSpace = raw.LastIndexOf(' ');
        return lastSpace > MaxLen / 2
            ? raw[..lastSpace].TrimEnd() + "…"
            : raw.TrimEnd();
    }
}
