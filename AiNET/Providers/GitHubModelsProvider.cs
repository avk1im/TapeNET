using System.ClientModel;

using Microsoft.Extensions.AI;

using OpenAI;

namespace AiNET.Providers;

/// <summary>
/// Provider adapter for <b>GitHub Models</b> marketplace
/// (<c>https://models.inference.ai.azure.com</c>). Uses a GitHub personal
/// access token for authentication.
/// </summary>
/// <remarks>
/// <b>Phase 1 status:</b> Stub — probe returns a synthetic result when a
/// token is present. Full integration tests are deferred to a later phase.
/// </remarks>
public sealed class GitHubModelsProvider : IAiProvider
{
    // The OpenAI SDK constructs request paths relative to the base URI,
    // appending e.g. "/chat/completions" itself.  The correct GitHub Models
    // base is simply the root of models.inference.ai.azure.com.
    private static readonly Uri DefaultEndpoint =
        new("https://models.inference.ai.azure.com");

    private static readonly AiProviderDescriptor _descriptor = new(
        Kind:            AiProviderKind.GitHubModels,
        Location:        AiProviderLocation.Cloud,
        DisplayName:     "GitHub Models",
        DefaultEndpoint: DefaultEndpoint,
        RequiresApiKey:  true,
        Capabilities:    AiCapabilities.Chat | AiCapabilities.Tools);

    /// <inheritdoc/>
    public AiProviderDescriptor Descriptor => _descriptor;

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a synthetic result; the endpoint is fixed and well-known.
    /// </remarks>
    public Task<AiProviderProbeResult> ProbeAsync(
        Uri endpoint, string? apiKey, CancellationToken ct)
    {
        bool healthy = !string.IsNullOrEmpty(apiKey);
        return Task.FromResult(new AiProviderProbeResult(
            _descriptor, endpoint, healthy,
            healthy ? ["gpt-4o-mini", "gpt-4o", "Phi-3.5-mini-instruct"] : [],
            [],
            TimeSpan.Zero,
            healthy ? null : "No GITHUB_TOKEN found."));
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
    /// <remarks>GitHub Models does not currently offer an embeddings endpoint.</remarks>
    public IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(
        AiProviderConfig config) => null;
}
