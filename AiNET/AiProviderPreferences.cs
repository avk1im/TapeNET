using System.Text.Json.Serialization;

namespace AiNET;

/// <summary>
/// User preferences for AI provider selection, persisted as JSON.
/// Does <b>not</b> store API keys (those go into a DPAPI-protected blob —
///  Phase 6); only stores non-secret connection metadata.
/// </summary>
public sealed class AiProviderPreferences
{
    /// <summary>
    /// <c>true</c> once the user has been asked at least once whether to set
    /// up an AI provider. Used to suppress the first-run prompt on subsequent
    /// launches.
    /// </summary>
    [JsonPropertyName("hasBeenAskedOnce")]
    public bool HasBeenAskedOnce { get; set; }

    /// <summary>
    /// When exactly one healthy provider is found during discovery, skip the
    /// selection dialog and use it automatically.
    /// </summary>
    [JsonPropertyName("autoUseIfSingle")]
    public bool AutoUseIfSingle { get; set; } = true;

    /// <summary>
    /// The <see cref="AiProviderKind"/> of the last successfully used
    /// provider, or <c>null</c> if none has been configured yet.
    /// </summary>
    [JsonPropertyName("lastProviderKind")]
    public AiProviderKind? LastProviderKind { get; set; }

    /// <summary>
    /// The endpoint URI used the last time a provider was configured.
    /// </summary>
    [JsonPropertyName("lastEndpoint")]
    public Uri? LastEndpoint { get; set; }

    /// <summary>
    /// The chat model ID that was last selected.
    /// </summary>
    [JsonPropertyName("lastChatModelId")]
    public string? LastChatModelId { get; set; }

    /// <summary>
    /// The embedding model ID that was last selected.
    /// </summary>
    [JsonPropertyName("lastEmbeddingModelId")]
    public string? LastEmbeddingModelId { get; set; }
}
