using System.Net;

using AiNET.Providers;

using Xunit;

namespace AiNET.Tests;

/// <summary>
/// Unit tests for <see cref="OllamaProvider"/> using a fake
/// <see cref="HttpMessageHandler"/> to avoid requiring a live Ollama
/// instance.
/// </summary>
public class OllamaProviderTests
{
    // ── Canned response payloads ────────────────────────────────────────────

    private const string TagsWithModels = """
        {
          "models": [
            { "name": "llama3:latest",   "modified_at": "2024-01-02T00:00:00Z" },
            { "name": "mistral:7b-q4_0", "modified_at": "2024-01-01T00:00:00Z" }
          ]
        }
        """;

    private const string TagsEmpty = """{ "models": [] }""";

    // ── ProbeAsync — healthy ────────────────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_HealthyInstance_ReturnsIsHealthyWithModels()
    {
        var provider = new OllamaProvider();
        var handler  = new FakeHttpHandler(HttpStatusCode.OK, TagsWithModels);
        InjectHandler(provider, handler);

        var result = await provider.ProbeAsync(
            new Uri("http://localhost:11434"), apiKey: null, CancellationToken.None);

        Assert.True(result.IsHealthy);
        Assert.Equal(2, result.DiscoveredChatModels.Count);
        Assert.Contains("llama3:latest",   result.DiscoveredChatModels);
        Assert.Contains("mistral:7b-q4_0", result.DiscoveredChatModels);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAsync_EmptyModelList_ReturnsHealthyWithNoModels()
    {
        var provider = new OllamaProvider();
        var handler  = new FakeHttpHandler(HttpStatusCode.OK, TagsEmpty);
        InjectHandler(provider, handler);

        var result = await provider.ProbeAsync(
            new Uri("http://localhost:11434"), apiKey: null, CancellationToken.None);

        Assert.True(result.IsHealthy);
        Assert.Empty(result.DiscoveredChatModels);
    }

    // ── ProbeAsync — unhealthy ──────────────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_ServerError_ReturnsUnhealthy()
    {
        var provider = new OllamaProvider();
        var handler  = new FakeHttpHandler(HttpStatusCode.InternalServerError, string.Empty);
        InjectHandler(provider, handler);

        var result = await provider.ProbeAsync(
            new Uri("http://localhost:11434"), apiKey: null, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAsync_NetworkException_ReturnsUnhealthy()
    {
        var provider = new OllamaProvider();
        var handler  = new ThrowingHttpHandler(new HttpRequestException("Connection refused"));
        InjectHandler(provider, handler);

        var result = await provider.ProbeAsync(
            new Uri("http://localhost:11434"), apiKey: null, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Contains("Connection refused", result.ErrorMessage);
    }

    // ── CreateChatClient ────────────────────────────────────────────────────

    [Fact]
    public void CreateChatClient_WithModelId_ReturnsNonNull()
    {
        var provider = new OllamaProvider();
        var config   = MakeConfig("llama3:latest");

        var client = provider.CreateChatClient(config);

        Assert.NotNull(client);
    }

    [Fact]
    public void CreateChatClient_NullModelId_ReturnsNull()
    {
        var provider = new OllamaProvider();
        var config   = MakeConfig(chatModelId: null);

        var client = provider.CreateChatClient(config);

        Assert.Null(client);
    }

    // ── CreateEmbeddingGenerator ────────────────────────────────────────────

    [Fact]
    public void CreateEmbeddingGenerator_WithEmbeddingModelId_ReturnsNonNull()
    {
        var provider = new OllamaProvider();
        var config   = MakeConfig(embeddingModelId: "nomic-embed-text");

        var generator = provider.CreateEmbeddingGenerator(config);

        Assert.NotNull(generator);
    }

    [Fact]
    public void CreateEmbeddingGenerator_NullEmbeddingModelId_ReturnsNull()
    {
        var provider = new OllamaProvider();
        var config   = MakeConfig();  // no embedding model

        var generator = provider.CreateEmbeddingGenerator(config);

        Assert.Null(generator);
    }

    // ── Descriptor ──────────────────────────────────────────────────────────

    [Fact]
    public void Descriptor_HasCorrectKindAndLocation()
    {
        var provider = new OllamaProvider();
        Assert.Equal(AiProviderKind.Ollama,        provider.Descriptor.Kind);
        Assert.Equal(AiProviderLocation.Local,     provider.Descriptor.Location);
        Assert.False(provider.Descriptor.RequiresApiKey);
        Assert.True(provider.Descriptor.Capabilities.HasFlag(AiCapabilities.Chat));
        Assert.True(provider.Descriptor.Capabilities.HasFlag(AiCapabilities.Embeddings));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Injects a fake <see cref="HttpMessageHandler"/> into the provider via
    /// the internal test constructor.
    /// <para>
    /// Since <see cref="OllamaProvider"/> creates its own
    /// <see cref="HttpClient"/> inside <c>ProbeAsync</c>, we use the
    /// <see cref="OllamaProvider(HttpMessageHandler)"/> test constructor
    /// (internal, visible via <c>InternalsVisibleTo</c>) to supply a fake.
    /// </para>
    /// </summary>
    private static void InjectHandler(OllamaProvider provider, HttpMessageHandler handler)
    {
        // The test constructor is internal; we set it via the internal setter.
        provider.SetTestHandler(handler);
    }

    private static AiProviderConfig MakeConfig(
        string? chatModelId = null, string? embeddingModelId = null)
    {
        var descriptor = new AiProviderDescriptor(
            AiProviderKind.Ollama, AiProviderLocation.Local, "Ollama",
            new Uri("http://localhost:11434"), false,
            AiCapabilities.Chat | AiCapabilities.Embeddings);

        return new AiProviderConfig(
            descriptor,
            new Uri("http://localhost:11434"),
            ApiKey: null,
            ChatModelId: chatModelId,
            EmbeddingModelId: embeddingModelId);
    }

    // ── Fake HTTP helpers ────────────────────────────────────────────────────

    private sealed class FakeHttpHandler(HttpStatusCode status, string body)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowingHttpHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
