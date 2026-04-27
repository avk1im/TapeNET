using System.Windows;
using System.Windows.Input;

using TapeLibNET;
using TapeLibNET.Services;
using TapeWinNET.Converters;
using TapeWinNET.Models;
using TapeWinNET.Services;
using TapeWinNET.Utils;
using System.Diagnostics;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Partial class containing restore/validate/verify functionality for MainViewModel.
/// </summary>
public partial class MainViewModel
{
    #region Restore Fields

    private int _restoreProgressPercent;
    private string _restoreProgressText = string.Empty;
    private string _currentRestoreFile = string.Empty;
    private bool _isRestoreInProgress;

    #endregion

    #region Restore Properties

    /// <summary>
    /// Whether a restore/validate/verify operation is currently in progress.
    /// </summary>
    public bool IsRestoreInProgress
    {
        get => _isRestoreInProgress;
        set
        {
            if (SetProperty(ref _isRestoreInProgress, value))
            {
                OnPropertyChanged(nameof(IsGeneralBusy));
                OnPropertyChanged(nameof(IsOperationInProgress));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int RestoreProgressPercent
    {
        get => _restoreProgressPercent;
        set => SetProperty(ref _restoreProgressPercent, value);
    }

    public string RestoreProgressText
    {
        get => _restoreProgressText;
        set => SetProperty(ref _restoreProgressText, value);
    }

    public string CurrentRestoreFile
    {
        get => _currentRestoreFile;
        set => SetProperty(ref _currentRestoreFile, value);
    }

    /// <summary>
    /// Dynamic menu text for the Restore command, reflecting the current selection.
    /// Examples: "Restore 53 files selected...", "Restore set #3 | -1...", "Restore..."
    /// </summary>
    public string RestoreCommandText => GetCommandText("Restore");

    /// <summary>
    /// Dynamic menu text for the Validate command, reflecting the current selection.
    /// </summary>
    public string ValidateCommandText => GetCommandText("Validate");

    /// <summary>
    /// Dynamic menu text for the Verify command, reflecting the current selection.
    /// </summary>
    public string VerifyCommandText => GetCommandText("Verify");

    /// <summary>
    /// Builds the command menu text for a given mode verb. Appends a selection summary
    ///  when files are checked or a set is selected. Always ends with "...".
    /// </summary>
    private string GetCommandText(string verb)
    {
        var summary = GetSelectionSummaryText();
        return summary != null ? $"{verb} {summary}..." : $"{verb}...";
    }

    /// <summary>
    /// Returns a concise description of the current restore selection, or <c>null</c>
    ///  when nothing actionable is selected.
    /// <para>
    /// Priority: (1) checked files across sets → "N files selected",
    ///  (2) tree/list selected set → "set #std | alt",
    ///  (3) media loaded with sets → "all sets",
    ///  (4) null.
    /// </para>
    /// </summary>
    private string? GetSelectionSummaryText()
    {
        // (1) Checked files across all backup set views
        int checkedCount = _tocView?.GetTotalCheckedCount() ?? 0;
        if (checkedCount > 0)
            return $"{checkedCount:N0} files selected";

        // (2) Tree or list selected backup set (no per-file selection)
        if (_selectedTreeItem?.ItemType == TreeItemType.BackupSet)
            return $"set #{_selectedTreeItem.IndexDisplay}";

        if (SelectedBackupSet is { } selSet)
            return $"set #{selSet.IndexDisplay}";

        // (3) No specific selection — all sets when media is loaded
        if (_tapeService.TOC is { Count: > 0 })
            return "all sets";

        return null;
    }

    #endregion

    #region Restore Commands

    public ICommand RestoreCommand { get; private set; } = null!;
    public ICommand ValidateCommand { get; private set; } = null!;
    public ICommand VerifyCommand { get; private set; } = null!;
    public ICommand RestoreAllSetsCommand { get; private set; } = null!;
    public ICommand AbortRestoreCommand { get; private set; } = null!;

    /// <summary>
    /// Initializes restore-related commands. Called from constructor.
    /// </summary>
    private void InitializeRestoreCommands()
    {
        RestoreCommand = new RelayCommand(
            _ => StartRestore(RestoreMode.Restore),
            _ => CanStartRestore);
        ValidateCommand = new RelayCommand(
            _ => StartRestore(RestoreMode.Validate),
            _ => CanStartRestore);
        VerifyCommand = new RelayCommand(
            _ => StartRestore(RestoreMode.Verify),
            _ => CanStartRestore);
        RestoreAllSetsCommand = new RelayCommand(
            _ => StartRestoreAllSets(),
            _ => CanRestoreAllSets);
        AbortRestoreCommand = new RelayCommand(AbortRestore, _ => IsRestoreInProgress);
    }

    /// <summary>
    /// Whether the "Restore All Sets" command should be enabled.
    /// Requires: not busy, media loaded, TOC available with at least one set.
    /// </summary>
    private bool CanRestoreAllSets =>
        !IsBusy && _tapeService.IsMediaLoaded && _tapeService.TOC is { Count: > 0 };

    /// <summary>
    /// Creates ad-hoc items for all backup sets and opens the RestoreWindow.
    ///  Available from the Media context menu for quick "restore everything" action.
    ///  Does not alter the check state of existing <see cref="BackupSetListItem"/>s,
    ///  so cancelling the dialog leaves the UI unchanged.
    /// </summary>
    private void StartRestoreAllSets()
    {
        var toc = _tapeService.TOC;
        if (toc == null || _tocView == null || toc.Count == 0)
            return;

        var setItems = CreateAdHocSetItems(); // for all sets
        OpenRestoreWindow(RestoreMode.Restore, setItems, targetDirectory: null);
    }

    private void StartRestore(RestoreMode mode)
    {
        StartRestore(mode, targetDirectory: null);
    }

    /// <summary>
    /// Unified entry point for restore/validate/verify operations.
    /// Builds the checked-files-by-set dictionary from MainWindow checkmarks
    /// (single source of truth), resolves display items, and opens RestoreWindow.
    /// Falls back to all sets when nothing specific is selected.
    /// </summary>
    private void StartRestore(RestoreMode mode, string? targetDirectory)
    {
        var toc = _tapeService.TOC;
        if (toc == null || _tocView == null)
            return;

        // Build display items for the selected sets
        var setItems = _tocView.BuildBackupSetItemList(checkedOnly: true);

        // Fall back to selected set(s) when no explicit file checkmarks exist
        if (setItems.Count == 0)
        {
            var fallbackIndexes = SelectedSetIndexes;
            // If nothing explicitly selected, fall back to all sets
            setItems = CreateAdHocSetItems(fallbackIndexes.Count > 0 ? fallbackIndexes : null);
        }

        if (setItems.Count == 0)
            return;

        OpenRestoreWindow(mode, setItems, targetDirectory);
    }

    /// <summary>
    /// Creates the <see cref="RestoreViewModel"/>, applies optional pre-fill, and shows
    ///  the <see cref="RestoreWindow"/> dialog. Shared by <see cref="StartRestore"/> and
    ///  <see cref="StartRestoreAllSets"/>.
    /// </summary>
    private void OpenRestoreWindow(RestoreMode mode, List<BackupSetListItem> setItems,
        string? targetDirectory)
    {
        var viewModel = new RestoreViewModel(
            mode,
            setItems,
            request =>
            {
                var updatedRequest = request with
                {
                    CheckedFilesBySet = _tocView?.FromCheckedBackupSetsToFilesBySet(setItems)
                        ?? []
                };

                Application.Current.Windows.OfType<RestoreWindow>().FirstOrDefault()?.Close();
                _ = ExecuteRestoreAsync(updatedRequest);
            },
            () =>
            {
                Application.Current.Windows.OfType<RestoreWindow>().FirstOrDefault()?.Close();
            });

        // Pre-fill target directory (e.g. from drag-to-Explorer)
        if (targetDirectory != null)
        {
            viewModel.RestoreToTargetDir = true;
            viewModel.TargetDirectory = targetDirectory;
        }

        var window = new RestoreWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    /// <summary>
    /// Creates ad-hoc <see cref="BackupSetListItem"/>s for the given set indexes,
    ///  marked as fully checked for restore. Used when the user has not explicitly
    ///  checked individual files (e.g. tree-selected set, or "all sets" fallback).
    /// </summary>
    /// <param name="setIndexes">
    /// The 1-based set indexes to create items for. If null, creates for all sets in the TOC.
    /// </param>
    private List<BackupSetListItem> CreateAdHocSetItems(List<int>? setIndexes = null)
    {
        var toc = _tapeService.TOC;
        if (toc == null)
            return [];

        setIndexes ??= [.. Enumerable.Range(1, toc.Count)];

        var items = new List<BackupSetListItem>(setIndexes.Count);
        foreach (var idx in setIndexes)
        {
            var setTOC = toc[idx];
            var altIdx = toc.SetIndexToAlt(idx);
            items.Add(new BackupSetListItem(setTOC, idx, altIdx, setTOC.Volume == toc.Volume)
            {
                IsCheckedForRestore = true,
                CheckedFileCount = setTOC.Count
            });
        }
        return items;
    }

    /// <summary>
    /// Initiates a drag-to-Explorer operation. Called from code-behind
    /// when the user drags from a TreeView or ListView control.
    /// Creates a marker file, performs DoDragDrop, detects the target Explorer folder,
    /// and opens RestoreWindow with the target directory pre-filled.
    /// </summary>
    public void StartDragRestoreToExplorer(DependencyObject dragSource)
    {
        if (!CanStartRestore) return;

        var markerPath = ExplorerDropHelper.CreateMarkerFile();
        if (markerPath == null)
            return;

        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { markerPath });
            var result = DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Copy);

            string? targetFolder = null;
            if (result != DragDropEffects.None)
                targetFolder = ExplorerDropHelper.GetExplorerFolderAtCursor();

            ExplorerDropHelper.CleanupMarker(markerPath, targetFolder);

            if (targetFolder != null)
            {
                // Bring our window back to the foreground — Explorer still has focus after the drop
                Application.Current.MainWindow?.Activate();
                StartRestore(RestoreMode.Restore, targetFolder);
            }
        }
        catch { }
        finally
        {
            ExplorerDropHelper.CleanupMarker(markerPath, null);
        }
    }

    /// <summary>
    /// Whether restore/validate/verify commands should be enabled.
    /// Requires: not busy, media loaded, TOC available with at least one set.
    ///  When nothing specific is selected, "all sets" is the implicit target.
    /// </summary>
    private bool CanStartRestore =>
        CanRestoreAllSets
        || (!IsBusy && _tapeService.IsMediaLoaded && _tapeService.TOC != null
            && (SelectedSetIndexes.Count > 0 || HasFilesCheckedForRestore));

    /// <summary>
    /// Gets or sets whether all backup sets are checked for restore.
    /// Setter checks or unchecks every item in BackupSetList, propagating
    ///  to underlying <see cref="FilteredFileList"/>s and updating counts.
    /// </summary>
    public bool? AreAllBackupSetsChecked
    {
        get => BackupSetList.Count == 0 ? false
            : BackupSetList.All(b => b.IsCheckedForRestore == true) ? true
            : BackupSetList.All(b => b.IsCheckedForRestore == false) ? false
            : null;
        set
        {
            if (BackupSetList.Count == 0)
                return;

            // Three-state cycle: false→true→null→false.
            //  true  = user clicked from unchecked       → check all
            //  null  = user clicked from fully checked   → uncheck all
            //  false = user clicked from indeterminate   → check all

            bool check = value != null;
            foreach (var item in BackupSetList)
            {
                // Ensure the BackupSetView exists when checking, so checked state
                //  propagates to FilteredFileList even for non-visited sets.
                var view = _tocView?[item.SetIndex];
                if (view == null && check && _tocView != null)
                    view = _tocView.GetOrCreate(item.SetIndex, ShowIncrementalSets);

                if (view != null)
                {
                    view.FilteredFiles.SetAllChecked(check);
                    item.CheckedFileCount = check ? view.FilteredFiles.SourceCount : null;
                }
                else
                {
                    item.CheckedFileCount = null;
                }
                item.IsCheckedForRestore = check;
            }

            // If the currently displayed set is affected, refresh file checkmarks
            if (_currentSetView != null)
            {
                foreach (var fi in FileList)
                    fi.NotifyIsCheckedChanged();
                OnPropertyChanged(nameof(AreAllFilesChecked));
                UpdateFileTableHeader();
            }

            OnPropertyChanged(nameof(AreAllBackupSetsChecked));
            NotifyCommandTextChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Gets or sets whether all files are checked for restore.
    /// Getter returns <c>true</c> (all checked), <c>false</c> (none checked),
    /// or <c>null</c> (some checked) for tri-state display.
    /// Setter checks or unchecks every item in the current filtered view — the
    /// three-state WPF cycle (false→true→null→false) is mapped so clicking
    /// always toggles between all-checked and all-unchecked.
    /// </summary>
    public bool? AreAllFilesChecked
    {
        get => _currentSetView is { } sv ? sv.FilteredFiles.AreAllFilteredChecked : false;
        set
        {
            if (_currentSetView is null)
                return;

            // Three-state cycle: false→true→null→false.
            //  true  = user clicked from unchecked       → check all
            //  null  = user clicked from fully checked   → uncheck all
            //  false = user clicked from indeterminate   → check all
            _currentSetView.FilteredFiles.SetFilteredChecked(value != null);

            // Notify all visible FileListItem rows
            foreach (var item in FileList)
                item.NotifyIsCheckedChanged();

            OnPropertyChanged(nameof(AreAllFilesChecked));
            UpdateFileTableHeader();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Whether any files are selected for restore — either explicitly checkmarked
    /// or selected (clicked) in the file ListView.
    /// </summary>
    private bool HasFilesCheckedForRestore =>
        (_currentSetView is { } sv && sv.FilteredFiles.CheckedCount > 0) || SelectedFile != null;

    /// <summary>
    /// Called from code-behind when a file row checkbox is toggled.
    ///  Pushes the new tri-state and checked count back to the set-level
    ///  <see cref="BackupSetListItem"/>.
    /// </summary>
    public void OnFileCheckChanged()
    {
        // Push tri-state and count back to the set-level display item
        if (_currentSetView is { } sv)
        {
            var setItem = BackupSetList.FirstOrDefault(b => b.SetIndex == sv.SetIndex);
            if (setItem != null)
            {
                int checked_ = sv.FilteredFiles.CheckedCount;
                int total = sv.FilteredFiles.SourceCount;
                setItem.CheckedFileCount = checked_ > 0 ? checked_ : null;
                setItem.IsCheckedForRestore = checked_ == 0 ? false
                    : checked_ == total ? true
                    : null; // partial
            }
        }

        OnPropertyChanged(nameof(AreAllFilesChecked));
        OnPropertyChanged(nameof(AreAllBackupSetsChecked));
        UpdateFileTableHeader();
        NotifyCommandTextChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Notifies all backup-set-level properties after a check state change.
    ///  Called after individual item changes and from <see cref="AreAllBackupSetsChecked"/> setter.
    /// </summary>
    public void OnBackupSetCheckChanged()
    {
        OnPropertyChanged(nameof(AreAllBackupSetsChecked));
        NotifyCommandTextChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Toggles a backup set's checked state and propagates to the underlying
    ///  <see cref="FilteredFileList"/> if the set has been navigated to.
    ///  Called from code-behind on row click (where <see cref="BackupSetListItem.IsCheckedForRestore"/>
    ///  has not yet been changed).
    /// </summary>
    public void ToggleBackupSetChecked(BackupSetListItem item)
    {
        // true → false (uncheck all), false/null → true (check all)
        bool check = item.IsCheckedForRestore != true;
        ApplyBackupSetCheckedState(item, check);
    }

    /// <summary>
    /// Synchronizes a backup set item's file-level checked state with its current
    ///  <see cref="BackupSetListItem.IsCheckedForRestore"/> value. Called from
    ///  code-behind after the WPF checkbox binding has already updated the property.
    /// </summary>
    public void SyncBackupSetCheckedState(BackupSetListItem item)
    {
        bool check = item.IsCheckedForRestore != false;
        ApplyBackupSetCheckedState(item, check);
    }

    /// <summary>
    /// Applies a definite checked/unchecked state to a backup set, propagating
    ///  to the underlying <see cref="FilteredFileList"/> and updating display counters.
    ///  Force-creates the <see cref="BackupSetView"/> when needed so that
    ///  <see cref="TOCView.GetCheckedFilesBySet"/> picks up the checked state
    ///  even for sets the user has not navigated to yet.
    /// </summary>
    private void ApplyBackupSetCheckedState(BackupSetListItem item, bool check)
    {
        // Ensure the BackupSetView exists so checked state is tracked in FilteredFileList.
        //  Non-visited sets won't have a view yet — create one on demand when checking.
        var view = _tocView?[item.SetIndex];
        if (view == null && check && _tocView != null)
            view = _tocView.GetOrCreate(item.SetIndex, ShowIncrementalSets);

        if (view != null)
        {
            view.FilteredFiles.SetAllChecked(check);
            item.CheckedFileCount = check ? view.FilteredFiles.SourceCount : null;
        }
        else
        {
            item.CheckedFileCount = null;
        }

        // After set-level toggle, state is always definite (true or false);
        //  indeterminate (null) only arises from per-file selection.
        item.IsCheckedForRestore = check;

        // If this is the currently displayed set, refresh file checkmarks
        if (_currentSetView?.SetIndex == item.SetIndex)
        {
            foreach (var fi in FileList)
                fi.NotifyIsCheckedChanged();
            OnPropertyChanged(nameof(AreAllFilesChecked));
            UpdateFileTableHeader();
        }

        OnBackupSetCheckChanged();
    }

    /// <summary>
    /// Fires PropertyChanged for all dynamic command text properties.
    ///  Called whenever the selection state changes (file checks, set checks, tree selection).
    /// </summary>
    private void NotifyCommandTextChanged()
    {
        OnPropertyChanged(nameof(RestoreCommandText));
        OnPropertyChanged(nameof(ValidateCommandText));
        OnPropertyChanged(nameof(VerifyCommandText));
    }

    /// <summary>
    /// Gets the set indexes selected for restore: checked sets in the backup set list,
    /// or the single set selected in the tree view.
    /// </summary>
    private List<int> SelectedSetIndexes
    {
        get
        {
            // First try: checked backup sets in the list view (multi-select via checkboxes)
            //  Include fully checked (true) and partially checked (null/indeterminate) sets
            var checkedSets = BackupSetList
                .Where(b => b.IsCheckedForRestore != false)
                .Select(b => b.SetIndex)
                .ToList();

            if (checkedSets.Count > 0)
                return checkedSets;

            // Second try: backup set selected in tree view
            if (_selectedTreeItem?.ItemType == TreeItemType.BackupSet && _selectedTreeItem.SetIndex.HasValue)
                return [_selectedTreeItem.SetIndex.Value];

            // Third try: backup set selected (clicked) in the backup set list view
            if (SelectedBackupSet != null)
                return [SelectedBackupSet.SetIndex];

            return [];
        }
    }

    #endregion

    #region Private Methods - Restore Operations

    private async Task ExecuteRestoreAsync(RestoreFormData request)
    {
        var mode = request.Mode;
        var checkedFilesBySet = request.CheckedFilesBySet;
        var incremental = request.Incremental;
        var targetDirectory = request.TargetDirectory;
        var recurseSubdirectories = request.RecurseSubdirectories;
        var handleExisting = request.HandleExisting;
        var uncheckProcessedFiles = request.UncheckProcessedFiles;

        string modeName = mode switch
        {
            RestoreMode.Restore => "Restore",
            RestoreMode.Validate => "Validate",
            RestoreMode.Verify => "Verify",
            _ => "Process"
        };

        IsBusy = true;
        IsRestoreInProgress = true;
        BusyMessage = $"{modeName} in progress...";
        RestoreProgressPercent = 0;
        RestoreProgressText = "Starting...";
        CurrentRestoreFile = string.Empty;

        RestoreResult? operationResult = null;

        try
        {
            operationResult = await _tapeService.ExecuteRestoreAsync(
                new RestoreRequest(
                    Mode: mode,
                    CheckedFilesBySet: checkedFilesBySet,
                    Incremental: incremental,
                    TargetDirectory: targetDirectory,
                    RecurseSubdirectories: recurseSubdirectories,
                    HandleExisting: handleExisting,
                    SkipAllErrors: request.SkipAllErrors));

            // Uncheck successfully processed files before refreshing the UI —
            //  RefreshAsync rebuilds BackupSetList from _tocView which already
            //  reflects the updated checked state, so no manual UI sync needed.
            if (uncheckProcessedFiles && operationResult is { ProcessedFiles.Count: > 0 } result2
                && _tocView != null)
            {
                var processedBySet = result2.ProcessedFiles
                    .ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<TapeFileInfo>?)kvp.Value);
                _tocView.SetCheckedFilesBySet(processedBySet, isChecked: false);
            }

            // Refresh tree after operation — always, regardless of outcome,
            //  to keep TOCView in sync with the (possibly modified) TOC
            await RefreshAsync();

            // Determine outcome from the result record
            if (operationResult is { HasFailed: true })
            {
                LogErr($"{modeName} failed");
                MessageBox.Show($"{modeName} failed. See log for details.", $"{modeName} Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else if (operationResult is { WasAborted: true })
            {
                LogErr($"{modeName} aborted by user");
                MessageBox.Show($"{modeName} was aborted.", $"{modeName} Aborted",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                bool hasErrors = operationResult is { } r && !r.IsFullSuccess;

                if (hasErrors)
                {
                    MessageBox.Show($"{modeName} completed with issues. See log for details.",
                        $"{modeName} Complete", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show($"{modeName} completed successfully!",
                        $"{modeName} Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            // Refresh even on failure — TOC state may have changed
            await RefreshAsync();
            LogErr($"{modeName} failed: {ex.Message}");
            MessageBox.Show($"{modeName} failed.\n\n{ex.Message}", $"{modeName} Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsRestoreInProgress = false;
            IsBusy = false;
            BusyMessage = string.Empty;
            RestoreProgressText = string.Empty;
            CurrentRestoreFile = string.Empty;
        }
    }

    private void AbortRestore(object? parameter)
    {
        var agent = _tapeService.Agent;
        if (agent != null)
        {
            var result = MessageBox.Show(
                "Are you sure you want to abort the current operation?",
                "Abort Operation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                agent.IsAbortRequested = true;
                BusyMessage = "Aborting...";
            }
        }
    }

    #endregion
}
