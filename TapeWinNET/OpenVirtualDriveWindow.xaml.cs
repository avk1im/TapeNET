using System.Windows;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

public partial class OpenVirtualDriveWindow : Window
{
    public OpenVirtualDriveWindow(OpenVirtualDriveViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}