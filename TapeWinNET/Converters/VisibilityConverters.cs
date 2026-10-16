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

/// <summary>
/// Inverse of <see cref="BoolToVisibilityCollapsedConverter"/>: true → Collapsed, false → Visible.
/// </summary>
public class InverseBoolToVisibilityCollapsedConverter : IValueConverter
{
    public static InverseBoolToVisibilityCollapsedConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
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
/// Converts a null/non-null value to Visibility: non-null → Visible, null → Collapsed.
/// Useful for showing UI elements only when an optional string or object is present.
/// </summary>
public class NullToVisibilityCollapsedConverter : IValueConverter
{
    public static NullToVisibilityCollapsedConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts a <see cref="bool"/> to one of two configurable <see cref="double"/> values.
/// </summary>
/// <returns>
/// <see cref="TrueValue"/> for <see langword="true"/> and <see cref="FalseValue"/> for
///  <see langword="false"/> or any non-<see langword="bool"/> input.
/// </returns>
/// <remarks>
/// <para>
/// Declare one converter instance per value pair as a resource. Set
///  <see cref="TrueValue"/> and <see cref="FalseValue"/> as XAML attributes. The converter
///  ignores <c>ConverterParameter</c>.
/// </para>
/// <para>
/// The conversion works one way only. <see cref="ConvertBack"/> throws
///  <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
/// <para>
/// The typical use is to show or hide a <c>GridViewColumn</c>. That type has no
///  <c>Visibility</c> property, so the converter switches its <c>Width</c> between a
///  visible width and zero. A <c>GridViewColumn</c> does not belong to the visual tree.
///  Bind through a <see cref="BindingProxy"/> instead of <c>RelativeSource</c> or
///  <c>ElementName</c>.
/// </para>
/// <example>
/// Hide the "Partition" column when <c>ShowPartitions</c> is <c>false</c>:
/// <code><![CDATA[
/// <Window xmlns:converters="clr-namespace:TapeWinNET.Converters" ...>
///     <Window.Resources>
///         <converters:BoolToValuesConverter x:Key="PartitionWidthConverter"
///                                           TrueValue="100"
///                                           FalseValue="0" />
///         <converters:BindingProxy x:Key="VmProxy" Data="{Binding}" />
///     </Window.Resources>
///
///     <ListView ItemsSource="{Binding Items}">
///         <ListView.View>
///             <GridView>
///                 <GridViewColumn Header="Partition"
///                                 DisplayMemberBinding="{Binding Location}"
///                                 Width="{Binding Data.ShowPartitions,
///                                                 Source={StaticResource VmProxy},
///                                                 Converter={StaticResource PartitionWidthConverter}}" />
///             </GridView>
///         </ListView.View>
///     </ListView>
/// </Window>
/// ]]></code>
/// </example>
public sealed class BoolToValuesConverter : IValueConverter
{
    /// <summary>
    /// The value returned when the bound value is <c>true</c>. Defaults to 1.0.
    /// </summary>
    public double TrueValue { get; set; } = 1.0;

    /// <summary>
    /// The value returned when the bound value is <see langword="true"/> or not
    ///  a <see langword="bool"/>. Defaults to <c>0.0</c>.
    /// </summary>
    public double FalseValue { get; set; } = 0.0;

    /// <summary>
    /// Returns <see cref="TrueValue"/> if <paramref name="value"/> is <see langword="true"/>,
    ///  otherwise <see cref="FalseValue"/>. The converter ignores all other parameters.
    /// </summary>
    public object Convert(object value, Type targetType,
                            object parameter, CultureInfo culture)
        => value is true ? TrueValue : FalseValue;

    /// <summary>
    /// Not supported. The conversion works one way only.
    /// </summary>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public object ConvertBack(object value, Type targetType,
                                object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Passes a <see cref="FrameworkElement.DataContext"/> (or any other object) to places
///  outside the visual tree, where ordinary bindings fail.
/// </summary>
/// <remarks>
/// <para>
/// Elements such as <c>GridViewColumn</c> and <c>DataGridColumn</c> do not belong to the
///  visual or logical tree. They inherit no <c>DataContext</c>, and <c>RelativeSource</c>
///  or <c>ElementName</c> bindings on them fail at runtime.
/// </para>
/// <para>
/// A <see cref="Freezable"/> declared in a resource dictionary does inherit the
///  <c>DataContext</c> of its resource scope. The proxy captures it in <see cref="Data"/>.
///  Any binding can then reach it with <c>Source={StaticResource ...}</c>.
/// </para>
/// <para>
/// Declare the proxy in the resources of the element whose <c>DataContext</c> you need.
///  For the window's view model, use <c>Window.Resources</c>.
/// </para>
/// </remarks>
/// <example>
/// Declare the proxy, then bind a column property through it:
/// <code><![CDATA[
/// <Window.Resources>
///     <converters:BindingProxy x:Key="VmProxy" Data="{Binding}" />
/// </Window.Resources>
///
/// <GridViewColumn Header="Partition"
///                 Width="{Binding Data.ShowPartitions,
///                                 Source={StaticResource VmProxy},
///                                 Converter={StaticResource PartitionWidthConverter}}" />
/// ]]></code>
/// The path <c>Data.ShowPartitions</c> reads <c>ShowPartitions</c> from the captured
/// view model.
/// </example>
public class BindingProxy : Freezable
{
    /// <inheritdoc/>
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    /// <summary>
    /// The captured object, usually the <c>DataContext</c> set via <c>Data="{Binding}"</c>.
    /// </summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="Data"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object),
                                    typeof(BindingProxy), new UIPropertyMetadata(null));
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

/// <summary>
/// Converts an integer count: 0 → Collapsed, >0 → Visible.
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public static CountToVisibilityConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts an integer count: 0 → false, >0 → true.
/// </summary>
public class CountToBoolConverter : IValueConverter
{
    public static CountToBoolConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i > 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts a <c>ConversationItemRole</c> enum value to Visibility.
/// ConverterParameter is the role name to match (e.g. "User" or "Assistant");
/// matching → Visible, non-matching → Collapsed.
/// </summary>
public class ConversationRoleToVisibilityConverter : IValueConverter
{
    public static ConversationRoleToVisibilityConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return Visibility.Collapsed;
        return value.ToString() == parameter.ToString() ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
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

public class RemoveUnderscoreConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? s.Replace("_", string.Empty) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class ReferenceEqualsConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (values.Length != 2
            || values[0] == DependencyProperty.UnsetValue
            || values[1] == DependencyProperty.UnsetValue)
        {
            return false;
        }

        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

