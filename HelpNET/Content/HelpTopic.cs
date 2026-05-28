namespace HelpNET.Content;

/// <summary>
/// A fully parsed help topic, produced by <see cref="HelpContentStore"/> from a
/// <see cref="HelpRawDocument"/>.
/// </summary>
/// <param name="Id">Globally unique topic id (e.g. <c>dialog.restore</c>).</param>
/// <param name="Title">Display title.</param>
/// <param name="Kind">Topic category.</param>
/// <param name="Host">
/// Optional host window name (e.g. <c>RestoreWindow</c>) used for F1 routing.
/// </param>
/// <param name="Keywords">Keyword list from front-matter for BM25 boosting.</param>
/// <param name="Intents">Natural-language intent phrases for the intent matcher.</param>
/// <param name="RelatedTopicIds">Ids of related topics surfaced as suggestions.</param>
/// <param name="MarkdownBody">Body text without the front-matter block.</param>
/// <param name="PlainText">Stripped text used for indexing.</param>
/// <param name="Walkthrough">Parsed walkthrough script; <c>null</c> unless <c>kind == walkthrough</c>.</param>
/// <param name="IncludeInAiCorpus">
/// When <c>false</c>, the topic is excluded from RAG retrieval.
/// Default is <c>true</c>.
/// </param>
public sealed record HelpTopic(
    string Id,
    string Title,
    HelpTopicKind Kind,
    string? Host,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Intents,
    IReadOnlyList<string> RelatedTopicIds,
    string MarkdownBody,
    string PlainText,
    WalkthroughScript? Walkthrough,
    bool IncludeInAiCorpus);
