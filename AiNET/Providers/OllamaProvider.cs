using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.AI;

using OpenAI;

namespace AiNET.Providers;

/// <summary>
/// Provider adapter for a locally running <b>Ollama</b> instance.
/// Probes <c>/api/tags</c> to discover available models and creates
/// an OpenAI-compatible <see cref="IChatClient"/> pointing at
/// <c>/v1/chat/completions</c>.
/// </summary>
public sealed class OllamaProvider : IAiProvider
{
    // ── Optional test handler (injected by unit tests) ─────────────────────
    private HttpMessageHandler? _testHandler;

    /// <summary>
    /// Injects a fake <see cref="HttpMessageHandler"/> for unit tests.
    /// Not for production use.
    /// </summary>
    internal void SetTestHandler(HttpMessageHandler handler) => _testHandler = handler;

    // ── Static descriptor ──────────────────────────────────────────────────
    private static readonly AiProviderDescriptor _descriptor = new(
        Kind:             AiProviderKind.Ollama,
        Location:         AiProviderLocation.Local,
        DisplayName:      "Ollama",
        DefaultEndpoint:  new Uri("http://localhost:11434"),
        RequiresApiKey:   false,
        Capabilities:     AiCapabilities.Chat | AiCapabilities.Embeddings | AiCapabilities.Tools);

    /// <inheritdoc/>
    public AiProviderDescriptor Descriptor => _descriptor;

    // ── Probe ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<AiProviderProbeResult> ProbeAsync(
        Uri endpoint, string? apiKey, CancellationToken ct)
    {
        // Ollama's model list lives at /api/tags (not under /v1)
        var tagsUri = new Uri(endpoint, "/api/tags");
        var sw = Stopwatch.StartNew();

        try
        {
            using var http = CreateProbeHttpClient();
            var response = await http.GetAsync(tagsUri, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return Unhealthy(endpoint, sw.Elapsed,
                    $"HTTP {(int)response.StatusCode} from {tagsUri}");
            }

            // { "models": [ { "name": "llama3:latest", ... }, ... ] }
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            List<string> chatModels = [];
            if (doc.RootElement.TryGetProperty("models", out var modelsEl))
            {
                chatModels = [.. modelsEl.EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString()!)];
            }

            return new AiProviderProbeResult(
                Descriptor:                 _descriptor,
                Endpoint:                   endpoint,
                IsHealthy:                  true,
                DiscoveredChatModels:       chatModels,
                DiscoveredEmbeddingModels:  chatModels, // Ollama uses same models for embeddings
                Latency:                    sw.Elapsed,
                ErrorMessage:               null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                        or OperationCanceledException or JsonException)
        {
            sw.Stop();
            return Unhealthy(endpoint, sw.Elapsed, ex.Message);
        }
    }

    // ── Client construction ────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Creates an OpenAI SDK <see cref="IChatClient"/> pointed at the
    /// Ollama OpenAI-compatible endpoint (<c>&lt;base&gt;/v1</c>).
    /// A dummy API key is used because Ollama does not require authentication.
    /// </remarks>
    public IChatClient? CreateChatClient(AiProviderConfig config)
    {
        if (config.ChatModelId is null)
            return null;

        var v1Endpoint = new Uri(config.Endpoint, "/v1");
        var credential  = new ApiKeyCredential(config.ApiKey ?? "local");
        var options     = new OpenAIClientOptions { Endpoint = v1Endpoint };
        return new OpenAI.Chat.ChatClient(config.ChatModelId, credential, options)
            .AsIChatClient();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Embedding support via the Ollama OpenAI-compatible
    /// <c>/v1/embeddings</c> endpoint is provided using the same SDK path.
    /// Returns <c>null</c> when no embedding model ID is specified.
    /// </remarks>
    public IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(
        AiProviderConfig config)
    {
        if (config.EmbeddingModelId is null)
            return null;

        var v1Endpoint = new Uri(config.Endpoint, "/v1");
        var credential  = new ApiKeyCredential(config.ApiKey ?? "local");
        var options     = new OpenAIClientOptions { Endpoint = v1Endpoint };
        return new OpenAI.Embeddings.EmbeddingClient(config.EmbeddingModelId, credential, options)
            .AsIEmbeddingGenerator();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static AiProviderProbeResult Unhealthy(Uri endpoint, TimeSpan latency, string error) =>
        new(_descriptor, endpoint, false, [], [], latency, error);

    private HttpClient CreateProbeHttpClient() =>
        _testHandler is not null
            ? new HttpClient(_testHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) }
            : new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
}
