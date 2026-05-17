using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TapeLibNET;

namespace TapeServiceNET;

/// <summary>
/// Holds one open backend together with session lifecycle metadata.
/// </summary>
public sealed class TapeDriveSessionEntry(
    TapeDriveBackend Backend,
    DateTime CreatedAt,
    string RemoteIp)
{
    /// <summary>The backend owned by this session. May be replaced by InsertMedia.</summary>
    public TapeDriveBackend Backend { get; set; } = Backend;

    /// <summary>UTC timestamp when the session was opened.</summary>
    public DateTime CreatedAt { get; } = CreatedAt;

    /// <summary>IP address of the client that opened the session.</summary>
    public string RemoteIp { get; } = RemoteIp;

    /// <summary>UTC timestamp of the most recent RPC call (including Ping) on this session.</summary>
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of RPC handlers currently executing against this session.
    /// Managed via <see cref="Interlocked"/> by <see cref="TapeDriveSessionScope"/>.
    /// The reaper skips sessions where this is non-zero.
    /// </summary>
    public int ActiveRpcCount;
}

/// <summary>
/// Shared constants for tape session management.
/// </summary>
internal static class TapeSessionConstants
{
    /// <summary>gRPC metadata key carrying the session ID on every call after Open.</summary>
    internal const string SessionIdHeader = "x-tape-session-id";
}

/// <summary>
/// Singleton registry that owns all active <see cref="TapeDriveBackend"/> instances,
/// each keyed by a unique session ID issued at Open time.
/// <para>
/// gRPC services are transient — each request gets a fresh service instance.
/// The backends must survive across requests, so they live here and are looked
/// up via the <c>x-tape-session-id</c> request header on every call.
/// </para>
/// <para>
/// The companion <see cref="TapeSessionReaperService"/> periodically removes sessions
/// that have been idle longer than <see cref="TapeSessionSettings.IdleTimeout"/>, as
/// long as no RPC is currently in flight for that session.
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
    /// Validates the given session ID and returns a <see cref="TapeDriveSessionScope"/>
    /// that protects the session from the reaper for the duration of one RPC handler.
    /// Throws <see cref="Grpc.Core.RpcException"/> if the session ID is missing or unknown.
    /// </summary>
    /// <remarks>Always consume with <c>using</c> so the in-flight counter is decremented.</remarks>
    public TapeDriveSessionScope RequireSession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(
                Grpc.Core.StatusCode.Unauthenticated,
                $"Missing '{TapeSessionConstants.SessionIdHeader}' header."));

        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(
                Grpc.Core.StatusCode.NotFound,
                $"Session '{sessionId}' not found or already closed."));

        // Touch LastActivity before entering the scope so the idle clock resets immediately.
        entry.LastActivity = DateTime.UtcNow;
        return new TapeDriveSessionScope(entry);
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
    /// Replaces the backend on an existing session without disrupting it.
    /// The old backend must already be disposed by the caller before calling this.
    /// Used by <c>InsertMedia</c> for multi-volume tape swaps.
    /// </summary>
    public void Replace(string sessionId, TapeDriveBackend newBackend)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(
                Grpc.Core.StatusCode.NotFound,
                $"Session '{sessionId}' not found; cannot replace backend."));

        entry.Backend = newBackend;
        entry.LastActivity = DateTime.UtcNow;
        logger.LogInformation("Session backend replaced: {SessionId} | new drive {DeviceName}",
            sessionId, newBackend.DeviceName);
    }

    /// <summary>
    /// Scans for sessions idle longer than <paramref name="idleTimeout"/> and removes them,
    /// skipping any session that currently has RPCs in flight.
    /// Called by <see cref="TapeSessionReaperService"/>.
    /// </summary>
    internal void ReapIdleSessions(TimeSpan idleTimeout)
    {
        var cutoff = DateTime.UtcNow - idleTimeout;

        foreach (var (sessionId, entry) in _sessions)
        {
            // Never reap a session while an RPC is executing — it could be a multi-hour tape read.
            if (entry.ActiveRpcCount > 0)
                continue;

            if (entry.LastActivity < cutoff)
            {
                if (_sessions.TryRemove(sessionId, out var removed))
                {
                    logger.LogWarning(
                        "Reaper: removing idle session {SessionId} | drive {DeviceName} | " +
                        "client {RemoteIp} | idle since {LastActivity:u}",
                        sessionId, removed.Backend.DeviceName,
                        removed.RemoteIp, removed.LastActivity);
                    removed.Backend.Dispose();
                }
            }
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

/// <summary>
/// Background service that periodically reaps idle sessions from
/// <see cref="TapeDriveSessionRegistry"/>. Sessions in the middle of an RPC
/// (active tape reads, writes, etc.) are never evicted regardless of idle time.
/// </summary>
public sealed class TapeSessionReaperService(
    TapeDriveSessionRegistry registry,
    IOptions<TapeSessionSettings> settings,
    ILogger<TapeSessionReaperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = settings.Value;
        logger.LogInformation(
            "Session reaper started — idle timeout: {IdleTimeout}, interval: {ReaperInterval}",
            cfg.IdleTimeout, cfg.ReaperInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(cfg.ReaperInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                registry.ReapIdleSessions(cfg.IdleTimeout);
            }
            catch (Exception ex)
            {
                // Never let a reaper exception crash the hosted service loop.
                logger.LogError(ex, "Session reaper encountered an unexpected error");
            }
        }

        logger.LogInformation("Session reaper stopped");
    }
}
