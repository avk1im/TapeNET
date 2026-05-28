using HelpNET.Content;
using HelpNET.Embeddings;
using HelpNET.Indexing;

namespace HelpNET.Retrieval;

/// <summary>
/// Blends BM25 lexical scores with ONNX-based semantic (cosine) scores to
/// produce a single ranked list of <see cref="HelpExcerpt"/>s.
/// </summary>
/// <remarks>
/// Each result score is computed as:
/// <c>score = lexicalWeight × normalisedBm25 + semanticWeight × cosine</c>
/// where both inputs are normalised to [0, 1] before blending.
/// The weights must sum to 1.0.
/// </remarks>
public sealed class HybridRetriever : IHelpRetriever
{
    // ── Defaults ──────────────────────────────────────────────────────────────

    /// <summary>Default weight for the BM25 component.</summary>
    public const float DefaultLexicalWeight = 0.4f;

    /// <summary>Default weight for the semantic (cosine) component.</summary>
    public const float DefaultSemanticWeight = 0.6f;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly BM25HelpIndex       _bm25;
    private readonly IHelpEmbeddingIndex _semantic;
    private readonly HelpContentStore    _store;
    private readonly float               _lexicalWeight;
    private readonly float               _semanticWeight;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <param name="bm25">Pre-built BM25 index.</param>
    /// <param name="semantic">Embedding-based index.</param>
    /// <param name="store">Content store (for excerpt building on the lexical side).</param>
    /// <param name="lexicalWeight">
    /// Weight for BM25 scores (0–1).  Semantic weight = 1 − <paramref name="lexicalWeight"/>.
    /// </param>
    public HybridRetriever(
        BM25HelpIndex       bm25,
        IHelpEmbeddingIndex semantic,
        HelpContentStore    store,
        float               lexicalWeight = DefaultLexicalWeight)
    {
        if (lexicalWeight < 0f || lexicalWeight > 1f)
            throw new ArgumentOutOfRangeException(nameof(lexicalWeight), "Must be in [0, 1].");

        _bm25           = bm25;
        _semantic       = semantic;
        _store          = store;
        _lexicalWeight  = lexicalWeight;
        _semanticWeight = 1f - lexicalWeight;
    }

    // ── IHelpRetriever ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HelpExcerpt>> RetrieveAsync(
        string            query,
        int               topK,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return [];

        // Fetch a larger candidate pool and then blend scores.
        int pool = Math.Max(topK * 3, 20);

        // ── Lexical side ──────────────────────────────────────────────────────
        var lexHits = _bm25.Search(query, pool);

        // Normalise BM25 scores to [0, 1].
        float maxBm25 = lexHits.Count > 0 ? lexHits[0].Score : 1f;
        if (maxBm25 < 1e-9f) maxBm25 = 1f;

        var lexByTopic = new Dictionary<string, float>(lexHits.Count);
        foreach (var h in lexHits)
            lexByTopic[h.Topic.Id] = h.Score / maxBm25;

        // ── Semantic side ─────────────────────────────────────────────────────
        var semHits = await _semantic.SearchAsync(query, pool, ct).ConfigureAwait(false);

        // Normalise cosine scores (already in [-1, 1]; shift to [0, 1]).
        float maxCos = semHits.Count > 0 ? semHits[0].Score : 1f;
        if (maxCos < 1e-9f) maxCos = 1f;

        // ── Merge ─────────────────────────────────────────────────────────────
        // Key: topic id → best excerpt and blended score.
        var merged = new Dictionary<string, (HelpExcerpt Excerpt, float Blended)>(pool);

        foreach (var sem in semHits)
        {
            float lexScore = lexByTopic.TryGetValue(sem.Topic.Id, out float l) ? l : 0f;
            float semScore = Math.Max(0f, sem.Score / maxCos);
            float blended  = _lexicalWeight * lexScore + _semanticWeight * semScore;

            if (!merged.TryGetValue(sem.Topic.Id, out var existing) ||
                blended > existing.Blended)
            {
                merged[sem.Topic.Id] = (sem with { Score = blended }, blended);
            }
        }

        // Include lexical-only hits that semantic missed.
        foreach (var h in lexHits)
        {
            if (merged.ContainsKey(h.Topic.Id)) continue;

            var topic = _store.GetById(h.Topic.Id);
            if (topic is null) continue;

            float blended = _lexicalWeight * (h.Score / maxBm25);
            merged[h.Topic.Id] = (
                new HelpExcerpt(topic, topic.Title, h.Excerpt, blended),
                blended);
        }

        // Sort and take top-K.
        return merged.Values
                     .OrderByDescending(v => v.Blended)
                     .Take(topK)
                     .Select(v => v.Excerpt)
                     .ToList();
    }
}
