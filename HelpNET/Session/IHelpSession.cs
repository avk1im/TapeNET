using HelpNET.Assistants;
using HelpNET.Content;

namespace HelpNET.Session;

/// <summary>
/// Stateful façade that the UI binds against.  Owns navigation history,
/// conversation state, and delegates retrieval/synthesis to the current
/// <see cref="IHelpAssistant"/>.
/// </summary>
public interface IHelpSession : IAsyncDisposable
{
    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>The topic currently shown in the content subpane.</summary>
    HelpTopic? CurrentTopic { get; }

    /// <summary>Topics accessible via the Back button (most-recent first).</summary>
    IReadOnlyList<HelpTopic> BackHistory { get; }

    /// <summary>Topics accessible via the Forward button (most-recent first).</summary>
    IReadOnlyList<HelpTopic> ForwardHistory { get; }

    /// <summary>All conversation turns in the current session.</summary>
    IReadOnlyList<ConversationTurn> Conversation { get; }

    /// <summary>The retrieval mode currently in use.</summary>
    HelpAssistantMode AssistantMode { get; }

    // ── Navigation ────────────────────────────────────────────────────────────

    /// <summary>Navigates to the topic identified by <paramref name="request"/>.</summary>
    Task<HelpTopic> NavigateAsync(HelpNavigationRequest request, CancellationToken ct);

    /// <summary>Navigates back one step.  Returns <c>null</c> if at the beginning.</summary>
    Task<HelpTopic?> BackAsync(CancellationToken ct);

    /// <summary>Navigates forward one step.  Returns <c>null</c> if at the end.</summary>
    Task<HelpTopic?> ForwardAsync(CancellationToken ct);

    /// <summary>Navigates to the help home topic.</summary>
    Task<HelpTopic> HomeAsync(CancellationToken ct);

    // ── Search / Ask ──────────────────────────────────────────────────────────

    /// <summary>Returns the top-<paramref name="topK"/> topics for a search query.</summary>
    Task<IReadOnlyList<HelpSearchHit>> SearchAsync(string query, int topK, CancellationToken ct);

    /// <summary>Asks the assistant a question and appends the exchange to <see cref="Conversation"/>.</summary>
    Task<HelpAssistantResponse> AskAsync(string query, CancellationToken ct);

    // ── Content helpers ───────────────────────────────────────────────────────

    /// <summary>Returns all walkthrough scripts applicable to the named host window.</summary>
    IReadOnlyList<WalkthroughScript> GetWalkthroughsForHost(string hostName);

    /// <summary>
    /// Returns all walkthrough topics (with their scripts) applicable to the named host window.
    /// Includes the owning <see cref="HelpTopic"/> so callers can display the tour title and id.
    /// </summary>
    IReadOnlyList<(HelpTopic Topic, WalkthroughScript Script)> GetWalkthroughTopicsForHost(string hostName);

    /// <summary>
    /// Returns the topic tagged for a specific control within a host window, or
    /// <c>null</c> if none exists.
    /// </summary>
    HelpTopic? GetTopicForControl(string hostName, string topicId);

    /// <summary>Clears all conversation turns.</summary>
    void ClearConversation();

    /// <summary>
    /// Returns the display title for the topic with the given <paramref name="id"/>,
    /// or <c>null</c> when the topic is not found in the content store.
    /// Used by <c>MarkdownRenderer</c> to substitute human-readable link text
    /// for bare topic-id citations emitted by the AI assistant.
    /// </summary>
    string? TryGetTopicTitle(string id);

    /// <summary>
    /// Returns the plain-text definition for the given glossary term slug, or <c>null</c>
    /// when not found.  Used by <c>MarkdownRenderer</c> to populate glossary popups.
    /// The slug is the term lowercased with spaces replaced by hyphens
    /// (e.g. <c>"backup-set"</c>, <c>"toc"</c>, <c>"fcl"</c>).
    /// </summary>
    string? TryGetGlossaryDefinition(string termSlug);

    /// <summary>
    /// Returns the plain-text Reveal explanation for a control within a topic's
    /// <c>## Controls</c> chapter, or <c>null</c> when not found.
    /// <para>
    /// Both the display name and the pre-slugified form of <paramref name="controlName"/>
    /// are accepted: <c>"Backup sets list"</c> and <c>"backup-sets-list"</c> both work.
    /// </para>
    /// </summary>
    string? TryGetControlHelp(string topicId, string controlName);

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised when <see cref="CurrentTopic"/> changes.</summary>
    event EventHandler? CurrentTopicChanged;

    /// <summary>Raised after <see cref="AskAsync"/> completes with the full response.</summary>
    event EventHandler<HelpAssistantResponse>? AnswerReceived;

    /// <summary>Raised when <see cref="AssistantMode"/> changes (e.g. due to provider swap).</summary>
    event EventHandler? AssistantModeChanged;
}
