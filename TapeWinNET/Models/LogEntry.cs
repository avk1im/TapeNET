// WarningLevel is an alias for ServiceReportLevel — same enum, single definition in TapeLibNET.
global using WarningLevel = TapeLibNET.Services.ServiceReportLevel;

using System.Windows;
using System.Windows.Media;

namespace TapeWinNET.Models;

/// <summary>
/// A structured log entry for the log pane.
/// Timestamp is captured at creation time (before UI thread marshalling).
/// </summary>
public record LogEntry(WarningLevel Level, string Message, bool IsSub, DateTime Timestamp)
{
    /// <summary>Formatted display text including timestamp and level icon.</summary>
    public string DisplayText => FormatDisplayText(showTimestamp: true);

    /// <summary>
    /// Formats the entry as a display string, optionally including the timestamp.
    /// Used by the UI converter (respects ShowTimestamps toggle) and clipboard copy.
    /// </summary>
    public string FormatDisplayText(bool showTimestamp)
    {
        var icon = IsSub && Level is WarningLevel.None or WarningLevel.Info
            ? null : WarningLevelHelper.GetIcon(Level);
        bool hasIcon = !string.IsNullOrEmpty(icon);

        return (showTimestamp, hasIcon) switch
        {
            (true, true)   => $"[{Timestamp:HH:mm:ss}] {icon} {Message}",
            (true, false)  => $"[{Timestamp:HH:mm:ss}] {Message}",
            (false, true)  => $"{icon} {Message}",
            (false, false) => Message,
        };
    }
}

/// <summary>
/// Static helpers for <see cref="WarningLevel"/>.
/// </summary>
public static class WarningLevelHelper
{
    /// <summary>
    /// Returns the standard icon character for the given warning level.
    /// </summary>
    public static string GetIcon(WarningLevel level) => level switch
    {
        WarningLevel.Error => "✖",
        WarningLevel.Failed => "✗",
        WarningLevel.Warning => "⚠\uFE0E", // gurantee monochrome glyph
        WarningLevel.Info => "ℹ\uFE0E", // gurantee monochrome glyph
        WarningLevel.Completed => "✓",
        _ => string.Empty
    };

    /// <summary>
    /// Returns the application-defined forderground brush for the given warning level.
    /// </summary>
    /// <param name="level">The warning level</param>
    /// <returns>The corresponding brush loaded from the application resource; <c>null</c> if not found</returns>
    public static Brush? GetBrush(WarningLevel level)
    {
        var key = level switch
        {
            WarningLevel.Info => "WarningFg.Info",
            WarningLevel.Completed => "WarningFg.Completed",
            WarningLevel.Warning => "WarningFg.Warning",
            WarningLevel.Failed => "WarningFg.Failed",
            WarningLevel.Error => "WarningFg.Error",
            _ => null,   // None → no override
        };

        return key is null ? null : Application.Current.TryFindResource(key) as Brush;
    }

    /// <summary>
    /// Translates a normalized double value into a <see cref="WarningLevel"/> based on severity thresholds.
    /// </summary>
    /// <param name="percentage">A normalized value representing a percentage (typically between 0.0 and 1.0).</param>
    /// <returns>The corresponding <see cref="WarningLevel"/> based on the threshold ranges.</returns>
    /// <remarks>
    /// <para>The mapping is evaluated sequentially as follows:</para>
    /// <list type="bullet">
    /// <item><description>Values &lt;= 0.025 (2.5%) map to <see cref="WarningLevel.Error"/>.</description></item>
    /// <item><description>Values &lt;= 0.05 (5.0%) map to <see cref="WarningLevel.Failed"/>.</description></item>
    /// <item><description>Values &lt;= 0.25 (25.0%) map to <see cref="WarningLevel.Warning"/>.</description></item>
    /// <item><description>Values &lt;= 0.50 (50.0%) map to <see cref="WarningLevel.Completed"/>.</description></item>
    /// <item><description>Values &lt;= 1.00 (100.0%) map to <see cref="WarningLevel.Info"/>.</description></item>
    /// <item><description>Any values greater than 1.00 map to <see cref="WarningLevel.None"/>.</description></item>
    /// </list>
    /// </remarks>
    public static WarningLevel Translate(double percentage) => percentage switch
    {
        <= 0.025 => WarningLevel.Error,
        <= 0.05 => WarningLevel.Failed,
        <= 0.25 => WarningLevel.Warning,
        <= 0.50 => WarningLevel.Completed,
        <= 1.00 => WarningLevel.Info,
        _ => WarningLevel.None // Discard pattern handles any value > 1.0 (or negative inputs)
    };

    /// <summary>
    /// Translates an integer percentage value (0 to 100) into a <see cref="WarningLevel"/>.
    /// </summary>
    /// <param name="percentage">An integer percentage value (typically 0 to 100).</param>
    /// <returns>The corresponding <see cref="WarningLevel"/>.</returns>
    /// <remarks>
    /// This method converts the integer to a normalized double (0.0 to 1.0) and delegates to <see cref="Translate(double)"/>.
    /// </remarks>
    public static WarningLevel Translate(int percentage) => Translate(percentage / 100.0);

}
