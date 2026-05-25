using System.Text.Json;

using Xunit;

namespace AiNET.Tests;

/// <summary>
/// Verifies JSON serialization round-trips for <see cref="AiProviderConfig"/>,
/// <see cref="AiProviderDescriptor"/>, and <see cref="AiProviderPreferences"/>.
/// </summary>
public class DescriptorRoundTripTests
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false };

    [Fact]
    public void AiProviderDescriptor_RoundTrips()
    {
        var original = new AiProviderDescriptor(
            Kind:            AiProviderKind.Ollama,
            Location:        AiProviderLocation.Local,
            DisplayName:     "Ollama",
            DefaultEndpoint: new Uri("http://localhost:11434"),
            RequiresApiKey:  false,
            Capabilities:    AiCapabilities.Chat | AiCapabilities.Embeddings);

        var json   = JsonSerializer.Serialize(original, JsonOpts);
        var result = JsonSerializer.Deserialize<AiProviderDescriptor>(json, JsonOpts);

        Assert.NotNull(result);
        Assert.Equal(original.Kind,            result.Kind);
        Assert.Equal(original.Location,        result.Location);
        Assert.Equal(original.DisplayName,     result.DisplayName);
        Assert.Equal(original.DefaultEndpoint, result.DefaultEndpoint);
        Assert.Equal(original.RequiresApiKey,  result.RequiresApiKey);
        Assert.Equal(original.Capabilities,    result.Capabilities);
    }

    [Fact]
    public void AiProviderConfig_RoundTrips()
    {
        var descriptor = new AiProviderDescriptor(
            AiProviderKind.GitHubModels, AiProviderLocation.Cloud,
            "GitHub Models", new Uri("https://models.inference.ai.azure.com"),
            true, AiCapabilities.Chat);

        var original = new AiProviderConfig(
            Descriptor:       descriptor,
            Endpoint:         new Uri("https://models.inference.ai.azure.com"),
            ApiKey:           "ghp_testtoken",
            ChatModelId:      "gpt-4o-mini",
            EmbeddingModelId: null,
            Options:          new Dictionary<string, string> { ["timeout"] = "30" });

        var json   = JsonSerializer.Serialize(original, JsonOpts);
        var result = JsonSerializer.Deserialize<AiProviderConfig>(json, JsonOpts);

        Assert.NotNull(result);
        Assert.Equal(original.Endpoint,         result.Endpoint);
        Assert.Equal(original.ApiKey,           result.ApiKey);
        Assert.Equal(original.ChatModelId,      result.ChatModelId);
        Assert.Equal(original.EmbeddingModelId, result.EmbeddingModelId);
        Assert.Equal("30",                      result.Options?["timeout"]);
    }

    [Fact]
    public void AiProviderPreferences_DefaultValues()
    {
        var prefs = new AiProviderPreferences();
        Assert.False(prefs.HasBeenAskedOnce);
        Assert.True(prefs.AutoUseIfSingle);
        Assert.Null(prefs.LastProviderKind);
        Assert.Null(prefs.LastEndpoint);
    }

    [Fact]
    public void AiProviderPreferences_RoundTrips()
    {
        var original = new AiProviderPreferences
        {
            HasBeenAskedOnce  = true,
            AutoUseIfSingle   = false,
            LastProviderKind  = AiProviderKind.Ollama,
            LastEndpoint      = new Uri("http://localhost:11434"),
            LastChatModelId   = "llama3:latest"
        };

        var json   = JsonSerializer.Serialize(original, JsonOpts);
        var result = JsonSerializer.Deserialize<AiProviderPreferences>(json, JsonOpts);

        Assert.NotNull(result);
        Assert.True(result.HasBeenAskedOnce);
        Assert.False(result.AutoUseIfSingle);
        Assert.Equal(AiProviderKind.Ollama,             result.LastProviderKind);
        Assert.Equal(new Uri("http://localhost:11434"),  result.LastEndpoint);
        Assert.Equal("llama3:latest",                   result.LastChatModelId);
    }

    [Theory]
    [InlineData(AiProviderKind.Ollama)]
    [InlineData(AiProviderKind.LmStudio)]
    [InlineData(AiProviderKind.OpenAi)]
    [InlineData(AiProviderKind.GitHubModels)]
    [InlineData(AiProviderKind.AzureOpenAi)]
    [InlineData(AiProviderKind.Onnx)]
    [InlineData(AiProviderKind.OpenAiCompatible)]
    public void AiProviderKind_SerializesAsString(AiProviderKind kind)
    {
        var json   = JsonSerializer.Serialize(kind, JsonOpts);
        var result = JsonSerializer.Deserialize<AiProviderKind>(json, JsonOpts);
        Assert.Equal(kind, result);
    }
}
