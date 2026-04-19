using Microsoft.Extensions.Logging;
using TapeLibNET;

namespace TapeServiceNET;

/// <summary>
/// Singleton that owns the active <see cref="TapeDriveBackend"/> for the service lifetime.
/// <para>
/// gRPC services are transient by default — each request gets a fresh instance.
/// The backend state (open drive, loaded media, position) must survive across
/// requests, so it lives here and is injected into <see cref="TapeDriveGrpcService"/>.
/// </para>
/// </summary>
public sealed class TapeDriveSession(ILoggerFactory loggerFactory, ILogger<TapeDriveSession> logger) : IDisposable
{
    private TapeDriveBackend? _backend;
    private readonly object _lock = new();

    /// <summary>Logger factory for creating backends.</summary>
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;

    /// <summary>The currently active backend, or null if no drive is open.</summary>
    public TapeDriveBackend? Backend
    {
        get { lock (_lock) return _backend; }
    }

    /// <summary>
    /// Replaces the current backend with a new one, disposing the old one first.
    /// </summary>
    public void SetBackend(TapeDriveBackend backend)
    {
        lock (_lock)
        {
            _backend?.Dispose();
            _backend = backend;
        }

        logger.LogInformation("Backend set: {DeviceName}", backend.DeviceName);
    }

    /// <summary>
    /// Disposes the current backend (called on service shutdown).
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_backend != null)
            {
                logger.LogInformation("Session disposing: closing backend {DeviceName}", _backend.DeviceName);
                _backend.Dispose();
                _backend = null;
            }
        }
    }
}
