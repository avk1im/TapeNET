using System.Windows;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Interaction logic for BackupPreviewWindow.xaml
/// </summary>
public partial class BackupPreviewWindow : Window
{
    public BackupPreviewViewModel ViewModel { get; }

    public BackupPreviewWindow(BackupPreviewViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        // Set window icon
        var icon = TapeIcons.GetTapeFileIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }
    }
}