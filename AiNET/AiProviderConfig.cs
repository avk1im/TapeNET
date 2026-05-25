namespace AiNET;

/// <summary>
/// Full configuration needed to connect to a specific AI provider instance —
/// combines static descriptor metadata with runtime connection details.
/// </summary>
/// <param name="Descriptor">Static provider type descriptor.</param>
/// <param name="Endpoint">The base URI to connect to.</param>
/// <param name="ApiKey">API key or token; <c>null</c> for local providers.</param>
/// <param name="ChatModelId">
/// Model identifier for chat completions; <c>null</c> if the provider has no
///  chat capability or the user has not chosen a model yet.
/// </param>
/// <param name="EmbeddingModelId">
/// Model identifier for embeddings; <c>null</c> if not applicable.
/// </param>
/// <param name="Options">
/// Provider-specific key/value options (e.g. temperature overrides).
/// </param>
public sealed record AiProviderConfig(
    AiProviderDescriptor Descriptor,
    Uri Endpoint,
    string? ApiKey,
    string? ChatModelId,
    string? EmbeddingModelId,
    IReadOnlyDictionary<string, string>? Options = null);
