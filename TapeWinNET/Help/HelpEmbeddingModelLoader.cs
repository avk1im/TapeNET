using System.IO;
using System.Reflection;
using System.Windows;

using HelpNET.Embeddings;
using Microsoft.Extensions.Logging;

namespace TapeWinNET.Help;


/// <summary>
/// Loads the (exchangeable) ONNX embedding model shipped next to the executable
/// and wraps it into <see cref="OnnxEmbeddingOptions"/> for semantic help search.
/// </summary>
/// <remarks>
/// The model files live in <c>HelpModel\model.onnx</c> + <c>HelpModel\vocab.txt</c>;
/// the model id, dimension and token limit are baked into the assembly by the build
/// (see <c>TapeWinNET.csproj</c>), so they always match the embedded embedding bundle.
/// Returns <c>null</c> when any piece is missing — help then runs in lexical mode.
/// </remarks>
internal static class HelpEmbeddingModelLoader
{
    private const string ModelFolder = "HelpModel";
    private const string ModelFile   = "model.onnx";
    private const string VocabFile   = "vocab.txt";

    private const string MetaModelId   = "HelpEmbeddingModelId";
    private const string MetaDimension = "HelpEmbeddingDimension";
    private const string MetaMaxTokens = "HelpEmbeddingMaxTokens";

    private const string LexicalPrefix = "Help semantic search unavailable (lexical mode): ";

    private static readonly Lazy<OnnxEmbeddingOptions?> _options =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Process-wide ONNX options, or <c>null</c> if no model is deployed.
    /// The same instance is returned on every call so HelpNET loads the model only once.
    /// </summary>
    public static OnnxEmbeddingOptions? Options => _options.Value;

    // Runs once per process (via the Lazy above), so the outcome is reported once.
    private static OnnxEmbeddingOptions? Load()
    {
        var (options, message) = TryLoad();
        Report(message, isFailure: options is null);
        return options;
    }

    /// <summary>
    /// Reports the outcome to the tracing logger and, as a sub-item, to the main
    ///  window's log pane (the AI provider selection supplies the top-level item).
    /// </summary>
    private static void Report(string message, bool isFailure)
    {
        var logger = App.LoggerFactory.CreateLogger(typeof(HelpEmbeddingModelLoader).FullName!);
        if (isFailure)
            logger.LogWarning("{Message}", message);
        else
            logger.LogInformation("{Message}", message);

        var app = Application.Current;
        app?.Dispatcher.BeginInvoke(() =>
        {
            if (app.MainWindow is MainWindow mw)
                mw.OnHelpSessionInfo(null, message);
        });
    }

    /// <summary>
    /// Loads the deployed model without reporting; <c>null</c> options plus the reason on failure.
    /// Internal for tests, which exercise exactly the production loading path.
    /// </summary>
    internal static (OnnxEmbeddingOptions? Options, string Message) TryLoad()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? modelId = GetMetadata(assembly, MetaModelId);
            if (string.IsNullOrEmpty(modelId)
                || !int.TryParse(GetMetadata(assembly, MetaDimension), out int dimension))
                return (null, $"{LexicalPrefix}no embedding model configured in this build.");
            if (!int.TryParse(GetMetadata(assembly, MetaMaxTokens), out int maxTokens))
                maxTokens = 512;

            string dir       = Path.Combine(AppContext.BaseDirectory, ModelFolder);
            string modelPath = Path.Combine(dir, ModelFile);
            string vocabPath = Path.Combine(dir, VocabFile);
            if (!File.Exists(modelPath) || !File.Exists(vocabPath))
                return (null, $"{LexicalPrefix}embedding model '{modelId}' not found in '{dir}'.");

            // Seekable read-only file streams (not in-memory copies of the ~90 MB model):
            //  HelpNET rewinds them if it has to (re)load the model.
            var options = new OnnxEmbeddingOptions(
                new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                new FileStream(vocabPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                modelId, dimension, maxTokens);
            return (options, $"Help semantic search: embedding model '{modelId}' ({dimension}-dim) loaded.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{LexicalPrefix}cannot open embedding model ({ex.Message}).");
        }
    }

    private static string? GetMetadata(Assembly assembly, string key)
        => assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                   .FirstOrDefault(a => a.Key == key)?.Value;
}
