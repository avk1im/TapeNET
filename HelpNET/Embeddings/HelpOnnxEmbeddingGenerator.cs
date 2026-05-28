using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace HelpNET.Embeddings;

/// <summary>
/// HelpNET-internal ONNX embedding generator that accepts the model and vocabulary
/// as <see cref="Stream"/>s rather than file paths.  Hosts (TapeWinNET) supply the
/// streams from embedded resources; the engine itself never touches the file system.
/// </summary>
/// <remarks>
/// Handles BERT-family sentence-encoder models such as <c>all-MiniLM-L6-v2</c>:
/// <list type="bullet">
///  <item>3-D output <c>[batch, seq, hidden]</c> → sequence mean-pooling</item>
///  <item>2-D output <c>[batch, hidden]</c> → used as-is (pre-pooled)</item>
/// </list>
/// All vectors are L2-normalised before being returned, so cosine similarity
/// can be computed with a plain dot product.
/// </remarks>
internal sealed class HelpOnnxEmbeddingGenerator
    : IEmbeddingGenerator<string, Embedding<float>>
{
    // ── Standard ONNX input tensor names ─────────────────────────────────────

    private const string InputIdName       = "input_ids";
    private const string AttentionMaskName = "attention_mask";
    private const string TokenTypeIdsName  = "token_type_ids";

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly InferenceSession             _session;
    private readonly BertTokenizer                _tokenizer;
    private readonly int                          _maxLength;
    private readonly EmbeddingGeneratorMetadata   _metadata;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the generator by loading the ONNX model and vocabulary from the
    /// supplied <paramref name="options"/>.  Both streams are fully consumed and
    /// the ONNX model is loaded into memory; the caller may dispose the streams
    /// after this constructor returns.
    /// </summary>
    internal HelpOnnxEmbeddingGenerator(OnnxEmbeddingOptions options)
    {
        // Read model bytes from stream (OnnxRuntime needs a byte array)
        byte[] modelBytes;
        using (var ms = new MemoryStream())
        {
            options.ModelStream.CopyTo(ms);
            modelBytes = ms.ToArray();
        }

        _session   = new InferenceSession(modelBytes);
        _tokenizer = BertTokenizer.Create(options.VocabStream);
        _maxLength = options.MaxTokens;
        _metadata  = new EmbeddingGeneratorMetadata(
            "onnx-help",
            new Uri($"onnx://help/{options.ModelId}"),
            options.ModelId,
            options.Dimension);
    }

    // ── IEmbeddingGenerator ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public EmbeddingGeneratorMetadata Metadata => _metadata;

    /// <inheritdoc/>
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string>          values,
        EmbeddingGenerationOptions?  options       = null,
        CancellationToken            cancellationToken = default)
    {
        var texts   = values.ToList();
        var results = new List<Embedding<float>>(texts.Count);

        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var encoding = _tokenizer.EncodeToIds(
                text,
                addSpecialTokens:        true,
                considerPreTokenization: true,
                considerNormalization:   true);

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

            int[] shape  = [1, len];
            var   inputs = BuildInputs(inputIds, attnMask, tokenTypes, shape);

            using var outputs = _session.Run(inputs);
            var vector = ExtractAndNormalize(outputs, attnMask);
            results.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(results));
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? key = null) => null;

    /// <inheritdoc/>
    public void Dispose() => _session.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds ONNX named-value inputs.  <c>token_type_ids</c> is optional and
    /// omitted if the model's metadata does not declare it.
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

        if (_session.InputMetadata.ContainsKey(TokenTypeIdsName))
            inputs.Add(NamedOnnxValue.CreateFromTensor(TokenTypeIdsName, tokenTypeTensor));

        return inputs;
    }

    /// <summary>
    /// Reads the first ONNX output, applies mean-pooling if 3-D, and L2-normalises.
    /// </summary>
    private static float[] ExtractAndNormalize(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        long[] attnMask)
    {
        var first  = outputs.First();
        var tensor = first.AsTensor<float>();
        int[] dims = tensor.Dimensions.ToArray();
        float[] vec;

        if (dims.Length == 3)
        {
            int seqLen    = dims[1];
            int hiddenDim = dims[2];
            int validLen  = (int)attnMask.Sum();
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

    /// <summary>Returns a new L2-normalised copy of <paramref name="vec"/>.</summary>
    private static float[] L2Normalize(float[] vec)
    {
        double sumSq = 0.0;
        foreach (var v in vec) sumSq += v * (double)v;
        float norm = (float)Math.Sqrt(sumSq);
        if (norm < 1e-9f) return vec;
        for (int i = 0; i < vec.Length; i++) vec[i] /= norm;
        return vec;
    }
}
