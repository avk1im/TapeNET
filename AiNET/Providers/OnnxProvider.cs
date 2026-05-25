using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace AiNET.Providers;

/// <summary>
/// Provider adapter for in-process <b>ONNX</b> embedding models.
/// No network round-trip — the model runs entirely on the local CPU/GPU.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capability:</b> embeddings only (<see cref="AiCapabilities.Embeddings"/>).
/// Chat is not offered by this provider.
/// </para>
/// <para>
/// <b>Configuration convention:</b><br/>
/// <see cref="AiProviderConfig.Endpoint"/> must be a <c>file://</c> URI pointing
/// to the <c>.onnx</c> model file.<br/>
/// The vocabulary file (<c>vocab.txt</c>) is assumed to reside in the same
/// directory. Override with <c>Options["VocabPath"]</c>.
/// </para>
/// </remarks>
public sealed class OnnxProvider : IAiProvider
{
    // ── Well-known option keys ────────────────────────────────────────────────

    /// <summary>
    /// Key in <see cref="AiProviderConfig.Options"/> for an explicit vocabulary
    /// file path. When absent, <c>vocab.txt</c> in the model directory is used.
    /// </summary>
    public const string OptionVocabPath = "VocabPath";

    /// <summary>
    /// Key in <see cref="AiProviderConfig.Options"/> for the maximum token
    /// sequence length. Defaults to 512 when absent.
    /// </summary>
    public const string OptionMaxLength = "MaxLength";

    // ── Descriptor ────────────────────────────────────────────────────────────

    private static readonly AiProviderDescriptor _descriptor = new(
        Kind:            AiProviderKind.Onnx,
        Location:        AiProviderLocation.Local,
        DisplayName:     "ONNX (in-process)",
        DefaultEndpoint: null,
        RequiresApiKey:  false,
        Capabilities:    AiCapabilities.Embeddings);

    /// <inheritdoc/>
    public AiProviderDescriptor Descriptor => _descriptor;

    // ── Probe ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// ONNX probing is entirely local: verifies that the <c>.onnx</c> model
    /// file and the vocabulary file both exist on disk. The model is <b>not</b>
    /// loaded during probing to keep startup fast.
    /// </remarks>
    public Task<AiProviderProbeResult> ProbeAsync(
        Uri endpoint, string? apiKey, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!endpoint.IsFile)
                return Task.FromResult(Unhealthy(endpoint, sw,
                    $"Endpoint must be a file:// URI pointing to the .onnx model; got '{endpoint}'."));

            string modelPath = Uri.UnescapeDataString(endpoint.AbsolutePath)
                                  .Replace('/', Path.DirectorySeparatorChar)
                                  .TrimStart(Path.DirectorySeparatorChar);

            // On Windows the path comes in as /C:/..., strip the leading slash
            if (Path.IsPathRooted(modelPath) && modelPath.StartsWith(Path.DirectorySeparatorChar))
                modelPath = modelPath.TrimStart(Path.DirectorySeparatorChar);

            if (!File.Exists(modelPath))
                return Task.FromResult(Unhealthy(endpoint, sw,
                    $"ONNX model file not found: {modelPath}"));

            string vocabPath = ResolveVocabPath(modelPath, options: null);
            if (!File.Exists(vocabPath))
                return Task.FromResult(Unhealthy(endpoint, sw,
                    $"Vocabulary file not found: {vocabPath} " +
                    $"(place vocab.txt next to the model or set Options[\"{OptionVocabPath}\"])"));

            sw.Stop();
            string modelName = Path.GetFileNameWithoutExtension(modelPath);
            return Task.FromResult(new AiProviderProbeResult(
                _descriptor, endpoint,
                IsHealthy:           true,
                DiscoveredChatModels:      [],
                DiscoveredEmbeddingModels: [modelName],
                Latency: sw.Elapsed,
                ErrorMessage: null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Unhealthy(endpoint, sw, ex.Message));
        }
    }

    // ── Client factories ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>ONNX does not offer chat completions — always returns <c>null</c>.</remarks>
    public IChatClient? CreateChatClient(AiProviderConfig config) => null;

    /// <inheritdoc/>
    /// <remarks>
    /// Loads the ONNX session and BertTokenizer from disk. Returns <c>null</c>
    /// if the model or vocabulary file cannot be found.
    /// </remarks>
    public IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(
        AiProviderConfig config)
    {
        try
        {
            string modelPath = FileUriToPath(config.Endpoint);
            if (!File.Exists(modelPath))
                return null;

            string vocabPath = ResolveVocabPath(modelPath, config.Options);
            if (!File.Exists(vocabPath))
                return null;

            int maxLength = 512;
            if (config.Options?.TryGetValue(OptionMaxLength, out var maxStr) == true
                && int.TryParse(maxStr, out var parsed))
                maxLength = parsed;

            var session   = new InferenceSession(modelPath);
            var tokenizer = BertTokenizer.Create(vocabPath);

            return new OnnxEmbeddingGenerator(session, tokenizer, modelPath, maxLength);
        }
        catch
        {
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a <c>file://</c> URI to a local file-system path.
    /// </summary>
    internal static string FileUriToPath(Uri fileUri)
    {
        // Uri.LocalPath handles Windows drive letters and UNC paths correctly.
        return fileUri.LocalPath;
    }

    /// <summary>
    /// Returns the vocabulary file path: either the explicit
    /// <see cref="OptionVocabPath"/> override or <c>vocab.txt</c>
    /// in the same directory as the model.
    /// </summary>
    private static string ResolveVocabPath(
        string modelPath,
        IReadOnlyDictionary<string, string>? options)
    {
        if (options?.TryGetValue(OptionVocabPath, out var explicitPath) == true
            && !string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        return Path.Combine(Path.GetDirectoryName(modelPath) ?? string.Empty, "vocab.txt");
    }

    private static AiProviderProbeResult Unhealthy(
        Uri endpoint, System.Diagnostics.Stopwatch sw, string message)
    {
        sw.Stop();
        return new AiProviderProbeResult(
            _descriptor, endpoint,
            IsHealthy:           false,
            DiscoveredChatModels:      [],
            DiscoveredEmbeddingModels: [],
            Latency:      sw.Elapsed,
            ErrorMessage: message);
    }
}

