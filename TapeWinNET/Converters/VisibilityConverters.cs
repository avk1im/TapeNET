using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TapeWinNET.Converters;

public class BoolToVisibilityCollapsedConverter : IValueConverter
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

/// <summary>
/// Converts a bool to an opacity value.
/// true = 1.0 (fully visible), false = DimmedOpacity (default 0.4).
/// Configure via the DimmedOpacity property on the resource declaration:
///   &lt;converters:BoolToOpacityConverter x:Key="..." DimmedOpacity="0.38"/&gt;
/// </summary>
public class BoolToOpacityConverter : IValueConverter
{
    /// <summary>
    /// Opacity applied when the bound value is false. Default is 0.4.
    /// </summary>
    public double DimmedOpacity { get; set; } = 0.4;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? 1.0 : DimmedOpacity;
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

/// <summary>
/// Multi-value converter: returns true if the first two values are equal (reference or Equals).
/// Used for radio-check menu items bound to a SelectedItem property.
/// </summary>
public class EqualityConverter : IMultiValueConverter
{
    public static EqualityConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] == DependencyProperty.UnsetValue || values[1] == DependencyProperty.UnsetValue)
            return false;
        return Equals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}