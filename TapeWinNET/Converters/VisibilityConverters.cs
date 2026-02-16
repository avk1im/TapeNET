using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TapeWinNET.Converters;

public class BooleanToVisibilityCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
}

public class BoolToVisibilityHiddenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Hidden;
        }
        return Visibility.Hidden;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
}

/// <summary>
/// Converts bool to Gray/LightGray brush for enabled/disabled text styling.
/// </summary>
public class BoolToGrayBrushConverter : IValueConverter
{
    private static readonly Brush EnabledBrush;
    private static readonly Brush DisabledBrush;

    static BoolToGrayBrushConverter()
    {
        EnabledBrush = new SolidColorBrush(Colors.Gray);
        EnabledBrush.Freeze();
        DisabledBrush = new SolidColorBrush(Colors.LightGray);
        DisabledBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool boolValue && boolValue ? EnabledBrush : DisabledBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class LogLevelConverter : IValueConverter
{
    public static LogLevelConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string message)
        {
            if (message.Contains("!!!"))
                return "Error";
            if (message.Contains("!?") || message.Contains("??"))
                return "Warning";
            if (message.Contains("iii") || message.Contains(">>>"))
                return "Info";
            if (message.Contains("vvv"))
                return "Completed";
        }
        return "Normal";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts log message text directly to a frozen Brush.
/// Using frozen brushes prevents WPF from tracking changes and causing flicker.
/// </summary>
public class LogLevelToBrushConverter : IValueConverter
{
    public static LogLevelToBrushConverter Instance { get; } = new();

    // Cache frozen brushes - this is critical to prevent flicker
    private static readonly Brush ErrorBrush;
    private static readonly Brush WarningBrush;
    private static readonly Brush InfoBrush;
    private static readonly Brush CompletedBrush;
    private static readonly Brush NormalBrush;

    static LogLevelToBrushConverter()
    {
        // Create and freeze brushes once - frozen objects don't trigger change tracking
        ErrorBrush = new SolidColorBrush(Colors.Red);
        ErrorBrush.Freeze();

        WarningBrush = new SolidColorBrush(Colors.Orange);
        WarningBrush.Freeze();

        InfoBrush = new SolidColorBrush(Colors.Blue);
        InfoBrush.Freeze();

        CompletedBrush = new SolidColorBrush(Colors.DarkGreen);
        InfoBrush.Freeze();

        NormalBrush = new SolidColorBrush(Colors.Black);
        NormalBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string message)
        {
            if (message.Contains("!!!"))
                return ErrorBrush;
            if (message.Contains("!?") || message.Contains("??"))
                return WarningBrush;
            if (message.Contains("vvv"))
                return CompletedBrush;
            if (message.Contains("iii") || message.Contains(">>>"))
                return InfoBrush;
        }
        return NormalBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}