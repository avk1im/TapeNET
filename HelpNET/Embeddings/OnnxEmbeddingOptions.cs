namespace HelpNET.Embeddings;

/// <summary>
/// Configuration for <see cref="HelpOnnxEmbeddingGenerator"/> — the HelpNET-internal
/// in-process ONNX embedding generator loaded from host-supplied streams.
/// </summary>
/// <param name="ModelStream">
/// Readable stream of the <c>.onnx</c> model file.
/// The stream is consumed during construction and may be disposed afterwards.
/// </param>
/// <param name="VocabStream">
/// Readable stream of the WordPiece vocabulary file (<c>vocab.txt</c>).
/// </param>
/// <param name="ModelId">
/// Stable identifier for the model (e.g. <c>"all-MiniLM-L6-v2"</c>).
/// Must match <see cref="Content.HelpEmbeddingBundle.ModelId"/> when loading a
/// precomputed bundle.
/// </param>
/// <param name="Dimension">
/// Expected output vector dimension.  Used for bundle validation.
/// </param>
/// <param name="MaxTokens">
/// Maximum token sequence length passed to the model.  Default is 512.
/// </param>
public sealed record OnnxEmbeddingOptions(
    Stream ModelStream,
    Stream VocabStream,
    string ModelId,
    int    Dimension,
    int    MaxTokens = 512);
