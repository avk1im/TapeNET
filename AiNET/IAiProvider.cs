using Microsoft.Extensions.AI;

namespace AiNET;

/// <summary>
/// Implemented by each provider adapter. Describes the provider and can
/// probe a given endpoint, then construct live clients from a validated
/// <see cref="AiProviderConfig"/>.
/// </summary>
public interface IAiProvider
{
    /// <summary>Static descriptor for this provider type.</summary>
    AiProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Contacts the given endpoint and returns a <see cref="AiProviderProbeResult"/>
    /// reporting health, discovered models, and latency.
    /// </summary>
    Task<AiProviderProbeResult> ProbeAsync(Uri endpoint, string? apiKey, CancellationToken ct);

    /// <summary>
    /// Creates an <see cref="IChatClient"/> from the supplied configuration.
    /// Returns <c>null</c> if this provider does not support chat.
    /// </summary>
    IChatClient? CreateChatClient(AiProviderConfig config);

    /// <summary>
    /// Creates an embedding generator from the supplied configuration.
    /// Returns <c>null</c> if this provider does not support embeddings.
    /// </summary>
    IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(AiProviderConfig config);
}
