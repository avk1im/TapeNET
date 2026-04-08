namespace TapeWinNET.Models;

/// <summary>
/// Centralized warning/severity levels used across all dialogs and log pane.
/// </summary>
public enum WarningLevel
{
    /// <summary>No warning — panel hidden.</summary>
    None,
    /// <summary>Blue — informational hint.</summary>
    Info,
    /// <summary>Green — success / all good.</summary>
    Completed,
    /// <summary>Yellow/orange — caution.</summary>
    Warning,
    /// <summary>Orange-red — operation failed (file error, partial failure).</summary>
    Failed,
    /// <summary>Red — danger / destructive action.</summary>
    Error
}

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
        WarningLevel.Error => "⚠",
        WarningLevel.Failed => "✗",
        WarningLevel.Warning => "⚠",
        WarningLevel.Info => "ℹ",
        WarningLevel.Completed => "✓",
        _ => string.Empty
    };
}
