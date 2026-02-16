using System.ComponentModel;
using System.Windows;

using TapeWinNET.ViewModels;

namespace TapeWinNET;

public partial class OpenVirtualDriveWindow : Window
{
    private readonly OpenVirtualDriveViewModel _viewModel;

    public OpenVirtualDriveWindow(OpenVirtualDriveViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // Cancel any running probe when window closes
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Cancel any running probe to release file locks
        _viewModel.CancelProbe();
    }
}