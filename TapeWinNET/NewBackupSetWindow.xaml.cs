using System.Windows;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Interaction logic for NewBackupSetWindow.xaml
/// </summary>
public partial class NewBackupSetWindow : Window
{
    public NewBackupSetViewModel ViewModel { get; }

    public NewBackupSetWindow(NewBackupSetViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        // Set window icon
        var icon = TapeIcons.GetBackupSetIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }
    }
}