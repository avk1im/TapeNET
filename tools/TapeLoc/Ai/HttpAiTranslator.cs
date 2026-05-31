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

    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public HttpAiTranslator(ProviderOptions provider)
    {
        _provider = provider;

        var key = Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AiTranslatorException(
                $"API key not found. Set the '{provider.ApiKeyEnvVar}' environment variable.");
        }
        _apiKey = key;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
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

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(_provider.Endpoint, content, ct)
                    .ConfigureAwait(false);

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
