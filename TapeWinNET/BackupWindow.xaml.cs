using System.Windows;
using System.Windows.Controls;

using TapeWinNET.Models;
using TapeWinNET.Utils;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Code-behind for BackupWindow.xaml.
/// Handles source selection → file resolution, checkbox events,
///  drag-drop, and FileFilterPane wiring.
/// </summary>
public partial class BackupWindow : Window
{
    private BackupViewModel ViewModel { get; }

    public BackupWindow(BackupViewModel viewModel)
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

        // Wire FileFilterPane in direct mode
        FileFilterPaneControl.FilterTarget = null; // will be set per-source
        FileFilterPaneControl.FilterStateChanged = OnFilterStateChanged;

        Closing += (_, _) => ViewModel.Dispose();

        // Enable shell-based drag-and-drop (works even when running elevated,
        //  where WPF's OLE-based AllowDrop is broken by COM security)
        Loaded += (_, _) => DragDropHelper.EnableFileDrop(this, paths => ViewModel.AddPaths(paths));
    }

    // ─────────────────────────────────────────────────
    //  Source selection → file resolution
    // ─────────────────────────────────────────────────

    private async void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var vm = ViewModel;
        var selected = vm.SelectedSource;

        if (selected is null)
        {
            // No source selected — clear Files pane
            FileFilterPaneControl.FilterTarget = null;
            FilesPaneSourceName.Text = "(select a source)";
            vm.CurrentFileItems = null;
            return;
        }

        FilesPaneSourceName.Text = selected.Pattern;

        // Check if already resolved
        var setView = vm.SourceView[selected.Entry];
        if (setView is null)
        {
            // Not yet resolved — trigger scan
            await vm.ScanSourceAsync(selected);
            setView = vm.SourceView[selected.Entry];
        }

        if (setView is not null)
        {
            // Wire FileFilterPane to this source's FilteredFileList
            FileFilterPaneControl.FilterTarget = setView.FilteredFiles;

            // Restore saved filter state if any
            if (setView.SavedFilterState is not null)
                await setView.SavedFilterState();

            // Update filter pane counts
            UpdateFilterPaneCounts(setView);

            // Build file display
            vm.RefreshFileDisplay();
        }
    }

    // ─────────────────────────────────────────────────
    //  FileFilterPane integration
    // ─────────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="Controls.FileFilterPane"/> after a direct-mode
    /// filter apply/disable completes. Stores the restore delegate and
    /// refreshes the file display.
    /// </summary>
    private async Task OnFilterStateChanged(Func<Task>? restoreAction)
    {
        var selected = ViewModel.SelectedSource;
        if (selected is null)
            return;

        var setView = ViewModel.SourceView[selected.Entry];
        if (setView is not null)
        {
            setView.SavedFilterState = restoreAction;
            UpdateFilterPaneCounts(setView);
        }

        // Refresh file list and source item stats
        ViewModel.RefreshFileDisplay();
        ViewModel.SourceView.SyncListItem(selected);
        ViewModel.OnFileCheckChanged();

        await Task.CompletedTask;
    }

    /// <summary>
    /// Updates the FileFilterPane TotalCount/FilteredCount from a set view.
    /// </summary>
    private void UpdateFilterPaneCounts(BackupSourceSetView setView)
    {
        FileFilterPaneControl.TotalCount = setView.FilteredFiles.SourceCount;
        FileFilterPaneControl.FilteredCount = setView.FilteredFiles.Count;
        FileFilterPaneControl.IsFilterActive = setView.FilteredFiles.IsFiltered;
    }

    // ─────────────────────────────────────────────────
    //  Checkbox events
    // ─────────────────────────────────────────────────

    /// <summary>
    /// Handles source checkbox changes in the Folders pane.
    /// </summary>
    private void SourceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var item = (e.OriginalSource as CheckBox)?.DataContext as BackupSourceListItem;
        ViewModel.OnSourceCheckChanged(item);
    }

    /// <summary>
    /// Handles file checkbox changes in the Files pane.
    /// </summary>
    private void FileCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ViewModel.OnFileCheckChanged();

        // Update filter pane counts
        var selected = ViewModel.SelectedSource;
        if (selected is not null)
        {
            var setView = ViewModel.SourceView[selected.Entry];
            if (setView is not null)
                UpdateFilterPaneCounts(setView);
        }
    }

    }
