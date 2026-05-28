namespace HelpNET.Session;

/// <summary>
/// Tuneable options supplied to <see cref="HelpSessionFactory.CreateAsync"/> when
/// creating a new <see cref="HelpSession"/>.
/// </summary>
/// <param name="HomeTopicId">
/// Id of the home/landing topic.  Defaults to <c>"home"</c>.
/// </param>
/// <param name="DefaultTopK">
/// Number of hits returned by <see cref="IHelpSession.SearchAsync"/>.
/// Defaults to 5.
/// </param>
/// <param name="AssistantTopK">
/// Number of excerpts the assistant assembles for each answer.
/// Defaults to 5.
/// </param>
/// <param name="MaxHistoryDepth">
/// Maximum number of topics kept in each of the back/forward stacks.
/// Defaults to 50.
/// </param>
/// <param name="MaxConversationTurns">
/// Maximum number of conversation turns kept in memory.
/// Defaults to 50.
/// </param>
/// <param name="PreferProviderEmbeddings">
/// When <c>true</c> and <c>IAiSession.EmbeddingGenerator</c> is available, the
/// session will use the provider's generator instead of the built-in ONNX one —
/// provided a precomputed bundle exists whose <c>ModelId</c> matches the
/// provider's current embedding model (Strategy A).
/// Defaults to <c>false</c> (built-in ONNX preferred).
/// </param>
public sealed record HelpSessionOptions(
    string HomeTopicId            = "home",
    int    DefaultTopK            = 5,
    int    AssistantTopK          = 5,
    int    MaxHistoryDepth        = 50,
    int    MaxConversationTurns   = 50,
    bool   PreferProviderEmbeddings = false);

