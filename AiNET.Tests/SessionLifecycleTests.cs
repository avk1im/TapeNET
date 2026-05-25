using Microsoft.Extensions.AI;

using Xunit;

namespace AiNET.Tests;

/// <summary>
/// Tests for <see cref="AiSession"/> and <see cref="AiProviderCatalog"/> —
/// verifies <see cref="IAiSession.ReplaceProviderAsync"/> swaps clients and
/// raises <see cref="IAiSession.ProviderChanged"/>, and that dispose
/// semantics are correct.
/// </summary>
public class SessionLifecycleTests
{
    // ── Capabilities ─────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_NoChatNoEmbedding_ReturnsNone()
    {
        var session = MakeSession(chatClient: null, embeddingGenerator: null);
        Assert.Equal(AiCapabilities.None, session.Capabilities);
    }

    [Fact]
    public void Capabilities_ChatOnly_ReturnsChat()
    {
        var session = MakeSession(chatClient: new FakeChatClient());
        Assert.Equal(AiCapabilities.Chat, session.Capabilities);
    }

    [Fact]
    public void Capabilities_EmbeddingsOnly_ReturnsEmbeddings()
    {
        var session = MakeSession(embeddingGenerator: new FakeEmbeddingGenerator());
        Assert.Equal(AiCapabilities.Embeddings, session.Capabilities);
    }

    [Fact]
    public void Capabilities_Both_ReturnsChatAndEmbeddings()
    {
        var session = MakeSession(new FakeChatClient(), new FakeEmbeddingGenerator());
        Assert.Equal(AiCapabilities.Chat | AiCapabilities.Embeddings, session.Capabilities);
    }

    // ── ReplaceProviderAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceProviderAsync_RaisesProviderChanged()
    {
        var catalog = AiProviderCatalog.CreateDefault();
        var session = MakeSession(catalog: catalog);

        bool eventRaised = false;
        session.ProviderChanged += (_, _) => eventRaised = true;

        var newConfig = MakeConfig(AiProviderKind.Ollama, "llama3:latest");
        await session.ReplaceProviderAsync(newConfig, CancellationToken.None);

        Assert.True(eventRaised);
    }

    [Fact]
    public async Task ReplaceProviderAsync_UpdatesConfig()
    {
        var catalog = AiProviderCatalog.CreateDefault();
        var session = MakeSession(catalog: catalog);

        var newConfig = MakeConfig(AiProviderKind.Ollama, "llama3:latest");
        await session.ReplaceProviderAsync(newConfig, CancellationToken.None);

        Assert.Equal(newConfig, session.Config);
    }

    [Fact]
    public async Task ReplaceProviderAsync_NullChatModel_ChatClientBecomesNull()
    {
        var catalog = AiProviderCatalog.CreateDefault();
        var session = MakeSession(catalog: catalog, chatClient: new FakeChatClient());

        // Replace with a config that has no chat model → OllamaProvider.CreateChatClient returns null
        var noModelConfig = MakeConfig(AiProviderKind.Ollama, chatModelId: null);
        await session.ReplaceProviderAsync(noModelConfig, CancellationToken.None);

        Assert.Null(session.ChatClient);
    }

    [Fact]
    public async Task ReplaceProviderAsync_UnknownKind_Throws()
    {
        var catalog = new AiProviderCatalog();  // empty — no providers registered
        var session = MakeSession(catalog: catalog);

        var config = MakeConfig(AiProviderKind.Ollama, "llama3");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ReplaceProviderAsync(config, CancellationToken.None));
    }

    // ── DisposeAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DisposesClients()
    {
        var chat = new TrackingChatClient();
        var session = MakeSession(chatClient: chat);

        await session.DisposeAsync();

        Assert.True(chat.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledTwiceSafely()
    {
        var session = MakeSession();
        await session.DisposeAsync();
        var ex = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    // ── AiProviderCatalog ─────────────────────────────────────────────────────

    [Fact]
    public void Catalog_CreateDefault_ContainsAllBuiltInProviders()
    {
        var catalog = AiProviderCatalog.CreateDefault();
        var kinds = catalog.Providers.Select(p => p.Descriptor.Kind).ToHashSet();

        Assert.Contains(AiProviderKind.Ollama,           kinds);
        Assert.Contains(AiProviderKind.LmStudio,         kinds);
        Assert.Contains(AiProviderKind.Onnx,             kinds);
        Assert.Contains(AiProviderKind.OpenAiCompatible, kinds);
        Assert.Contains(AiProviderKind.OpenAi,           kinds);
        Assert.Contains(AiProviderKind.AzureOpenAi,      kinds);
        Assert.Contains(AiProviderKind.GitHubModels,     kinds);
    }

    [Fact]
    public void Catalog_Find_ReturnsCorrectProvider()
    {
        var catalog = AiProviderCatalog.CreateDefault();
        var provider = catalog.Find(AiProviderKind.Ollama);

        Assert.NotNull(provider);
        Assert.Equal(AiProviderKind.Ollama, provider.Descriptor.Kind);
    }

    [Fact]
    public void Catalog_Find_UnknownKind_ReturnsNull()
    {
        var catalog = new AiProviderCatalog();
        Assert.Null(catalog.Find(AiProviderKind.Ollama));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AiSession MakeSession(
        IChatClient? chatClient = null,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        IAiProviderCatalog? catalog = null)
    {
        catalog ??= AiProviderCatalog.CreateDefault();
        var config = MakeConfig(AiProviderKind.Ollama, "llama3:latest");
        return new AiSession(catalog, config, chatClient, embeddingGenerator);
    }

    private static AiProviderConfig MakeConfig(
        AiProviderKind kind, string? chatModelId = "llama3:latest")
    {
        var descriptor = new AiProviderDescriptor(
            kind, AiProviderLocation.Local, kind.ToString(),
            new Uri("http://localhost:11434"), false,
            AiCapabilities.Chat | AiCapabilities.Embeddings);

        return new AiProviderConfig(
            descriptor,
            new Uri("http://localhost:11434"),
            ApiKey: null,
            ChatModelId: chatModelId,
            EmbeddingModelId: null);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeChatClient : IChatClient
    {
        public static ChatClientMetadata Metadata => new("fake", null, null);
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class TrackingChatClient : IChatClient
    {
        public bool Disposed { get; private set; }
        public static ChatClientMetadata Metadata => new("tracking", null, null);
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeEmbeddingGenerator
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public static EmbeddingGeneratorMetadata Metadata => new("fake", null, null, null);
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>([]));
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
