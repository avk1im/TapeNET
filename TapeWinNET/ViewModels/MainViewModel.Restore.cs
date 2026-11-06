using System.Windows;
using System.Windows.Input;
using TapeLibNET.Services;
using TapeLibNET.Toc;
using TapeWinNET.Models;
using TapeWinNET.Utils;

namespace TapeWinNET.ViewModels;


/// <summary>
/// Partial class containing restore/validate/verify functionality for MainViewModel.
/// </summary>
public partial class MainViewModel
{
    #region Restore Fields

    private double _restoreProgressPercent;
    private string _restoreProgressText = string.Empty;
    private string _currentRestoreFile = string.Empty;
    private bool _isRestoreInProgress;
    // True while no abort has been requested; bound to the Abort Restore button's IsEnabled
    private bool _isAbortRestoreEnabled = true;

    #endregion

    #region Restore Properties

    /// <summary>
    /// Whether a restore/validate/verify operation is currently in progress.
    /// </summary>
    /// <summary>
    /// Becomes <see langword="false"/> once the user confirms the abort, disabling the Abort button.
    /// Reset to <see langword="true"/> when no restore operation is in progress.
    /// </summary>
    public bool IsAbortRestoreEnabled
    {
        get => _isAbortRestoreEnabled;
        set
        {
            if (SetProperty(ref _isAbortRestoreEnabled, value))
                OnPropertyChanged(nameof(IsAbortOperationEnabled));
        }
    }

    public bool IsRestoreInProgress
    {
        get => _isRestoreInProgress;
        set
        {
            if (SetProperty(ref _isRestoreInProgress, value))
            {
                OnPropertyChanged(nameof(IsGeneralBusy));
                OnPropertyChanged(nameof(IsOperationInProgress));
                OnPropertyChanged(nameof(IsMediaBrowsingEnabled));
                NotifyOperationPropertiesChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public double RestoreProgressPercent
    {
        get => _restoreProgressPercent;
        set
        {
            if (SetProperty(ref _restoreProgressPercent, value))
                OnPropertyChanged(nameof(OperationProgressPercent));
        }
    }

    public string RestoreProgressText
    {
        get => _restoreProgressText;
        set
        {
            if (SetProperty(ref _restoreProgressText, value))
                OnPropertyChanged(nameof(OperationProgressText));
        }
    }

    public string CurrentRestoreFile
    {
        get => _currentRestoreFile;
        set
        {
            if (SetProperty(ref _currentRestoreFile, value))
                OnPropertyChanged(nameof(CurrentOperationFile));
        }
    }

    /// <summary>
    /// Dynamic menu text for the Restore command, reflecting the current selection.
    /// Examples: "Restore 53 files selected...", "Restore set #3 | -1...", "Restore..."
    /// </summary>
    public string RestoreCommandText => GetCommandText("_Restore");

    /// <summary>
    /// Dynamic menu text for the Validate command, reflecting the current selection.
    /// </summary>
    public string ValidateCommandText => GetCommandText("_Validate");

    /// <summary>
    /// Dynamic menu text for the Verify command, reflecting the current selection.
    /// </summary>
    public string VerifyCommandText => GetCommandText("Verif_y");

    /// <summary>
    /// Builds the command menu text for a given mode verb. Appends a selection summary
    ///  when files are checked or a set is selected. Always ends with "...".
    /// </summary>
    private string GetCommandText(string verb)
    {
        var summary = GetSelectionSummaryText();
        return summary != null ? $"{verb} {summary}..." : $"{verb}...";
    }

    /// <summary>Where the restore selection comes from (Design-Encryption §11.1.2), in priority order.</summary>
    private enum RestoreSelectionKind
    {
        /// <summary>No TOC: nothing to restore.</summary>
        None,
        /// <summary>Type 1 — checkboxes: checked files across sets, and checked locked sets.</summary>
        Checked,
        /// <summary>Type 2a — the set selected in the tree (its file view is shown).</summary>
        TreeSet,
        /// <summary>Type 2b — one or more highlighted rows of the backup sets list.</summary>
        Highlighted,
        /// <summary>Type 3 — nothing selected: all sets.</summary>
        AllSets,
    }

    /// <summary>The current restore selection — computed once per change by <see cref="NotifyCommandTextChanged"/>.</summary>
    private sealed record RestoreSelection(RestoreSelectionKind Kind, IReadOnlyList<int> SetIndexes,
        int Files, long Bytes, int LockedSets)
    {
        public static readonly RestoreSelection Empty = new(RestoreSelectionKind.None, [], 0, 0, 0);
    }

    private RestoreSelection _selection = RestoreSelection.Empty;

    /// <summary>
    /// Type 1, else 2a, else 2b, else 3 — see <see cref="RestoreSelectionKind"/>. Reads the TOC: call only while it is quiet
    ///  (the views are built from the current TOC, so every index here is valid).
    /// </summary>
    private RestoreSelection ComputeRestoreSelection()
    {
        AssertTocQuiet();
        if (_tapeService.TOC is not { Count: > 0 } toc || _tocView is null)
            return RestoreSelection.Empty;

        var checkedSel = _tocView.GetCheckedSummary();
        if (checkedSel.SetIndexes.Count > 0)
            return new(RestoreSelectionKind.Checked, checkedSel.SetIndexes,
                checkedSel.Files, checkedSel.Bytes, checkedSel.LockedSets);

        // A tree node outlives its set until the tree is rebuilt: range-checked, else ignored
        if (_selectedTreeItem is { ItemType: TreeItemType.BackupSet, SetIndex: int treeSet })
            return WholeSets(RestoreSelectionKind.TreeSet, [treeSet]);

        if (_selectedBackupSets.Count > 0)
            return WholeSets(RestoreSelectionKind.Highlighted, [.. _selectedBackupSets.Select(s => s.SetIndex)]);

        // Counts are not needed for "all sets": the text names no figures, the status bar shows the media
        return new(RestoreSelectionKind.AllSets, [], 0, 0, 0);

        RestoreSelection WholeSets(RestoreSelectionKind kind, IReadOnlyList<int> sets)
        {
            int files = 0, locked = 0;
            long bytes = 0;
            foreach (int i in sets)
            {
                var set = toc[i];
                if (set.IsLocked)
                {
                    locked++;
                    continue;
                }
                files += set.Count;
                bytes += set.TotalFileSize;
            }
            return new(kind, sets, files, bytes, locked);
        }
    }


    /// <summary>"set #3 | 0" for a single set, else null.</summary>
    private string? SingleSetDisplay(RestoreSelection s)
        => s.SetIndexes.Count == 1 && _tapeService.TOC is { } toc
            ? $"set #{s.SetIndexes[0]} | {toc.SetIndexToAlt(s.SetIndexes[0])}"
            : null;

    /// <summary>
    /// The selection summary of the command texts — "195 files selected from 2 sets", "27 files from set #3 | 0",
    ///  "3 locked sets", "all sets" — or null when nothing can be restored.
    /// </summary>
    private string? GetSelectionSummaryText() => _selection.Kind switch
    {
        RestoreSelectionKind.None => null,
        RestoreSelectionKind.AllSets => "all sets",
        _ => SelectionText.Describe(_selection.Files, _selection.SetIndexes.Count, _selection.LockedSets,
            SingleSetDisplay(_selection), selected: _selection.Kind == RestoreSelectionKind.Checked),
    };

    /// <summary>
    /// The selection for the status bar, with its size — "195 files selected from 2 sets (119 MB)". Null for "all sets"
    ///  and no selection: the status bar then shows the media and its capacity.
    /// </summary>
    public string? SelectionStatus => _selection.Kind is RestoreSelectionKind.None or RestoreSelectionKind.AllSets
        ? null
        : SelectionText.Describe(_selection.Files, _selection.SetIndexes.Count, _selection.LockedSets,
            SingleSetDisplay(_selection), selected: _selection.Kind == RestoreSelectionKind.Checked,
            bytes: _selection.Bytes);

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
        if (_tocStale)
            RefreshAfterTocChange();            // a TOC change not yet shown: rebuild before acting

        var toc = _tapeService.TOC;
        if (toc == null || _tocView == null)
            return;

        // Build display items for the selected sets depending on the selection kind
        var selection = ComputeRestoreSelection();      // fresh — never act on a stale summary
        var setItems = selection.Kind switch
        {
            RestoreSelectionKind.Checked => _tocView.BuildBackupSetItemList(checkedOnly: true),   // incl. locked (F2)
            RestoreSelectionKind.AllSets => CreateAdHocSetItems(),
            RestoreSelectionKind.None => [],
            _ => CreateAdHocSetItems([.. selection.SetIndexes]),
        };

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
            mode: mode,
            preSelectedSets: setItems,
            onStart: request =>
            {
                var updatedRequest = request with
                {
                    CheckedFilesBySet = _tocView?.FromCheckedBackupSetsToFilesBySet(setItems)
                        ?? []
                };

                Application.Current.Windows.OfType<RestoreWindow>().FirstOrDefault()?.Close();
                _ = ExecuteRestoreAsync(updatedRequest);
            },
            onCancel: () =>
            {
                Application.Current.Windows.OfType<RestoreWindow>().FirstOrDefault()?.Close();
            },
            suggestNoMultivolume: _tapeService.IsInMemoryDrive);

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
                CheckedFileCount = setTOC.IsLocked ? null : setTOC.Count,   // hidden while locked
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
    /// Whether restore/validate/verify commands are enabled: not busy, media loaded, and a selection
    ///  — "all sets" when nothing is chosen.
    ///  A locked-only selection stays enabled: the service unlocks before reading.
    /// </summary>
    private bool CanStartRestore =>
        !IsBusy && _tapeService.IsMediaLoaded && _selection.Kind != RestoreSelectionKind.None;

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
                    view = _tocView.TryGetOrCreate(item.SetIndex, ShowIncrementalSets);   // null for a locked set

                if (view != null)
                {
                    view.FilteredFiles.SetAllChecked(check);
                    item.CheckedFileCount = check ? view.FilteredFiles.SourceCount : null;
                }
                else
                {
                    if (item.IsLocked)
                        _tocView?.SetCheckedWhole(item.SetIndex, check);   // no view: TOCView keeps the set-level choice
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
    /// Getter returns <see langword="true"/> (all checked), <see langword="false"/> (none checked),
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
            view = _tocView.TryGetOrCreate(item.SetIndex, ShowIncrementalSets);   // null for a locked set

        if (view != null)
        {
            view.FilteredFiles.SetFilteredChecked(check);
            item.CheckedFileCount = check ? view.FilteredFiles.FilteredCheckedCount : null;
        }
        else
        {
            if (item.IsLocked)
                _tocView?.SetCheckedWhole(item.SetIndex, check);   // no view: TOCView keeps the set-level choice
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
    /// Recomputes the restore selection and notifies everything that shows it: the three command texts, the status bar,
    ///  the commands' enabled state. Called on every selection change (file checks, set checks, tree and list selection,
    ///  TOC changes).
    /// </summary>
    private void NotifyCommandTextChanged()
    {
        if (IsTocQuiet)
        {
            _selection = ComputeRestoreSelection();
            UpdateUnlockTargets();          // the Unlock command follows the same selection (MainViewModel.Encryption.cs)
        }
        // else keep the last selection: the refresh recomputes it

        OnPropertyChanged(nameof(RestoreCommandText));
        OnPropertyChanged(nameof(ValidateCommandText));
        OnPropertyChanged(nameof(VerifyCommandText));
        OnPropertyChanged(nameof(SelectionStatus));
        OnPropertyChanged(nameof(StatusBarText));
        OnPropertyChanged(nameof(StatusBarRemaining));
        CommandManager.InvalidateRequerySuggested();
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
                    SkipAllErrors: request.SkipAllErrors,
                    EjectWhenDone: false) // we handle request.EjectWhenDone manually below, for proper window housekeeping
                {
                    NoMultivolume = request.NoMultivolume,
                    ProceedOnMediaMismatch = request.ProceedOnMediaMismatch,
                    CorrectSetNavigation = request.CorrectSetNavigation,
                    VerifySetHeader = request.VerifySetHeader,
                });

            if (!request.EjectWhenDone)
            {
                // Uncheck successfully processed files before refreshing the UI —
                //  ReloadMediaAsync rebuilds BackupSetList from _tocView which already
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
                try { await ReloadMediaAsync(); } catch { /* ignore */ }
            }
            else
                try { await EjectAsync(); } catch { /* ignore */ }

            // Determine outcome from the result record
            if (operationResult is { HasFailed: true })
            {
                LogErr($"{modeName} failed");
                SimpleBox.Show($"{modeName} failed. See log for details.", $"{modeName} Failed",
                    MessageBoxButton.OK, SimpleBox.ImageFailed);
            }
            else if (operationResult is { WasAborted: true })
            {
                LogErr($"{modeName} aborted by user");
                SimpleBox.Show($"{modeName} was aborted.", $"{modeName} Aborted",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                bool hasErrors = operationResult is { } r && !r.IsFullSuccess;

                if (hasErrors)
                {
                    SimpleBox.Show($"{modeName} completed with issues. See log for details.",
                        $"{modeName} Complete", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    SimpleBox.Show($"{modeName} completed successfully!",
                        $"{modeName} Complete", MessageBoxButton.OK, SimpleBox.ImageComplete);
                }
            }

            // A swap left the series TOC held while the drive holds another cartridge (TOCSource.Series)
            if (!request.EjectWhenDone && operationResult is { TocDescribesDrive: false } && _tapeService.IsMediaLoaded)
            {
                string inDrive = operationResult.VolumeInDrive is int v
                    ? $"The cartridge in the drive is volume #{v}"
                    : "The cartridge in the drive is not identified";
                if (SimpleBox.Show(
                        $"{inDrive}; the view shows the series' table of contents.\n\n" +
                        "Read the loaded cartridge's table of contents now? Until then, backup, delete and rename are unavailable.",
                        $"{modeName} — Volume Changed", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    await RereadMediaAsync();
            }
        }
        catch (Exception ex)
        {
            // Refresh even on failure — TOC state may have changed
            await ReloadMediaAsync();
            LogErr($"{modeName} failed: {ex.Message}");
            SimpleBox.Show($"{modeName} failed.\n\n{ex.Message}", $"{modeName} Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsRestoreInProgress = false;
            IsAbortRestoreEnabled = true;
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
            var result = SimpleBox.Show(
                "Are you sure you want to abort the current operation?",
                "Abort Operation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                agent.IsAbortRequested = true;
                IsAbortRestoreEnabled = false; // disable the command UI to indicate we're aborting
                BusyMessage = "Aborting...";
            }
        }
    }

    #endregion
}
