using HelpNET.Retrieval;

namespace HelpNET.Embeddings;

/// <summary>
/// Semantic search index backed by precomputed or on-the-fly embedding vectors.
/// </summary>
public interface IHelpEmbeddingIndex
{
    /// <summary>
    /// Returns up to <paramref name="topK"/> chunks semantically closest to
    /// <paramref name="query"/>, ordered by descending cosine similarity.
    /// </summary>
    Task<IReadOnlyList<HelpExcerpt>> SearchAsync(
        string            query,
        int               topK,
        CancellationToken ct = default);
}
