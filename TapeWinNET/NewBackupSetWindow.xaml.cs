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

    private void SourceList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SourceList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            ViewModel.AddPaths(paths);
        }
    }

    private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
        => ViewModel.OnItemCheckChanged();
}