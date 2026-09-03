using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TapeWinNET.Models;

namespace TapeWinNET.Converters;

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

/// <summary>
/// Converts a <see cref="WarningLevel"/> to its foreground brush.
/// Usage: Foreground="{Binding HighlightLevel, Converter={x:Static converters:WarningLevelToBrushConverter.Instance}}"
/// </summary>
public class WarningLevelToBrushConverter : IValueConverter
{
    public static WarningLevelToBrushConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Return the brush, or UnsetValue so None keeps the inherited Foreground.
        return value is WarningLevel level && WarningLevelHelper.GetBrush(level) is Brush brush
            ? brush
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Formats a <see cref="LogEntry"/> display string with an optional timestamp.
/// <para>Values[0] = <see cref="LogEntry"/>, Values[1] = <c>bool ShowTimestamps</c>.</para>
/// </summary>
public class LogDisplayTextConverter : IMultiValueConverter
{
    public static LogDisplayTextConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not LogEntry entry)
            return string.Empty;

        return entry.FormatDisplayText(showTimestamp: values[1] is true);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
