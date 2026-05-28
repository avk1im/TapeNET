using HelpNET.Content;

namespace HelpNET.Indexing;

/// <summary>
/// Interface for the lexical help index.  Enables testing with a stub.
/// </summary>
public interface IHelpIndex
{
    /// <summary>
    /// Returns the top-<paramref name="topK"/> topics for <paramref name="query"/>.
    /// </summary>
    IReadOnlyList<HelpSearchHit> Search(string query, int topK);
}
