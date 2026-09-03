using AiNET.Providers;

using Xunit;

namespace AiNET.Tests;

/// <summary>
/// Tests for the cloud provider adapters (OpenAI, Azure OpenAI, Anthropic) —
/// descriptor contracts and offline probe behaviour. Live-endpoint behaviour
/// is not covered here; these assert the contracts consumers rely on.
/// </summary>
public class CloudProviderTests
{
    // ── Descriptors ──────────────────────────────────────────────────────────

    [Fact]
    public void Anthropic_Descriptor_DeclaresChatAndToolsButNotEmbeddings()
    {
        var descriptor = new AnthropicProvider().Descriptor;

        Assert.Equal(AiProviderKind.Anthropic, descriptor.Kind);
        Assert.Equal(AiProviderLocation.Cloud, descriptor.Location);
        Assert.True(descriptor.RequiresApiKey);
        Assert.True(descriptor.Capabilities.HasFlag(AiCapabilities.Chat));
        Assert.True(descriptor.Capabilities.HasFlag(AiCapabilities.Tools));
        // Anthropic exposes no embeddings API.
        Assert.False(descriptor.Capabilities.HasFlag(AiCapabilities.Embeddings));
    }

    [Fact]
    public void AzureOpenAi_Descriptor_HasNoDefaultEndpoint()
    {
        // The endpoint is always the customer's own resource URL.
        Assert.Null(new AzureOpenAiProvider().Descriptor.DefaultEndpoint);
    }

    [Fact]
    public void CloudProviders_AreRegisteredInDefaultCatalog()
    {
        var catalog = AiProviderCatalog.CreateDefault();

        Assert.NotNull(catalog.Find(AiProviderKind.OpenAi));
        Assert.NotNull(catalog.Find(AiProviderKind.AzureOpenAi));
        Assert.NotNull(catalog.Find(AiProviderKind.Anthropic));
        Assert.NotNull(catalog.Find(AiProviderKind.Anthropic));
    }

    // ── Probing without a credential ─────────────────────────────────────────

    // Provider kinds are enums and endpoints plain strings, so Test Explorer can
    //  serialize (and therefore enumerate) the individual data rows.
    public static TheoryData<AiProviderKind, string> KeyRequiringProviders => new()
    {
        { AiProviderKind.OpenAi,      "https://api.openai.com" },
        { AiProviderKind.Anthropic,   "https://api.anthropic.com" },
        { AiProviderKind.AzureOpenAi, "https://contoso.openai.azure.com" },
    };

    [Theory]
    [MemberData(nameof(KeyRequiringProviders))]
    public async Task ProbeAsync_NoApiKey_ReturnsAuthFailureWithoutNetworkCall(
        AiProviderKind kind, string endpointUrl)
    {
        var provider = AiProviderCatalog.CreateDefault().Find(kind);
        Assert.NotNull(provider);

        var result = await provider.ProbeAsync(new Uri(endpointUrl), apiKey: null,
                                               CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.True(result.IsAuthFailure);
        Assert.NotNull(result.ErrorMessage);
        // No network call should have been attempted.
        Assert.Equal(TimeSpan.Zero, result.Latency);
    }

    [Fact]
    public async Task ProbeAsync_EmptyApiKey_IsTreatedAsMissing()
    {
        var result = await new OpenAiProvider().ProbeAsync(
            new Uri("https://api.openai.com"), apiKey: "", CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.True(result.IsAuthFailure);
    }

    // ── Client construction guards ───────────────────────────────────────────

    [Fact]
    public void Anthropic_CreateEmbeddingGenerator_AlwaysReturnsNull()
    {
        var provider = new AnthropicProvider();
        var config = new AiProviderConfig(
            provider.Descriptor,
            new Uri("https://api.anthropic.com"),
            ApiKey: "sk-ant-test",
            ChatModelId: "claude-sonnet-4-20250514",
            EmbeddingModelId: "anything");

        Assert.Null(provider.CreateEmbeddingGenerator(config));
    }

    [Fact]
    public void Anthropic_CreateChatClient_WithModelAndKey_ReturnsClient()
    {
        var provider = new AnthropicProvider();
        var config = new AiProviderConfig(
            provider.Descriptor,
            new Uri("https://api.anthropic.com"),
            ApiKey: "sk-ant-test",
            ChatModelId: "claude-sonnet-4-20250514",
            EmbeddingModelId: null);

        using var client = provider.CreateChatClient(config);
        Assert.NotNull(client);
    }

    [Fact]
    public void CloudProviders_CreateChatClient_WithoutKey_ReturnsNull()
    {
        var provider = new OpenAiProvider();
        var config = new AiProviderConfig(
            provider.Descriptor,
            new Uri("https://api.openai.com"),
            ApiKey: null,
            ChatModelId: "gpt-4o-mini",
            EmbeddingModelId: null);

        Assert.Null(provider.CreateChatClient(config));
    }
}
