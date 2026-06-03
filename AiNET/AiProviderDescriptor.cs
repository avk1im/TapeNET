namespace AiNET;

/// <summary>
/// Immutable description of an AI provider type — its kind, default endpoint,
/// authentication requirements, and supported capabilities.
/// One instance exists per provider kind; it carries no per-connection state.
/// </summary>
/// <param name="Kind">Provider type discriminator.</param>
/// <param name="Location">Logical location (Local / LAN / Cloud) for UI grouping.</param>
/// <param name="DisplayName">Human-readable name shown in provider lists.</param>
/// <param name="DefaultEndpoint">
/// Pre-filled base URI (e.g. <c>http://localhost:11434</c> for Ollama).
///  <c>null</c> for providers where the endpoint is always user-supplied.
/// </param>
/// <param name="RequiresApiKey">
/// <c>true</c> when an API key or token is mandatory before probing.
/// </param>
/// <param name="Capabilities">
/// The set of <see cref="AiCapabilities"/> this provider type can offer.
/// </param>
public sealed record AiProviderDescriptor(
    AiProviderKind Kind,
    AiProviderLocation Location,
    string DisplayName,
    Uri? DefaultEndpoint,
    bool RequiresApiKey,
    AiCapabilities Capabilities)
{
    /// <summary>
    /// A sentinel descriptor representing "Do not use any AI provider"
    /// </summary>
    public static readonly AiProviderDescriptor None =
        new(
            Kind: AiProviderKind.None,
            Location: AiProviderLocation.Local,
            DisplayName: "None",
            DefaultEndpoint: null,
            RequiresApiKey: false,
            Capabilities: AiCapabilities.None
        );
}
