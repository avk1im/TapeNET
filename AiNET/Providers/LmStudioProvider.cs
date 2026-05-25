using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.AI;

using OpenAI;

namespace AiNET.Providers;

/// <summary>
/// Provider adapter for a locally running <b>LM Studio</b> instance.
/// Probes <c>/v1/models</c> and creates an OpenAI-compatible
/// <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// <b>Phase 1 status:</b> Stub — probe and client construction follow the
/// same OpenAI-compatible pattern as <see cref="OllamaProvider"/> and will
/// be fully exercised once LM Studio integration tests are authored in a
/// later phase.
/// </remarks>
public sealed class LmStudioProvider : IAiProvider
{
    private static readonly AiProviderDescriptor _descriptor = new(
        Kind:            AiProviderKind.LmStudio,
        Location:        AiProviderLocation.Local,
        DisplayName:     "LM Studio",
        DefaultEndpoint: new Uri("http://localhost:1234"),
        RequiresApiKey:  false,
        Capabilities:    AiCapabilities.Chat | AiCapabilities.Embeddings | AiCapabilities.Tools);

    /// <inheritdoc/>
    public AiProviderDescriptor Descriptor => _descriptor;

    /// <inheritdoc/>
    public async Task<AiProviderProbeResult> ProbeAsync(
        Uri endpoint, string? apiKey, CancellationToken ct)
    {
        var modelsUri = new Uri(endpoint, "/v1/models");
        var sw = Stopwatch.StartNew();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync(modelsUri, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
                return Unhealthy(endpoint, sw.Elapsed,
                    $"HTTP {(int)response.StatusCode} from {modelsUri}");

            // OpenAI-compat: { "data": [ { "id": "...", ... } ] }
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            List<string> models = [];
            if (doc.RootElement.TryGetProperty("data", out var dataEl))
            {
                models = [.. dataEl.EnumerateArray()
                    .Select(m => m.GetProperty("id").GetString()!)];
            }

            return new AiProviderProbeResult(
                _descriptor, endpoint, true, models, models, sw.Elapsed, null);
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
        var options    = new OpenAIClientOptions { Endpoint = config.Endpoint };
        return new OpenAI.Embeddings.EmbeddingClient(config.EmbeddingModelId, credential, options)
            .AsIEmbeddingGenerator();
    }

    private static AiProviderProbeResult Unhealthy(Uri ep, TimeSpan lat, string err) =>
        new(_descriptor, ep, false, [], [], lat, err);
}
