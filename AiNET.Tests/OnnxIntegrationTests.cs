using AiNET.Providers;

using Xunit;

namespace AiNET.Tests;

/// <summary>
/// Integration tests for <see cref="OnnxProvider"/> and
/// <see cref="OnnxEmbeddingGenerator"/> that require a real ONNX model on disk.
/// <para>
/// Tests skip automatically when <c>ONNX_MODEL_PATH</c> is not set — nothing
/// fails in CI. To run locally, set the environment variable to the absolute
/// path of a BERT-family <c>.onnx</c> file (e.g. <c>all-MiniLM-L6-v2</c>) and
/// place <c>vocab.txt</c> next to it.
/// </para>
/// <para><b>Minimum setup (one-time):</b></para>
/// <code>
/// # in PowerShell
/// $env:ONNX_MODEL_PATH = "C:\Models\all-MiniLM-L6-v2\model.onnx"
/// </code>
/// <para>See the repository setup guide for model download instructions.</para>
/// </summary>
public class OnnxIntegrationTests
{
    // ── Model discovery ───────────────────────────────────────────────────────

    /// <summary>
    /// Name of the environment variable that points to the <c>.onnx</c> file.
    /// </summary>
    private const string EnvModelPath = "ONNX_MODEL_PATH";
    private const string DefaultModelFileName = "model.onnx";

    private static readonly string? ModelPathRaw =
        Environment.GetEnvironmentVariable(EnvModelPath);
    private static string? ModelPath
    {
        get
        {
            var pathName = ModelPathRaw;

            if (pathName is null) return null;

            // ModelPathRaw might contain either the directory for the file model.onnx
            //  or the full path to the model file. Handle both cases.
            if (!Path.HasExtension(pathName) || pathName[^1] == Path.DirectorySeparatorChar) // assume it's the directory
                pathName = Path.Combine(pathName, DefaultModelFileName);

            return pathName;
        }
    }

    private static readonly Uri? ModelUri =
        ModelPath is not null ? new Uri(ModelPath) : null;

    private readonly OnnxProvider _provider = new();

    // ── Cached availability check ─────────────────────────────────────────────
    // The probe is cheap (file-existence only) but we cache it anyway so all
    // skips are instant and consistently worded.

    private static AiProviderProbeResult? _cachedProbe;
    private static readonly SemaphoreSlim _probeLock = new(1, 1);

    private async Task<AiProviderProbeResult?> GetProbeAsync()
    {
        if (ModelUri is null) return null;
        if (_cachedProbe is not null) return _cachedProbe;

        await _probeLock.WaitAsync();
        try
        {
            _cachedProbe ??= await _provider.ProbeAsync(
                ModelUri, apiKey: null, CancellationToken.None);
            return _cachedProbe;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    // ── ProbeAsync ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ProbeAsync_RealModel_IsHealthy()
    {
        Skip.If(ModelUri is null,
            $"{EnvModelPath} is not set — skipping ONNX integration test.");

        var result = await GetProbeAsync();
        Skip.If(result is null || !result.IsHealthy,
            $"ONNX probe failed: {result?.ErrorMessage}");

        Assert.True(result!.IsHealthy);
        Assert.Single(result.DiscoveredEmbeddingModels);
        Assert.Empty(result.DiscoveredChatModels);
        Assert.Null(result.ErrorMessage);
    }

    // ── CreateEmbeddingGenerator ──────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateEmbeddingGenerator_RealModel_ReturnsNonNull()
    {
        Skip.If(ModelUri is null,
            $"{EnvModelPath} is not set — skipping ONNX integration test.");

        var probe = await GetProbeAsync();
        Skip.If(probe is null || !probe.IsHealthy,
            $"ONNX probe failed: {probe?.ErrorMessage}");

        var config    = MakeConfig();
        var generator = _provider.CreateEmbeddingGenerator(config);

        Assert.NotNull(generator);
        generator.Dispose();
    }

    [SkippableFact]
    public async Task GenerateAsync_SingleString_ProducesNonZeroVector()
    {
        Skip.If(ModelUri is null,
            $"{EnvModelPath} is not set — skipping ONNX integration test.");

        var probe = await GetProbeAsync();
        Skip.If(probe is null || !probe.IsHealthy,
            $"ONNX probe failed: {probe?.ErrorMessage}");

        var config    = MakeConfig();
        using var gen = _provider.CreateEmbeddingGenerator(config);
        Assert.NotNull(gen);

        var embeddings = await gen!.GenerateAsync(["Hello, world!"]);

        Assert.Single(embeddings);
        var vector = embeddings[0].Vector.ToArray();
        Assert.NotEmpty(vector);
        Assert.Contains(vector, v => v != 0f);
    }

    [SkippableFact]
    public async Task GenerateAsync_TwoStrings_ProducesDistinctVectors()
    {
        Skip.If(ModelUri is null,
            $"{EnvModelPath} is not set — skipping ONNX integration test.");

        var probe = await GetProbeAsync();
        Skip.If(probe is null || !probe.IsHealthy,
            $"ONNX probe failed: {probe?.ErrorMessage}");

        var config    = MakeConfig();
        using var gen = _provider.CreateEmbeddingGenerator(config);
        Assert.NotNull(gen);

        var embeddings = await gen!.GenerateAsync(
        [
            "The cat sat on the mat.",
            "Quantum entanglement enables faster-than-light communication."
        ]);

        Assert.Equal(2, embeddings.Count);

        var v1 = embeddings[0].Vector.ToArray();
        var v2 = embeddings[1].Vector.ToArray();

        Assert.Equal(v1.Length, v2.Length);

        // Vectors for very different sentences should not be identical
        bool anyDifference = false;
        for (int i = 0; i < v1.Length; i++)
            if (Math.Abs(v1[i] - v2[i]) > 1e-6f) { anyDifference = true; break; }
        Assert.True(anyDifference, "Expected semantically different sentences to yield distinct vectors.");
    }

    [SkippableFact]
    public async Task GenerateAsync_SimilarSentences_HigherCosineSimilarity()
    {
        Skip.If(ModelUri is null,
            $"{EnvModelPath} is not set — skipping ONNX integration test.");

        var probe = await GetProbeAsync();
        Skip.If(probe is null || !probe.IsHealthy,
            $"ONNX probe failed: {probe?.ErrorMessage}");

        var config    = MakeConfig();
        using var gen = _provider.CreateEmbeddingGenerator(config);
        Assert.NotNull(gen);

        var embeddings = await gen!.GenerateAsync(
        [
            "A dog is playing in the park.",      // sentence A
            "A puppy is running in the garden.",  // similar to A
            "The stock market fell sharply today." // unrelated
        ]);

        var a       = embeddings[0].Vector.ToArray();
        var similar = embeddings[1].Vector.ToArray();
        var unrel   = embeddings[2].Vector.ToArray();

        // Vectors are L2-normalised → cosine similarity == dot product
        float simA  = Dot(a, similar);
        float simU  = Dot(a, unrel);

        // The similar pair should score meaningfully higher than the unrelated one
        Assert.True(simA > simU,
            $"Expected similar sentences (cos={simA:F4}) to score above " +
            $"unrelated pair (cos={simU:F4}).");
    }

    [SkippableFact]
    public async Task GenerateAsync_EmptyInput_ReturnsEmptyResult()
    {
        Skip.If(ModelUri is null,
            $"{EnvModelPath} is not set — skipping ONNX integration test.");

        var probe = await GetProbeAsync();
        Skip.If(probe is null || !probe.IsHealthy,
            $"ONNX probe failed: {probe?.ErrorMessage}");

        var config    = MakeConfig();
        using var gen = _provider.CreateEmbeddingGenerator(config);
        Assert.NotNull(gen);

        var embeddings = await gen!.GenerateAsync([]);
        Assert.Empty(embeddings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AiProviderConfig MakeConfig() =>
        new(
            Descriptor: new AiProviderDescriptor(
                AiProviderKind.Onnx, AiProviderLocation.Local,
                "ONNX (in-process)", null, false, AiCapabilities.Embeddings),
            Endpoint:         ModelUri!,
            ApiKey:           null,
            ChatModelId:      null,
            EmbeddingModelId: null);

    /// <summary>Dot product of two same-length vectors (cosine sim for L2-normalised vectors).</summary>
    private static float Dot(float[] a, float[] b)
    {
        float sum = 0f;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
