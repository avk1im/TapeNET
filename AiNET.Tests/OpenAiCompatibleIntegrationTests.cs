using AiNET.Providers;
using AiNET.Tests.Helpers;

using Microsoft.Extensions.AI;

using Xunit;

namespace AiNET.Tests;

/// <summary>
/// Integration tests for <see cref="OpenAiCompatibleProvider"/> against a
/// <b>real OpenAI-compatible server</b> on the LAN (e.g. OpenVINO Model Server,
/// LM Studio, vLLM, text-generation-webui).
/// <para>
/// Tests skip automatically when the server is not configured — no failure is
/// reported in CI or on machines without the server running.
/// </para>
/// <para><b>Setup:</b> copy <c>remote-test-settings.template.json</c> to
/// <c>AiNET.Tests\remote-test-settings.json</c> and fill in your values:</para>
/// <code>
/// {
///   "Endpoint":       "http://192.168.178.42:8080",
///   "ChatModel":      null,
///   "EmbeddingModel": "BAAI/bge-small-en-v1.5"
/// }
/// </code>
/// <para>
/// Alternatively set environment variables:
/// <c>AINET_REMOTE_ENDPOINT</c>, <c>AINET_REMOTE_CHAT_MODEL</c>,
/// <c>AINET_REMOTE_EMBEDDING_MODEL</c>.
/// </para>
/// </summary>
public class OpenAiCompatibleIntegrationTests
{
    // ── Settings + provider ───────────────────────────────────────────────────

    private static readonly OpenAiRemoteTestSettings Settings = new();
    private readonly OpenAiCompatibleProvider _provider = new();

    // ── Cached probe ──────────────────────────────────────────────────────────
    // Probe once per class lifetime so a slow or unavailable server only
    // incurs one timeout penalty, not one per test.

    private static AiProviderProbeResult? _cachedProbe;
    private static readonly SemaphoreSlim _probeLock = new(1, 1);

    private async Task<AiProviderProbeResult?> GetProbeAsync()
    {
        if (!Settings.IsAvailable) return null;
        if (_cachedProbe is not null) return _cachedProbe;

        await _probeLock.WaitAsync();
        try
        {
            _cachedProbe ??= await _provider.ProbeAsync(
                Settings.Endpoint!, apiKey: null, CancellationToken.None);
            return _cachedProbe;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    // ── ProbeAsync ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ProbeAsync_LiveServer_IsHealthy()
    {
        Skip.If(!Settings.IsAvailable,
            "Remote endpoint not configured — set Endpoint in remote-test-settings.json " +
            "or AINET_REMOTE_ENDPOINT env var.");

        var result = await GetProbeAsync();
        Skip.If(result is null || !result.IsHealthy,
            $"Remote server unreachable or unhealthy: {result?.ErrorMessage}");

        Assert.True(result!.IsHealthy);
        Assert.Equal(AiProviderKind.OpenAiCompatible, result.Descriptor.Kind);
        Assert.Null(result.ErrorMessage);
        Assert.True(result.Latency > TimeSpan.Zero);
    }

    [SkippableFact]
    public async Task ProbeAsync_LiveServer_ReturnsAtLeastOneModel()
    {
        Skip.If(!Settings.IsAvailable,
            "Remote endpoint not configured.");

        var result = await GetProbeAsync();
        Skip.If(result is null || !result.IsHealthy,
            $"Remote server unreachable: {result?.ErrorMessage}");

        // Healthy servers should advertise at least one model via /v1/models
        Assert.NotEmpty(result!.DiscoveredChatModels);
    }

    // ── CreateChatClient ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateChatClient_LiveServer_CanCompleteSimplePrompt()
    {
        Skip.If(!Settings.IsAvailable,
            "Remote endpoint not configured.");
        Skip.If(Settings.ChatModel is null,
            "ChatModel not set in remote-test-settings.json — skipping chat test.");

        var probe = await GetProbeAsync();
        Skip.If(probe is null || !probe.IsHealthy,
            $"Remote server unreachable: {probe?.ErrorMessage}");

        var config = MakeChatConfig(Settings.ChatModel!);
        var client = _provider.CreateChatClient(config);
        Assert.NotNull(client);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Reply with exactly the word: pong")
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Text);
    }

    // ── CreateEmbeddingGenerator ──────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateEmbeddingGenerator_LiveServer_ProducesNonZeroVector()
    {
        Skip.If(!Settings.IsAvailable,
            "Remote endpoint not configured.");
        Skip.If(Settings.EmbeddingModel is null,
            "EmbeddingModel not set in remote-test-settings.json — skipping embedding test.");

        var probe = await GetProbeAsync();
        Skip.If(probe is null || !probe.IsHealthy,
            $"Remote server unreachable: {probe?.ErrorMessage}");

        var config    = MakeEmbeddingConfig(Settings.EmbeddingModel!);
        var generator = _provider.CreateEmbeddingGenerator(config);
        Assert.NotNull(generator);

        var embeddings = await generator!.GenerateAsync(["hello world"]);

        Assert.Single(embeddings);
        var vector = embeddings[0].Vector.ToArray();
        Assert.NotEmpty(vector);
        Assert.Contains(vector, v => v != 0f);
    }

    [SkippableFact]
    public async Task CreateEmbeddingGenerator_LiveServer_SimilarSentencesScoreHigher()
    {
        Skip.If(!Settings.IsAvailable,
            "Remote endpoint not configured.");
        Skip.If(Settings.EmbeddingModel is null,
            "EmbeddingModel not set in remote-test-settings.json — skipping embedding test.");

        var probe = await GetProbeAsync();
        Skip.If(probe is null || !probe.IsHealthy,
            $"Remote server unreachable: {probe?.ErrorMessage}");

        var config    = MakeEmbeddingConfig(Settings.EmbeddingModel!);
        var generator = _provider.CreateEmbeddingGenerator(config);
        Assert.NotNull(generator);

        var embeddings = await generator!.GenerateAsync(
        [
            "A dog is playing in the park.",        // sentence A
            "A puppy is running in the garden.",    // similar to A
            "The stock market fell sharply today."  // unrelated
        ]);

        Assert.Equal(3, embeddings.Count);

        var a       = embeddings[0].Vector.ToArray();
        var similar = embeddings[1].Vector.ToArray();
        var unrel   = embeddings[2].Vector.ToArray();

        float simA = Dot(Normalize(a), Normalize(similar));
        float simU = Dot(Normalize(a), Normalize(unrel));

        Assert.True(simA > simU,
            $"Expected similar pair (cos={simA:F4}) to score above " +
            $"unrelated pair (cos={simU:F4}).");
    }

    // ── Descriptor ────────────────────────────────────────────────────────────

    [Fact]
    public void Descriptor_HasCorrectKindAndCapabilities()
    {
        Assert.Equal(AiProviderKind.OpenAiCompatible, _provider.Descriptor.Kind);
        Assert.Equal(AiProviderLocation.LocalNetwork, _provider.Descriptor.Location);
        Assert.True(_provider.Descriptor.Capabilities.HasFlag(AiCapabilities.Chat));
        Assert.True(_provider.Descriptor.Capabilities.HasFlag(AiCapabilities.Embeddings));
        Assert.False(_provider.Descriptor.RequiresApiKey);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private AiProviderConfig MakeChatConfig(string chatModelId) =>
        new(
            Descriptor:       _provider.Descriptor,
            Endpoint:         Settings.Endpoint!,
            ApiKey:           null,
            ChatModelId:      chatModelId,
            EmbeddingModelId: null);

    private AiProviderConfig MakeEmbeddingConfig(string embeddingModelId) =>
        new(
            Descriptor:       _provider.Descriptor,
            Endpoint:         Settings.Endpoint!,
            ApiKey:           null,
            ChatModelId:      null,
            EmbeddingModelId: embeddingModelId);

    /// <summary>Dot product of two same-length vectors.</summary>
    private static float Dot(float[] a, float[] b)
    {
        float sum = 0f;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>Returns an L2-normalised copy of <paramref name="v"/>.</summary>
    private static float[] Normalize(float[] v)
    {
        double sumSq = 0.0;
        foreach (var x in v) sumSq += x * (double)x;
        float norm = (float)Math.Sqrt(sumSq);
        if (norm < 1e-9f) return v;
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++) result[i] = v[i] / norm;
        return result;
    }
}
