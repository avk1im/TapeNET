using HelpNET.Assistants;
using HelpNET.Session;
using TapeWinNET.Help;
using Xunit;

namespace TapeWinNET.Tests;


/// <summary>
/// End-to-end tests of the <b>production</b> semantic help workflow:
/// <see cref="HelpEmbeddingModelLoader"/> loads the deployed ONNX model (<c>HelpModel\</c>
/// next to the binaries, id/dimension from the TapeWin assembly metadata), and
/// <see cref="HelpSessionFactory"/> pairs it with the embedding bundle that the build
/// embedded into TapeWin. This is exactly what TapeWinNET does at runtime.
/// </summary>
/// <remarks>
/// Skipped when the build had no model (<c>HelpEmbeddingsEnabled</c> false) — help then
///  runs in lexical mode by design.
/// </remarks>
public sealed class HelpSemanticSearchWorkflowTests
{
    private static readonly HelpSessionOptions Options = new(HomeTopicId: "home", DefaultTopK: 6);

    private static HelpNET.Embeddings.OnnxEmbeddingOptions LoadOrSkip()
    {
        var (options, message) = HelpEmbeddingModelLoader.TryLoad();
        Skip.If(options is null, message);
        return options!;
    }

    [SkippableFact]
    public async Task ModelAndBundle_Match()
    {
        var options = LoadOrSkip();

        var bundle = await new EmbeddedResourceHelpContentSource().TryLoadEmbeddingBundleAsync(CancellationToken.None);

        Assert.NotNull(bundle);
        Assert.Equal(options.ModelId, bundle!.ModelId, ignoreCase: true);
        Assert.Equal(options.Dimension, bundle.Dimension);
    }

    [SkippableFact]
    public async Task Session_WithoutAiProvider_UsesSemanticMode()
    {
        var options = LoadOrSkip();

        var session = await HelpSessionFactory.CreateAsync(
            new EmbeddedResourceHelpContentSource(), aiSession: null, Options, options);

        Assert.Equal(HelpAssistantMode.Semantic, session.AssistantMode);
    }

    [SkippableFact]
    public async Task Ask_ParaphrasedQuestion_FindsRelevantTopic()
    {
        var options = LoadOrSkip();
        var session = await HelpSessionFactory.CreateAsync(
            new EmbeddedResourceHelpContentSource(), aiSession: null, Options, options);

        // No literal keyword overlap with "restore" — relies on semantic similarity
        var response = await session.AskAsync("how do I get my files back from the tape?", CancellationToken.None);

        Assert.Equal(HelpAssistantMode.Semantic, response.Mode);
        Assert.Contains(response.Citations, c => c.TopicId.Contains("restore", StringComparison.OrdinalIgnoreCase));
    }
}
