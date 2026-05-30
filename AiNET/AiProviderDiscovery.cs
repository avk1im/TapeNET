using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiNET;

/// <summary>
/// Default implementation of <see cref="IAiProviderDiscovery"/>.
/// Probes each endpoint concurrently (one task per endpoint/provider
/// combination) and collects the results.
/// </summary>
internal sealed class AiProviderDiscovery(IAiProviderCatalog catalog, ILogger? logger = null)
    : IAiProviderDiscovery
{
    // ── Well-known environment variable names ────────────────────────────────
    private const string EnvGitHubToken         = "GITHUB_TOKEN";
    private const string EnvOpenAiApiKey        = "OPENAI_API_KEY";
    private const string EnvAzureOpenAiApiKey   = "AZURE_OPENAI_API_KEY";
    private const string EnvAzureOpenAiEndpoint = "AZURE_OPENAI_ENDPOINT";

    private static readonly TimeSpan DefaultPerProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AiProviderProbeResult>> DiscoverAsync(
        AiProviderDiscoveryOptions options, CancellationToken ct,
        Func<string, CancellationToken, Task>? onProviderDiscovering = null)
    {
        var timeout = options.PerProbeTimeout == TimeSpan.Zero
            ? DefaultPerProbeTimeout
            : options.PerProbeTimeout;

        var tasks = new List<Task<AiProviderProbeResult?>>();

        // Local helper: reports progress then starts the probe task.
        Task<AiProviderProbeResult?> Probe(IAiProvider provider, Uri ep, string? apiKey,
                                           string? epLabel = null)
        {
            // Fire-and-forget the progress notification (best-effort; never blocks discovery).
            if (onProviderDiscovering is not null)
            {
                // For LAN probes include the host so each entry is distinguishable.
                var label = epLabel is not null
                    ? $"{provider.Descriptor.DisplayName} @ {epLabel}"
                    : provider.Descriptor.DisplayName;
                _ = onProviderDiscovering(label, ct);
            }

            return ProbeWithTimeoutAsync(provider, ep, apiKey, timeout, ct);
        }

        // ── Localhost probes ─────────────────────────────────────────────────
        if (options.ProbeLocalhost)
        {
            foreach (var provider in catalog.Providers)
            {
                var ep = provider.Descriptor.DefaultEndpoint;
                if (ep is null || provider.Descriptor.Location != AiProviderLocation.Local)
                    continue;

                tasks.Add(Probe(provider, ep, apiKey: null));
            }
        }

        // ── LAN endpoint probes ──────────────────────────────────────────────
        if (options.LanEndpoints is { Count: > 0 })
        {
            // For each LAN URI, probe every OpenAI-compatible and Ollama provider
            var lanProviders = catalog.Providers
                .Where(p => p.Descriptor.Location == AiProviderLocation.LocalNetwork ||
                            p.Descriptor.Kind == AiProviderKind.OpenAiCompatible)
                .ToList();

            foreach (var ep in options.LanEndpoints)
            {
                // Use host:port as the label so each LAN entry is distinguishable in logs.
                var epLabel = ep.IsDefaultPort ? ep.Host : $"{ep.Host}:{ep.Port}";
                foreach (var provider in lanProviders)
                    tasks.Add(Probe(provider, ep, apiKey: null, epLabel));
            }
        }

        // ── Environment-variable-based providers ─────────────────────────────
        if (options.CheckEnvironmentVariables)
        {
            // GitHub Models
            var githubToken = Environment.GetEnvironmentVariable(EnvGitHubToken);
            if (!string.IsNullOrEmpty(githubToken))
            {
                var provider = catalog.Find(AiProviderKind.GitHubModels);
                if (provider?.Descriptor.DefaultEndpoint is { } ep)
                    tasks.Add(Probe(provider, ep, githubToken));
            }

            // OpenAI
            var openAiKey = Environment.GetEnvironmentVariable(EnvOpenAiApiKey);
            if (!string.IsNullOrEmpty(openAiKey))
            {
                var provider = catalog.Find(AiProviderKind.OpenAi);
                if (provider?.Descriptor.DefaultEndpoint is { } ep)
                    tasks.Add(Probe(provider, ep, openAiKey));
            }

            // Azure OpenAI
            var azureKey = Environment.GetEnvironmentVariable(EnvAzureOpenAiApiKey);
            var azureEpStr = Environment.GetEnvironmentVariable(EnvAzureOpenAiEndpoint);
            if (!string.IsNullOrEmpty(azureKey) && !string.IsNullOrEmpty(azureEpStr) &&
                Uri.TryCreate(azureEpStr, UriKind.Absolute, out var azureEp))
            {
                var provider = catalog.Find(AiProviderKind.AzureOpenAi);
                if (provider is not null)
                    tasks.Add(Probe(provider, azureEp, azureKey));
            }
        }

        // ── Await all probes ─────────────────────────────────────────────────
        var results = await Task.WhenAll(tasks);

        return [.. results
            .Where(r => r is not null)
            .Select(r => r!)];
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<AiProviderProbeResult?> ProbeWithTimeoutAsync(
        IAiProvider provider,
        Uri endpoint,
        string? apiKey,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            return await provider.ProbeAsync(endpoint, apiKey, linked.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            _logger.LogDebug("Probe timed out for {Provider} at {Endpoint}.",
                provider.Descriptor.DisplayName, endpoint);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probe failed for {Provider} at {Endpoint}.",
                provider.Descriptor.DisplayName, endpoint);
            return null;
        }
    }
}
