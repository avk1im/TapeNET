using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using OpenAI;
using OpenAI.Chat;

namespace FclAiNET;

/// <summary>
/// Discovers and creates <see cref="IChatClient"/> instances for AI-assisted
/// FCL generation. Tries local providers first (Ollama, LM Studio), then
/// environment-variable-based providers (GitHub Models, OpenAI), then asks
/// the user for cloud credentials via <see cref="IFclAiInteraction"/>.
/// </summary>
public sealed class FclAiProviderFactory(IFclAiInteraction interaction, ILogger<FclAiProviderFactory> logger)
{
    // ── Well-known local endpoints ──────────────────────
    private static readonly Uri OllamaEndpoint = new("http://localhost:11434/v1");
    private static readonly Uri LmStudioEndpoint = new("http://localhost:1234/v1");

    // ── GitHub Models endpoint ──────────────────────────
    // The OpenAI SDK appends "/chat/completions" to the base URI,
    //  so we include "/inference" to produce the correct full path:
    //  https://models.github.ai/inference/chat/completions
    private static readonly Uri GitHubModelsEndpoint = new("https://models.github.ai/inference");

    // ── Probe / health-check paths (relative to base, not /v1) ──
    private static readonly Uri OllamaTagsUri = new("http://localhost:11434/api/tags");
    private static readonly Uri LmStudioModelsUri = new("http://localhost:1234/v1/models");

    /// <summary>Timeout for local provider health checks.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    // ── Well-known environment variable names ────────────

    /// <summary>GitHub personal access token (used for GitHub Models marketplace).</summary>
    public const string EnvGitHubToken = "GITHUB_TOKEN";

    /// <summary>OpenAI API key.</summary>
    public const string EnvOpenAiApiKey = "OPENAI_API_KEY";

    /// <summary>Azure OpenAI API key.</summary>
    public const string EnvAzureOpenAiApiKey = "AZURE_OPENAI_API_KEY";

    /// <summary>Azure OpenAI endpoint URL.</summary>
    public const string EnvAzureOpenAiEndpoint = "AZURE_OPENAI_ENDPOINT";

    /// <summary>Default model used for GitHub Models when auto-detected via env var.</summary>
    private const string GitHubModelsDefaultModel = "gpt-4o-mini";

    /// <summary>Default model used for OpenAI when auto-detected via env var.</summary>
    private const string OpenAiDefaultModel = "gpt-4o-mini";

    private readonly IFclAiInteraction _interaction = interaction;
    private readonly ILogger _logger = logger;

    // ── Local model fallback state ──────────────────────
    //  Populated during CreateAsync(); TryNextLocalModel() iterates through them.
    private readonly record struct LocalModel(Uri Endpoint, string ProviderName, string ModelName);
    private readonly List<LocalModel> _localModels = [];
    private int _localModelIndex = -1;

    // ─────────────────────────────────────────────────────
    //  Public entry point
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to create a working <see cref="IChatClient"/>.
    /// <list type="number">
    ///   <item>Probes Ollama at <c>localhost:11434</c>.</item>
    ///   <item>Probes LM Studio at <c>localhost:1234</c>.</item>
    ///   <item>Checks well-known environment variables
    ///         (<c>GITHUB_TOKEN</c>, <c>OPENAI_API_KEY</c>).</item>
    ///   <item>If all fail, asks the user to select a cloud provider
    ///         via <see cref="IFclAiInteraction.ChooseCloudProviderAsync"/>.</item>
    /// </list>
    /// </summary>
    /// <returns>
    /// A ready-to-use <see cref="IChatClient"/>, or <c>null</c> if no provider
    /// could be established (user cancelled or all attempts failed).
    /// </returns>
    public async Task<IChatClient?> CreateAsync(CancellationToken cancellationToken = default)
    {
        // ── Try local providers ─────────────────────────
        var localResult = await TryLocalProvidersAsync(cancellationToken);
        if (localResult is not null)
            return localResult;

        // ── Try environment-variable-based providers ────
        var envResult = TryEnvironmentProviders();
        if (envResult is not null)
            return envResult;

        // ── Fall back to interactive selection ───────────
        var choice = await _interaction.ChooseCloudProviderAsync(cancellationToken);
        if (choice is null)
        {
            _logger.LogInformation("User cancelled cloud provider selection — AI assistance unavailable.");
            return null;
        }

        var cloudClient = CreateCloudClient(choice);
        if (cloudClient is null)
            return null;

        _logger.LogInformation("Using cloud provider {Provider} with model {Model}.",
            choice.Provider, choice.ModelId);
        return cloudClient;
    }

    /// <summary>
    /// Attempts to create a client with the next available local model.
    /// Call this after <see cref="CreateAsync"/> when the current model
    /// fails the smoke test. Returns <c>null</c> when all local models
    /// have been exhausted.
    /// </summary>
    /// <returns>
    /// A new <see cref="IChatClient"/> for the next model, or <c>null</c>
    ///  if no more models are available.
    /// </returns>
    public IChatClient? TryNextLocalModel()
    {
        _localModelIndex++;
        if (_localModelIndex >= _localModels.Count)
            return null;

        var next = _localModels[_localModelIndex];
        _interaction.OnProviderStatus(next.ProviderName, available: true, modelName: next.ModelName);
        _logger.LogInformation("Trying {Provider} with model {Model}.", next.ProviderName, next.ModelName);
        return CreateOpenAiCompatibleClient(next.Endpoint, next.ModelName);
    }

    // ─────────────────────────────────────────────────────
    //  Local provider probing
    // ─────────────────────────────────────────────────────

    private async Task<IChatClient?> TryLocalProvidersAsync(CancellationToken cancellationToken)
    {
        // Ollama — discover all available models
        var ollamaModels = await ProbeOllamaAsync(cancellationToken);
        if (ollamaModels.Length > 0)
        {
            foreach (var model in ollamaModels)
                _localModels.Add(new(OllamaEndpoint, "Ollama", model));

            _interaction.OnProviderStatus("Ollama", available: true, modelName: ollamaModels[0]);
            _localModelIndex = 0;
            _logger.LogInformation("Using Ollama with model {Model} ({Count} model(s) available).",
                ollamaModels[0], ollamaModels.Length);
            return CreateOpenAiCompatibleClient(OllamaEndpoint, ollamaModels[0]);
        }
        _interaction.OnProviderStatus("Ollama", available: false);

        // LM Studio — discover all available models
        var lmModels = await ProbeLmStudioAsync(cancellationToken);
        if (lmModels.Length > 0)
        {
            foreach (var model in lmModels)
                _localModels.Add(new(LmStudioEndpoint, "LM Studio", model));

            _interaction.OnProviderStatus("LM Studio", available: true, modelName: lmModels[0]);
            _localModelIndex = 0;
            _logger.LogInformation("Using LM Studio with model {Model} ({Count} model(s) available).",
                lmModels[0], lmModels.Length);
            return CreateOpenAiCompatibleClient(LmStudioEndpoint, lmModels[0]);
        }
        _interaction.OnProviderStatus("LM Studio", available: false);

        return null;
    }

    /// <summary>
    /// Probes the Ollama API at <c>localhost:11434</c> and returns the names
    /// of all available models, or an empty array if Ollama is not running
    /// or has no models loaded.
    /// </summary>
    /// <remarks>
    /// Ollama's <c>/api/tags</c> returns models sorted by <c>modified_at</c>
    ///  descending — most recently used/pulled model first.
    /// </remarks>
    private async Task<string[]> ProbeOllamaAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = CreateProbeHttpClient();
            var response = await http.GetAsync(OllamaTagsUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            // Ollama's /api/tags returns { "models": [ { "name": "...", ... }, ... ] }
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

            if (doc.RootElement.TryGetProperty("models", out var models) &&
                models.GetArrayLength() > 0)
            {
                var modelNames = models.EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString()!)
                    .ToArray();
                _logger.LogDebug("Ollama probe: found {Count} model(s): {Models}.",
                    modelNames.Length, string.Join(", ", modelNames));
                return modelNames;
            }

            _logger.LogDebug("Ollama is running but has no models.");
            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "Ollama probe failed.");
            return [];
        }
    }

    /// <summary>
    /// Probes the LM Studio API at <c>localhost:1234</c> and returns the names
    /// of all available models, or an empty array if LM Studio is not running.
    /// </summary>
    private async Task<string[]> ProbeLmStudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = CreateProbeHttpClient();
            var response = await http.GetAsync(LmStudioModelsUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            // LM Studio's /v1/models returns OpenAI-compatible { "data": [ { "id": "...", ... } ] }
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.GetArrayLength() > 0)
            {
                var modelIds = data.EnumerateArray()
                    .Select(m => m.GetProperty("id").GetString()!)
                    .ToArray();
                _logger.LogDebug("LM Studio probe: found {Count} model(s): {Models}.",
                    modelIds.Length, string.Join(", ", modelIds));
                return modelIds;
            }

            _logger.LogDebug("LM Studio is running but has no models loaded.");
            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "LM Studio probe failed.");
            return [];
        }
    }

    // ─────────────────────────────────────────────────────
    //  Environment variable auto-detection
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Checks well-known environment variables and creates a client
    /// if credentials are found. Checks in order:
    /// <c>GITHUB_TOKEN</c> → GitHub Models,
    /// <c>OPENAI_API_KEY</c> → OpenAI.
    /// </summary>
    private IChatClient? TryEnvironmentProviders()
    {
        // GitHub Models via GITHUB_TOKEN
        var githubToken = Environment.GetEnvironmentVariable(EnvGitHubToken);
        if (!string.IsNullOrEmpty(githubToken))
        {
            _interaction.OnProviderStatus($"GitHub Models (${EnvGitHubToken})", available: true,
                modelName: GitHubModelsDefaultModel);
            _logger.LogInformation(
                "Found {EnvVar} — using GitHub Models with default model {Model}.",
                EnvGitHubToken, GitHubModelsDefaultModel);
            return CreateOpenAiCompatibleClient(
                GitHubModelsEndpoint, GitHubModelsDefaultModel, githubToken);
        }

        // OpenAI via OPENAI_API_KEY
        var openAiKey = Environment.GetEnvironmentVariable(EnvOpenAiApiKey);
        if (!string.IsNullOrEmpty(openAiKey))
        {
            _interaction.OnProviderStatus($"OpenAI (${EnvOpenAiApiKey})", available: true,
                modelName: OpenAiDefaultModel);
            _logger.LogInformation(
                "Found {EnvVar} — using OpenAI with default model {Model}.",
                EnvOpenAiApiKey, OpenAiDefaultModel);
            return new ChatClient(OpenAiDefaultModel,
                new ApiKeyCredential(openAiKey)).AsIChatClient();
        }

        return null;
    }

    // ─────────────────────────────────────────────────────
    //  Client construction
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="IChatClient"/> pointing at an OpenAI-compatible
    /// endpoint (local: Ollama / LM Studio, or remote: GitHub Models).
    /// </summary>
    /// <param name="endpoint">The base URI of the OpenAI-compatible API.</param>
    /// <param name="modelId">Model name or ID.</param>
    /// <param name="apiKey">
    /// API key or token. Pass <c>null</c> for local providers that don't
    ///  require authentication (a placeholder value is used internally).
    /// </param>
    private static IChatClient CreateOpenAiCompatibleClient(
        Uri endpoint, string modelId, string? apiKey = null)
    {
        var credential = new ApiKeyCredential(apiKey ?? "local");
        var options = new OpenAIClientOptions { Endpoint = endpoint };
        var chatClient = new ChatClient(modelId, credential, options);
        return chatClient.AsIChatClient();
    }

    /// <summary>
    /// Creates an <see cref="IChatClient"/> for a cloud or remote provider
    /// (OpenAI, Azure OpenAI, or GitHub Models).
    /// </summary>
    private IChatClient? CreateCloudClient(FclAiProviderChoice choice)
    {
        try
        {
            var credential = new ApiKeyCredential(choice.ApiKey);

            return choice.Provider switch
            {
                FclAiProviderType.GitHubModels =>
                    CreateOpenAiCompatibleClient(
                        GitHubModelsEndpoint, choice.ModelId, choice.ApiKey),

                FclAiProviderType.OpenAI =>
                    new ChatClient(choice.ModelId, credential).AsIChatClient(),

                FclAiProviderType.AzureOpenAI when choice.Endpoint is not null =>
                    new ChatClient(choice.ModelId, credential,
                        new OpenAIClientOptions { Endpoint = choice.Endpoint })
                        .AsIChatClient(),

                _ => LogAndReturnNull($"Invalid cloud provider configuration: {choice.Provider}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create cloud AI client for {Provider}.", choice.Provider);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────

    private static HttpClient CreateProbeHttpClient() =>
        new() { Timeout = ProbeTimeout };

    private IChatClient? LogAndReturnNull(string message)
    {
        _logger.LogWarning("{Message}", message);
        return null;
    }
}
