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
    private List<FileListItem>? _unfilteredFiles;
    private FclEvaluator? _storedEvaluator;
    private bool _isFilterActive;
    private int _fileTotalCount;
    private int _fileFilteredCount;
    private string _itemsGroupHeader = string.Empty;

    /// <summary>Whether this dialog operates on individual files rather than backup sets.</summary>
    public bool IsFileMode { get; }

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
        _fileTotalCount = preSelectedSets.Sum(s => s.FileCount);
        _fileFilteredCount = _fileTotalCount;
        UpdateItemsGroupHeader();

        BrowseTargetCommand = new RelayCommand(BrowseTarget);
        StartCommand = new RelayCommand(ExecuteStart, _ => CanStart);
        CancelCommand = new RelayCommand(_ => _onCancel());
    }

    /// <summary>Constructor for file-based restore.</summary>
    public RestoreViewModel(
        RestoreMode mode,
        List<FileListItem> preSelectedFiles,
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

        _files = preSelectedFiles;
        foreach (var item in preSelectedFiles)
            item.IsCheckedForRestore = true;

        _incremental = isIncremental;

        // Initialize file stats
        _fileTotalCount = preSelectedFiles.Count;
        _fileFilteredCount = _fileTotalCount;
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
    public bool IsFilterActive
    {
        get => _isFilterActive;
        private set => SetProperty(ref _isFilterActive, value);
    }

    /// <summary>Total number of files (before filtering). Bound to FileFilterPane.TotalCount.</summary>
    public int FileTotalCount
    {
        get => _fileTotalCount;
        private set => SetProperty(ref _fileTotalCount, value);
    }

    /// <summary>Number of files after filtering. Bound to FileFilterPane.FilteredCount.</summary>
    public int FileFilteredCount
    {
        get => _fileFilteredCount;
        private set => SetProperty(ref _fileFilteredCount, value);
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
        get
        {
            if (Files.Count == 0) return false;
            bool any = Files.Any(f => f.IsCheckedForRestore);
            if (!any) return false;
            return Files.All(f => f.IsCheckedForRestore) ? true : null;
        }
        set
        {
            // Three-state cycle: false→true→null→false.
            //  true  = user clicked from unchecked       → check all
            //  null  = user clicked from fully checked   → uncheck all
            //  false = user clicked from indeterminate   → check all
            bool check = value != null;
            foreach (var item in Files)
                item.IsCheckedForRestore = check;
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
            UpdateAggregateStats();
            UpdateItemsGroupHeader();
            OnPropertyChanged(nameof(CanStart));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanStart => IsFileMode
        ? Files.Any(f => f.IsCheckedForRestore)
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
            UpdateAggregateStats();
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
            var checkedPaths = Files
                .Where(f => f.IsCheckedForRestore)
                .Select(f => f.FullPath)
                .ToList();

            if (checkedPaths.Count == 0)
                return;

            fileFilter = new FclTapeFileFilter(checkedPaths);
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
    /// Callback for the <see cref="Controls.FileFilterPane"/>.
    /// Called when the user applies or disables a filter.
    /// </summary>
    /// <param name="evaluator">Ready-to-use FCL evaluator, or null to disable.</param>
    /// <param name="reapplyAction">Opaque delegate that restores the pane's UI state (unused in dialog).</param>
    public async Task OnFilterApplied(FclEvaluator? evaluator, Func<Task>? reapplyAction)
    {
        _storedEvaluator = evaluator;

        if (IsFileMode)
        {
            if (evaluator is null)
            {
                // Disable filter — restore original file list
                if (_unfilteredFiles != null)
                {
                    Files = _unfilteredFiles;
                    _unfilteredFiles = null;
                }
                IsFilterActive = false;
                FileTotalCount = Files.Count;
                FileFilteredCount = Files.Count;
            }
            else
            {
                // Apply filter — narrow visible file list
                _unfilteredFiles ??= Files;
                var filtered = await FileFilter.FilterAsync(
                    _unfilteredFiles, evaluator, item => item.FileInfo.FileDescr);
                Files = filtered;
                IsFilterActive = true;
                FileTotalCount = _unfilteredFiles.Count;
                FileFilteredCount = filtered.Count;
            }
            OnPropertyChanged(nameof(AreAllFilesChecked));
            OnPropertyChanged(nameof(CanStart));
        }
        else
        {
            // Set mode — push filter into each BackupSetListItem (async parallel),
            //  then re-sum cached per-item results
            IsFilterActive = evaluator is not null;
            await ApplyFilterToBackupSetsAsync(evaluator);
            UpdateAggregateStats();
        }

        UpdateItemsGroupHeader();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Pushes the filter into each <see cref="BackupSetListItem"/> so it can
    ///  self-compute its <see cref="BackupSetListItem.FilteredFileCount"/> asynchronously.
    /// Each item runs on a thread-pool thread in parallel, with cancellation support.
    /// </summary>
    private async Task ApplyFilterToBackupSetsAsync(FclEvaluator? evaluator)
    {
        ITapeFileFilter? filter = evaluator is not null ? new FclTapeFileFilter(evaluator) : null;
        foreach (var item in BackupSets)
            item.FileFilter = filter;

        // Await all parallel filter computations
        await Task.WhenAll(BackupSets.Select(b => b.FilterTask));
    }

    /// <summary>
    /// Sums <see cref="BackupSetListItem.FileCount"/> and
    ///  <see cref="BackupSetListItem.FilteredFileCount"/> across checked sets.
    /// Uses cached per-item values — no I/O, safe to call synchronously.
    /// </summary>
    private void UpdateAggregateStats()
    {
        var checkedSets = BackupSets.Where(b => b.IsCheckedForRestore).ToList();
        FileTotalCount = checkedSets.Sum(s => s.FileCount);
        FileFilteredCount = checkedSets.Sum(s => s.FilteredFileCount ?? s.FileCount);
    }

    /// <summary>
    /// Recomputes <see cref="ItemsGroupHeader"/> from current counts and mode.
    /// </summary>
    private void UpdateItemsGroupHeader()
    {
        if (IsFileMode)
        {
            int total = FileTotalCount;
            bool hasFilter = IsFilterActive && FileFilteredCount != total;
            int selectedCount = Files.Count(f => f.IsCheckedForRestore);

            if (!hasFilter && selectedCount == Files.Count)
            {
                ItemsGroupHeader = $"Files to process ({total})";
                return;
            }

            var header = $"Files to process ({total}";
            if (hasFilter)
                header += $" → {FileFilteredCount} filtered";
            if (selectedCount < Files.Count)
                header += $" → {selectedCount} selected";
            header += ")";
            ItemsGroupHeader = header;
        }
        else
        {
            int total = FileTotalCount;
            bool hasFilter = IsFilterActive && FileFilteredCount != total;

            if (!hasFilter)
            {
                ItemsGroupHeader = total > 0
                    ? $"Backup sets to process ({total:N0} files)"
                    : "Backup sets to process";
                return;
            }

            ItemsGroupHeader = $"Backup sets to process ({total:N0} → {FileFilteredCount:N0} filtered)";
        }
    }

    #endregion
}
