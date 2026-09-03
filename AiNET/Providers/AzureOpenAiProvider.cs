using System.ClientModel;

using AiNET.Internal;

using Azure.AI.OpenAI;

using Microsoft.Extensions.AI;

namespace AiNET.Providers;

/// <summary>
/// Provider adapter for <b>Azure OpenAI Service</b> (user-supplied
/// endpoint, e.g. <c>https://myresource.openai.azure.com/</c>).
/// </summary>
/// <remarks>
/// Azure differs from stock OpenAI in three ways that this adapter handles:
/// <list type="bullet">
///  <item>authentication uses the <c>api-key</c> header, not a Bearer token;</item>
///  <item>every request carries an <c>api-version</c> query parameter;</item>
///  <item>the "model id" is really a <i>deployment name</i> chosen by whoever
///   provisioned the resource, so discovered names are deployment names.</item>
/// </list>
/// Probing lists the resource's deployments, which both validates the key and
///  populates the picker with names that actually exist on the resource.
/// </remarks>
public sealed class AzureOpenAiProvider : IAiProvider
{
    /// <summary>
    /// Data-plane API version used for the deployment-listing probe.
    /// </summary>
    private const string ProbeApiVersion = "2024-10-21";

    /// <summary>
    /// Key in <see cref="AiProviderConfig.Options"/> overriding the Azure
    /// data-plane API version (value must name an
    /// <see cref="AzureOpenAIClientOptions.ServiceVersion"/> member).
    /// </summary>
    public const string ApiVersionOption = "azure.apiVersion";

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
    /// Lists deployments via <c>/openai/deployments?api-version=…</c>. A 401/403
    ///  is reported as an auth failure so the caller can re-prompt for the key.
    /// </remarks>
    public Task<AiProviderProbeResult> ProbeAsync(
        Uri endpoint, string? apiKey, CancellationToken ct) =>
        OpenAiModelProbe.ProbeAsync(
            _descriptor,
            endpoint,
            new Uri(endpoint, $"/openai/deployments?api-version={ProbeApiVersion}"),
            headers => headers.Add("api-key", apiKey),
            missingKeyMessage: "No Azure OpenAI API key supplied.",
            hasCredential: !string.IsNullOrEmpty(apiKey),
            ct);

    /// <inheritdoc/>
    public IChatClient? CreateChatClient(AiProviderConfig config)
    {
        if (config.ChatModelId is null || config.ApiKey is null) return null;
        // ChatModelId carries the Azure *deployment name*.
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

    // Builds the Azure client, which applies the api-key header, the
    //  api-version query parameter, and /openai/deployments/{name}/… routing.
    private static AzureOpenAIClient CreateClient(AiProviderConfig config)
    {
        var options = new AzureOpenAIClientOptions(ResolveApiVersion(config));
        return new AzureOpenAIClient(
            config.Endpoint, new ApiKeyCredential(config.ApiKey!), options);
    }

    /// <summary>
    /// Maps the configured API-version string onto the SDK's supported
    /// service-version enum, falling back to a known-good default when the
    /// value is absent or unrecognised.
    /// </summary>
    private static AzureOpenAIClientOptions.ServiceVersion ResolveApiVersion(
        AiProviderConfig config)
    {
        if (config.Options is not null &&
            config.Options.TryGetValue(ApiVersionOption, out var raw) &&
            Enum.TryParse<AzureOpenAIClientOptions.ServiceVersion>(raw, out var parsed))
        {
            return parsed;
        }

        return AzureOpenAIClientOptions.ServiceVersion.V2024_10_21;
    }
}
