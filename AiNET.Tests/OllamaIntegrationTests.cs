using AiNET.Providers;

using Microsoft.Extensions.AI;

using Xunit;

namespace AiNET.Tests;

/// <summary>
/// Integration tests that exercise <see cref="OllamaProvider"/> against a
/// <b>live</b> Ollama instance at <c>http://localhost:11434</c>.
/// <para>
/// Each test skips automatically when Ollama is not running — no failure is
/// reported in CI. Start Ollama locally to run these tests:
/// <code>ollama serve</code>
/// </para>
/// </summary>
public class OllamaIntegrationTests
{
    private static readonly Uri OllamaBase = new("http://localhost:11434");
    private readonly OllamaProvider _provider = new();

    // ── Cached availability check ────────────────────────────────────────────
    // Probe once per test-class lifetime so we don't burn 5 s per test when
    // Ollama is offline. The result is shared across all tests in the class.

    private static AiProviderProbeResult? _cachedProbe;
    private static readonly SemaphoreSlim _probeLock = new(1, 1);

    private async Task<AiProviderProbeResult> GetProbeAsync()
    {
        if (_cachedProbe is not null)
            return _cachedProbe;

        await _probeLock.WaitAsync();
        try
        {
            _cachedProbe ??= await _provider.ProbeAsync(
                OllamaBase, apiKey: null, CancellationToken.None);
            return _cachedProbe;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    // ── ProbeAsync ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ProbeAsync_LiveOllama_IsHealthy()
    {
        var result = await GetProbeAsync();
        Skip.If(!result.IsHealthy, "Ollama is not running — skipping integration test.");

        Assert.True(result.IsHealthy);
        Assert.NotNull(result.Descriptor);
        Assert.Equal(AiProviderKind.Ollama, result.Descriptor.Kind);
        Assert.True(result.Latency > TimeSpan.Zero);
        Assert.Null(result.ErrorMessage);
    }

    [SkippableFact]
    public async Task ProbeAsync_LiveOllama_ReturnsAtLeastOneModel()
    {
        var result = await GetProbeAsync();
        Skip.If(!result.IsHealthy, "Ollama is not running — skipping integration test.");

        Assert.NotEmpty(result.DiscoveredChatModels);
    }

    // ── CreateChatClient ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateChatClient_LiveOllama_CanCompleteSimplePrompt()
    {
        var probe = await GetProbeAsync();
        Skip.If(!probe.IsHealthy,            "Ollama is not running — skipping integration test.");
        Skip.If(probe.DiscoveredChatModels.Count == 0, "No models loaded in Ollama — skipping.");

        var modelId = probe.DiscoveredChatModels[0];
        var config  = MakeConfig(chatModelId: modelId);
        var client  = _provider.CreateChatClient(config);

        Assert.NotNull(client);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Reply with exactly the word: pong")
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Text);
    }

    // ── CreateEmbeddingGenerator ─────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateEmbeddingGenerator_LiveOllama_ProducesNonZeroVector()
    {
        var probe = await GetProbeAsync();
        Skip.If(!probe.IsHealthy, "Ollama is not running — skipping integration test.");

        // Prefer a known embedding model; fall back to the first available model.
#pragma warning disable CA1826 // Do not use Enumerable methods on indexable collections -- or default case!
        var embeddingModel = probe.DiscoveredEmbeddingModels
            .FirstOrDefault(m => m.Contains("embed", StringComparison.OrdinalIgnoreCase))
            ?? probe.DiscoveredEmbeddingModels.FirstOrDefault();
#pragma warning restore CA1826 // Do not use Enumerable methods on indexable collections

        Skip.If(embeddingModel is null, "No embedding-capable model loaded in Ollama — skipping.");

        var config    = MakeConfig(embeddingModelId: embeddingModel);
        var generator = _provider.CreateEmbeddingGenerator(config);

        Assert.NotNull(generator);

        try
        {
            var embeddings = await generator.GenerateAsync(["hello world"]);

            Assert.Single(embeddings);
            var vector = embeddings[0].Vector.ToArray();
            Assert.NotEmpty(vector);
            // Vector must not be all-zeros
            Assert.Contains(vector, v => v != 0f);
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            Skip.If(ex.Status == 501 /*api_error*/,
                $"Ollama model {embeddingModel} does not support embeddings — skipping integration test.");
            Assert.Fail("Exception thrown while generating embeddings: " + ex);
        }
        catch (Exception ex)
        {
            Assert.Fail($"Exception thrown while generating embeddings with Ollama model {embeddingModel}: " + ex);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AiProviderConfig MakeConfig(
        string? chatModelId = null, string? embeddingModelId = null) =>
        new(
            Descriptor:       new AiProviderDescriptor(
                AiProviderKind.Ollama, AiProviderLocation.Local, "Ollama",
                OllamaBase, false,
                AiCapabilities.Chat | AiCapabilities.Embeddings),
            Endpoint:         OllamaBase,
            ApiKey:           null,
            ChatModelId:      chatModelId,
            EmbeddingModelId: embeddingModelId);
}
