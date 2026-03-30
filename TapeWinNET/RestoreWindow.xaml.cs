using System.Windows;
using System.Windows.Controls;

using TapeWinNET.Models;
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
    }

    private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // Identify the specific BackupSetListItem whose checkbox was toggled
        var item = (e.OriginalSource as CheckBox)?.DataContext as BackupSetListItem;
        (DataContext as RestoreViewModel)?.OnItemCheckChanged(item);
    }
}
