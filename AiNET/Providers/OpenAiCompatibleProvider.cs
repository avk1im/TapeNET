using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.AI;

using OpenAI;

namespace AiNET.Providers;

/// <summary>
/// Provider adapter for any <b>generic OpenAI-compatible</b> endpoint
/// (LAN gateways, vLLM, text-generation-webui, etc.).
/// </summary>
/// <remarks>
/// <b>Phase 1 status:</b> Functional stub — probe tries several well-known
/// API-version paths and embeds the winning version base into the returned
/// endpoint (e.g. <c>http://host/v3</c>) so that
/// <see cref="CreateChatClient"/> can route requests correctly without any
/// additional configuration.
/// </remarks>
public sealed class OpenAiCompatibleProvider : IAiProvider
{
    private static readonly AiProviderDescriptor _descriptor = new(
        Kind:            AiProviderKind.OpenAiCompatible,
        Location:        AiProviderLocation.LocalNetwork,
        DisplayName:     "OpenAI-compatible (LAN)",
        DefaultEndpoint: null,
        RequiresApiKey:  false,
        Capabilities:    AiCapabilities.Chat | AiCapabilities.Embeddings | AiCapabilities.Tools);

    /// <inheritdoc/>
    public AiProviderDescriptor Descriptor => _descriptor;

    // Known model-list paths tried in order; first success wins.
    // The associated version-base path is embedded into the returned endpoint
    //  so CreateChatClient/CreateEmbeddingGenerator can pass it verbatim to the
    //  OpenAI SDK without any extra configuration.
    // /v1/models  — standard OpenAI / Ollama / LM Studio
    // /v3/models  — OpenVINO Model Server (OVMS)
    private static readonly (string ModelsPath, string VersionBase)[] _modelsPaths =
        [("/v1/models", "/v1"), ("/v3/models", "/v3")];

    /// <inheritdoc/>
    public async Task<AiProviderProbeResult> ProbeAsync(
        Uri endpoint, string? apiKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrEmpty(apiKey))
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            foreach (var (modelsPath, versionBase) in _modelsPaths)
            {
                var modelsUri = new Uri(endpoint, modelsPath);
                var response = await http.GetAsync(modelsUri, ct);

                if (!response.IsSuccessStatusCode)
                    continue;   // try next path

                using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

                List<string> models = [];
                if (doc.RootElement.TryGetProperty("data", out var dataEl))
                {
                    models = [.. dataEl.EnumerateArray()
                        .Select(m => m.GetProperty("id").GetString()!)];
                }

                sw.Stop();
                // Embed the version base (e.g. /v1 or /v3) into the endpoint so
                //  CreateChatClient passes it verbatim to OpenAIClientOptions.Endpoint.
                var versionedEndpoint = new Uri(endpoint, versionBase);
                return new AiProviderProbeResult(
                    _descriptor, versionedEndpoint, true, models, models, sw.Elapsed, null);
            }

            // All paths returned non-success
            sw.Stop();
            return Unhealthy(endpoint, sw.Elapsed,
                $"No model-list endpoint responded at {endpoint}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                        or OperationCanceledException or JsonException)
        {
            sw.Stop();
            return Unhealthy(endpoint, sw.Elapsed, ex.Message);
        }
    }

    /// <inheritdoc/>
    public IChatClient? CreateChatClient(AiProviderConfig config)
    {
        if (config.ChatModelId is null) return null;
        var credential = new ApiKeyCredential(config.ApiKey ?? "local");
        // config.Endpoint already contains the version base (e.g. /v1 or /v3) as
        //  embedded by ProbeAsync, so the OpenAI SDK appends /chat/completions correctly.
        var options    = new OpenAIClientOptions { Endpoint = config.Endpoint };
        return new OpenAI.Chat.ChatClient(config.ChatModelId, credential, options)
            .AsIChatClient();
    }

    /// <inheritdoc/>
    public IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(
        AiProviderConfig config)
    {
        if (config.EmbeddingModelId is null) return null;
        var credential = new ApiKeyCredential(config.ApiKey ?? "local");
        // config.Endpoint already contains the version base — see CreateChatClient.
        var options    = new OpenAIClientOptions { Endpoint = config.Endpoint };
        return new OpenAI.Embeddings.EmbeddingClient(config.EmbeddingModelId, credential, options)
            .AsIEmbeddingGenerator();
    }

    private static AiProviderProbeResult Unhealthy(Uri ep, TimeSpan lat, string err) =>
        new(_descriptor, ep, false, [], [], lat, err);
}
