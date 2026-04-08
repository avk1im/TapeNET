using System.Globalization;
using System.Windows.Data;
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
