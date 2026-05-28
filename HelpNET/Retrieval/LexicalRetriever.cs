using HelpNET.Content;
using HelpNET.Indexing;

namespace HelpNET.Retrieval;

/// <summary>
/// An <see cref="IHelpRetriever"/> backed exclusively by <see cref="BM25HelpIndex"/>.
/// Used by <see cref="RagHelpAssistant"/> when no embedding bundle is available.
/// </summary>
internal sealed class LexicalRetriever : IHelpRetriever
{
    private readonly BM25HelpIndex    _bm25;
    private readonly HelpContentStore _store;

    internal LexicalRetriever(BM25HelpIndex bm25, HelpContentStore store)
    {
        _bm25  = bm25;
        _store = store;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<HelpExcerpt>> RetrieveAsync(
        string            query,
        int               topK,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var hits = _bm25.Search(query, topK);
        var results = hits
            .Select(h => new HelpExcerpt(h.Topic, h.Topic.Title, h.Excerpt, h.Score))
            .ToList();

        return Task.FromResult<IReadOnlyList<HelpExcerpt>>(results);
    }
}
