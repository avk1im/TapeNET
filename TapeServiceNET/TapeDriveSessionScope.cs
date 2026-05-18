using TapeLibNET;

namespace TapeServiceNET;

/// <summary>
/// RAII scope that brackets a single RPC handler, protecting the owning session
/// from being reaped while work is in progress.
/// <para>
/// Obtained from <see cref="TapeDriveSessionRegistry.RequireSession"/>; the entry's
/// <see cref="TapeDriveSessionEntry.ActiveRpcCount"/> is incremented on construction
/// and decremented on <see cref="Dispose"/>. The reaper skips any session where
/// <c>ActiveRpcCount &gt; 0</c>, so even a multi-hour tape read is safe.
/// </para>
/// Usage inside every RPC handler:
/// <code>
/// using var scope = registry.RequireSession(context);
/// var b = scope.Backend;
/// </code>
/// </summary>
public sealed class TapeDriveSessionScope : IDisposable
{
    private readonly TapeDriveSessionEntry _entry;
    private bool _disposed;

    internal TapeDriveSessionScope(TapeDriveSessionEntry entry)
    {
        _entry = entry;
        Interlocked.Increment(ref _entry.ActiveRpcCount);
    }

    /// <summary>The backend associated with this session.</summary>
    public TapeDriveBackend Backend => _entry.Backend;

    /// <summary>The session entry, for catalog and metadata updates within service handlers.</summary>
    internal TapeDriveSessionEntry Entry => _entry;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Decrement(ref _entry.ActiveRpcCount);
    }
}
