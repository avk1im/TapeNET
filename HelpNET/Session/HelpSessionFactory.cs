using AiNET;
using HelpNET.Assistants;
using HelpNET.Content;
using HelpNET.Embeddings;
using HelpNET.Indexing;
using HelpNET.Retrieval;
using Microsoft.Extensions.AI;

namespace HelpNET.Session;

/// <summary>
/// Creates <see cref="IHelpSession"/> instances and wires them to the appropriate
/// <see cref="IHelpAssistant"/> based on the available AI capabilities and content.
/// </summary>
/// <remarks>
/// <b>Mode-selection matrix</b> (Strategy A for embeddings):
/// <list type="table">
///   <listheader>
///     <term>IAiSession capabilities</term>
///     <term>Bundle present</term>
///     <term>Embeddings source</term>
///     <term>Mode</term>
///   </listheader>
///   <item><term>null / None</term><term>any</term><term>—</term><term>Lexical</term></item>
///   <item><term>Chat only</term><term>any</term><term>—</term><term>Rag (lexical-only retrieval)</term></item>
///   <item>
///     <term>Chat + Embeddings</term>
///     <term>yes, model-id match, PreferProviderEmbeddings=true</term>
///     <term>Provider generator</term><term>Rag (hybrid retrieval)</term>
///   </item>
///   <item>
///     <term>Chat + any</term><term>yes, ONNX options supplied with matching model id</term>
///     <term>Built-in ONNX</term><term>Rag (hybrid retrieval)</term>
///   </item>
///   <item><term>Embeddings only</term><term>yes, provider match + PreferProviderEmbeddings</term><term>Provider</term><term>Semantic</term></item>
///   <item><term>Embeddings only / none</term><term>yes, ONNX options supplied</term><term>ONNX</term><term>Semantic</term></item>
///   <item><term>any</term><term>no bundle or model mismatch</term><term>—</term><term>Lexical</term></item>
/// </list>
/// </remarks>
public static class HelpSessionFactory
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads content from <paramref name="contentSource"/>, builds indexes, selects the
    /// appropriate assistant mode, and returns a ready-to-use <see cref="IHelpSession"/>.
    /// </summary>
    /// <param name="contentSource">Source of help documents and optional embedding bundle.</param>
    /// <param name="aiSession">
    /// Optional AI session.  Used for chat (Rag mode) and/or provider-supplied embeddings.
    /// Pass <c>null</c> to force Lexical mode.
    /// </param>
    /// <param name="options">Tuneable session options; pass <c>new()</c> for defaults.</param>
    /// <param name="onnxOptions">
    /// Optional host-supplied ONNX embedding options.  When provided and a matching
    /// bundle is available, enables Semantic / Rag mode without a live AI session.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<IHelpSession> CreateAsync(
        IHelpContentSource    contentSource,
        IAiSession?           aiSession,
        HelpSessionOptions    options,
        OnnxEmbeddingOptions? onnxOptions = null,
        CancellationToken     ct          = default)
    {
        // Load and parse all topics.
        var store = await HelpContentStore.LoadAsync(contentSource, ct).ConfigureAwait(false);

        // Build the lexical index and intent matcher (always needed).
        var bm25   = BM25HelpIndex.Build(store.All);
        var intent = new IntentMatcher(store.All);

        // Try to load the precomputed embedding bundle (may be null).
        var bundle = await contentSource.TryLoadEmbeddingBundleAsync(ct).ConfigureAwait(false);

        // Select assistant mode.
        var assistant = await SelectAssistantAsync(
            store, bm25, intent, bundle, aiSession, onnxOptions, options, ct)
            .ConfigureAwait(false);

        var session = new HelpSession(store, bm25, assistant, options);

        // Subscribe to provider changes so the session can upgrade/downgrade its mode.
        if (aiSession is not null)
        {
            aiSession.ProviderChanged += (_, _) =>
                _ = OnProviderChangedAsync(
                    session, store, bm25, intent, bundle,
                    aiSession, onnxOptions, options, CancellationToken.None);
        }

        return session;
    }

    // ── Mode selection ────────────────────────────────────────────────────────

    private static async Task<IHelpAssistant> SelectAssistantAsync(
        HelpContentStore      store,
        BM25HelpIndex         bm25,
        IntentMatcher         intent,
        HelpEmbeddingBundle?  bundle,
        IAiSession?           aiSession,
        OnnxEmbeddingOptions? onnxOptions,
        HelpSessionOptions    options,
        CancellationToken     ct)
    {
        bool hasChat    = aiSession?.Capabilities.HasFlag(AiCapabilities.Chat)       == true;
        var  chatClient = aiSession?.ChatClient;

        // Try to resolve an embedding generator (Strategy A — may return null).
        var embGen = TryResolveEmbeddingGenerator(bundle, aiSession, onnxOptions, options);

        // ── Rag: chat available ───────────────────────────────────────────────
        if (hasChat && chatClient is not null)
        {
            IHelpRetriever retriever = embGen is not null && bundle is not null
                ? (IHelpRetriever)new HybridRetriever(
                    bm25,
                    HelpEmbeddingIndex.Build(bundle, embGen, store),
                    store)
                : new LexicalRetriever(bm25, store);

            return new RagHelpAssistant(retriever, chatClient, store, options.AssistantTopK);
        }

        // ── Semantic: embeddings available, no chat ───────────────────────────
        if (embGen is not null && bundle is not null)
        {
            var embIndex = HelpEmbeddingIndex.Build(bundle, embGen, store);
            return new SemanticHelpAssistant(embIndex, store, options.AssistantTopK);
        }

        // ── Lexical fallback ──────────────────────────────────────────────────
        return new LexicalHelpAssistant(bm25, intent, store, options.AssistantTopK);
    }

    /// <summary>
    /// Resolves a suitable embedding generator using Strategy A:
    /// <list type="number">
    ///  <item>Provider generator — when <see cref="HelpSessionOptions.PreferProviderEmbeddings"/>
    ///   is set, the session provides one, and the bundle's ModelId matches the provider's
    ///   <see cref="AiProviderConfig.EmbeddingModelId"/>.</item>
    ///  <item>Built-in ONNX generator — from host-supplied <see cref="OnnxEmbeddingOptions"/>
    ///   when the bundle's ModelId matches.</item>
    ///  <item><c>null</c> — no usable generator available; caller falls back to Lexical.</item>
    /// </list>
    /// </summary>
    private static IEmbeddingGenerator<string, Embedding<float>>? TryResolveEmbeddingGenerator(
        HelpEmbeddingBundle?  bundle,
        IAiSession?           aiSession,
        OnnxEmbeddingOptions? onnxOptions,
        HelpSessionOptions    options)
    {
        if (bundle is null) return null;

        // Strategy A.1 — provider generator (opt-in).
        if (options.PreferProviderEmbeddings
            && aiSession?.EmbeddingGenerator is not null
            && aiSession.Capabilities.HasFlag(AiCapabilities.Embeddings))
        {
            string? providerModelId = aiSession.Config.EmbeddingModelId;
            if (providerModelId is not null &&
                string.Equals(providerModelId, bundle.ModelId, StringComparison.OrdinalIgnoreCase))
            {
                return aiSession.EmbeddingGenerator;
            }
        }

        // Strategy A.2 — built-in ONNX generator.
        if (onnxOptions is not null &&
            string.Equals(onnxOptions.ModelId, bundle.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            try { return new HelpOnnxEmbeddingGenerator(onnxOptions); }
            catch { /* bad model file / wrong platform — fall through */ }
        }

        // Strategy A.3 — no usable generator.
        return null;
    }

    // ── Provider-change handler ───────────────────────────────────────────────

    private static async Task OnProviderChangedAsync(
        HelpSession           session,
        HelpContentStore      store,
        BM25HelpIndex         bm25,
        IntentMatcher         intent,
        HelpEmbeddingBundle?  bundle,
        IAiSession            aiSession,
        OnnxEmbeddingOptions? onnxOptions,
        HelpSessionOptions    options,
        CancellationToken     ct)
    {
        try
        {
            var assistant = await SelectAssistantAsync(
                store, bm25, intent, bundle, aiSession, onnxOptions, options, ct)
                .ConfigureAwait(false);

            session.ReplaceAssistant(assistant);
        }
        catch
        {
            // Provider change errors must not crash the app; the session remains on
            // its current assistant.
        }
    }
}
