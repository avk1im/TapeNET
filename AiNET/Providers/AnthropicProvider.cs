using AiNET.Internal;

using Anthropic.SDK;

using Microsoft.Extensions.AI;

namespace AiNET.Providers;

/// <summary>
/// Provider adapter for the <b>Anthropic Claude</b> cloud API
/// (<c>https://api.anthropic.com</c>).
/// </summary>
/// <remarks>
/// Anthropic offers <i>no embeddings API</i>, so the descriptor advertises
///  <see cref="AiCapabilities.Chat"/> and <see cref="AiCapabilities.Tools"/>
///  only and <see cref="CreateEmbeddingGenerator"/> always returns
///  <c>null</c>. Consumers needing retrieval (e.g. HelpNET's RAG index) fall
///  back to the in-process ONNX embedding generator.
/// <para>
/// Authentication uses the <c>x-api-key</c> header plus a required
///  <c>anthropic-version</c> header, rather than a Bearer token.
/// </para>
/// </remarks>
public sealed class AnthropicProvider : IAiProvider
{
    private static readonly Uri DefaultEndpoint = new("https://api.anthropic.com");

    /// <summary>
    /// Value of the mandatory <c>anthropic-version</c> request header.
    /// </summary>
    private const string AnthropicVersion = "2023-06-01";

    private static readonly AiProviderDescriptor _descriptor = new(
        Kind:            AiProviderKind.Anthropic,
        Location:        AiProviderLocation.Cloud,
        DisplayName:     "Anthropic (Claude)",
        DefaultEndpoint: DefaultEndpoint,
        RequiresApiKey:  true,
        Capabilities:    AiCapabilities.Chat | AiCapabilities.Tools);

    /// <inheritdoc/>
    public AiProviderDescriptor Descriptor => _descriptor;

    /// <inheritdoc/>
    /// <remarks>
    /// Calls <c>GET /v1/models</c>, which validates the key and returns the
    ///  Claude models the account may use.
    /// </remarks>
    public Task<AiProviderProbeResult> ProbeAsync(
        Uri endpoint, string? apiKey, CancellationToken ct) =>
        OpenAiModelProbe.ProbeAsync(
            _descriptor,
            endpoint,
            new Uri(endpoint, "/v1/models"),
            headers =>
            {
                headers.Add("x-api-key", apiKey);
                headers.Add("anthropic-version", AnthropicVersion);
            },
            missingKeyMessage: "No Anthropic API key supplied.",
            hasCredential: !string.IsNullOrEmpty(apiKey),
            ct);

    /// <inheritdoc/>
    public IChatClient? CreateChatClient(AiProviderConfig config)
    {
        if (config.ChatModelId is null || config.ApiKey is null) return null;

        // AnthropicClient.Messages implements IChatClient explicitly; the
        //  requested model is supplied per-call via ChatOptions.ModelId.
        var client = new AnthropicClient(new APIAuthentication(config.ApiKey));
        return new AnthropicChatClient(client, config.ChatModelId);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Always <c>null</c> — Anthropic exposes no embeddings endpoint.
    /// </remarks>
    public IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(
        AiProviderConfig config) => null;

    /// <summary>
    /// Thin <see cref="IChatClient"/> wrapper that pins the configured model
    /// id onto every request and owns the underlying
    /// <see cref="AnthropicClient"/>'s lifetime.
    /// </summary>
    /// <remarks>
    /// Needed because <c>AnthropicClient.Messages</c> takes the model from
    ///  <see cref="ChatOptions.ModelId"/> per call, whereas AiNET selects the
    ///  model once when the session is built.
    /// </remarks>
    private sealed class AnthropicChatClient(AnthropicClient client, string modelId) : IChatClient
    {
        private readonly IChatClient _inner = client.Messages;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            _inner.GetResponseAsync(messages, WithModel(options), cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            _inner.GetStreamingResponseAsync(messages, WithModel(options), cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            _inner.GetService(serviceType, serviceKey);

        public void Dispose() => client.Dispose();

        // Applies the session's model id unless the caller specified one.
        private ChatOptions WithModel(ChatOptions? options)
        {
            if (options is null)
                return new ChatOptions { ModelId = modelId };

            if (string.IsNullOrEmpty(options.ModelId))
            {
                options = options.Clone();
                options.ModelId = modelId;
            }

            return options;
        }
    }
}
