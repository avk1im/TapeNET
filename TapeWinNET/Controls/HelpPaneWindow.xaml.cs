using System.Windows;

using TapeWinNET.ViewModels;

namespace TapeWinNET.Controls;

/// <summary>
/// Lightweight tool window that hosts a <see cref="HelpPane"/> for dialogs
/// that use <see cref="Help.HelpPaneHostMode.Adjacent"/> mode.
/// The window is owned by the calling dialog so it moves/minimises with it.
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
