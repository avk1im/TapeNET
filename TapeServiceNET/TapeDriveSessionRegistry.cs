using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TapeLibNET;

namespace TapeServiceNET;

/// <summary>
/// Holds one open backend together with session lifecycle metadata.
/// </summary>
public sealed record TapeDriveSessionEntry(
    TapeDriveBackend Backend,
    DateTime CreatedAt,
    string RemoteIp)
{
    /// <summary>UTC timestamp of the most recent RPC call on this session.</summary>
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Singleton registry that owns all active <see cref="TapeDriveBackend"/> instances,
/// each keyed by a unique session ID issued at Open time.
/// <para>
/// gRPC services are transient — each request gets a fresh service instance.
/// The backends must survive across requests, so they live here and are looked
/// up via the <c>x-tape-session-id</c> request header on every call.
/// </para>
/// </summary>
public sealed class TapeDriveSessionRegistry(
    ILoggerFactory loggerFactory,
    ILogger<TapeDriveSessionRegistry> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, TapeDriveSessionEntry> _sessions = new();

    /// <summary>Logger factory for creating backends.</summary>
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;

    /// <summary>
    /// Registers a newly opened backend under the given session ID.
    /// </summary>
    public void Add(string sessionId, TapeDriveBackend backend, string remoteIp)
    {
        var entry = new TapeDriveSessionEntry(backend, DateTime.UtcNow, remoteIp);
        _sessions[sessionId] = entry;
        logger.LogInformation("Session opened: {SessionId} | drive {DeviceName} | client {RemoteIp}",
            sessionId, backend.DeviceName, remoteIp);
    }

    /// <summary>
    /// Returns the session entry for the given ID, or <c>null</c> if not found.
    /// Also bumps <see cref="TapeDriveSessionEntry.LastActivity"/>.
    /// </summary>
    public TapeDriveSessionEntry? Get(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            entry.LastActivity = DateTime.UtcNow;
            return entry;
        }

        return null;
    }

    /// <summary>
    /// Removes and disposes the session entry for the given ID.
    /// No-op if the session does not exist.
    /// </summary>
    public void Remove(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            logger.LogInformation("Session closed: {SessionId} | drive {DeviceName}",
                sessionId, entry.Backend.DeviceName);
            entry.Backend.Dispose();
        }
    }

    /// <summary>
    /// Disposes all active sessions (called on service shutdown).
    /// </summary>
    public void Dispose()
    {
        foreach (var (sessionId, entry) in _sessions)
        {
            logger.LogInformation("Service shutdown — closing session {SessionId} | drive {DeviceName}",
                sessionId, entry.Backend.DeviceName);
            entry.Backend.Dispose();
        }

        _sessions.Clear();
    }
}
