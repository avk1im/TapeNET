namespace HelpNET.Embeddings;

/// <summary>
/// Efficient cosine similarity search over a precomputed embedding matrix.
/// </summary>
/// <remarks>
/// All vectors are assumed to be L2-normalised, so cosine similarity equals
/// the plain dot product.  The search runs in O(n × d) time — acceptable for
/// corpora up to a few thousand chunks.
/// </remarks>
internal static class CosineSearch
{
    /// <summary>
    /// Returns the <paramref name="topK"/> rows from <paramref name="store"/> that
    /// have the highest cosine similarity to <paramref name="queryVector"/>,
    /// in descending score order.
    /// </summary>
    /// <param name="store">Precomputed embedding store containing row vectors.</param>
    /// <param name="queryVector">
    /// L2-normalised query vector whose length must equal <see cref="PrecomputedEmbeddingStore.Dimension"/>.
    /// </param>
    /// <param name="topK">Maximum number of results to return.</param>
    internal static IReadOnlyList<(int RowIndex, float Score)> Search(
        PrecomputedEmbeddingStore store,
        ReadOnlySpan<float>       queryVector,
        int                       topK)
    {
        if (store.ChunkIndex.Count == 0 || topK <= 0)
            return [];

        int n   = store.ChunkIndex.Count;
        int dim = store.Dimension;

        // Score every row.
        var scores = new (int Index, float Score)[n];
        for (int i = 0; i < n; i++)
        {
            var row   = store.GetVector(i);
            float dot = 0f;
            for (int d = 0; d < dim; d++)
                dot += row[d] * queryVector[d];
            scores[i] = (i, dot);
        }

        // Partial sort: move the top-K into the front of the array.
        int k = Math.Min(topK, n);
        scores.AsSpan().PartialSortDescending(k);

        var results = new (int RowIndex, float Score)[k];
        for (int i = 0; i < k; i++)
            results[i] = (scores[i].Index, scores[i].Score);

        return results;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Partial selection sort: rearranges <paramref name="span"/> so that the
    /// first <paramref name="k"/> elements are the top-K by descending Score.
    /// O(n × k) — fine for typical corpus sizes.
    /// </summary>
    private static void PartialSortDescending(
        this Span<(int Index, float Score)> span, int k)
    {
        for (int i = 0; i < k; i++)
        {
            int best = i;
            for (int j = i + 1; j < span.Length; j++)
                if (span[j].Score > span[best].Score)
                    best = j;
            if (best != i)
                (span[i], span[best]) = (span[best], span[i]);
        }
    }
}
