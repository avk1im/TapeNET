using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiNET.Internal;

/// <summary>
/// Shared helper that performs a real, authenticated <c>GET {base}/models</c>
/// call against an OpenAI-style endpoint and turns the response into an
/// <see cref="AiProviderProbeResult"/>.
/// </summary>
/// <remarks>
/// Used by the cloud provider adapters (OpenAI, Anthropic, Azure OpenAI) so
///  that probing performs genuine credential validation and model discovery
///  rather than returning a synthetic result. Distinguishes HTTP 401/403
///  (credentials rejected — worth re-prompting) from transport failures.
/// </remarks>
internal static class OpenAiModelProbe
{
    /// <summary>Default per-probe HTTP timeout.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Heuristic classifier deciding whether a discovered model id can be used
    /// for embeddings. Providers pass their own when the naming differs.
    /// </summary>
    internal static bool LooksLikeEmbeddingModel(string modelId) =>
        modelId.Contains("embed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Calls the model-listing endpoint and returns a populated probe result.
    /// </summary>
    /// <param name="descriptor">Descriptor to embed in the result.</param>
    /// <param name="endpoint">Endpoint reported back in the result.</param>
    /// <param name="modelsUri">Absolute URI of the model-listing resource.</param>
    /// <param name="applyAuth">
    /// Applies provider-specific authentication headers to the request.
    /// </param>
    /// <param name="missingKeyMessage">
    /// Error text used when <paramref name="hasCredential"/> is <c>false</c>.
    /// </param>
    /// <param name="hasCredential">
    /// Whether a credential is present; when <c>false</c> the network call is
    ///  skipped and an unhealthy auth-failure result is returned immediately.
    /// </param>
    /// <param name="httpClientFactory">
    /// Optional factory so unit tests can inject a fake handler.
    /// </param>
    internal static async Task<AiProviderProbeResult> ProbeAsync(
        AiProviderDescriptor descriptor,
        Uri endpoint,
        Uri modelsUri,
        Action<HttpRequestHeaders> applyAuth,
        string missingKeyMessage,
        bool hasCredential,
        CancellationToken ct,
        Func<HttpClient>? httpClientFactory = null)
    {
        if (!hasCredential)
            return Unhealthy(descriptor, endpoint, TimeSpan.Zero, missingKeyMessage,
                             isAuthFailure: true);

        var sw = Stopwatch.StartNew();

        try
        {
            using var http = httpClientFactory?.Invoke()
                             ?? new HttpClient { Timeout = DefaultTimeout };

            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUri);
            applyAuth(request.Headers);

            using var response = await http.SendAsync(request, ct);
            sw.Stop();

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Unhealthy(descriptor, endpoint, sw.Elapsed,
                    $"Credentials rejected by {descriptor.DisplayName} ({(int)response.StatusCode}).",
                    isAuthFailure: true);
            }

            // HTTP 410 Gone means the service itself is retired — re-prompting for
            //  a key cannot help, so say so plainly rather than blaming the token.
            if (response.StatusCode == HttpStatusCode.Gone)
            {
                return Unhealthy(descriptor, endpoint, sw.Elapsed,
                    $"{descriptor.DisplayName} is no longer available at this endpoint " +
                    "(HTTP 410 Gone) — the service appears to have been retired.",
                    isAuthFailure: false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Unhealthy(descriptor, endpoint, sw.Elapsed,
                    $"{descriptor.DisplayName} returned HTTP {(int)response.StatusCode}.",
                    isAuthFailure: false);
            }

            var models = await ParseModelIdsAsync(response, ct);

            // Split the flat model list into chat vs embedding candidates.
            //  Providers without embedding support declare it in Capabilities,
            //  so an empty embedding list there is expected and harmless.
            var embeddingModels = models.Where(LooksLikeEmbeddingModel).ToList();
            var chatModels      = models.Where(m => !LooksLikeEmbeddingModel(m)).ToList();

            return new AiProviderProbeResult(
                descriptor, endpoint, true, chatModels, embeddingModels, sw.Elapsed, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                        or OperationCanceledException or JsonException
                                        or UriFormatException)
        {
            sw.Stop();
            return Unhealthy(descriptor, endpoint, sw.Elapsed, ex.Message, isAuthFailure: false);
        }
    }

    /// <summary>
    /// Extracts model identifiers from an OpenAI-style <c>{ "data": [ { "id": … } ] }</c>
    /// payload, tolerating both <c>id</c> and Anthropic's equivalent field.
    /// </summary>
    private static async Task<List<string>> ParseModelIdsAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // OpenAI-style payloads wrap the list in "data"; some services return
        //  a bare array instead. Accept both shapes.
        JsonElement data;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            data = doc.RootElement;
        }
        else if (!doc.RootElement.TryGetProperty("data", out data) ||
                 data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> ids = [];
        foreach (var item in data.EnumerateArray())
        {
            // "id" is the OpenAI/Anthropic field; "name" is accepted as a
            //  fallback for services that label their models that way.
            if ((item.TryGetProperty("id", out var idEl) ||
                 item.TryGetProperty("name", out idEl)) &&
                idEl.GetString() is { Length: > 0 } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    internal static AiProviderProbeResult Unhealthy(
        AiProviderDescriptor descriptor, Uri endpoint, TimeSpan latency,
        string error, bool isAuthFailure) =>
        new(descriptor, endpoint, false, [], [], latency, error)
        {
            IsAuthFailure = isAuthFailure
        };
}
