namespace AiNET;

/// <summary>
/// Probes a set of endpoints in parallel and returns a health report for
/// each reachable provider.
/// </summary>
public interface IAiProviderDiscovery
{
    /// <summary>
    /// Sweeps all endpoints described by <paramref name="options"/> and
    /// returns one <see cref="AiProviderProbeResult"/> per probed endpoint.
    /// <paramref name="onProviderDiscovering"/> is invoked (best-effort, fire-and-forget)
    /// with each provider's display name just before its probe task is started.
    /// </summary>
    Task<IReadOnlyList<AiProviderProbeResult>> DiscoverAsync(
        AiProviderDiscoveryOptions options, CancellationToken ct,
        Func<string, CancellationToken, Task>? onProviderDiscovering = null);
}
