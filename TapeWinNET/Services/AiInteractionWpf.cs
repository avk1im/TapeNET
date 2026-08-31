using System.Windows;
using System.Windows.Threading;

using AiNET;

using TapeWinNET.ViewModels;

namespace TapeWinNET.Services;

/// <summary>
/// WPF implementation of <see cref="IAiInteraction"/>.
/// Uses <see cref="AskDialog"/> and <see cref="SelectDialog"/> for user input
///  and routes status messages to the app log pane via <see cref="MainViewModel"/>.
/// <para>
/// Threading contract: all methods may be called from a background thread; the
///  implementation marshals every UI interaction to the UI dispatcher internally.
/// </para>
/// </summary>
public sealed class AiInteractionWpf : IAiInteraction
{
    // Injected by MainWindow after both the ViewModel and the window are ready.
    private MainViewModel? _viewModel;
    private Dispatcher?    _dispatcher;

    // Used to support "Add OpenAI-compatible provider…" re-discovery inside ChooseProviderAsync.
    private IAiProviderCatalog? _catalog;
    private LanHostsRegistry?   _lanRegistry;

    /// <summary>
    /// Provides the dispatcher and ViewModel needed for log-pane feedback.
    /// Must be called from MainWindow before any interactive AI session build.
    /// </summary>
    public void SetContext(Dispatcher dispatcher, MainViewModel viewModel)
    {
        _dispatcher = dispatcher;
        _viewModel  = viewModel;
    }

    /// <summary>
    /// Provides the catalog and LAN registry needed to re-probe after the user
    /// adds a new OpenAI-compatible LAN host inside <see cref="ChooseProviderAsync"/>.
    /// Called once from <see cref="AppAiSessionHost"/> right after construction.
    /// </summary>
    public void SetDiscoveryContext(IAiProviderCatalog catalog, LanHostsRegistry lanRegistry)
    {
        _catalog     = catalog;
        _lanRegistry = lanRegistry;
    }

    // ── Logging helpers ───────────────────────────────────────────────────

    private void LogInfo(string msg) => _viewModel?.LogInfo(msg);
    private void LogSub(string msg)  => _viewModel?.LogSub(msg);
    private void LogOk(string msg)   => _viewModel?.LogOk(msg);
    private void LogWarn(string msg) => _viewModel?.LogWarn(msg);

    // ── IAiInteraction ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task ShowStatusAsync(string message, CancellationToken ct)
    {
        // Top-level status messages go to the log pane as Info entries.
        System.Diagnostics.Debug.WriteLine($"[AiNET] {message}");
        LogInfo(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// Logs per-provider discovery notifications as subordinate (indented) entries.
    public Task ShowProviderDiscoveryAsync(string providerName, CancellationToken ct)
    {
        System.Diagnostics.Debug.WriteLine($"[AiNET]   Discovering {providerName}…");
        LogSub($"Discovering {providerName}…");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// Routes credential/connection failures to the log pane as warnings.
    public Task ShowWarningAsync(string message, CancellationToken ct)
    {
        System.Diagnostics.Debug.WriteLine($"[AiNET] ⚠ {message}");
        LogWarn(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<AiProviderConfig?> ChooseProviderAsync(
        IReadOnlyList<AiProviderProbeResult> probes, CancellationToken ct)
    {
        // Sentinel items shown at the bottom of every provider list.
        const string AddLanChoice   = "➕  Add OpenAI-compatible provider…";
        const string AddCloudChoice = "☁  Add cloud provider (OpenAI, Azure, Anthropic)…";
        const string NoneChoice     = "✗  none — disable AI assistance";

        // Add-LAN prompt text with correct port examples.
        const string AddLanPrompt =
            "Specify the address and port of an OpenAI-compatible provider.\n\n" +
            "Examples:\n" +
            "  http://192.168.1.42:11434 — Ollama on a LAN machine\n" +
            "  http://localhost:8000     — OpenVINO Model Server running locally";

        // Keep re-showing the dialog after a successful LAN-host add + re-probe.
        // allProbes includes both healthy and unreachable entries; the latter are
        //  shown with a ⚠ prefix so the user can still select them for a later start.
        var allProbes = probes.ToList();

        while (true)
        {
            var healthy   = allProbes.Where(p =>  p.IsHealthy).ToList();
            var unhealthy = allProbes.Where(p => !p.IsHealthy).ToList();

            // ── Show the provider SelectDialog on the UI thread ───────────────
            //  InvokeAsync returns an awaitable without blocking the caller.
            AiProviderConfig? result = null;
            bool              addLan = false;
            bool              addCloud = false;

            if (_dispatcher is not null)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    // Build the choice list:
                    //  None · healthy providers · ⚠ unreachable providers · Add LAN · Add cloud
                    var allSelectable = healthy.Concat(unhealthy).ToList();
                    var providerChoices = allSelectable
                        .Select(p => p.IsHealthy
                            ? $"✓ {p.Descriptor.DisplayName}  ({p.Endpoint})"
                            : $"⚠  {p.Descriptor.DisplayName}  ({p.Endpoint})  — not responding")
                        .Prepend(NoneChoice)
                        .Append(AddLanChoice)
                        .Append(AddCloudChoice)
                        .ToList();

                    string prompt = healthy.Count == 0 && unhealthy.Count == 0
                        ? "No AI providers were found. Add a LAN or cloud provider, or select None:"
                        : "The following AI providers were discovered. Select one to use for Help:";

                SELECT_PROVIDER:
                    var providerDialog = new SelectDialog(
                        "Choose AI Provider",
                        prompt,
                        providerChoices,
                        defaultIndex: healthy.Count > 0 ? 1 : 0)
                    {
                        Owner = Application.Current.MainWindow
                    };

                    if (providerDialog.ShowDialog() != true)
                        return;   // user cancelled

                    var idx = providerDialog.SelectedIndex;

                    // The two trailing entries are the "add" sentinels.
                    if (idx == providerChoices.Count - 1)
                    {
                        addCloud = true;
                        return;
                    }

                    if (idx == providerChoices.Count - 2)
                    {
                        addLan = true;
                        return;
                    }

                    if (idx == 0)
                    {
                        LogWarn("No AI provider selected — Help will use local-search mode.");
                        result = AiProviderConfig.NoAiProvider;
                        return;
                    }

                    var selected = allSelectable[idx - 1];  // -1 for the None entry

                    // ── Choose chat model (if more than one available) ────────
                    // For unhealthy providers there are no discovered models, so
                    //  we skip the model-selection step and leave ChatModelId null
                    //  (the provider will use its own default when it comes online).
                    string? chatModel = null;
                    if (selected.IsHealthy)
                    {
                        if (selected.DiscoveredChatModels.Count > 1)
                        {
                            var modelDialog = new SelectDialog(
                                "Choose Chat Model",
                                $"Select the chat model to use with {selected.Descriptor.DisplayName}:",
                                selected.DiscoveredChatModels,
                                defaultIndex: 0)
                            {
                                Owner = Application.Current.MainWindow
                            };

                            if (modelDialog.ShowDialog() != true)
                                goto SELECT_PROVIDER; // user cancelled — go back to provider selection

                            chatModel = selected.DiscoveredChatModels[modelDialog.SelectedIndex];
                        }
                        else
                        {
#pragma warning disable CA1826 // Do not use Enumerable methods on indexable collections — or default case!
                            chatModel = selected.DiscoveredChatModels.FirstOrDefault();
#pragma warning restore CA1826 // Do not use Enumerable methods on indexable collections
                        }
                    }

                    // Fall back to provider display name when no model was discovered.
                    chatModel ??= selected.Descriptor.DisplayName;

                    var embeddingModel = selected.IsHealthy
#pragma warning disable CA1826 // Do not use Enumerable methods on indexable collections — or default case!
                        ? selected.DiscoveredEmbeddingModels.FirstOrDefault()
#pragma warning restore CA1826 // Do not use Enumerable methods on indexable collections
                        : null;

                    result = new AiProviderConfig(
                        Descriptor:       selected.Descriptor,
                        Endpoint:         selected.Endpoint,
                        ApiKey:           null,
                        ChatModelId:      chatModel,
                        EmbeddingModelId: embeddingModel);
                });
            }

            // ── Handle "Add OpenAI-compatible provider…" ──────────────────────
            if (addLan)
            {
                var newUri = await PromptAndAddLanHostAsync(AddLanPrompt);
                if (newUri is null)
                    continue;   // user cancelled — re-show provider list

                // Probe BEFORE persisting, so a mistyped address never ends up
                //  polluting the registry (and every future discovery sweep).
                //  ConfigureAwait(false) ensures we never resume on the
                //  dispatcher, so no deadlock is possible.
                LogInfo($"Probing LAN host {newUri}…");
                var freshProbes = await ReprobeWithNewLanHostAsync(newUri, ct).ConfigureAwait(false);

                bool responded = freshProbes.Any(
                    p => p.IsHealthy &&
                         string.Equals(p.Endpoint.GetLeftPart(UriPartial.Authority),
                                       newUri.GetLeftPart(UriPartial.Authority),
                                       StringComparison.OrdinalIgnoreCase));

                if (responded)
                {
                    if (_lanRegistry?.Add(newUri) ?? false)
                        LogOk($"LAN host {newUri} responded and was saved.");
                }
                else if (await ConfirmSaveUnreachableHostAsync(newUri))
                {
                    _lanRegistry?.Add(newUri);
                    LogWarn($"LAN host {newUri} did not respond but was saved on request.");
                }
                else
                {
                    LogWarn($"LAN host {newUri} did not respond — not saved.");
                    continue;   // re-show provider list without the dead entry
                }

                // Merge the fresh results with allProbes, replacing any existing entry
                //  for a given endpoint. If the new host didn't respond, inject a
                //  synthetic unhealthy entry so the user can still select it.
                allProbes = MergeProbes(allProbes, freshProbes, newUri);
                continue;
            }

            // ── Handle "Add cloud provider…" ──────────────────────────────────
            if (addCloud)
            {
                var cloudConfig = await ConfigureCloudProviderAsync(ct).ConfigureAwait(false);
                if (cloudConfig is null)
                    continue;   // user cancelled — re-show provider list

                return cloudConfig;
            }

            return result;
        }
    }

    // ── Cloud-provider helpers ───────────────────────────────────────────────

    /// <summary>
    /// Walks the user through configuring a cloud provider: pick the service,
    /// supply the endpoint (Azure only), enter the API key, then validate it
    /// with a live probe and let the user pick a model.
    /// </summary>
    /// <returns>
    /// A ready-to-use config, or <c>null</c> if the user cancelled at any point.
    /// </returns>
    private async Task<AiProviderConfig?> ConfigureCloudProviderAsync(CancellationToken ct)
    {
        if (_dispatcher is null || _catalog is null)
            return null;

        // Offer every registered cloud provider, in catalog order.
        var cloudProviders = _catalog.Providers
            .Where(p => p.Descriptor.Location == AiProviderLocation.Cloud)
            .ToList();

        if (cloudProviders.Count == 0)
            return null;

        // ── 1. Choose which cloud service ────────────────────────────────────
        var chosen = await _dispatcher.InvokeAsync(() =>
        {
            var choices = cloudProviders
                .Select(p => p.Descriptor.Capabilities.HasFlag(AiCapabilities.Embeddings)
                    ? p.Descriptor.DisplayName
                    : $"{p.Descriptor.DisplayName}  — chat only, no embeddings")
                .ToList();

            var dlg = new SelectDialog(
                "Add Cloud AI Provider",
                "Select the cloud service to configure:",
                choices,
                defaultIndex: 0)
            {
                Owner = Application.Current.MainWindow
            };

            return dlg.ShowDialog() == true ? cloudProviders[dlg.SelectedIndex] : null;
        });

        if (chosen is null)
            return null;

        var descriptor = chosen.Descriptor;

        // ── 2. Resolve the endpoint ──────────────────────────────────────────
        //  Providers without a fixed endpoint (Azure OpenAI) must be told which
        //  resource to talk to.
        var endpoint = descriptor.DefaultEndpoint;
        if (endpoint is null)
        {
            endpoint = await PromptEndpointAsync(descriptor, suggested: null, ct)
                .ConfigureAwait(false);
            if (endpoint is null)
                return null;
        }

        // ── 3. Prompt for the API key ────────────────────────────────────────
        var apiKey = await PromptApiKeyAsync(descriptor, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
            return null;

        // ── 4. Validate with a live probe (off the dispatcher) ───────────────
        LogInfo($"Verifying {descriptor.DisplayName}…");
        await Task.Yield();
        var probe = await chosen.ProbeAsync(endpoint, apiKey, ct).ConfigureAwait(false);

        if (!probe.IsHealthy)
        {
            var reason = probe.ErrorMessage ?? "connection failed";
            LogWarn($"{descriptor.DisplayName}: {reason}");
            await _dispatcher.InvokeAsync(() => SimpleBox.Show(
                $"Could not connect to {descriptor.DisplayName}.\n\n{reason}",
                "Cloud Provider Not Available",
                MessageBoxButton.OK, MessageBoxImage.Warning));
            return null;
        }

        LogOk($"{descriptor.DisplayName} verified.");

        // ── 5. Choose the chat model / Azure deployment ──────────────────────
        var chatModel = await ChooseCloudModelAsync(descriptor, probe).ConfigureAwait(false);
        if (chatModel is null)
            return null;

        // Only pick an embedding model when the provider actually supports it.
#pragma warning disable CA1826 // Do not use Enumerable methods on indexable collections — or default case!
        var embeddingModel = descriptor.Capabilities.HasFlag(AiCapabilities.Embeddings)
            ? probe.DiscoveredEmbeddingModels.FirstOrDefault()
            : null;
#pragma warning restore CA1826 // Do not use Enumerable methods on indexable collections

        return new AiProviderConfig(
            Descriptor:       descriptor,
            Endpoint:         probe.Endpoint,
            ApiKey:           apiKey,
            ChatModelId:      chatModel,
            EmbeddingModelId: embeddingModel);
    }

    /// <summary>
    /// Lets the user pick a chat model from those the probe discovered. When
    /// the account exposes none (some Azure resources hide the deployment
    /// list), falls back to asking for the name directly.
    /// </summary>
    private async Task<string?> ChooseCloudModelAsync(
        AiProviderDescriptor descriptor, AiProviderProbeResult probe)
    {
        // Azure names deployments, not models — reflect that in the wording.
        bool isAzure = descriptor.Kind == AiProviderKind.AzureOpenAi;
        string noun  = isAzure ? "deployment" : "model";

        if (probe.DiscoveredChatModels.Count == 0)
        {
            // Nothing discovered — ask the user to type the name.
            string? typed = null;
            await _dispatcher!.InvokeAsync(() =>
            {
                var dlg = new AskDialog(
                    $"Choose {descriptor.DisplayName} {noun}",
                    $"No {noun}s could be listed for this account.\n" +
                    $"Enter the {noun} name to use:",
                    defaultValue: null)
                {
                    Owner = Application.Current.MainWindow
                };
                if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Answer))
                    typed = dlg.Answer.Trim();
            });
            return typed;
        }

        if (probe.DiscoveredChatModels.Count == 1)
            return probe.DiscoveredChatModels[0];

        return await _dispatcher!.InvokeAsync(() =>
        {
            var dlg = new SelectDialog(
                $"Choose {descriptor.DisplayName} {noun}",
                $"Select the {noun} to use:",
                [.. probe.DiscoveredChatModels],
                defaultIndex: 0)
            {
                Owner = Application.Current.MainWindow
            };

            return dlg.ShowDialog() == true
                ? probe.DiscoveredChatModels[dlg.SelectedIndex]
                : null;
        });
    }

    /// <summary>
    /// Asks the user whether an unreachable LAN host should still be saved.
    /// </summary>
    /// <remarks>
    /// A host may legitimately be offline at the moment it is added, so we offer
    ///  the choice rather than silently discarding it. Declining keeps the
    ///  registry free of typos, which is the common case.
    /// </remarks>
    private async Task<bool> ConfirmSaveUnreachableHostAsync(Uri host)
    {
        if (_dispatcher is null)
            return true; // default yes, save

        var answer = await _dispatcher.InvokeAsync(() => SimpleBox.Show(
            $"No AI provider responded at {host}.\n\n" +
            "Save it anyway? Choose No if you mistyped the address.",
            "LAN Host Did Not Respond",
            MessageBoxButton.YesNo, MessageBoxImage.Warning));

        return answer == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Merges <paramref name="fresh"/> probe results into <paramref name="existing"/>,
    /// replacing any entry whose endpoint matches. If <paramref name="newHost"/> is
    /// not present in <paramref name="fresh"/> (probe timed out / refused), appends a
    /// synthetic unhealthy entry so the user can still select it in the dialog.
    /// </summary>
    private static List<AiProviderProbeResult> MergeProbes(
        List<AiProviderProbeResult> existing,
        IReadOnlyList<AiProviderProbeResult> fresh,
        Uri newHost)
    {
        // Build a lookup of fresh results by endpoint, keeping the healthy entry
        //  when multiple providers respond on the same endpoint (e.g. OllamaProvider
        //  and OpenAiCompatibleProvider both probe http://localhost:11434/).
        var freshByEndpoint = new Dictionary<Uri, AiProviderProbeResult>();
        foreach (var p in fresh)
        {
            if (!freshByEndpoint.TryGetValue(p.Endpoint, out var current) ||
                (p.IsHealthy && !current.IsHealthy))
            {
                freshByEndpoint[p.Endpoint] = p;
            }
        }

        // Replace any existing entry whose endpoint origin (scheme+host+port) appears
        //  in the fresh set. A probe may return a versioned endpoint (e.g. /v3) while
        //  the existing entry was stored with the bare host URI — match on origin only.
        var freshByOrigin = freshByEndpoint.ToDictionary(
            kvp => kvp.Key.GetLeftPart(UriPartial.Authority),
            kvp => kvp.Value,
            StringComparer.OrdinalIgnoreCase);

        var merged = existing
            .Select(p => freshByOrigin.TryGetValue(
                p.Endpoint.GetLeftPart(UriPartial.Authority), out var updated) ? updated : p)
            .ToList();

        // Add genuinely new entries from the fresh set.
        var existingOrigins = existing
            .Select(p => p.Endpoint.GetLeftPart(UriPartial.Authority))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in fresh)
            if (!existingOrigins.Contains(p.Endpoint.GetLeftPart(UriPartial.Authority)))
                merged.Add(p);

        // If newHost still has no entry (probe returned nothing), inject a synthetic
        //  unhealthy result so the user can select the host for a deferred start.
        // Note: a successful probe may return a versioned endpoint (e.g. /v3) while
        //  newHost is the bare host URI, so we match on origin (scheme+host+port) only.
        bool hasEntry = merged.Any(p =>
            string.Equals(p.Endpoint.GetLeftPart(UriPartial.Authority),
                          newHost.GetLeftPart(UriPartial.Authority),
                          StringComparison.OrdinalIgnoreCase));
        if (!hasEntry)
        {
            var descriptor = new AiProviderDescriptor(
                Kind:            AiProviderKind.OpenAiCompatible,
                Location:        AiProviderLocation.LocalNetwork,
                DisplayName:     "OpenAI-compatible (LAN)",
                DefaultEndpoint: null,
                RequiresApiKey:  false,
                Capabilities:    AiCapabilities.Chat | AiCapabilities.Embeddings);

            merged.Add(new AiProviderProbeResult(
                Descriptor:              descriptor,
                Endpoint:                newHost,
                IsHealthy:               false,
                DiscoveredChatModels:    [],
                DiscoveredEmbeddingModels: [],
                Latency:                 TimeSpan.Zero,
                ErrorMessage:            "Host did not respond"));
        }

        return merged;
    }

    // ── LAN-host helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Shows the "Add LAN host" <see cref="AskDialog"/> on the UI thread,
    /// validates and normalises the URI, adds it to the registry, and returns
    /// the parsed <see cref="Uri"/> (or <c>null</c> on cancel).
    /// </summary>
    private async Task<Uri?> PromptAndAddLanHostAsync(string prompt)
    {
        if (_dispatcher is null)
            return null;

        return await _dispatcher.InvokeAsync(() =>
        {
            while (true)
            {
                var dlg = new AskDialog(
                    "Add OpenAI-compatible Provider",
                    prompt,
                    defaultValue: "http://")
                {
                    Owner = Application.Current.MainWindow
                };

                if (dlg.ShowDialog() != true)
                    return (Uri?)null;   // cancelled

                var input = dlg.Answer.Trim();

                // Normalise: prepend scheme if the user omitted it.
                if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    input = "http://" + input;
                }

                // NOTE: deliberately NOT persisted here — the caller probes the
                //  host first and only then decides whether to save it.
                if (Uri.TryCreate(input, UriKind.Absolute, out var parsed))
                    return parsed;

                // Invalid — warn and loop back to the AskDialog.
                SimpleBox.Show(
                    $"'{dlg.Answer.Trim()}' is not a valid URL.\n" +
                    "Please enter a full address, e.g. http://192.168.1.42:11434",
                    "Invalid Address",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    /// <summary>
    /// Runs a fresh discovery pass on a background thread that includes all
    /// hosts currently in the registry (which now includes the newly added host).
    /// </summary>
    private async Task<IReadOnlyList<AiProviderProbeResult>> ReprobeWithNewLanHostAsync(
        Uri newHost, CancellationToken ct)
    {
        if (_catalog is null)
            return [];

        // The new host is not in the registry yet (it is saved only after a
        //  successful probe), so union it in explicitly for this sweep.
        var lanHosts = _lanRegistry is null
            ? (IReadOnlyList<Uri>)[newHost]
            : [.. _lanRegistry.GetAll().Append(newHost).Distinct()];

        var options = new AiProviderDiscoveryOptions(
            ProbeLocalhost:            true,
            LanEndpoints:              lanHosts,
            CheckEnvironmentVariables: true);

        var discovery = new AiProviderDiscovery(_catalog);
        try
        {
            // Explicitly hop off the dispatcher before awaiting, so discovery's
            //  HTTP tasks never try to resume on the UI thread.
            await Task.Yield();
            return await discovery.DiscoverAsync(options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    /// <inheritdoc/>
    public Task<string?> PromptApiKeyAsync(AiProviderDescriptor descriptor, CancellationToken ct)
    {
        string? key = null;
        _dispatcher?.Invoke(() =>
        {
            var dialog = new AskDialog(
                "API Key Required",
                $"Enter the API key for {descriptor.DisplayName}:",
                defaultValue: null)
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Answer))
                key = dialog.Answer;
        });
        return Task.FromResult(key);
    }

    /// <inheritdoc/>
    public Task<Uri?> PromptEndpointAsync(
        AiProviderDescriptor descriptor, Uri? suggested, CancellationToken ct)
    {
        Uri? uri = null;
        _dispatcher?.Invoke(() =>
        {
            var dialog = new AskDialog(
                "Endpoint Required",
                $"Enter the endpoint URL for {descriptor.DisplayName}:",
                defaultValue: suggested?.ToString())
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() == true &&
                Uri.TryCreate(dialog.Answer.Trim(), UriKind.Absolute, out var parsed))
            {
                uri = parsed;
            }
        });
        return Task.FromResult(uri);
    }
}
