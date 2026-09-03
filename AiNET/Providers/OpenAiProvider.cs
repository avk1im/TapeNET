using System.ClientModel;

using AiNET.Internal;

using Microsoft.Extensions.AI;

using OpenAI;

namespace AiNET.Providers;

/// <summary>
/// Provider adapter for the <b>OpenAI</b> cloud API
/// (<c>https://api.openai.com</c>).
/// </summary>
/// <remarks>
/// Probing performs a real authenticated <c>GET /v1/models</c> call, so an
///  invalid key is rejected up front and the model picker is populated from
///  the account's actual entitlements.
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
    public Task<AiProviderProbeResult> ProbeAsync(
        Uri endpoint, string? apiKey, CancellationToken ct) =>
        OpenAiModelProbe.ProbeAsync(
            _descriptor,
            endpoint,
            new Uri(endpoint, "/v1/models"),
            headers => headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey),
            missingKeyMessage: "No OpenAI API key supplied.",
            hasCredential: !string.IsNullOrEmpty(apiKey),
            ct);

    /// <inheritdoc/>
    public IChatClient? CreateChatClient(AiProviderConfig config)
    {
        if (config.ChatModelId is null || config.ApiKey is null) return null;
        return CreateClient(config).GetChatClient(config.ChatModelId).AsIChatClient();
    }

    /// <inheritdoc/>
    public IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(
        AiProviderConfig config)
    {
        if (config.EmbeddingModelId is null || config.ApiKey is null) return null;
        return CreateClient(config)
            .GetEmbeddingClient(config.EmbeddingModelId)
            .AsIEmbeddingGenerator();
    }

    // Honours a non-default endpoint (proxy / gateway) while keeping the
    //  official default when the user did not override it.
    private static OpenAIClient CreateClient(AiProviderConfig config)
    {
        var credential = new ApiKeyCredential(config.ApiKey!);
        if (config.Endpoint == DefaultEndpoint)
            return new OpenAIClient(credential);

        return new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = config.Endpoint });
    }
}
