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
    }
}
