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
    private bool _userDeclinedSetup;   // set when user chose "no AI for now"

    // UI-context objects injected by MainWindow after construction
    // (set before any EnsureAsync call that prompts the user).
    private AiInteractionWpf? _interaction;

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

        // Give the interaction layer access to the catalog and LAN registry so it
        //  can re-probe after the user adds an OpenAI-compatible LAN host.
        var catalog  = AiProviderCatalog.CreateDefault();
        var registry = new LanHostsRegistry();
        interaction.SetDiscoveryContext(catalog, registry);
    }

    /// <summary>
    /// Returns the current session, building it on first call.
    /// If the user has already declined setup, returns <c>null</c> immediately.
    /// Pass <paramref name="promptUser"/> = <c>false</c> to suppress any UI prompt
    /// (silent mode — returns <c>null</c> if not yet built).
    /// </summary>
    public async Task<IAiSession?> EnsureAsync(
        bool promptUser = true, CancellationToken ct = default)
        => await EnsureAsync(promptUser, autoUseIfSingle: true, raiseChanged: true, ct);

    private async Task<IAiSession?> EnsureAsync(
        bool promptUser, bool autoUseIfSingle, bool raiseChanged, CancellationToken ct)
    {
        if (_disposed) return null;
        if (_current != null) return _current;
        if (_userDeclinedSetup && !promptUser) return null;

        await _lock.WaitAsync(ct);
        try
        {
            if (_current != null) return _current;

            // Use the injected interaction context if available; fall back to a plain
            //  instance (without VM logging) when called before MainWindow is ready.
            var interaction = _interaction ?? new AiInteractionWpf();
            var catalog     = AiProviderCatalog.CreateDefault();
            var prefs       = App.Settings.AIProviderPrefs ?? new AiProviderPreferences
            {
                AutoUseIfSingle = autoUseIfSingle,
            };

            var logger = App.LoggerFactory.CreateLogger<AppAiSessionHost>();

            _current = await AiSessionFactory.BuildAsync(autouseLast: !promptUser, catalog, interaction, prefs, ct, logger);

            if (_current == null)
                _userDeclinedSetup = true;
            else // success
            {
                if (raiseChanged)
                    SessionChanged?.Invoke(this, EventArgs.Empty);

                // Persist the successful config for next time
                App.Settings.AIProviderPrefs = new AiProviderPreferences
                {
                    HasBeenAskedOnce = true,
                    AutoUseIfSingle = autoUseIfSingle,
                    LastProviderKind = _current.Config.Descriptor.Kind,
                    LastEndpoint = _current.Config.Endpoint,
                    LastChatModelId = _current.Config.ChatModelId,
                    LastEmbeddingModelId = _current.Config.EmbeddingModelId,
                };
            }

            return _current;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Forces a re-run of the provider selection dialog, replacing any existing session.
    /// Called from "Help → AI Provider settings…".
    /// </summary>
    public async Task ReconfigureAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_current != null)
            {
                await _current.DisposeAsync();
                _current = null;
            }

            _userDeclinedSetup = false;   // reset so EnsureAsync will prompt again
        }
        finally
        {
            _lock.Release();
        }

        // Re-build with full UI prompt and always show the selection dialog
        //  (autoUseIfSingle: false) — the user explicitly asked to reconfigure.
        //  Suppress the EnsureAsync-internal raise so we fire SessionChanged
        //  exactly once below, regardless of outcome.
        await EnsureAsync(promptUser: true, autoUseIfSingle: false, raiseChanged: false, ct);

        // Always notify consumers — even if the user chose "None" — so they
        //  can update their state (e.g. rebuild the help session without AI).
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

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
            _userDeclinedSetup = false;   // allow future EnsureAsync calls to prompt again
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
