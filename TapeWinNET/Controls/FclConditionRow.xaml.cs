using System.Windows.Controls;

namespace TapeWinNET.Controls;

/// <summary>
/// A single FCL condition row in the visual filter editor.
/// Bound to <see cref="ViewModels.FclConditionRowVM"/> via DataContext.
/// All logic lives in the ViewModel; this is a pure XAML host.
/// </summary>
public partial class FclConditionRow : UserControl
{
    public FclConditionRow()
    {
        InitializeComponent();
    }
}
