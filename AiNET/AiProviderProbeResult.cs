namespace AiNET;

/// <summary>
/// Result of probing a specific provider endpoint — reports health, discovered
/// models, measured latency, and any error text.
/// </summary>
/// <param name="Descriptor">The provider type that was probed.</param>
/// <param name="Endpoint">The endpoint URI that was contacted.</param>
/// <param name="IsHealthy">
/// <c>true</c> if the provider responded and is usable.
/// </param>
/// <param name="DiscoveredChatModels">
/// Model IDs available for chat completions. Empty when none were found or
///  the probe failed.
/// </param>
/// <param name="DiscoveredEmbeddingModels">
/// Model IDs available for embeddings. Empty when none were found or not
///  applicable.
/// </param>
/// <param name="Latency">
/// Round-trip time for the health-check call. <see cref="TimeSpan.Zero"/> on
///  failure.
/// </param>
/// <param name="ErrorMessage">
/// Human-readable failure description; <c>null</c> when <see cref="IsHealthy"/>
///  is <c>true</c>.
/// </param>
public sealed record AiProviderProbeResult(
    AiProviderDescriptor Descriptor,
    Uri Endpoint,
    bool IsHealthy,
    IReadOnlyList<string> DiscoveredChatModels,
    IReadOnlyList<string> DiscoveredEmbeddingModels,
    TimeSpan Latency,
    string? ErrorMessage);
