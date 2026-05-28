namespace HelpNET.Indexing;

/// <summary>
/// A single chunk produced by <see cref="Chunker"/> from a help topic body.
/// </summary>
/// <param name="TopicId">Id of the topic this chunk belongs to.</param>
/// <param name="Heading">
/// Nearest preceding heading text, or the topic title if no heading precedes
/// the chunk.  Used as a display label in citations.
/// </param>
/// <param name="Text">Plain-text content of this chunk.</param>
/// <param name="Index">Zero-based ordinal of this chunk within its topic.</param>
public sealed record HelpChunk(
    string TopicId,
    string Heading,
    string Text,
    int    Index);
