using System.ClientModel;

using Microsoft.Extensions.AI;

namespace AiNET.Providers;

/// <summary>
/// Provider adapter for the <b>OpenAI</b> cloud API
/// (<c>https://api.openai.com</c>).
/// </summary>
/// <remarks>
/// <b>Phase 1 status:</b> Stub — probe returns a static unhealthy result
/// (environment-variable detection in <see cref="AiProviderDiscovery"/> is
/// sufficient for auto-discovery). Client construction is implemented but
/// not yet tested end-to-end.
/// </remarks>
public sealed class OpenAiProvider : IAiProvider
{
    private static readonly Uri DefaultEndpoint = new("https://api.openai.com");

    private static readonly AiProviderDescriptor _descriptor = new(
        Kind:            AiProviderKind.OpenAi,
        Location:        AiProviderLocation.Cloud,
        DisplayName:     "OpenAI",
        DefaultEndpoint: DefaultEndpoint,
        RequiresApiKey:  true,
        Capabilities:    AiCapabilities.Chat | AiCapabilities.Embeddings | AiCapabilities.Tools);

    /// <inheritdoc/>
    public AiProviderDescriptor Descriptor => _descriptor;

    /// <inheritdoc/>
    /// <remarks>
    /// Discovery for cloud providers is driven by environment variables;
    /// network probing is not performed here. Returns unhealthy so that
    /// <see cref="AiProviderDiscovery"/> skips the network probe for this
    /// provider type.
    /// </remarks>
    public Task<AiProviderProbeResult> ProbeAsync(
        Uri endpoint, string? apiKey, CancellationToken ct)
    {
        // We don't do a live network call here — env-var detection is handled
        // by AiProviderDiscovery. Return a synthetic "healthy" result when a
        // key is present so the session can be built without an extra roundtrip.
        bool healthy = !string.IsNullOrEmpty(apiKey);
        return Task.FromResult(new AiProviderProbeResult(
            _descriptor, endpoint, healthy,
            healthy ? ["gpt-4o-mini", "gpt-4o"] : [],
            healthy ? ["text-embedding-3-small"] : [],
            TimeSpan.Zero,
            healthy ? null : "No OPENAI_API_KEY found."));
    }

    /// <inheritdoc/>
    public IChatClient? CreateChatClient(AiProviderConfig config)
    {
        if (config.ChatModelId is null || config.ApiKey is null) return null;
        var credential = new ApiKeyCredential(config.ApiKey);
        return new OpenAI.Chat.ChatClient(config.ChatModelId, credential)
            .AsIChatClient();
    }

    /// <inheritdoc/>
    public IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(
        AiProviderConfig config)
    {
        if (config.EmbeddingModelId is null || config.ApiKey is null) return null;
        var credential = new ApiKeyCredential(config.ApiKey);
        return new OpenAI.Embeddings.EmbeddingClient(config.EmbeddingModelId, credential)
            .AsIEmbeddingGenerator();
    }
}
