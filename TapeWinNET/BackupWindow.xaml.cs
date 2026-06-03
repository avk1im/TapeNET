using System.Windows;
using System.Windows.Controls;

using TapeWinNET.Help;
using TapeWinNET.Models;
using TapeWinNET.Utils;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Code-behind for BackupWindow.xaml.
/// Handles source selection → file resolution, checkbox events,
///  drag-drop, and FileFilterPane wiring.
/// </summary>
public partial class BackupWindow : Window, IHelpPaneHost
{
    private BackupViewModel ViewModel { get; }

    // All embedded help-pane boilerplate (window expansion, F1 resolution,
    //  session lifecycle, and AppSettings persistence) lives in this controller.
    private readonly DialogHelpPaneController _help;

    public BackupWindow(BackupViewModel viewModel, string[]? paths = null)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        WindowPlacementApplicator.Attach(this);

        _help = new DialogHelpPaneController(
            this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl,
            defaultTopicId: "dialog.backup", helpButton: HelpButton);

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
        Loaded += (_, _) => DragDropHelper.EnableFileDrop(this, p => ViewModel.AddPaths(p));

        // Pre-populate sources from initial paths (e.g. dropped onto MainWindow)
        if (paths is { Length: > 0 })
            viewModel.AddPaths(paths);
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

    // ─────────────────────────────────────────────────
    //  Help pane (embedded, adjacent mode)
    // ─────────────────────────────────────────────────

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => _help.ToggleHelpPane();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => _help.HandleF1(e);

    #region IHelpPaneHost

    public string HostName => nameof(BackupWindow);

    // Adjacent: the window expands to the right; the HelpPane is a column inside
    //  the same window, not a separate floating window.
    public HelpPaneHostMode HostMode => HelpPaneHostMode.Adjacent;

    public void OnPaneOpening(double desiredWidth) => _help.OnPaneOpening(desiredWidth);

    public void OnPaneClosed() => _help.OnPaneClosed();

    public FrameworkElement? ResolveControlByName(string name)
        => FindName(name) as FrameworkElement;

    public void OpenHelpPane(string? topicId = null) => _help.OpenHelpPane(topicId);

    #endregion
}
