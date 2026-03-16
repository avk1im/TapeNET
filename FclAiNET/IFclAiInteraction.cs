namespace FclAiNET;

/// <summary>
/// Supported AI provider types for FCL natural language translation.
/// </summary>
public enum FclAiProviderType
{
    /// <summary>Local Ollama instance (OpenAI-compatible endpoint).</summary>
    Ollama,

    /// <summary>Local LM Studio instance (OpenAI-compatible endpoint).</summary>
    LmStudio,

    /// <summary>GitHub Models marketplace (OpenAI-compatible, uses GitHub PAT).</summary>
    GitHubModels,

    /// <summary>OpenAI cloud API.</summary>
    OpenAI,

    /// <summary>Azure OpenAI Service.</summary>
    AzureOpenAI
}

/// <summary>
/// Provider selection result returned by <see cref="IFclAiInteraction.ChooseCloudProviderAsync"/>.
/// Contains everything needed to create an <see cref="Microsoft.Extensions.AI.IChatClient"/>.
/// </summary>
/// <param name="Provider">The selected provider type.</param>
/// <param name="ApiKey">API key for authentication.</param>
/// <param name="ModelId">
/// Model identifier (e.g. "gpt-4o-mini" for OpenAI,
///  deployment name for Azure OpenAI).
/// </param>
/// <param name="Endpoint">
/// Custom endpoint URI. Required for Azure OpenAI (e.g. "https://myresource.openai.azure.com/").
///  Ignored for plain OpenAI.
/// </param>
public record FclAiProviderChoice(
    FclAiProviderType Provider,
    string ApiKey,
    string ModelId,
    Uri? Endpoint = null);

/// <summary>
/// Result of <see cref="FclAiTranslator.TranslateAsync"/>: holds the generated
/// FCL expression (if successful), or an explanation of why translation failed.
/// </summary>
/// <param name="Success">Whether valid FCL was produced.</param>
/// <param name="Fcl">The generated FCL string in canonical form, or <c>null</c> on failure.</param>
/// <param name="Expression">The parsed and validated AST, or <c>null</c> on failure.</param>
/// <param name="Explanation">
/// On failure: description of what went wrong and how the user could rephrase.
///  <c>null</c> on success.
/// </param>
/// <param name="Attempts">Number of generation/validation attempts made.</param>
public record FclTranslationResult(
    bool Success,
    string? Fcl,
    FclNET.Ast.FclExpression? Expression,
    string? Explanation,
    int Attempts);

/// <summary>
/// Callback interface for user interaction during AI provider setup.
/// Implemented by the consuming application (CLI or WPF) to supply
/// provider choices and receive status updates without coupling
/// FclAiNET to any specific UI framework.
/// </summary>
/// <remarks>
/// Modeled after TapeLibNET's <c>ITapeFileNotifiable</c> pattern:
///  a minimal interface that the library calls into, letting each app
///  decide how to present the interaction to the user.
/// </remarks>
public interface IFclAiInteraction
{
    /// <summary>
    /// Called when a local or cloud provider is being probed.
    /// Allows the UI to display progress (e.g. "Checking Ollama…").
    /// </summary>
    /// <param name="providerName">Human-readable provider name (e.g. "Ollama", "LM Studio").</param>
    /// <param name="available">
    /// <c>true</c> if the provider responded successfully,
    ///  <c>false</c> if it is unavailable.
    /// </param>
    /// <param name="modelName">
    /// The model name or ID when a specific model has been selected.
    ///  <c>null</c> when the provider is unavailable or the model is not yet known.
    /// </param>
    void OnProviderStatus(string providerName, bool available, string? modelName = null);

    /// <summary>
    /// Called when no local provider is available and the user must select
    /// a cloud provider and supply credentials.
    /// </summary>
    /// <returns>
    /// A <see cref="FclAiProviderChoice"/> with the selected provider and credentials,
    ///  or <c>null</c> if the user cancels (AI assistance will be unavailable).
    /// </returns>
    Task<FclAiProviderChoice?> ChooseCloudProviderAsync(CancellationToken cancellationToken);
}
