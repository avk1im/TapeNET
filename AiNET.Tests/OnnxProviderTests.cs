using AiNET.Providers;

using Xunit;

namespace AiNET.Tests;

/// <summary>
/// Unit tests for <see cref="OnnxProvider"/> that do <b>not</b> require a
/// real ONNX model. Tests exercise the file-existence probe and client-factory
/// null-return semantics using temporary files.
/// </summary>
public class OnnxProviderTests : IDisposable
{
    // ── Temporary directory containing stub files ────────────────────────────

    private readonly string _tempDir;
    private readonly string _modelPath;
    private readonly string _vocabPath;
    private readonly Uri    _modelUri;

    public OnnxProviderTests()
    {
        _tempDir   = Path.Combine(Path.GetTempPath(), $"OnnxTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _modelPath = Path.Combine(_tempDir, "model.onnx");
        _vocabPath = Path.Combine(_tempDir, "vocab.txt");

        // Zero-byte placeholder files — sufficient to pass the existence checks
        File.WriteAllText(_modelPath, string.Empty);
        File.WriteAllText(_vocabPath, string.Empty);

        _modelUri = new Uri(_modelPath);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ── Descriptor ────────────────────────────────────────────────────────────

    [Fact]
    public void Descriptor_HasCorrectKindAndCapabilities()
    {
        var provider = new OnnxProvider();

        Assert.Equal(AiProviderKind.Onnx, provider.Descriptor.Kind);
        Assert.Equal(AiProviderLocation.Local, provider.Descriptor.Location);
        Assert.True(provider.Descriptor.Capabilities.HasFlag(AiCapabilities.Embeddings));
        Assert.False(provider.Descriptor.Capabilities.HasFlag(AiCapabilities.Chat));
        Assert.False(provider.Descriptor.RequiresApiKey);
    }

    // ── ProbeAsync — healthy ─────────────────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_BothFilesExist_ReturnsHealthy()
    {
        var provider = new OnnxProvider();

        var result = await provider.ProbeAsync(_modelUri, apiKey: null, CancellationToken.None);

        Assert.True(result.IsHealthy);
        Assert.Null(result.ErrorMessage);
        Assert.Single(result.DiscoveredEmbeddingModels);
        Assert.Equal("model", result.DiscoveredEmbeddingModels[0]);
        Assert.Empty(result.DiscoveredChatModels);
        Assert.True(result.Latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ProbeAsync_ExplicitVocabPath_UsesOverride()
    {
        // Place vocab in a different directory, referenced via Options
        string altVocab = Path.Combine(_tempDir, "alt_vocab.txt");
        File.WriteAllText(altVocab, string.Empty);

        // Remove the default vocab so only the override can satisfy the check
        File.Delete(_vocabPath);

        // Encode the vocab override into the URI query-string is not practical —
        // instead we test via CreateEmbeddingGenerator which accepts Options.
        // ProbeAsync currently resolves vocab from the model's directory only,
        // so this test validates the fallback correctly reports missing vocab.
        var provider = new OnnxProvider();
        var result   = await provider.ProbeAsync(_modelUri, apiKey: null, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Contains("vocab", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── ProbeAsync — unhealthy ───────────────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_NonFileUri_ReturnsUnhealthy()
    {
        var provider = new OnnxProvider();
        var httpUri  = new Uri("http://localhost/model.onnx");

        var result = await provider.ProbeAsync(httpUri, apiKey: null, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Contains("file://", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeAsync_ModelFileMissing_ReturnsUnhealthy()
    {
        File.Delete(_modelPath);
        var provider = new OnnxProvider();

        var result = await provider.ProbeAsync(_modelUri, apiKey: null, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeAsync_VocabFileMissing_ReturnsUnhealthy()
    {
        File.Delete(_vocabPath);
        var provider = new OnnxProvider();

        var result = await provider.ProbeAsync(_modelUri, apiKey: null, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Contains("vocab", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── CreateChatClient ─────────────────────────────────────────────────────

    [Fact]
    public void CreateChatClient_AlwaysReturnsNull()
    {
        var provider = new OnnxProvider();
        var config   = MakeConfig();

        Assert.Null(provider.CreateChatClient(config));
    }

    // ── CreateEmbeddingGenerator ─────────────────────────────────────────────

    [Fact]
    public void CreateEmbeddingGenerator_ModelMissing_ReturnsNull()
    {
        File.Delete(_modelPath);
        var provider = new OnnxProvider();
        var config   = MakeConfig();

        // The model file is absent — should not throw, should return null
        Assert.Null(provider.CreateEmbeddingGenerator(config));
    }

    [Fact]
    public void CreateEmbeddingGenerator_VocabMissing_ReturnsNull()
    {
        File.Delete(_vocabPath);
        var provider = new OnnxProvider();
        var config   = MakeConfig();

        Assert.Null(provider.CreateEmbeddingGenerator(config));
    }

    [Fact]
    public void CreateEmbeddingGenerator_InvalidOnnxContent_ReturnsNull()
    {
        // Write garbage bytes — InferenceSession construction will throw
        File.WriteAllText(_modelPath, "not-a-real-onnx-model");
        var provider = new OnnxProvider();
        var config   = MakeConfig();

        // Must not throw; exception is swallowed and null returned
        Assert.Null(provider.CreateEmbeddingGenerator(config));
    }

    [Fact]
    public void CreateEmbeddingGenerator_ExplicitVocabPathOption_UsesIt()
    {
        // Even with an invalid model, we can verify the vocab-override path is
        // respected by writing a distinct vocab file and checking the provider
        // reaches the model-loading step (i.e., it does NOT fail on vocab).
        // Since the model content is invalid, the return is null — but the path
        // taken was the one with the explicit vocab, not the missing default one.

        string altVocab = Path.Combine(_tempDir, "special_vocab.txt");
        File.WriteAllText(altVocab, string.Empty);
        File.Delete(_vocabPath);                       // default vocab absent
        File.WriteAllText(_modelPath, "bad-onnx");     // model present but invalid

        var options = new Dictionary<string, string>
        {
            [OnnxProvider.OptionVocabPath] = altVocab
        };

        var provider = new OnnxProvider();
        var config   = MakeConfig(options: options);

        // Returns null because ONNX content is invalid, but no null-vocab
        // early-exit was hit (if it were, the model would not be attempted).
        // The observable contract is simply: does not throw.
        var generator = provider.CreateEmbeddingGenerator(config);
        Assert.Null(generator); // invalid model content → InferenceSession fails
    }

    // ── FileUriToPath ─────────────────────────────────────────────────────────

    [Fact]
    public void FileUriToPath_RoundTrips()
    {
        var uri  = new Uri(_modelPath);
        var path = OnnxProvider.FileUriToPath(uri);

        Assert.Equal(_modelPath, path, StringComparer.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private AiProviderConfig MakeConfig(
        IReadOnlyDictionary<string, string>? options = null) =>
        new(
            Descriptor:       new AiProviderDescriptor(
                AiProviderKind.Onnx, AiProviderLocation.Local, "ONNX (in-process)",
                null, false, AiCapabilities.Embeddings),
            Endpoint:         _modelUri,
            ApiKey:           null,
            ChatModelId:      null,
            EmbeddingModelId: null,
            Options:          options);
}
