using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AiNET.Providers;

/// <summary>
/// Generates embeddings in-process using an ONNX model loaded from disk.
/// Intended for BERT-family sentence-encoder models such as
/// <c>all-MiniLM-L6-v2</c>.
/// </summary>
/// <remarks>
/// <b>Tensor layout handled automatically:</b>
/// <list type="bullet">
///  <item>3-D output <c>[batch, seq, hidden]</c> → sequence mean-pooling</item>
///  <item>2-D output <c>[batch, hidden]</c> → used as-is (pre-pooled CLS token)</item>
/// </list>
/// All vectors are L2-normalised before being returned, so cosine similarity
/// can be computed cheaply with a plain dot product.
/// </remarks>
internal sealed class OnnxEmbeddingGenerator
    : IEmbeddingGenerator<string, Embedding<float>>
{
    // ── State ────────────────────────────────────────────────────────────────

    private readonly InferenceSession _session;
    private readonly BertTokenizer    _tokenizer;
    private readonly int              _maxLength;
    private readonly EmbeddingGeneratorMetadata _metadata;

    // ── Standard ONNX input tensor names for BERT-style models ───────────────

    private const string InputIdName       = "input_ids";
    private const string AttentionMaskName = "attention_mask";
    private const string TokenTypeIdsName  = "token_type_ids";

    // ── Construction ─────────────────────────────────────────────────────────

    /// <param name="session">ONNX inference session (caller-created).</param>
    /// <param name="tokenizer">BertTokenizer loaded from the matching vocabulary file.</param>
    /// <param name="modelPath">Path of the .onnx file (used to populate metadata).</param>
    /// <param name="maxLength">Maximum token sequence length (default 512).</param>
    internal OnnxEmbeddingGenerator(
        InferenceSession session,
        BertTokenizer    tokenizer,
        string           modelPath,
        int              maxLength = 512)
    {
        _session   = session;
        _tokenizer = tokenizer;
        _maxLength = maxLength;
        _metadata  = new EmbeddingGeneratorMetadata(
            "onnx", new Uri($"file:///{modelPath.Replace('\\', '/')}"),
            Path.GetFileNameWithoutExtension(modelPath), null);
    }

    // ── IEmbeddingGenerator ──────────────────────────────────────────────────

    public EmbeddingGeneratorMetadata Metadata => _metadata;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        var results = new List<Embedding<float>>(texts.Count);

        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var encoding = _tokenizer.EncodeToIds(
                text,
                addSpecialTokens:         true,
                considerPreTokenization:  true,
                considerNormalization:    true);

            // Truncate to max allowed length
            int len = Math.Min(encoding.Count, _maxLength);

            var inputIds   = new long[len];
            var attnMask   = new long[len];
            var tokenTypes = new long[len];

            for (int i = 0; i < len; i++)
            {
                inputIds[i]   = encoding[i];
                attnMask[i]   = 1L;
                tokenTypes[i] = 0L;
            }

            // ONNX expects batch dimension → shape [1, len] (dimensions are int[])
            int[] shape = [1, len];

            var inputs = BuildInputs(inputIds, attnMask, tokenTypes, shape);
            using var outputs = _session.Run(inputs);

            var vector = ExtractAndNormalize(outputs, attnMask);
            results.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(results));
    }

    public object? GetService(Type serviceType, object? key = null) => null;

    public void Dispose()
    {
        _session.Dispose();
        // BertTokenizer does not implement IDisposable; nothing to release.
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the ONNX named-value input list. <c>token_type_ids</c> is
    /// optional — omitted if not declared in the model's metadata.
    /// </summary>
    private List<NamedOnnxValue> BuildInputs(
        long[] inputIds, long[] attnMask, long[] tokenTypes, int[] shape)
    {
        var inputIdTensor   = new DenseTensor<long>(new Memory<long>(inputIds),   shape);
        var attnMaskTensor  = new DenseTensor<long>(new Memory<long>(attnMask),   shape);
        var tokenTypeTensor = new DenseTensor<long>(new Memory<long>(tokenTypes), shape);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(InputIdName,       inputIdTensor),
            NamedOnnxValue.CreateFromTensor(AttentionMaskName, attnMaskTensor)
        };

        // Some models (e.g., DistilBERT) omit token_type_ids
        if (_session.InputMetadata.ContainsKey(TokenTypeIdsName))
            inputs.Add(NamedOnnxValue.CreateFromTensor(TokenTypeIdsName, tokenTypeTensor));

        return inputs;
    }

    /// <summary>
    /// Reads the first ONNX output, applies mean-pooling if 3-D, then
    /// L2-normalises the resulting vector.
    /// </summary>
    private static float[] ExtractAndNormalize(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        long[] attnMask)
    {
        var first  = outputs.First();
        var tensor = first.AsTensor<float>();

        int[] dims   = tensor.Dimensions.ToArray();
        float[] vec;

        if (dims.Length == 3)
        {
            // Shape [1, seq, hidden] → mean-pool over the non-padding positions
            int seqLen    = dims[1];
            int hiddenDim = dims[2];
            int validLen  = (int)attnMask.Sum(); // non-padded token count
            vec = new float[hiddenDim];
            for (int s = 0; s < validLen && s < seqLen; s++)
                for (int h = 0; h < hiddenDim; h++)
                    vec[h] += tensor[0, s, h];
            int denom = Math.Max(validLen, 1);
            for (int h = 0; h < hiddenDim; h++)
                vec[h] /= denom;
        }
        else if (dims.Length == 2)
        {
            // Shape [1, hidden] — model already pools internally (CLS token)
            int hiddenDim = dims[1];
            vec = new float[hiddenDim];
            for (int h = 0; h < hiddenDim; h++)
                vec[h] = tensor[0, h];
        }
        else
        {
            throw new InvalidOperationException(
                $"Unexpected ONNX output shape: [{string.Join(", ", dims)}]. " +
                 "Expected 2-D [batch, hidden] or 3-D [batch, seq, hidden].");
        }

        return L2Normalize(vec);
    }

    /// <summary>Returns a new array that is the L2-normalised version of <paramref name="vec"/>.</summary>
    private static float[] L2Normalize(float[] vec)
    {
        double sumSq = 0.0;
        foreach (var v in vec) sumSq += v * (double)v;
        float norm = (float)Math.Sqrt(sumSq);
        if (norm < 1e-9f) return vec; // zero vector — leave as-is
        for (int i = 0; i < vec.Length; i++) vec[i] /= norm;
        return vec;
    }
}
