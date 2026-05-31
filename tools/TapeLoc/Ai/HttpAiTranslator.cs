using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using TapeLoc.Configuration;

namespace TapeLoc.Ai;

// OpenAI-compatible chat-completions client (docs/Design-TapeLoc.md §7).
//  Reads the API key from the configured environment variable, applies
//  exponential backoff on transient/429/5xx responses, and returns the model's
//  transformed file content.

internal sealed class HttpAiTranslator : IAiTranslator, IDisposable
{
    private readonly ProviderOptions _provider;
    private readonly string _apiKey;
    private readonly HttpClient _http;

    // Candidate endpoints tried in order. Derived from the configured endpoint by
    //  swapping the API-version path segment (e.g. OpenVINO Model Server uses /v3
    //  where OpenAI uses /v1). The first candidate that does not 404 is cached in
    //  _resolvedEndpoint so subsequent chunks skip the probing.
    private readonly IReadOnlyList<string> _endpoints;
    private string? _resolvedEndpoint;

    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public HttpAiTranslator(ProviderOptions provider)
    {
        _provider = provider;

        // Keyless local/LAN providers (Ollama, LM Studio, OpenVINO Model Server,
        //  vLLM, …) accept requests without a bearer token. Only require — and
        //  attach — a key when the provider asks for one.
        var key = Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar);
        if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(key))
        {
            throw new AiTranslatorException(
                $"API key not found. Set the '{provider.ApiKeyEnvVar}' environment variable, " +
                $"or set provider.requiresApiKey=false for a keyless local provider.");
        }
        _apiKey = key ?? string.Empty;

        _endpoints = BuildEndpointCandidates(provider.Endpoint);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds) };
        if (!string.IsNullOrEmpty(_apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    // Produces the ordered set of endpoints to probe: the configured URL first,
    //  then the same URL with its /v1 <-> /v3 version segment swapped. Duplicates
    //  are removed and the original is always tried first.
    internal static IReadOnlyList<string> BuildEndpointCandidates(string endpoint)
    {
        var candidates = new List<string> { endpoint };

        // Swap a /vN/ segment to the alternate version. Matches the segment
        //  bounded by slashes so we don't touch host names or query strings.
        foreach (var (from, to) in new[] { ("/v1/", "/v3/"), ("/v3/", "/v1/") })
        {
            if (endpoint.Contains(from, StringComparison.Ordinal))
            {
                var swapped = endpoint.Replace(from, to, StringComparison.Ordinal);
                if (!candidates.Contains(swapped, StringComparer.Ordinal))
                    candidates.Add(swapped);
            }
        }

        return candidates;
    }

    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        var payload = new ChatRequest
        {
            Model = _provider.Model,
            Temperature = _provider.Temperature,
            Messages =
            [
                new ChatMessage { Role = "system", Content = request.SystemPrompt },
                new ChatMessage { Role = "user", Content = request.Content },
            ],
        };

        var body = JsonSerializer.Serialize(payload, s_json);

        // If a working endpoint was already resolved, use it directly. Otherwise
        //  probe each candidate, advancing only when one responds with 404
        //  (wrong API-version path); any other outcome is final for that call.
        if (_resolvedEndpoint is not null)
            return await PostAsync(_resolvedEndpoint, body, ct).ConfigureAwait(false);

        AiTranslatorException? lastNotFound = null;
        for (int i = 0; i < _endpoints.Count; i++)
        {
            var endpoint = _endpoints[i];
            var isLast = i == _endpoints.Count - 1;
            try
            {
                var result = await PostAsync(endpoint, body, ct).ConfigureAwait(false);
                _resolvedEndpoint = endpoint; // cache the winner for later chunks
                return result;
            }
            catch (EndpointNotFoundException ex)
            {
                // This version path is wrong — try the next candidate, unless it
                //  was the last one, in which case surface a helpful message.
                lastNotFound = new AiTranslatorException(
                    isLast
                        ? $"No working endpoint found. Tried: {string.Join(", ", _endpoints)}. Last response: {ex.Message}"
                        : ex.Message);
                if (isLast)
                    throw lastNotFound;
            }
        }

        // Unreachable in practice (loop either returns or throws on the last item).
        throw lastNotFound ?? new AiTranslatorException("No endpoint candidates were configured.");
    }

    // Posts to a single endpoint with exponential backoff on transient/429/5xx.
    //  Throws EndpointNotFoundException on 404 so the caller can fall back to the
    //  alternate API-version path.
    private async Task<string> PostAsync(string endpoint, string body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(endpoint, content, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    var notFoundBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw new EndpointNotFoundException(
                        $"{endpoint} returned 404 NotFound: {Truncate(notFoundBody)}");
                }

                if (IsTransient(response.StatusCode) && attempt < _provider.MaxRetries)
                {
                    await BackoffAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new AiTranslatorException(
                        $"Provider returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(json)}");
                }

                var parsed = JsonSerializer.Deserialize<ChatResponse>(json, s_json);
                var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
                if (string.IsNullOrEmpty(text))
                    throw new AiTranslatorException("Provider returned an empty completion.");

                return StripCodeFences(text);
            }
            catch (HttpRequestException ex) when (attempt < _provider.MaxRetries)
            {
                await BackoffAsync(attempt, ct).ConfigureAwait(false);
                _ = ex; // retried
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < _provider.MaxRetries)
            {
                // Request timeout (not user cancellation) — retry.
                await BackoffAsync(attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || (int)code >= 500;

    private static Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        return Task.Delay(delay, ct);
    }

    // Defensive: some models wrap output in ```...``` despite instructions.
    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        var withoutOpen = trimmed[(firstNewline + 1)..];
        var lastFence = withoutOpen.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? withoutOpen[..lastFence].TrimEnd('\n') : withoutOpen;
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";

    public void Dispose() => _http.Dispose();

    // Internal signal that an endpoint candidate responded 404 (wrong API-version
    //  path), so the caller should try the next candidate. Never surfaced to users.
    private sealed class EndpointNotFoundException(string message) : Exception(message);

    // --- Minimal OpenAI-compatible DTOs ---

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = [];
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")] public List<ChatChoice>? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
}
