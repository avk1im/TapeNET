using System.Windows;

using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Interaction logic for RestoreWindow.xaml
/// </summary>
public partial class RestoreWindow : Window
{
    public RestoreWindow(RestoreViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Wire filter pane: direct mode for file mode, callback mode for set mode
        if (viewModel.IsFileMode)
        {
            FileFilterPaneControl.FilterTarget = viewModel.FilterTargetList;
            FileFilterPaneControl.FilterStateChanged = viewModel.OnFilterStateChanged;
        }
        else
        {
            FileFilterPaneControl.FilterRequested = viewModel.OnFilterApplied;
        }
    }

    private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
        => (DataContext as RestoreViewModel)?.OnItemCheckChanged();
}
