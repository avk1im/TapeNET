using System.Windows.Controls;

namespace TapeWinNET.Controls;

/// <summary>
/// A group of AND-connected FCL conditions in the visual filter editor.
/// Bound to <see cref="ViewModels.FclConditionGroupVM"/> via DataContext.
/// </summary>
public partial class FclConditionGroup : UserControl
{
    public FclConditionGroup()
    {
        InitializeComponent();
    }
}
