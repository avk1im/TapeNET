using AiNET;
using Microsoft.Extensions.Logging;

namespace TapeWinNET.Services;


/// <summary>
/// Process-wide singleton that owns the <see cref="IAiSession"/>.
/// Constructed once in <see cref="App.OnStartup"/>; all consumers call
/// <see cref="EnsureAsync"/> to obtain the session (built lazily on first use).
/// </summary>
public sealed class AppAiSessionHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IAiSession? _current;
    private bool _disposed;
    private bool _setupAttempted;      // lazy restore / setup prompt already done this app session

    // Persists cloud API keys in the Windows Credential Manager so the user is
    //  not re-prompted on every launch. Falls back to in-memory off-Windows.
    private readonly IAiSecretStore _secretStore = AiSecretStore.CreateDefault();

    // UI-context objects injected by MainWindow after construction
    // (set before any EnsureAsync call that prompts the user).
    private AiInteractionWpf? _interaction;

    // Authoritative provider list (enabled state, order, endpoints, pinned models).
    // Created in SetInteraction and shared with the interaction layer.
    private IAiProviderRegistry? _providerRegistry;

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// The live session, or <c>null</c> if not yet built or the user declined.
    /// </summary>
    public IAiSession? Current => _current;

    /// <summary>
    /// The provider configuration of the current session, or <c>null</c> if none is active.
    /// Consumers use this to build human-readable label strings.
    /// </summary>
    public AiProviderConfig? CurrentConfig => _current?.Config;

    /// <summary>Raised when the session is replaced (provider swap or first build).</summary>
    public event EventHandler? SessionChanged;

    /// <summary>
    /// Provides the <see cref="AiInteractionWpf"/> instance used for UI prompts and log feedback.
    /// Must be called once from <c>MainWindow</c> before any interactive <c>EnsureAsync</c>.
    /// </summary>
    public void SetInteraction(AiInteractionWpf interaction)
    {
        _interaction = interaction;

        // Give the interaction layer access to the catalog and registries so it
        //  can manage providers and re-probe after the user edits the list.
        var catalog          = AiProviderCatalog.CreateDefault();
        var lanRegistry      = new LanHostsRegistry();
        var providerRegistry = new AiProviderRegistry(catalog, _secretStore);
        _providerRegistry = providerRegistry;
        interaction.SetDiscoveryContext(catalog, lanRegistry, providerRegistry);
    }

    /// <summary>
    /// Opens the provider-management dialog, where the user edits the provider
    ///  list and selects the provider and model to use (or none). The selection
    ///  is applied directly — no further chooser is shown. Cancel keeps the
    ///  current session; list edits persist regardless.
    /// Called from "Help → Manage AI Providers…".
    /// </summary>
    public async Task ManageProvidersAsync(CancellationToken ct = default)
    {
        if (_interaction is null || _providerRegistry is null)
            return;

        // The user is seeing the manager now — no lazy setup prompt is needed this
        //  session. Setting this up front also keeps a help session built from within
        //  the manager (its Help pane) from opening a nested manager via EnsureAsync.
        _setupAttempted = true;

        var selected = await _interaction.ManageProvidersAsync(_providerRegistry, _current?.Config, ct);
        if (selected is null)
            return;   // cancelled — keep the current session

        await _lock.WaitAsync(ct);
        try
        {
            _setupAttempted = true;
            if (_current != null)
            {
                await _current.DisposeAsync();
                _current = null;
            }

            await ApplySelectionAsync(selected, ct);
        }
        finally
        {
            _lock.Release();
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Builds the session for a provider selected in the manager dialog and
    ///  persists the choice; an explicit "none" is persisted as such.
    ///  Caller must hold <see cref="_lock"/>.
    /// </summary>
    private async Task ApplySelectionAsync(AiProviderConfig selected, CancellationToken ct)
    {
        if (selected.IsNone)
        {
            SavePreferences(null, userChoseNone: true);
            return;
        }

        var logger = App.LoggerFactory.CreateLogger<AppAiSessionHost>();
        _current = await AiSessionFactory.BuildFromConfigAsync(
            selected, AiProviderCatalog.CreateDefault(), _interaction ?? new AiInteractionWpf(), ct, logger, _secretStore);

        if (_current is not null)
            SavePreferences(_current.Config, userChoseNone: false);
    }

    /// <summary>
    /// Persists the given configuration as the last-used provider, or clears
    ///  the last-used provider when <paramref name="config"/> is <c>null</c>.
    ///  Other preference fields are preserved.
    /// </summary>
    private static void SavePreferences(AiProviderConfig? config, bool userChoseNone)
    {
        var prefs = App.Settings.AIProviderPrefs ?? new AiProviderPreferences();
        prefs.HasBeenAskedOnce     = true;
        prefs.UserChoseNone        = userChoseNone;
        prefs.LastProviderKind     = config?.Descriptor.Kind;
        prefs.LastEndpoint         = config?.Endpoint;
        prefs.LastChatModelId      = config?.ChatModelId;
        prefs.LastEmbeddingModelId = config?.EmbeddingModelId;
        App.Settings.AIProviderPrefs = prefs;
    }

    /// <summary>
    /// Returns the current session, building it lazily on the first call per app session:
    ///  the provider / model stored in the app settings is restored silently; if nothing
    ///  usable is stored (and the user has not explicitly chosen "none"), the stored
    ///  selection is reset and the "Manage AI Providers" dialog is opened — once per
    ///  app session at most. Subsequent calls return the outcome without prompting.
    /// Pass <paramref name="promptUser"/> = <see langword="false"/> to suppress the dialog.
    /// </summary>
    public async Task<IAiSession?> EnsureAsync(
        bool promptUser = true, CancellationToken ct = default)
    {
        if (_disposed) return null;
        if (_current != null || _setupAttempted) return _current;

        bool changed = false;
        await _lock.WaitAsync(ct);
        try
        {
            if (_current != null || _setupAttempted) return _current;

            var prefs = App.Settings.AIProviderPrefs;
            if (prefs?.UserChoseNone == true)
            {
                _setupAttempted = true;   // user explicitly opted out — never prompt
                return null;
            }

            // ── 1. Try the stored provider / model ──────────────────────────────
            if (prefs is not null)
            {
                var logger = App.LoggerFactory.CreateLogger<AppAiSessionHost>();
                _current = await AiSessionFactory.TryRestoreAsync(
                    AiProviderCatalog.CreateDefault(), prefs, ct, logger, _secretStore);
                if (_current is not null)
                {
                    _setupAttempted = true;
                    changed = true;
                    return _current;
                }

                // Stored selection doesn't work — reset to "none" (not user-chosen)
                if (prefs.LastProviderKind is not null)
                    SavePreferences(null, userChoseNone: false);
            }

            // ── 2. Let the user pick via the provider manager (once per session) ─
            if (!promptUser || _interaction is null || _providerRegistry is null)
                return null;   // not marked attempted — a later prompting call may still ask

            _setupAttempted = true;
            var selected = await _interaction.ManageProvidersAsync(_providerRegistry, null, ct);
            if (selected is not null)
            {
                await ApplySelectionAsync(selected, ct);
                changed = _current is not null;
            }
            return _current;
        }
        finally
        {
            _lock.Release();
            if (changed)
                SessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Opens the provider manager, replacing any existing session.
    /// Called from the help pane's AI setup link.
    /// </summary>
    public Task ReconfigureAsync(CancellationToken ct = default)
        => ManageProvidersAsync(ct);

    /// <summary>
    /// Convenience helper: reconfigures the app-level AI session and notifies consumers.
    /// Delegates to <see cref="App.AiSessionHost"/>.
    /// </summary>
    public static Task ReconfigureAndNotifyAsync(CancellationToken ct = default)
        => App.AiSessionHost.ReconfigureAsync(ct);

    /// <summary>
    /// Disposes the current session and resets all state without triggering
    /// re-discovery. Used by "Help → Reset AI Providers" to clear persisted
    /// settings and start fresh.
    /// Also erases every stored API key, so no credential survives the reset.
    /// Raises <see cref="SessionChanged"/> so consumers (HelpPane, etc.) rebind.
    /// </summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_current != null)
            {
                await _current.DisposeAsync();
                _current = null;
            }
            _setupAttempted = false;   // allow future EnsureAsync calls to prompt again

            // Erase persisted credentials — a "sign out" that left keys behind
            //  would be misleading. Only AiNET-owned entries are removed.
            try
            {
                _secretStore.Clear();
            }
            catch (Exception ex)
            {
                App.LoggerFactory.CreateLogger<AppAiSessionHost>()
                   .LogWarning(ex, "Could not clear stored AI credentials.");
            }
        }
        finally
        {
            _lock.Release();
        }

        // Notify consumers so they drop their AI references immediately.
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_current != null)
        {
            await _current.DisposeAsync();
            _current = null;
        }
        _lock.Dispose();
    }
}
