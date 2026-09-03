namespace AiNET;

/// <summary>
/// Controls which endpoints are contacted during an
/// <see cref="IAiProviderDiscovery.DiscoverAsync"/> sweep.
/// </summary>
/// <param name="ProbeLocalhost">
/// Whether to probe well-known localhost ports (Ollama 11434, LM Studio 1234).
/// </param>
/// <param name="LanEndpoints">
/// Additional LAN base URIs to probe. Sourced from
/// <see cref="LanHostsRegistry"/> at call time.
/// </param>
/// <param name="CheckEnvironmentVariables">
/// Whether to inspect environment variables such as <c>OPENAI_API_KEY</c>,
///  <c>OPENAI_API_KEY</c>, and <c>AZURE_OPENAI_API_KEY</c>.
/// </param>
/// <param name="PerProbeTimeout">
/// Timeout applied to each individual endpoint probe.
/// Defaults to 5 seconds when <see cref="TimeSpan.Zero"/> is passed.
/// </param>
/// <param name="SecretStore">
/// Optional credential store. When supplied, cloud providers with a
///  previously saved API key are probed too, so a returning user sees them
///  without needing environment variables set.
/// </param>
/// <param name="KnownCloudEndpoints">
/// Endpoints for cloud providers that have no fixed default (Azure OpenAI).
///  Probed only when <paramref name="SecretStore"/> holds a matching key.
/// </param>
/// <param name="Registry">
/// Optional provider registry. When supplied it becomes authoritative for
///  <i>what</i> is probed: disabled entries are skipped entirely and user-added
///  entries replace <paramref name="LanEndpoints"/> as the LAN source.
/// </param>
public sealed record AiProviderDiscoveryOptions(
    bool ProbeLocalhost = true,
    IReadOnlyList<Uri>? LanEndpoints = null,
    bool CheckEnvironmentVariables = true,
    TimeSpan PerProbeTimeout = default,
    IAiSecretStore? SecretStore = null,
    IReadOnlyDictionary<AiProviderKind, Uri>? KnownCloudEndpoints = null,
    IAiProviderRegistry? Registry = null);
