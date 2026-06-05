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
        // Sentinel item shown at the bottom of every provider list.
        const string AddLanChoice = "➕  Add OpenAI-compatible provider…";
        const string NoneChoice   = "✗  none — disable AI assistance";

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

            if (_dispatcher is not null)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    // Build the choice list:
                    //  None · healthy providers · ⚠ unreachable providers · Add LAN
                    var allSelectable = healthy.Concat(unhealthy).ToList();
                    var providerChoices = allSelectable
                        .Select(p => p.IsHealthy
                            ? $"✓ {p.Descriptor.DisplayName}  ({p.Endpoint})"
                            : $"⚠  {p.Descriptor.DisplayName}  ({p.Endpoint})  — not responding")
                        .Prepend(NoneChoice)
                        .Append(AddLanChoice)
                        .ToList();

                    string prompt = healthy.Count == 0 && unhealthy.Count == 0
                        ? "No AI providers were found. Add an OpenAI-compatible LAN host, or select None:"
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

                    if (idx == providerChoices.Count - 1)
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

                // Re-probe on a background thread (ConfigureAwait(false) ensures
                //  we never resume on the dispatcher, so no deadlock is possible).
                LogInfo($"Added LAN host {newUri}; re-probing…");
                var freshProbes = await ReprobeWithNewLanHostAsync(newUri, ct).ConfigureAwait(false);

                // Merge the fresh results with allProbes, replacing any existing entry
                //  for a given endpoint. If the new host didn't respond, inject a
                //  synthetic unhealthy entry so the user can still select it —
                //  it is already persisted in LanHostsRegistry for future sessions.
                allProbes = MergeProbes(allProbes, freshProbes, newUri);
                continue;
            }

            return result;
        }
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

                if (Uri.TryCreate(input, UriKind.Absolute, out var parsed))
                {
                    _lanRegistry?.Add(parsed);
                    return parsed;
                }

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

        var lanHosts = _lanRegistry?.GetAll() ?? (IReadOnlyList<Uri>)[newHost];
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
