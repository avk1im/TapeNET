using HelpNET.Embeddings;

using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Integration tests for <see cref="HelpOnnxEmbeddingGenerator"/> — the HelpNET-internal
/// in-process ONNX embedding engine — that require a real BERT-family <c>.onnx</c> model
/// on disk together with its <c>vocab.txt</c>.
/// <para>
/// Tests skip automatically when <c>ONNX_MODEL_PATH</c> is not set (or the model /
/// vocabulary file is missing), so nothing fails in CI. To run locally:
/// </para>
/// <code>
/// # in PowerShell
/// $env:ONNX_MODEL_PATH = "C:\Models\all-MiniLM-L6-v2\model.onnx"
/// # vocab.txt must sit next to model.onnx
/// </code>
/// <para>
/// The generator is constructed from <see cref="Stream"/>s (matching how TapeWinNET
/// supplies the model from embedded resources), so these tests open the files as
/// streams rather than passing paths.
/// </para>
/// </summary>
public class OnnxEmbeddingGeneratorTests
{
    // ── Model discovery ───────────────────────────────────────────────────────

    private const string EnvModelPath = "ONNX_MODEL_PATH";

    // Known parameters for all-MiniLM-L6-v2 — the reference sentence encoder.
    private const string ModelId   = "all-MiniLM-L6-v2";
    private const int    Dimension = 384;

    private static readonly string? ModelPath =
        Environment.GetEnvironmentVariable(EnvModelPath);

    private static string? VocabPath =>
        ModelPath is null
            ? null
            : Path.Combine(Path.GetDirectoryName(ModelPath) ?? ".", "vocab.txt");

    private static string? SkipReason()
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
            return $"{EnvModelPath} is not set — skipping ONNX embedding test.";
        if (!File.Exists(ModelPath))
            return $"ONNX model not found at {ModelPath}.";
        if (VocabPath is null || !File.Exists(VocabPath))
            return $"vocab.txt not found next to the model ({VocabPath}).";
        return null;
    }

    /// <summary>
    /// Builds a generator from on-disk model + vocab streams.  The streams are
    /// fully consumed by the constructor, so they are disposed immediately after.
    /// </summary>
    private static HelpOnnxEmbeddingGenerator CreateGenerator()
    {
        using var modelStream = File.OpenRead(ModelPath!);
        using var vocabStream = File.OpenRead(VocabPath!);

        var options = new OnnxEmbeddingOptions(
            ModelStream: modelStream,
            VocabStream: vocabStream,
            ModelId:     ModelId,
            Dimension:   Dimension);

        return new HelpOnnxEmbeddingGenerator(options);
    }

    /// <summary>Dot product of two same-length vectors (cosine sim for L2-normalised vectors).</summary>
    private static float Dot(float[] a, float[] b)
    {
        float sum = 0f;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Metadata_ReflectsConfiguredModel()
    {
        var reason = SkipReason();
        Skip.If(reason is not null, reason ?? "");

        using var gen = CreateGenerator();

        Assert.Equal(ModelId, gen.Metadata.DefaultModelId);
        Assert.Equal(Dimension, gen.Metadata.DefaultModelDimensions);
    }

    // ── Single vector ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GenerateAsync_SingleString_ProducesNormalisedVectorOfExpectedDimension()
    {
        var reason = SkipReason();
        Skip.If(reason is not null, reason ?? "");

        using var gen = CreateGenerator();

        var embeddings = await gen.GenerateAsync(["Insert a tape and start a backup."]);

        Assert.Single(embeddings);
        var vector = embeddings[0].Vector.ToArray();

        Assert.Equal(Dimension, vector.Length);
        Assert.Contains(vector, v => v != 0f);

        // The generator L2-normalises every vector, so its magnitude must be ~1.
        float norm = MathF.Sqrt(vector.Sum(v => v * v));
        Assert.InRange(norm, 0.99f, 1.01f);
    }

    [SkippableFact]
    public async Task GenerateAsync_EmptyInput_ReturnsEmptyResult()
    {
        var reason = SkipReason();
        Skip.If(reason is not null, reason ?? "");

        using var gen = CreateGenerator();

        var embeddings = await gen.GenerateAsync([]);
        Assert.Empty(embeddings);
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GenerateAsync_SameText_IsDeterministic()
    {
        var reason = SkipReason();
        Skip.If(reason is not null, reason ?? "");

        using var gen = CreateGenerator();

        var first  = await gen.GenerateAsync(["restore my files from tape"]);
        var second = await gen.GenerateAsync(["restore my files from tape"]);

        var v1 = first[0].Vector.ToArray();
        var v2 = second[0].Vector.ToArray();

        Assert.Equal(v1.Length, v2.Length);
        for (int i = 0; i < v1.Length; i++)
            Assert.Equal(v1[i], v2[i], precision: 4);
    }

    // ── Cosine sanity on known phrase pairs ───────────────────────────────────

    [SkippableFact]
    public async Task GenerateAsync_SimilarPhrases_OutscoreUnrelatedPhrase()
    {
        var reason = SkipReason();
        Skip.If(reason is not null, reason ?? "");

        using var gen = CreateGenerator();

        var embeddings = await gen.GenerateAsync(
        [
            "How do I back up my files to tape?", // anchor
            "Creating a backup of my data onto a tape drive.", // semantically similar
            "The weather forecast predicts rain tomorrow."     // unrelated
        ]);

        var anchor  = embeddings[0].Vector.ToArray();
        var similar = embeddings[1].Vector.ToArray();
        var unrel   = embeddings[2].Vector.ToArray();

        // Vectors are L2-normalised → cosine similarity == dot product.
        float simRelated   = Dot(anchor, similar);
        float simUnrelated = Dot(anchor, unrel);

        Assert.True(simRelated > simUnrelated,
            $"Expected related phrases (cos={simRelated:F4}) to outscore the " +
            $"unrelated phrase (cos={simUnrelated:F4}).");
    }

    [SkippableFact]
    public async Task GenerateAsync_DifferentPhrases_ProduceDistinctVectors()
    {
        var reason = SkipReason();
        Skip.If(reason is not null, reason ?? "");

        using var gen = CreateGenerator();

        var embeddings = await gen.GenerateAsync(
        [
            "Backing up files to a tape drive.",
            "Quantum entanglement and faster-than-light signalling."
        ]);

        var v1 = embeddings[0].Vector.ToArray();
        var v2 = embeddings[1].Vector.ToArray();

        Assert.Equal(v1.Length, v2.Length);

        bool anyDifference = false;
        for (int i = 0; i < v1.Length; i++)
            if (Math.Abs(v1[i] - v2[i]) > 1e-6f) { anyDifference = true; break; }

        Assert.True(anyDifference,
            "Expected semantically different sentences to yield distinct vectors.");
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GenerateAsync_CancelledToken_Throws()
    {
        var reason = SkipReason();
        Skip.If(reason is not null, reason ?? "");

        using var gen = CreateGenerator();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gen.GenerateAsync(["anything"], null, cts.Token));
    }
}



