namespace HelpNET.Assistants;

/// <summary>
/// A help assistant that answers user queries by combining retrieved excerpts.
/// The concrete mode (Lexical / Semantic / Rag) is exposed via <see cref="Mode"/>.
/// </summary>
public interface IHelpAssistant
{
    /// <summary>The retrieval + synthesis strategy this assistant uses.</summary>
    HelpAssistantMode Mode { get; }

    /// <summary>Answers the user's question asynchronously.</summary>
    Task<HelpAssistantResponse> AskAsync(HelpAssistantRequest request, CancellationToken ct);
}
