using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TapeWinNET.Controls;

/// <summary>
/// A button with an attached dropdown menu of alternative actions — the primary (left) segment
/// invokes <see cref="Command"/> directly, while the narrow (right) segment opens a popup listing
/// <see cref="MenuItems"/> as secondary actions. Used e.g. for "Export…" with a "Current profile /
/// All for this drive / All on this system" scope choice.
/// </summary>
public partial class SplitButton : UserControl
{
    public SplitButton()
    {
        InitializeComponent();
    }

    /// <summary>Content of the primary (left) button, e.g. "Export…".</summary>
    public object ButtonContent
    {
        get => GetValue(ButtonContentProperty);
        set => SetValue(ButtonContentProperty, value);
    }

    public static readonly DependencyProperty ButtonContentProperty =
        DependencyProperty.Register(
            nameof(ButtonContent),
            typeof(object),
            typeof(SplitButton));

    /// <summary>Tooltip shown on the primary (left) button.</summary>
    public string? ButtonToolTip
    {
        get => (string?)GetValue(ButtonToolTipProperty);
        set => SetValue(ButtonToolTipProperty, value);
    }

    public static readonly DependencyProperty ButtonToolTipProperty =
        DependencyProperty.Register(
            nameof(ButtonToolTip),
            typeof(string),
            typeof(SplitButton));

    /// <summary>Command invoked by the primary (left) button — the default/most common action.</summary>
    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
            nameof(Command),
            typeof(ICommand),
            typeof(SplitButton));

    /// <summary>Parameter passed to <see cref="Command"/>.</summary>
    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(
            nameof(CommandParameter),
            typeof(object),
            typeof(SplitButton));

    /// <summary>Secondary actions listed in the dropdown popup.</summary>
    public ObservableCollection<SplitButtonMenuItem> MenuItems
    {
        get => (ObservableCollection<SplitButtonMenuItem>)GetValue(MenuItemsProperty);
        set => SetValue(MenuItemsProperty, value);
    }

    public static readonly DependencyProperty MenuItemsProperty =
        DependencyProperty.Register(
            nameof(MenuItems),
            typeof(ObservableCollection<SplitButtonMenuItem>),
            typeof(SplitButton),
            new PropertyMetadata(new ObservableCollection<SplitButtonMenuItem>()));

    private void DropDownButton_Click(object sender, RoutedEventArgs e)
    {
        PopupMenu.IsOpen = true;
    }

    private void MenuItemButton_Click(object sender, RoutedEventArgs e)
    {
        PopupMenu.IsOpen = false;
    }
}

/// <summary>A single entry in a <see cref="SplitButton"/>'s dropdown menu.</summary>
public sealed class SplitButtonMenuItem
{
    public string Header { get; set; } = string.Empty;

    public ICommand? Command { get; set; }
}
