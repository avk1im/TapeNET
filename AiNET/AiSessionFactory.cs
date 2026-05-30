using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiNET;

/// <summary>
/// Entry point for creating an <see cref="IAiSession"/>.
/// Runs the full discovery → selection → smoke-test flow described in
/// the design doc §2.5.
/// </summary>
public static class AiSessionFactory
{
    /// <summary>
    /// Discovers available providers, presents a selection UI (via
    /// <paramref name="interaction"/>), and returns a ready-to-use
    /// <see cref="IAiSession"/>.
    /// </summary>
    /// <param name="catalog">Registry of all provider adapters.</param>
    /// <param name="interaction">
    /// Host-supplied UI callbacks for status reporting, provider selection,
    /// and credential prompting.
    /// </param>
    /// <param name="preferences">
    /// Persisted user preferences that may shortcut the interactive flow
    /// (e.g. <see cref="AiProviderPreferences.AutoUseIfSingle"/>).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="logger">Optional logger; uses <see cref="NullLogger"/> if omitted.</param>
    /// <returns>
    /// A live <see cref="IAiSession"/>, or <c>null</c> if the user
    /// cancelled or all providers are unavailable.
    /// </returns>
    public static async Task<IAiSession?> BuildAsync(
        IAiProviderCatalog catalog,
        IAiInteraction interaction,
        AiProviderPreferences preferences,
        CancellationToken ct,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        // ── 1. Report status ────────────────────────────────────────────────
        await interaction.ShowStatusAsync("Discovering AI providers…", ct);

        // ── 2. Run discovery (parallel, with per-provider progress) ─────────
        var registry = new LanHostsRegistry();
        var lanHosts = registry.GetAll();

        var options = new AiProviderDiscoveryOptions(
            ProbeLocalhost: true,
            LanEndpoints: lanHosts.Count > 0 ? lanHosts : null,
            CheckEnvironmentVariables: true);

        var discovery = new AiProviderDiscovery(catalog, logger);
        var probes = await discovery.DiscoverAsync(
            options, ct,
            onProviderDiscovering: (name, token) =>
                interaction.ShowProviderDiscoveryAsync(name, token));

        logger.LogDebug("Discovery completed: {Total} probe(s), {Healthy} healthy.",
            probes.Count, probes.Count(p => p.IsHealthy));

        // ── 3. Select provider ──────────────────────────────────────────────
        AiProviderConfig? config;

        var healthy = probes.Where(p => p.IsHealthy).ToList();

        if (healthy.Count == 1 && preferences.AutoUseIfSingle)
        {
            // Auto-use the single healthy provider
            var probe = healthy[0];
#pragma warning disable CA1826 // Do not use Enumerable methods on indexable collections -- or default case!
            var modelId = probe.DiscoveredChatModels.FirstOrDefault();
            config = new AiProviderConfig(
                probe.Descriptor,
                probe.Endpoint,
                ApiKey: null,
                ChatModelId: modelId,
                EmbeddingModelId: probe.DiscoveredEmbeddingModels.FirstOrDefault());
#pragma warning restore CA1826 // Do not use Enumerable methods on indexable collections
            logger.LogInformation(
                "Auto-selected single healthy provider '{Provider}' with model '{Model}'.",
                probe.Descriptor.DisplayName, modelId);
        }
        else
        {
            // Let the user choose
            config = await interaction.ChooseProviderAsync(probes, ct);
            if (config is null)
            {
                logger.LogInformation("User cancelled provider selection — AI unavailable.");
                return null;
            }
        }

        // ── 4. Resolve the provider adapter ────────────────────────────────
        var provider = catalog.Find(config.Descriptor.Kind);
        if (provider is null)
        {
            logger.LogError("No provider registered for kind '{Kind}'.", config.Descriptor.Kind);
            return null;
        }

        // ── 5. Prompt for credentials and verify — retry until valid or cancelled ──
        //  We loop here so that a failed API-key / bad-endpoint can be corrected
        //  without restarting the whole selection flow.
        while (true)
        {
            // Prompt for API key if required and not yet present
            if (config.Descriptor.RequiresApiKey && string.IsNullOrEmpty(config.ApiKey))
            {
                var key = await interaction.PromptApiKeyAsync(config.Descriptor, ct);
                if (key is null)
                {
                    logger.LogInformation("User cancelled API key prompt — AI unavailable.");
                    return null;
                }
                config = config with { ApiKey = key };
            }

            // Verify the credentials with a lightweight probe
            await interaction.ShowStatusAsync($"Verifying {config.Descriptor.DisplayName}…", ct);
            var verify = await provider.ProbeAsync(config.Endpoint, config.ApiKey, ct);

            if (verify.IsHealthy)
                break;   // credentials accepted — fall through to session creation

            // Probe failed — report and decide whether to retry
            var errDetail = verify.ErrorMessage ?? "connection failed";
            logger.LogWarning("Credential verification failed for '{Provider}': {Error}",
                config.Descriptor.DisplayName, errDetail);
            await interaction.ShowWarningAsync(
                $"Could not connect to {config.Descriptor.DisplayName}: {errDetail}", ct);

            // If the provider requires an API key, clear it and re-prompt.
            // Otherwise (wrong endpoint / server down) there is nothing to retry — bail out.
            if (!config.Descriptor.RequiresApiKey)
            {
                logger.LogInformation("No credentials to retry — AI unavailable.");
                return null;
            }
            config = config with { ApiKey = null };   // force re-prompt on next iteration
        }

        // ── 6. Build the session ────────────────────────────────────────────
        var chatClient = provider.CreateChatClient(config);
        var embeddingGenerator = provider.CreateEmbeddingGenerator(config);

        logger.LogInformation(
            "AI session created for '{Provider}' (chat={HasChat}, embeddings={HasEmbed}).",
            config.Descriptor.DisplayName, chatClient is not null, embeddingGenerator is not null);

        return new AiSession(catalog, config, chatClient, embeddingGenerator);
    }

    // ── Convenience overload: builds the default catalog ───────────────────

    /// <summary>
    /// Builds the default <see cref="AiProviderCatalog"/> containing all
    /// built-in providers, then runs <see cref="BuildAsync"/>.
    /// </summary>
    public static Task<IAiSession?> BuildAsync(
        IAiInteraction interaction,
        AiProviderPreferences preferences,
        CancellationToken ct,
        ILogger? logger = null) =>
        BuildAsync(AiProviderCatalog.CreateDefault(), interaction, preferences, ct, logger);
}
