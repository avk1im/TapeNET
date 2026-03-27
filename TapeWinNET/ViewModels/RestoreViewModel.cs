using System.Collections.ObjectModel;
using System.Windows.Input;

using FclNET;

using TapeLibNET;
using TapeWinNET.Converters;
using TapeWinNET.Models;
using TapeWinNET.Services;
using TapeWinNET.Utils;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Request data gathered by the RestoreWindow for executing a restore/validate/verify operation.
/// </summary>
public record RestoreRequest(
    RestoreMode Mode,
    List<int> SetIndexes,
    bool Incremental,
    ITapeFileFilter? FileFilter,
    string? TargetDirectory,
    bool RecurseSubdirectories,
    TapeHowToHandleExisting HandleExisting);

/// <summary>
/// Represents an option in the "Handle existing files" combo box.
/// </summary>
public record HandleExistingOption(TapeHowToHandleExisting Value, string Display)
{
    public override string ToString() => Display;

    public static HandleExistingOption[] All { get; } =
    [
        new(TapeHowToHandleExisting.KeepBoth, "Keep Both"),
        new(TapeHowToHandleExisting.Skip, "Skip"),
        new(TapeHowToHandleExisting.Overwrite, "Overwrite"),
    ];
}

/// <summary>
/// ViewModel for the RestoreWindow dialog.
/// Gathers settings for a restore/validate/verify operation.
/// Supports two modes: set-based (backup sets) and file-based (individual files from one set).
/// </summary>
public class RestoreViewModel : ViewModelBase
{
    private readonly Action<RestoreRequest> _onStart;
    private readonly Action _onCancel;

    private RestoreMode _mode;
    private bool _incremental;
    private bool _restoreToOriginal = true;
    private bool _recurseSubdirectories;
    private string _targetDirectory = string.Empty;
    private HandleExistingOption _selectedHandleExisting = HandleExistingOption.All[0]; // Keep Both

    // Filter infrastructure
    private FilteredFileList? _filteredFiles;
    private Dictionary<TapeFileInfo, FileListItem>? _fileListItemsByFile;
    private FclEvaluator? _storedEvaluator;
    private string _itemsGroupHeader = string.Empty;

    /// <summary>Whether this dialog operates on individual files rather than backup sets.</summary>
    public bool IsFileMode { get; }

    /// <summary>The dialog-local <see cref="FilteredFileList"/> for file mode.
    ///  Used by RestoreWindow to wire <see cref="Controls.FileFilterPane.FilterTarget"/>.
    ///  <c>null</c> in set mode.</summary>
    public FilteredFileList? FilterTargetList => _filteredFiles;

    /// <summary>Set index for file mode (the single set the files come from).</summary>
    private readonly int _fileSetIndex;

    /// <summary>Constructor for set-based restore.</summary>
    public RestoreViewModel(
        RestoreMode mode,
        List<BackupSetListItem> preSelectedSets,
        Action<RestoreRequest> onStart,
        Action onCancel)
    {
        _onStart = onStart;
        _onCancel = onCancel;
        _mode = mode;
        IsFileMode = false;
        _recurseSubdirectories = true; // in set mode, pre-assume user wants to include subdirs

        // Clone the pre-selected sets into our local collection (all pre-checked)
        foreach (var item in preSelectedSets)
        {
            BackupSets.Add(item);
            item.IsCheckedForRestore = true;
        }

        // Auto-detect incremental: check if any selected set is incremental
        _incremental = preSelectedSets.Any(s => s.IsIncremental);

        // Initialize aggregate file stats
        UpdateItemsGroupHeader();

        BrowseTargetCommand = new RelayCommand(BrowseTarget);
        StartCommand = new RelayCommand(ExecuteStart, _ => CanStart);
        CancelCommand = new RelayCommand(_ => _onCancel());
    }

    /// <summary>Constructor for file-based restore.</summary>
    public RestoreViewModel(
        RestoreMode mode,
        IReadOnlyList<TapeFileInfo> preSelectedFiles,
        int setIndex,
        bool isIncremental,
        Action<RestoreRequest> onStart,
        Action onCancel)
    {
        _onStart = onStart;
        _onCancel = onCancel;
        _mode = mode;
        IsFileMode = true;
        _fileSetIndex = setIndex;
        _recurseSubdirectories = false; // in file mode, pre-assume user only wants the specific files, not subdirs

        // Create own FilteredFileList and FileListItems (dialog owns its checked state)
        _filteredFiles = new FilteredFileList(preSelectedFiles);
        _filteredFiles.SetAllChecked(true);

        _fileListItemsByFile = new Dictionary<TapeFileInfo, FileListItem>(preSelectedFiles.Count);
        var items = new List<FileListItem>(preSelectedFiles.Count);
        foreach (var fi in preSelectedFiles)
        {
            var item = new FileListItem(_filteredFiles, fi, showFullPath: false);
            items.Add(item);
            _fileListItemsByFile[fi] = item;
        }
        _files = items;

        _incremental = isIncremental;
        UpdateItemsGroupHeader();

        BrowseTargetCommand = new RelayCommand(BrowseTarget);
        StartCommand = new RelayCommand(ExecuteStart, _ => CanStart);
        CancelCommand = new RelayCommand(_ => _onCancel());
    }

    #region Properties

    /// <summary>The backup sets available for this operation (set mode).</summary>
    public ObservableCollection<BackupSetListItem> BackupSets { get; } = [];

    /// <summary>The files available for this operation (file mode).</summary>
    private List<FileListItem> _files = [];
    public List<FileListItem> Files
    {
        get => _files;
        private set => SetProperty(ref _files, value);
    }

    /// <summary>Whether the sets list is visible (set mode).</summary>
    public bool IsSetsListVisible => !IsFileMode;

    /// <summary>Whether the files list is visible (file mode).</summary>
    public bool IsFilesListVisible => IsFileMode;

    /// <summary>Dynamic header text for the items GroupBox.</summary>
    public string ItemsGroupHeader
    {
        get => _itemsGroupHeader;
        private set => SetProperty(ref _itemsGroupHeader, value);
    }

    /// <summary>Whether a file filter is currently active.</summary>
    public bool IsFilterActive => _filteredFiles?.IsFiltered
        ?? _storedEvaluator is not null;

    /// <summary>Total number of files (before filtering). Bound to FileFilterPane.TotalCount.</summary>
    public int FileTotalCount
    {
        get
        {
            if (IsFileMode)
                return _filteredFiles?.SourceCount ?? 0;
            var checkedSets = BackupSets.Where(b => b.IsCheckedForRestore);
            return checkedSets.Sum(s => s.FileCount);
        }
    }

    /// <summary>Number of files after filtering. Bound to FileFilterPane.FilteredCount.</summary>
    public int FileFilteredCount
    {
        get
        {
            if (IsFileMode)
                return _filteredFiles?.Count ?? 0;
            var checkedSets = BackupSets.Where(b => b.IsCheckedForRestore);
            return checkedSets.Sum(s => s.FilteredFileCount ?? s.FileCount);
        }
    }

    /// <summary>Available handle-existing options for the combo box.</summary>
    public HandleExistingOption[] HandleExistingOptions { get; } = HandleExistingOption.All;

    public string DialogTitle => _mode switch
    {
        RestoreMode.Restore => "Restore",
        RestoreMode.Validate => "Validate",
        RestoreMode.Verify => "Verify",
        _ => "Restore"
    } + (IsFileMode ? " Files" : " Backup Sets");

    public string ActionButtonText => _mode switch
    {
        RestoreMode.Restore => "Start Restore",
        RestoreMode.Validate => "Start Validate",
        RestoreMode.Verify => "Start Verify",
        _ => "Start"
    };

    public bool IsRestoreMode
    {
        get => _mode == RestoreMode.Restore;
        set { if (value) Mode = RestoreMode.Restore; }
    }

    public bool IsValidateMode
    {
        get => _mode == RestoreMode.Validate;
        set { if (value) Mode = RestoreMode.Validate; }
    }

    public bool IsVerifyMode
    {
        get => _mode == RestoreMode.Verify;
        set { if (value) Mode = RestoreMode.Verify; }
    }

    private RestoreMode Mode
    {
        set
        {
            if (_mode != value)
            {
                _mode = value;
                OnPropertyChanged(nameof(IsRestoreMode));
                OnPropertyChanged(nameof(IsValidateMode));
                OnPropertyChanged(nameof(IsVerifyMode));
                OnPropertyChanged(nameof(DialogTitle));
                OnPropertyChanged(nameof(ActionButtonText));
                OnPropertyChanged(nameof(IsRestoreToEnabled));
                OnPropertyChanged(nameof(IsTargetDirectoryInputEnabled));
                OnPropertyChanged(nameof(IsHandleExistingEnabled));
                OnPropertyChanged(nameof(WarningLevel));
                OnPropertyChanged(nameof(WarningMessage));
                OnPropertyChanged(nameof(CanStart));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool Incremental
    {
        get => _incremental;
        set => SetProperty(ref _incremental, value);
    }

    /// <summary>Whether to restore files to their original locations.</summary>
    public bool RestoreToOriginal
    {
        get => _restoreToOriginal;
        set
        {
            if (SetProperty(ref _restoreToOriginal, value))
            {
                OnPropertyChanged(nameof(RestoreToTargetDir));
                OnPropertyChanged(nameof(IsTargetDirectoryInputEnabled));
                OnPropertyChanged(nameof(IsRecurseSubdirsEnabled));
            }
        }
    }

    /// <summary>Whether to restore files to a target directory.</summary>
    public bool RestoreToTargetDir
    {
        get => !_restoreToOriginal;
        set => RestoreToOriginal = !value;
    }

    public string TargetDirectory
    {
        get => _targetDirectory;
        set => SetProperty(ref _targetDirectory, value);
    }

    public HandleExistingOption SelectedHandleExisting
    {
        get => _selectedHandleExisting;
        set
        {
            if (SetProperty(ref _selectedHandleExisting, value))
            {
                OnPropertyChanged(nameof(WarningLevel));
                OnPropertyChanged(nameof(WarningMessage));
            }
        }
    }

    /// <summary>Whether the "Restore to" group is enabled (Restore mode only).</summary>
    public bool IsRestoreToEnabled => _mode == RestoreMode.Restore;

    /// <summary>Whether the target directory text box and Browse button are enabled.</summary>
    public bool IsTargetDirectoryInputEnabled => _mode == RestoreMode.Restore && !_restoreToOriginal;

    /// <summary>Whether to recreate subdirectory structure under the target directory.</summary>
    public bool RecurseSubdirectories
    {
        get => _recurseSubdirectories;
        set => SetProperty(ref _recurseSubdirectories, value);
    }

    /// <summary>Whether the "Restore with subdirectories" checkbox is enabled.</summary>
    public bool IsRecurseSubdirsEnabled => _mode == RestoreMode.Restore && !_restoreToOriginal;

    /// <summary>Whether the handle-existing combo is enabled (Restore mode only).</summary>
    public bool IsHandleExistingEnabled => _mode == RestoreMode.Restore;

    /// <summary>Warning level for the options panel.</summary>
    public WarningLevel WarningLevel => _mode == RestoreMode.Restore ? _selectedHandleExisting.Value switch
    {
        TapeHowToHandleExisting.Overwrite => WarningLevel.Warning,
        TapeHowToHandleExisting.KeepBoth => WarningLevel.Info,
        _ => WarningLevel.None
    } : WarningLevel.None;

    /// <summary>Message for the warning panel.</summary>
    public string WarningMessage => _selectedHandleExisting.Value switch
    {
        TapeHowToHandleExisting.Overwrite => "Existing files will be overwritten without prompt.",
        TapeHowToHandleExisting.KeepBoth => "Append version number to the file name if it already exists.",
        _ => string.Empty
    };

    /// <summary>
    /// Whether all files are checked (file mode).
    /// Tri-state: true (all), false (none), null (some).
    /// Setter maps the three-state WPF cycle so clicking always
    /// toggles between all-checked and all-unchecked.
    /// </summary>
    public bool? AreAllFilesChecked
    {
        get => _filteredFiles?.AreAllFilteredChecked ?? false;
        set
        {
            if (_filteredFiles is null)
                return;

            // Three-state cycle: false→true→null→false.
            //  true  = user clicked from unchecked       → check all
            //  null  = user clicked from fully checked   → uncheck all
            //  false = user clicked from indeterminate   → check all
            _filteredFiles.SetFilteredChecked(value != null);

            // Notify all visible FileListItem rows
            foreach (var item in Files)
                item.NotifyIsCheckedChanged();

            OnPropertyChanged(nameof(AreAllFilesChecked));
            UpdateItemsGroupHeader();
            OnPropertyChanged(nameof(CanStart));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Whether all backup sets are checked (set mode).
    /// Tri-state: true (all), false (none), null (some).
    /// Setter maps the three-state WPF cycle so clicking always
    /// toggles between all-checked and all-unchecked.
    /// </summary>
    public bool? AreAllSetsChecked
    {
        get
        {
            if (BackupSets.Count == 0) return false;
            bool any = BackupSets.Any(b => b.IsCheckedForRestore);
            if (!any) return false;
            return BackupSets.All(b => b.IsCheckedForRestore) ? true : null;
        }
        set
        {
            // Three-state cycle: false→true→null→false.
            //  true  = user clicked from unchecked       → check all
            //  null  = user clicked from fully checked   → uncheck all
            //  false = user clicked from indeterminate   → check all
            bool check = value != null;
            foreach (var item in BackupSets)
                item.IsCheckedForRestore = check;
            OnPropertyChanged(nameof(AreAllSetsChecked));
            OnPropertyChanged(nameof(FileTotalCount));
            OnPropertyChanged(nameof(FileFilteredCount));
            UpdateItemsGroupHeader();
            OnPropertyChanged(nameof(CanStart));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanStart => IsFileMode
        ? (_filteredFiles?.CheckedCount ?? 0) > 0
        : BackupSets.Any(b => b.IsCheckedForRestore);

    /// <summary>Called from code-behind when a row checkbox is toggled.</summary>
    public void OnItemCheckChanged()
    {
        if (IsFileMode)
        {
            OnPropertyChanged(nameof(AreAllFilesChecked));
            UpdateItemsGroupHeader();
        }
        else
        {
            OnPropertyChanged(nameof(AreAllSetsChecked));
            OnPropertyChanged(nameof(FileTotalCount));
            OnPropertyChanged(nameof(FileFilteredCount));
            UpdateItemsGroupHeader();
        }
        OnPropertyChanged(nameof(CanStart));
        CommandManager.InvalidateRequerySuggested();
    }

    #endregion

    #region Commands

    public ICommand BrowseTargetCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }

    #endregion

    #region Command Handlers

    private void BrowseTarget(object? _)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Target Directory for Restore"
        };

        if (!string.IsNullOrWhiteSpace(_targetDirectory))
            dialog.InitialDirectory = _targetDirectory;

        if (dialog.ShowDialog() == true)
            TargetDirectory = dialog.FolderName;
    }

    private void ExecuteStart(object? _)
    {
        List<int> checkedIndexes;
        ITapeFileFilter? fileFilter = null;

        if (IsFileMode)
        {
            checkedIndexes = [_fileSetIndex];

            // Build a path-based filter from the checked files
            if (_filteredFiles is { CheckedCount: > 0 })
            {
                var checkedPaths = _filteredFiles.CheckedItems
                    .Select(fi => fi.FileDescr.FullName)
                    .ToList();

                if (checkedPaths.Count == 0)
                    return;

                fileFilter = new FclTapeFileFilter(checkedPaths);
            }
            else
            {
                return;
            }
        }
        else
        {
            checkedIndexes = [.. BackupSets
                .Where(b => b.IsCheckedForRestore)
                .Select(b => b.SetIndex)];

            // Use the stored evaluator from the FileFilterPane (if any)
            if (_storedEvaluator is not null)
                fileFilter = new FclTapeFileFilter(_storedEvaluator);
        }

        // Target directory is only used in Restore mode when "Target directory" radio is selected
        string? targetDir = (_mode == RestoreMode.Restore && !_restoreToOriginal && !string.IsNullOrWhiteSpace(_targetDirectory))
            ? _targetDirectory : null;

        var request = new RestoreRequest(
            Mode: _mode,
            SetIndexes: checkedIndexes,
            Incremental: _incremental,
            FileFilter: fileFilter,
            TargetDirectory: targetDir,
            RecurseSubdirectories: targetDir != null && _recurseSubdirectories,
            HandleExisting: _selectedHandleExisting.Value);

        _onStart(request);
    }

    #endregion

    #region Filter Methods

    /// <summary>
    /// Direct-mode callback for file mode. Called by <see cref="Controls.FileFilterPane"/>
    ///  after applying or disabling a filter on <see cref="FilterTargetList"/>.
    /// </summary>
    public async Task OnFilterStateChanged(Func<Task>? restoreAction)
    {
        if (_filteredFiles is null)
            return;

        RebuildFileListFromFilteredFiles();
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(FileTotalCount));
        OnPropertyChanged(nameof(FileFilteredCount));
        OnPropertyChanged(nameof(AreAllFilesChecked));
        OnPropertyChanged(nameof(CanStart));
        UpdateItemsGroupHeader();
        CommandManager.InvalidateRequerySuggested();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Callback-mode handler for set mode. Called by <see cref="Controls.FileFilterPane"/>
    ///  when <see cref="Controls.FileFilterPane.FilterRequested"/> is used.
    /// </summary>
    public async Task OnFilterApplied(FclEvaluator? evaluator, Func<Task>? reapplyAction)
    {
        _storedEvaluator = evaluator;

        // Set mode — create temp FilteredFileLists per set and push counts
        await ApplyFilterToBackupSetsAsync(evaluator);
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(FileTotalCount));
        OnPropertyChanged(nameof(FileFilteredCount));

        UpdateItemsGroupHeader();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Creates a temporary <see cref="FilteredFileList"/> per backup set, runs the
    ///  filter computation in parallel, then pushes the resulting
    ///  <see cref="BackupSetListItem.FilteredFileCount"/> back to each item.
    /// </summary>
    private async Task ApplyFilterToBackupSetsAsync(FclEvaluator? evaluator)
    {
        ITapeFileFilter? filter = evaluator is not null ? new FclTapeFileFilter(evaluator) : null;

        if (filter is null)
        {
            // Clear all filtered counts
            foreach (var item in BackupSets)
                item.FilteredFileCount = null;
            return;
        }

        // Create temp FilteredFileLists and start parallel computation
        var tempLists = new List<(BackupSetListItem Item, FilteredFileList FFL)>(BackupSets.Count);
        foreach (var item in BackupSets)
        {
            var ffl = new FilteredFileList(item.SetTOC);
            ffl.Filter = filter;
            tempLists.Add((item, ffl));
        }

        // Await all parallel filter computations
        await Task.WhenAll(tempLists.Select(t => t.FFL.FilterTask));

        // Push results back to BackupSetListItems
        foreach (var (item, ffl) in tempLists)
        {
            item.FilteredFileCount = ffl.IsFiltered ? ffl.Count : null;
        }
    }

    /// <summary>
    /// Rebuilds <see cref="Files"/> from the current <see cref="_filteredFiles"/> view.
    /// Reuses existing <see cref="FileListItem"/> instances via the lookup dictionary.
    /// </summary>
    private void RebuildFileListFromFilteredFiles()
    {
        if (_filteredFiles is null || _fileListItemsByFile is null)
            return;

        var newFileList = new List<FileListItem>(_filteredFiles.Count);
        foreach (var fileInfo in _filteredFiles)
        {
            if (_fileListItemsByFile.TryGetValue(fileInfo, out var item))
                newFileList.Add(item);
        }
        Files = newFileList;
    }

    /// <summary>
    /// Recomputes <see cref="ItemsGroupHeader"/> from current counts and mode.
    /// </summary>
    private void UpdateItemsGroupHeader()
    {
        if (IsFileMode)
        {
            int total = FileTotalCount;
            bool hasFilter = IsFilterActive;
            int selectedCount = _filteredFiles?.FilteredCheckedCount ?? 0;
            int filteredCount = FileFilteredCount;

            if (!hasFilter && selectedCount == filteredCount)
            {
                ItemsGroupHeader = $"Files to process ({total})";
                return;
            }

            var header = $"Files to process ({total}";
            if (hasFilter)
                header += $" → {filteredCount} filtered";
            if (selectedCount < filteredCount)
                header += $" → {selectedCount} selected";
            header += ")";
            ItemsGroupHeader = header;
        }
        else
        {
            int total = FileTotalCount;
            int filtered = FileFilteredCount;
            bool hasFilter = IsFilterActive && filtered != total;

            if (!hasFilter)
            {
                ItemsGroupHeader = total > 0
                    ? $"Backup sets to process ({total:N0} files)"
                    : "Backup sets to process";
                return;
            }

            ItemsGroupHeader = $"Backup sets to process ({total:N0} → {filtered:N0} filtered)";
        }
    }

    #endregion
}
