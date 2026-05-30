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

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised when <see cref="CurrentTopic"/> changes.</summary>
    event EventHandler? CurrentTopicChanged;

    /// <summary>Raised after <see cref="AskAsync"/> completes with the full response.</summary>
    event EventHandler<HelpAssistantResponse>? AnswerReceived;

    /// <summary>Raised when <see cref="AssistantMode"/> changes (e.g. due to provider swap).</summary>
    event EventHandler? AssistantModeChanged;
}
