using AiNET;
using HelpNET.Assistants;
using HelpNET.Content;
using HelpNET.Session;
using Microsoft.Extensions.AI;
using Xunit;

namespace HelpNET.Tests.Phase4;

/// <summary>
/// Tests for <see cref="HelpSessionFactory"/> Phase 4 mode-selection matrix.
/// Verifies that the factory selects Lexical, Semantic, or Rag mode based on
/// the combination of AI session capabilities, bundle presence, and options.
/// </summary>
public class HelpSessionFactoryPhase4Tests
{
    // ── 1. No AI session ─────────────────────────────────────────────────────

    [Fact]
    public async Task NoAiSession_NoBundleNoOnnx_IsLexical()
    {
        await using var session = await HelpSessionFactory.CreateAsync(
            TestContentFixture.CreateSource(),
            aiSession: null,
            new HelpSessionOptions(),
            ct: CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Lexical, session.AssistantMode);
    }

    [Fact]
    public async Task NoAiSession_BundlePresent_NoOnnxOptions_IsLexical()
    {
        // Bundle is present but no ONNX options and no AI session → Lexical.
        var (bundle, store) = await BundleBuilder.BuildFromFixtureAsync();
        var source = new BundledInMemorySource(TestContentFixture.CreateSource(), bundle);

        await using var session = await HelpSessionFactory.CreateAsync(
            source,
            aiSession: null,
            new HelpSessionOptions(),
            onnxOptions: null,
            ct: CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Lexical, session.AssistantMode);
    }

    // ── 2. Chat only (no embeddings, no bundle) ───────────────────────────────

    [Fact]
    public async Task ChatOnlySession_NoBundleNoOnnx_IsRag()
    {
        // Chat but no bundle → Rag with lexical-only retrieval.
        var aiSession = new FakeAiSession(
            capabilities:     AiCapabilities.Chat,
            embeddingModelId: null);

        await using var session = await HelpSessionFactory.CreateAsync(
            TestContentFixture.CreateSource(),
            aiSession,
            new HelpSessionOptions(),
            ct: CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Rag, session.AssistantMode);
    }

    // ── 3. Semantic: no chat, provider embeddings with matching model id ──────

    [Fact]
    public async Task EmbeddingsOnlySession_BundlePresent_ModelIdMatches_IsSemantic()
    {
        // Embeddings capability only (no chat) + bundle with matching model id
        // + PreferProviderEmbeddings → Semantic mode.
        var (bundle, _) = await BundleBuilder.BuildFromFixtureAsync();
        var source      = new BundledInMemorySource(TestContentFixture.CreateSource(), bundle);

        var aiSession = new FakeAiSession(
            capabilities:     AiCapabilities.Embeddings,
            embeddingModelId: BundleBuilder.TestModelId);  // exact match

        var options = new HelpSessionOptions(PreferProviderEmbeddings: true);

        await using var session = await HelpSessionFactory.CreateAsync(
            source,
            aiSession,
            options,
            ct: CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Semantic, session.AssistantMode);
    }

    // ── 4. Rag: chat + provider embeddings ───────────────────────────────────

    [Fact]
    public async Task ChatPlusEmbeddingsSession_BundlePresent_ModelIdMatches_IsRag()
    {
        // Chat + Embeddings + bundle match + PreferProviderEmbeddings → Rag (hybrid retrieval).
        var (bundle, _) = await BundleBuilder.BuildFromFixtureAsync();
        var source      = new BundledInMemorySource(TestContentFixture.CreateSource(), bundle);

        var aiSession = new FakeAiSession(
            capabilities:     AiCapabilities.Chat | AiCapabilities.Embeddings,
            embeddingModelId: BundleBuilder.TestModelId);

        var options = new HelpSessionOptions(PreferProviderEmbeddings: true);

        await using var session = await HelpSessionFactory.CreateAsync(
            source,
            aiSession,
            options,
            ct: CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Rag, session.AssistantMode);
    }

    // ── 5. Strategy A: prefer provider embeddings ─────────────────────────────

    [Fact]
    public async Task ProviderEmbeddings_PreferEnabled_ModelIdMatches_IsRag()
    {
        // Provider embedding model id matches the bundle model id and
        // PreferProviderEmbeddings is true → provider embeddings used for Rag.
        var (bundle, _) = await BundleBuilder.BuildFromFixtureAsync();

        var aiSession = new FakeAiSession(
            capabilities:     AiCapabilities.Chat | AiCapabilities.Embeddings,
            embeddingModelId: BundleBuilder.TestModelId);   // exact match

        var source  = new BundledInMemorySource(TestContentFixture.CreateSource(), bundle);
        var options = new HelpSessionOptions(PreferProviderEmbeddings: true);

        await using var session = await HelpSessionFactory.CreateAsync(
            source,
            aiSession,
            options,
            ct: CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Rag, session.AssistantMode);
    }

    [Fact]
    public async Task ProviderEmbeddings_PreferEnabled_ModelIdMismatch_FallsBackToLexical()
    {
        // Model id mismatch → no usable embedding generator → Rag with lexical retriever,
        // because chat is still available.
        var (bundle, _) = await BundleBuilder.BuildFromFixtureAsync();

        var aiSession = new FakeAiSession(
            capabilities:     AiCapabilities.Chat | AiCapabilities.Embeddings,
            embeddingModelId: "some-other-model");  // mismatch

        var source  = new BundledInMemorySource(TestContentFixture.CreateSource(), bundle);
        var options = new HelpSessionOptions(PreferProviderEmbeddings: true);

        await using var session = await HelpSessionFactory.CreateAsync(
            source,
            aiSession,
            options,
            ct: CancellationToken.None);

        // Chat is available, but no usable embeddings → Rag (lexical-only retrieval).
        Assert.Equal(HelpAssistantMode.Rag, session.AssistantMode);
    }

    [Fact]
    public async Task ProviderEmbeddings_PreferDisabled_ModelIdMatches_IgnoresProvider()
    {
        // PreferProviderEmbeddings is false → provider embedding generator is ignored,
        // no ONNX options supplied → no embedding gen → Rag (lexical-only).
        var (bundle, _) = await BundleBuilder.BuildFromFixtureAsync();

        var aiSession = new FakeAiSession(
            capabilities:     AiCapabilities.Chat | AiCapabilities.Embeddings,
            embeddingModelId: BundleBuilder.TestModelId);

        var source  = new BundledInMemorySource(TestContentFixture.CreateSource(), bundle);
        var options = new HelpSessionOptions(PreferProviderEmbeddings: false);

        await using var session = await HelpSessionFactory.CreateAsync(
            source,
            aiSession,
            options,
            ct: CancellationToken.None);

        // PreferProviderEmbeddings is false and no ONNX options → lexical-only Rag.
        Assert.Equal(HelpAssistantMode.Rag, session.AssistantMode);
    }

    // ── 6. No bundle → always Lexical or Rag (chat determines) ──────────────

    [Fact]
    public async Task NoBundleNoOnnx_ChatAvailable_IsRag()
    {
        var aiSession = new FakeAiSession(AiCapabilities.Chat, null);

        await using var session = await HelpSessionFactory.CreateAsync(
            TestContentFixture.CreateSource(),  // no bundle
            aiSession,
            new HelpSessionOptions(),
            ct: CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Rag, session.AssistantMode);
    }

    [Fact]
    public async Task NoBundleNoOnnx_NoChatAvailable_IsLexical()
    {
        var aiSession = new FakeAiSession(AiCapabilities.Embeddings, BundleBuilder.TestModelId);

        await using var session = await HelpSessionFactory.CreateAsync(
            TestContentFixture.CreateSource(),  // no bundle
            aiSession,
            new HelpSessionOptions(),
            ct: CancellationToken.None);

        // No bundle → no embeddings usable; no chat → Lexical.
        Assert.Equal(HelpAssistantMode.Lexical, session.AssistantMode);
    }

    // ── Fake AI session ───────────────────────────────────────────────────────

    private sealed class FakeAiSession(
        AiCapabilities capabilities,
        string?        embeddingModelId) : IAiSession
    {
        // Minimal stub config used by TryResolveEmbeddingGenerator.
        public AiProviderConfig Config { get; } = new AiProviderConfig(
            new AiProviderDescriptor(
                AiProviderKind.Custom,
                AiProviderLocation.Local,
                "Fake",
                null,
                false,
                AiCapabilities.Chat | AiCapabilities.Embeddings),
            new Uri("http://localhost"),
            null,
            "fake-chat-model",
            embeddingModelId);

        public AiCapabilities Capabilities => capabilities;

        public IChatClient? ChatClient =>
            capabilities.HasFlag(AiCapabilities.Chat) ? new FakeChatClient() : null;

        public IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator =>
            capabilities.HasFlag(AiCapabilities.Embeddings)
                ? new FakeEmbeddingGenerator(BundleBuilder.TestDim)
                : null;

        public event EventHandler? ProviderChanged;

        public Task ReplaceProviderAsync(AiProviderConfig config, CancellationToken ct)
        {
            ProviderChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── Fake chat client ──────────────────────────────────────────────────────

    private sealed class FakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("fake", null, null);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions?             options = null,
            CancellationToken        ct      = default)
            => Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions?             options = null,
            CancellationToken        ct      = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

