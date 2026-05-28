namespace HelpNET.Assistants;

/// <summary>
/// Indicates the retrieval and synthesis strategy used by an <see cref="IHelpAssistant"/>.
/// </summary>
public enum HelpAssistantMode
{
    /// <summary>BM25 lexical search + intent matching only; no AI synthesis.</summary>
    Lexical,

    /// <summary>ONNX-based semantic (vector) search; no AI synthesis.</summary>
    Semantic,

    /// <summary>Hybrid retrieval combined with AI-based answer synthesis (RAG).</summary>
    Rag,
}
