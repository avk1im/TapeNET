namespace HelpNET.Retrieval;

/// <summary>
/// Retrieves ranked <see cref="HelpExcerpt"/>s for a natural-language query.
/// Concrete implementations may be lexical-only, semantic-only, or hybrid.
/// </summary>
public interface IHelpRetriever
{
    /// <summary>
    /// Returns up to <paramref name="topK"/> excerpts for <paramref name="query"/>,
    /// ranked by descending relevance score.
    /// </summary>
    Task<IReadOnlyList<HelpExcerpt>> RetrieveAsync(
        string            query,
        int               topK,
        CancellationToken ct = default);
}
