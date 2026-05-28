namespace HelpNET.Session;

/// <summary>
/// A single question/answer exchange stored in a help session's conversation.
/// </summary>
/// <param name="Query">The user's question.</param>
/// <param name="AnswerMarkdown">The assistant's answer in Markdown.</param>
/// <param name="Timestamp">Wall-clock time when the exchange occurred.</param>
public sealed record ConversationTurn(
    string   Query,
    string   AnswerMarkdown,
    DateTime Timestamp);
