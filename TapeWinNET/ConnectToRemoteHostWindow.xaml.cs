using System.Windows;

using TapeWinNET.ViewModels;

namespace TapeWinNET;

public partial class ConnectToRemoteHostWindow : Window
{
    public ConnectToRemoteHostWindow(ConnectToRemoteHostViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        var icon = TapeIcons.GetTapeDriveIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }

        // Wire callbacks so the VM can close the window without referencing it directly
        viewModel.OnConnectSuccess = () => DialogResult = true;
        viewModel.OnCancel        = () => DialogResult = false;
    }
}
