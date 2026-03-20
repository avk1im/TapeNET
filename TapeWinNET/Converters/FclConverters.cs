using System.Globalization;
using System.Windows;
using System.Windows.Data;

using FclNET;

using TapeWinNET.ViewModels;

namespace TapeWinNET.Converters;

/// <summary>
/// Converts an <see cref="FclField"/> enum value to a user-friendly display name.
/// </summary>
public class FclFieldDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is FclField field
            ? FclConditionRowVM.GetFieldDisplayName(field)
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts an <see cref="FclOperator"/> enum value to a user-friendly display name.
/// </summary>
public class FclOperatorDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is FclOperator op
            ? FclConditionRowVM.GetOperatorDisplayName(op)
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns <c>true</c> when the bound value is not null.
/// Useful for enabling controls only when a selection has been made.
/// </summary>
public class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is not null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
