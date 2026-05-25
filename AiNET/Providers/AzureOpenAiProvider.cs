using System.ClientModel;

using Microsoft.Extensions.AI;

using OpenAI;

namespace AiNET.Providers;

/// <summary>
/// Provider adapter for <b>Azure OpenAI Service</b> (user-supplied
/// endpoint, e.g. <c>https://myresource.openai.azure.com/</c>).
/// </summary>
/// <remarks>
/// <b>Phase 1 status:</b> Stub — probe returns a synthetic result when an
/// API key is present. Full integration tests are deferred to a later phase.
/// </remarks>
public sealed class AzureOpenAiProvider : IAiProvider
{
    private static readonly AiProviderDescriptor _descriptor = new(
        Kind:            AiProviderKind.AzureOpenAi,
        Location:        AiProviderLocation.Cloud,
        DisplayName:     "Azure OpenAI",
        DefaultEndpoint: null,   // always user-supplied
        RequiresApiKey:  true,
        Capabilities:    AiCapabilities.Chat | AiCapabilities.Embeddings | AiCapabilities.Tools);

    /// <inheritdoc/>
    public AiProviderDescriptor Descriptor => _descriptor;

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a synthetic result; the actual endpoint is user-supplied and
    /// may vary by deployment.
    /// </remarks>
    public Task<AiProviderProbeResult> ProbeAsync(
        Uri endpoint, string? apiKey, CancellationToken ct)
    {
        bool healthy = !string.IsNullOrEmpty(apiKey);
        return Task.FromResult(new AiProviderProbeResult(
            _descriptor, endpoint, healthy, [], [], TimeSpan.Zero,
            healthy ? null : "No AZURE_OPENAI_API_KEY found."));
    }

    /// <inheritdoc/>
    public IChatClient? CreateChatClient(AiProviderConfig config)
    {
        if (config.ChatModelId is null || config.ApiKey is null) return null;
        var credential = new ApiKeyCredential(config.ApiKey);
        var options    = new OpenAIClientOptions { Endpoint = config.Endpoint };
        return new OpenAI.Chat.ChatClient(config.ChatModelId, credential, options)
            .AsIChatClient();
    }

    /// <inheritdoc/>
    public IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(
        AiProviderConfig config)
    {
        if (config.EmbeddingModelId is null || config.ApiKey is null) return null;
        var credential = new ApiKeyCredential(config.ApiKey);
        var options    = new OpenAIClientOptions { Endpoint = config.Endpoint };
        return new OpenAI.Embeddings.EmbeddingClient(config.EmbeddingModelId, credential, options)
            .AsIEmbeddingGenerator();
    }
}
