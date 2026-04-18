using System.Windows;

using TapeWinNET.ViewModels;

namespace TapeWinNET;

public partial class DeleteBackupSetsWindow : Window
{
    public DeleteBackupSetsWindow(DeleteBackupSetsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        var icon = TapeIcons.GetTapeMediaIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }
    }
}
