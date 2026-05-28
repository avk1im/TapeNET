namespace HelpNET.Content;

/// <summary>
/// In-memory implementation of <see cref="IHelpContentSource"/> for unit tests.
/// Constructed with a list of raw Markdown strings; no embedding bundle.
/// </summary>
public sealed class InMemoryHelpContentSource : IHelpContentSource
{
    private readonly IReadOnlyList<HelpRawDocument> _docs;

    /// <param name="sourceId">Identifier for this source instance.</param>
    /// <param name="docs">Pre-built raw documents to serve.</param>
    public InMemoryHelpContentSource(string sourceId, IReadOnlyList<HelpRawDocument> docs)
    {
        SourceId = sourceId;
        _docs    = docs;
    }

    /// <summary>Convenience constructor that wraps plain Markdown strings.</summary>
    /// <param name="sourceId">Identifier for this source instance.</param>
    /// <param name="markdownDocuments">
    /// Sequence of <c>(logicalPath, markdown)</c> pairs.
    /// </param>
    public InMemoryHelpContentSource(
        string sourceId,
        IEnumerable<(string LogicalPath, string Markdown)> markdownDocuments)
    {
        SourceId = sourceId;
        _docs    = markdownDocuments
            .Select(t => new HelpRawDocument(t.LogicalPath, t.Markdown, null))
            .ToList();
    }

    /// <inheritdoc/>
    public string SourceId { get; }

    /// <inheritdoc/>
    public async IAsyncEnumerable<HelpRawDocument> EnumerateAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var doc in _docs)
        {
            ct.ThrowIfCancellationRequested();
            yield return doc;
            await Task.Yield(); // allow cancellation checks between items
        }
    }

    /// <inheritdoc/>
    public Task<HelpEmbeddingBundle?> TryLoadEmbeddingBundleAsync(CancellationToken ct)
        => Task.FromResult<HelpEmbeddingBundle?>(null);
}
