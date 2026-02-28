using System.Globalization;
using System.Windows.Data;

namespace TapeWinNET.Converters;

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
    public string DisplayText
    {
        get
        {
            var icon = IsSub ? null : WarningLevelHelper.GetIcon(Level);
            return string.IsNullOrEmpty(icon)
                ? $"[{Timestamp:HH:mm:ss}] {Message}"
                : $"[{Timestamp:HH:mm:ss}] {icon} {Message}";
        }
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

/// <summary>
/// Converts a <see cref="WarningLevel"/> enum value to its standard icon character.
/// Usage in XAML: Text="{Binding WarningLevel, Converter={x:Static converters:WarningLevelToIconConverter.Instance}}"
/// </summary>
public class WarningLevelToIconConverter : IValueConverter
{
    public static WarningLevelToIconConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is WarningLevel level ? WarningLevelHelper.GetIcon(level) : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
