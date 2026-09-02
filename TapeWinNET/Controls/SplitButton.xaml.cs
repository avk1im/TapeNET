using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace TapeWinNET.Controls;

/// <summary>
/// A button with an attached dropdown menu of alternative actions.
///
/// The primary left segment invokes <see cref="Command"/> directly.
/// The narrow right segment opens a context menu containing the secondary
/// actions declared inside the control.
/// <remarks>
/// <para>
/// Menu items bind against the <see cref="FrameworkElement.DataContext"/>
/// inherited by this SplitButton. The dropdown uses a ContextMenu hosted in
/// a separate popup tree and explicitly forwards the placement target's
/// DataContext to that menu.
/// When commands live on a containing UserControl rather than its view model,
/// assign that control as the SplitButton's DataContext:
///
/// <c>DataContext="{Binding ElementName=Root}"</c>
///
/// MenuItem bindings can then use normal paths such as:
///
/// <c>Command="{Binding ApplyFilterCommand}"</c>
/// </para>
/// </remarks>
/// </summary>
[ContentProperty(nameof(MenuItems))]
public partial class SplitButton : UserControl
{
    public SplitButton()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Content displayed by the primary left button, e.g. "Export...".
    /// </summary>
    public object? ButtonContent
    {
        get => GetValue(ButtonContentProperty);
        set => SetValue(ButtonContentProperty, value);
    }

    public static readonly DependencyProperty ButtonContentProperty =
        DependencyProperty.Register(
            nameof(ButtonContent),
            typeof(object),
            typeof(SplitButton));

    /// <summary>
    /// Tooltip shown on the primary left button.
    /// </summary>
    public object? ButtonToolTip
    {
        get => GetValue(ButtonToolTipProperty);
        set => SetValue(ButtonToolTipProperty, value);
    }

    public static readonly DependencyProperty ButtonToolTipProperty =
        DependencyProperty.Register(
            nameof(ButtonToolTip),
            typeof(object),
            typeof(SplitButton));

    /// <summary>
    /// Tooltip shown on the dropdown button.
    /// </summary>
    public object? DropDownToolTip
    {
        get => GetValue(DropDownToolTipProperty);
        set => SetValue(DropDownToolTipProperty, value);
    }

    public static readonly DependencyProperty DropDownToolTipProperty =
        DependencyProperty.Register(
            nameof(DropDownToolTip),
            typeof(object),
            typeof(SplitButton),
            new PropertyMetadata("More options"));

    /// <summary>
    /// Command invoked by the primary left button.
    /// </summary>
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

    /// <summary>
    /// Parameter passed to <see cref="Command"/>.
    /// </summary>
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

    /// <summary>
    /// Native WPF menu items displayed in the dropdown menu.
    ///
    /// The <see cref="ContentPropertyAttribute"/> allows callers to declare
    /// MenuItem and Separator elements directly inside the SplitButton.
    /// </summary>
    public ItemCollection MenuItems => DropDownMenu.Items;

    private void DropDownButton_Click(object sender, RoutedEventArgs e)
    {
        // Keep access to the owning SplitButton for default-command detection.
        DropDownMenu.Tag = this;

        // The custom placement callback positions the menu relative to the
        // arrow button and lets WPF select the candidate that fits the screen...
        DropDownMenu.PlacementTarget = DropDownButton;
        DropDownMenu.IsOpen = true;
        // ...which makes the fixed placement like below unnecessary
        // DropDownMenu.HorizontalOffset = 120;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "IDE0051", // unused method
        Justification = "Referenced from SplitButton.xaml via CustomPopupPlacementCallback.")]
#pragma warning disable CA1822 // Mark members as static -- referenced from XAML as instance method
    private CustomPopupPlacement[] DropDownMenu_CustomPopupPlacement(
#pragma warning restore CA1822 // Mark members as static
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        return
        [
            // Preferred: menu's upper-left corner directly below the arrow's
            //  lower-left corner:
            // +----------------+---+
            // | Export...      | ▼ |
            // +----------------+---+
            //                  +----------------------+
            //                  | Default menu item    |
            //                  | Another menu item    |
            //                  | And another one      |
            //                  +----------------------+
            new CustomPopupPlacement(
                new Point(offset.X, targetSize.Height + offset.Y),
                PopupPrimaryAxis.Horizontal),

            // Horizontal fallback: align the menu's right edge with the
            // arrow's right edge if the preferred placement crosses the
            // right screen edge. This also gives WPF a viable candidate near
            // either the left or right screen edge.
            //       +----------------+---+
            //       | Export...      | ▼ |
            //       +----------------+---+
            //     +----------------------+
            //     | Default menu item    |
            //     | Another menu item    |
            //     | And another one      |
            //     +----------------------+
            new CustomPopupPlacement(
                new Point(
                    targetSize.Width - popupSize.Width + offset.X,
                    targetSize.Height + offset.Y),
                PopupPrimaryAxis.Horizontal)
        ];
    }
}
