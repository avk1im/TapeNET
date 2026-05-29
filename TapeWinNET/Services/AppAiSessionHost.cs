using AiNET;

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

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// The live session, or <c>null</c> if not yet built or the user declined.
    /// </summary>
    public IAiSession? Current => _current;

    /// <summary>Raised when the session is replaced (provider swap or first build).</summary>
    public event EventHandler? SessionChanged;

    /// <summary>
    /// Returns the current session, building it on first call.
    /// If the user has already declined setup, returns <c>null</c> immediately.
    /// Pass <paramref name="promptUser"/> = <c>false</c> to suppress any UI prompt
    /// (silent mode — returns <c>null</c> if not yet built).
    /// </summary>
    public async Task<IAiSession?> EnsureAsync(
        bool promptUser = true, CancellationToken ct = default)
    {
        if (_disposed) return null;
        if (_current != null) return _current;
        if (_userDeclinedSetup && !promptUser) return null;

        await _lock.WaitAsync(ct);
        try
        {
            if (_current != null) return _current;

            var interaction = new AiInteractionWpf();
            var catalog     = AiProviderCatalog.CreateDefault();
            var prefs       = new AiProviderPreferences
            {
                AutoUseIfSingle = true,
            };

            _current = await AiSessionFactory.BuildAsync(catalog, interaction, prefs, ct);

            if (_current == null)
                _userDeclinedSetup = true;
            else
                SessionChanged?.Invoke(this, EventArgs.Empty);

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

        // Now re-build with full UI prompt
        await EnsureAsync(promptUser: true, ct);
    }

    /// <summary>
    /// Convenience helper: reconfigures the app-level AI session and notifies consumers.
    /// Delegates to <see cref="App.AiSessionHost"/>.
    /// </summary>
    public static Task ReconfigureAndNotifyAsync(CancellationToken ct = default)
        => App.AiSessionHost.ReconfigureAsync(ct);

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
