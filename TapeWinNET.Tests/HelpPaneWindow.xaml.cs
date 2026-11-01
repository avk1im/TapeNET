using System.Windows;

using TapeWinNET.ViewModels;

namespace TapeWinNET.Tests;


/// <summary>
/// Test-only tool window that hosts a <see cref="Controls.HelpPane"/>; handy for
/// manually exercising the pane and <see cref="Help.HelpPaneLayoutCoordinator"/>
/// outside a production dialog. Not shipped with TapeWinNET.
/// </summary>
public partial class HelpPaneWindow : Window
{
    public HelpPaneWindow(HelpPaneViewModel viewModel, double width)
    {
        InitializeComponent();
        DataContext = viewModel;
        Width       = width;

        // Close this tool window when the VM's CloseCommand fires
        viewModel.PaneCloseRequested += (_, _) => Close();
    }
}
