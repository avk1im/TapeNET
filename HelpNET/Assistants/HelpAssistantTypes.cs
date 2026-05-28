using HelpNET.Content;
using HelpNET.Session;

namespace HelpNET.Assistants;

/// <summary>
/// Input to <see cref="IHelpAssistant.AskAsync"/>.
/// </summary>
/// <param name="Query">The user's natural-language question.</param>
/// <param name="CurrentHost">
/// The name of the active host window, if known.  Assistants may use this to
/// bias retrieval toward the current context.
/// </param>
/// <param name="CurrentTopicId">
/// The topic the user is currently viewing, if any.
/// </param>
/// <param name="History">
/// Conversation turns so far; <c>Rag</c> assistants pass these to the LLM for
/// multi-turn context.
/// </param>
public sealed record HelpAssistantRequest(
    string                          Query,
    string?                         CurrentHost,
    string?                         CurrentTopicId,
    IReadOnlyList<ConversationTurn> History);

/// <summary>
/// Output from <see cref="IHelpAssistant.AskAsync"/>.
/// </summary>
/// <param name="AnswerMarkdown">
/// The assistant's answer in Markdown, suitable for rendering in the chat subpane.
/// </param>
/// <param name="Citations">Topics that directly back the answer.</param>
/// <param name="SuggestedTopics">Related topic chips shown below the answer.</param>
/// <param name="SuggestedActions">Host-defined action buttons shown below the answer.</param>
/// <param name="Confidence">Retrieval confidence in the range 0–1.</param>
/// <param name="Mode">The mode that produced this response.</param>
public sealed record HelpAssistantResponse(
    string                          AnswerMarkdown,
    IReadOnlyList<HelpCitation>     Citations,
    IReadOnlyList<HelpTopicRef>     SuggestedTopics,
    IReadOnlyList<HelpActionRef>    SuggestedActions,
    float                           Confidence,
    HelpAssistantMode               Mode);
