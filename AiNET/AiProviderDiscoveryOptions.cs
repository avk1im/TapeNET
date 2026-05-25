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
/// Whether to inspect environment variables such as <c>GITHUB_TOKEN</c>,
///  <c>OPENAI_API_KEY</c>, and <c>AZURE_OPENAI_API_KEY</c>.
/// </param>
/// <param name="PerProbeTimeout">
/// Timeout applied to each individual endpoint probe.
/// Defaults to 5 seconds when <see cref="TimeSpan.Zero"/> is passed.
/// </param>
public sealed record AiProviderDiscoveryOptions(
    bool ProbeLocalhost = true,
    IReadOnlyList<Uri>? LanEndpoints = null,
    bool CheckEnvironmentVariables = true,
    TimeSpan PerProbeTimeout = default);
