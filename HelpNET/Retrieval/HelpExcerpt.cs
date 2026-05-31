using HelpNET.Content;

namespace HelpNET.Retrieval;

/// <summary>
/// A scored chunk excerpt returned by a retriever.
/// </summary>
/// <param name="Topic">The topic this excerpt belongs to.</param>
/// <param name="Heading">
/// Nearest heading within the topic that scopes this excerpt.
/// </param>
/// <param name="Snippet">Short plain-text snippet from the chunk (≤400 chars).</param>
/// <param name="Score">Retrieval score in the range 0–1 (higher is better).</param>
public sealed record HelpExcerpt(
    HelpTopic Topic,
    string    Heading,
    string    Snippet,
    float     Score);
