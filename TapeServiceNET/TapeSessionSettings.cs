namespace TapeServiceNET;

/// <summary>
/// Configuration settings for tape drive session lifecycle management.
/// Bound from the <c>TapeSession</c> section in <c>appsettings.json</c>.
/// </summary>
public sealed class TapeSessionSettings
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "TapeSession";

    /// <summary>
    /// How long a session may be idle (no RPCs, no pings) before the reaper considers
    /// it orphaned. Default: 30 minutes.
    /// <para>
    /// This should be comfortably longer than the longest expected inter-operation pause
    /// in automated pipelines. Interactive clients send periodic <c>Ping</c> RPCs to
    /// keep their session alive indefinitely across user breaks.
    /// </para>
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How often the reaper wakes up to scan for idle sessions. Default: 5 minutes.
    /// <para>
    /// Actual reap lag = at most <see cref="IdleTimeout"/> + <see cref="ReaperInterval"/>.
    /// </para>
    /// </summary>
    public TimeSpan ReaperInterval { get; set; } = TimeSpan.FromMinutes(5);
}
