namespace HelpNET.Content;

/// <summary>
/// A raw help document as loaded from a content source before parsing.
/// </summary>
/// <param name="LogicalPath">
/// Source-relative path, e.g. <c>concepts/incremental-backup.md</c>.
/// Used for diagnostics; the authoritative id comes from the front-matter.
/// </param>
/// <param name="Markdown">Full file content including the YAML front-matter block.</param>
/// <param name="LastModified">Optional last-modified timestamp from the source.</param>
public sealed record HelpRawDocument(
    string LogicalPath,
    string Markdown,
    DateTimeOffset? LastModified);

/// <summary>
/// A precomputed embedding bundle produced by the <c>HelpIndexBuilder</c> tool.
/// Loaded at runtime by <see cref="IHelpContentSource.TryLoadEmbeddingBundleAsync"/>.
/// </summary>
/// <param name="ModelId">Id of the embedding model that produced these vectors.</param>
/// <param name="Dimension">Embedding vector dimension.</param>
/// <param name="ModelHash">Hash of the model weights for sanity-check against the runtime model.</param>
/// <param name="EmbeddingBlob">
/// Packed <c>float[]</c> data (little-endian, chunk-major: dim floats per chunk).
/// </param>
/// <param name="ChunkIndexJson">
/// JSON mapping chunk index → (topicId, heading, position), used to resolve hits.
/// </param>
public sealed record HelpEmbeddingBundle(
    string ModelId,
    int Dimension,
    string ModelHash,
    ReadOnlyMemory<byte> EmbeddingBlob,
    string ChunkIndexJson);

/// <summary>
/// Supplies raw help documents and an optional precomputed embedding bundle
/// to the HelpNET engine.  The engine is content-agnostic; the host (e.g.
/// TapeWinNET) provides a concrete implementation that enumerates its own
/// embedded resources.
/// </summary>
public interface IHelpContentSource
{
    /// <summary>
    /// Stable identifier for this source (used for logging and cache keys).
    /// </summary>
    string SourceId { get; }

    /// <summary>Enumerates all available topic documents from this source.</summary>
    IAsyncEnumerable<HelpRawDocument> EnumerateAsync(CancellationToken ct);

    /// <summary>
    /// Optionally loads a precomputed embedding bundle.
    /// Returns <c>null</c> when no bundle is available (engine falls back to
    /// Lexical or on-the-fly embedding mode).
    /// </summary>
    Task<HelpEmbeddingBundle?> TryLoadEmbeddingBundleAsync(CancellationToken ct);
}
