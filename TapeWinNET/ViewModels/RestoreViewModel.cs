using System.Collections.ObjectModel;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeWinNET.Converters;
using TapeWinNET.Models;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Request data gathered by the RestoreWindow for executing a restore/validate/verify operation.
/// </summary>
/// <param name="CheckedFilesBySet">Per-set file selection.
///  Keys are 1-based set indexes. A <c>null</c> value means all files in that set;
///  a non-null list means only those specific files.</param>
public record RestoreRequest(
    RestoreMode Mode,
    Dictionary<int, IReadOnlyList<TapeFileInfo>?> CheckedFilesBySet,
    bool Incremental,
    string? TargetDirectory,
    bool RecurseSubdirectories,
    TapeHowToHandleExisting HandleExisting,
    bool UncheckProcessedFiles);

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
/// ViewModel for the RestoreWindow confirmation dialog.
/// Displays the selected backup sets (from MainWindow checkmarks) and gathers
///  operation settings (mode, target directory, incremental, handle-existing).
/// </summary>
public class RestoreViewModel : ViewModelBase
{
    private readonly Action<RestoreRequest> _onStart;
    private readonly Action _onCancel;

    private RestoreMode _mode;
    private bool _incremental;
    private bool _thisVolumeOnly;
    private bool _restoreToOriginal = true;
    private bool _recurseSubdirectories;
    private bool _uncheckProcessedFiles = true;
    private string _targetDirectory = string.Empty;
    private HandleExistingOption _selectedHandleExisting = HandleExistingOption.All[0]; // Keep Both
    private string _itemsGroupHeader = string.Empty;

    /// <summary>
    /// Tracks whether any sets come from a different volume, cached at construction.
    /// </summary>
    private readonly bool _isMultivolume;

    public RestoreViewModel(
        RestoreMode mode,
        List<BackupSetListItem> preSelectedSets,
        Action<RestoreRequest> onStart,
        Action onCancel)
    {
        _onStart = onStart;
        _onCancel = onCancel;
        _mode = mode;
        _recurseSubdirectories = true; // pre-assume user wants to include subdirs

        // sort preSelectedSets by SetIndex descending (newest to oldest)
        preSelectedSets.Sort((a, b) => b.SetIndex.CompareTo(a.SetIndex));

        // Populate the local collection (keep checked files as-is)
        foreach (var item in preSelectedSets)
            BackupSets.Add(item);

        // Detect multivolume: sets span more than one volume, or any set is continued from prev
        var volumes = preSelectedSets.Select(s => s.Volume).Distinct().ToList();
        _isMultivolume = volumes.Count > 1
            || preSelectedSets.Any(s => s.ContinuedFromPrevVolume);

        // Auto-detect incremental: check if any selected set is incremental
        _incremental = preSelectedSets.Any(s => s.IsIncremental);

        // Initialize aggregate summaries
        UpdateItemsGroupHeader();
        UpdatePreview();

        BrowseTargetCommand = new RelayCommand(BrowseTarget);
        StartCommand = new RelayCommand(ExecuteStart, _ => CanStart);
        CancelCommand = new RelayCommand(_ => _onCancel());
        RemoveUncheckedSetsCommand = new RelayCommand(
            _ => RemoveUncheckedSets(),
            _ => BackupSets.Any(b => b.IsCheckedForRestore == false));
    }

    #region Properties

    /// <summary>The backup sets available for this operation.</summary>
    public ObservableCollection<BackupSetListItem> BackupSets { get; } = [];

    /// <summary>Dynamic header text for the items GroupBox.</summary>
    public string ItemsGroupHeader
    {
        get => _itemsGroupHeader;
        private set => SetProperty(ref _itemsGroupHeader, value);
    }

    /// <summary>
    /// Total number of files across all checked sets.
    ///  For fully checked sets (<c>true</c>), counts all files;
    ///  for partially checked sets (<c>null</c>), counts only the checked files.
    /// </summary>
    public int TotalFileCount
    {
        get
        {
            return BackupSets
                .Where(b => b.IsCheckedForRestore != false)
                .Sum(s => s.IsCheckedForRestore == true
                    ? s.FileCount
                    : (s.CheckedFileCount ?? s.FileCount));
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
    };

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

    /// <summary>
    /// Whether to confine the operation to backup sets on the current volume only.
    ///  When checked, unchecks all sets not on the current volume. Only enabled
    ///  when the selection spans multiple volumes.
    /// </summary>
    public bool ThisVolumeOnly
    {
        get => _thisVolumeOnly;
        set
        {
            if (SetProperty(ref _thisVolumeOnly, value) && value)
            {
                // Uncheck all sets that aren't on the current volume
                foreach (var item in BackupSets)
                {
                    if (!item.IsOnCurrentVolume)
                        item.IsCheckedForRestore = false;
                }
                NotifySelectionChanged();
            }
        }
    }

    /// <summary>
    /// "This volume only" is enabled only when multivolume is detected.
    /// </summary>
    public bool IsThisVolumeOnlyEnabled => _isMultivolume;

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

    /// <summary>
    /// Whether to uncheck successfully processed files after the operation completes.
    /// This makes it easy to identify unprocessed files and retry the operation.
    /// </summary>
    public bool UncheckProcessedFiles
    {
        get => _uncheckProcessedFiles;
        set => SetProperty(ref _uncheckProcessedFiles, value);
    }

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

    #region Preview Properties

    private string _previewTotalFiles = string.Empty;
    private string _previewTotalSize = string.Empty;
    private string _previewMultivolume = string.Empty;

    /// <summary>Total file count across all checked sets, formatted.</summary>
    public string PreviewTotalFiles
    {
        get => _previewTotalFiles;
        private set => SetProperty(ref _previewTotalFiles, value);
    }

    /// <summary>Total file size across all checked sets, formatted.</summary>
    public string PreviewTotalSize
    {
        get => _previewTotalSize;
        private set => SetProperty(ref _previewTotalSize, value);
    }

    /// <summary>
    /// Multivolume status: "No" for single-volume, or "Vol #1 \u2013 #3" for multi-volume.
    /// </summary>
    public string PreviewMultivolume
    {
        get => _previewMultivolume;
        private set => SetProperty(ref _previewMultivolume, value);
    }

    #endregion

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
            bool any = BackupSets.Any(b => b.IsCheckedForRestore != false);
            if (!any) return false;
            return BackupSets.All(b => b.IsCheckedForRestore == true) ? true : null;
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

            // If checking all includes non-current-volume sets, uncheck "This volume only"
            if (check && _thisVolumeOnly && BackupSets.Any(b => !b.IsOnCurrentVolume))
            {
                _thisVolumeOnly = false;
                OnPropertyChanged(nameof(ThisVolumeOnly));
            }

            NotifySelectionChanged();
        }
    }

    public bool CanStart => BackupSets.Any(b => b.IsCheckedForRestore != false);

    /// <summary>
    /// Called from code-behind when a set checkbox is toggled.
    ///  The 3-state cycling is handled by the WPF CheckBox itself (enabled via
    ///  <see cref="BackupSetListItem.HasPartialSelection"/>). We only need to
    ///  handle the "This volume only" interlock and update dependent properties.
    /// </summary>
    public void OnItemCheckChanged(BackupSetListItem? changedItem = null)
    {
        // If a non-current-volume set is (re-)checked, uncheck "This volume only"
        if (changedItem is { IsOnCurrentVolume: false, IsCheckedForRestore: not false }
            && _thisVolumeOnly)
        {
            _thisVolumeOnly = false;
            OnPropertyChanged(nameof(ThisVolumeOnly));
        }

        NotifySelectionChanged();
    }

    #endregion

    #region Commands

    public ICommand BrowseTargetCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RemoveUncheckedSetsCommand { get; }

    #endregion

    #region Command Handlers

    private void BrowseTarget(object? _)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Target Folder for Restore"
        };

        if (!string.IsNullOrWhiteSpace(_targetDirectory))
            dialog.InitialDirectory = _targetDirectory;

        if (dialog.ShowDialog() == true)
            TargetDirectory = dialog.FolderName;
    }

    private void ExecuteStart(object? _)
    {
        // Build per-set selection from checked sets (dialog doesn't do per-file selection)
        var checkedFilesBySet = new Dictionary<int, IReadOnlyList<TapeFileInfo>?>();
        foreach (var item in BackupSets.Where(b => b.IsCheckedForRestore != false))
            checkedFilesBySet[item.SetIndex] = null; // null = all files

        // Target directory is only used in Restore mode when "Target directory" radio is selected
        string? targetDir = (_mode == RestoreMode.Restore && !_restoreToOriginal && !string.IsNullOrWhiteSpace(_targetDirectory))
            ? _targetDirectory : null;

        var request = new RestoreRequest(
            Mode: _mode,
            CheckedFilesBySet: checkedFilesBySet,
            Incremental: _incremental,
            TargetDirectory: targetDir,
            RecurseSubdirectories: targetDir != null && _recurseSubdirectories,
            HandleExisting: _selectedHandleExisting.Value,
            UncheckProcessedFiles: _uncheckProcessedFiles);

        _onStart(request);
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Recomputes <see cref="ItemsGroupHeader"/> from current set/file counts.
    /// </summary>
    private void UpdateItemsGroupHeader()
    {
        int checkedSets = BackupSets.Count(b => b.IsCheckedForRestore != false);
        int totalFiles = TotalFileCount;

        if (checkedSets == 0)
        {
            ItemsGroupHeader = "Backup sets to process";
            return;
        }

        // e.g. "Backup sets to process (2 of 3 sets — 1,234 files)"
        if (checkedSets == BackupSets.Count)
            ItemsGroupHeader = totalFiles > 0
                ? $"Backup sets to process ({BackupSets.Count} sets \u2192 {totalFiles:N0} files)"
                : "Backup sets to process";
        else
            ItemsGroupHeader = $"Backup sets to process ({checkedSets} of {BackupSets.Count} sets \u2192 {totalFiles:N0} files)";
    }

    /// <summary>
    /// Recomputes the Preview section (total files, total size, multivolume status)
    ///  from the currently checked sets.
    /// </summary>
    private void UpdatePreview()
    {
        var checkedSets = BackupSets.Where(b => b.IsCheckedForRestore != false).ToList();

        if (checkedSets.Count == 0)
        {
            PreviewTotalFiles = "\u2014";
            PreviewTotalSize = "\u2014";
            PreviewMultivolume = "\u2014";
            return;
        }

        // Total files: reuse TotalFileCount which correctly distinguishes
        //  true (all files) from null (partial — only checked files)
        PreviewTotalFiles = TotalFileCount.ToString("N0");

        // Total size: for fully checked (true) or indeterminate-but-all-originally, use full set size;
        //  for partial selections, we approximate with full set size (exact per-file is in TOCView data model)
        long totalSize = checkedSets.Sum(s => s.TotalSize);
        PreviewTotalSize = Helpers.BytesToStringLong(totalSize);

        // Multivolume: determine volume range across checked sets
        var volumes = checkedSets.Select(s => s.Volume).Distinct().OrderBy(v => v).ToList();
        bool anyContinued = checkedSets.Any(s => s.ContinuedFromPrevVolume);

        if (volumes.Count <= 1 && !anyContinued)
        {
            PreviewMultivolume = "No";
        }
        else
        {
            int minVol = volumes[0];
            int maxVol = volumes[^1];
            // If a set is continued from a previous volume, the actual range extends one below
            if (anyContinued && minVol > 1)
                minVol--;
            PreviewMultivolume = $"Vol #{minVol} \u2013 #{maxVol}";
        }
    }

    /// <summary>
    /// Centralized notification after any selection state change. Updates all
    ///  dependent properties, preview, group header, and command states.
    /// </summary>
    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(AreAllSetsChecked));
        OnPropertyChanged(nameof(TotalFileCount));
        UpdateItemsGroupHeader();
        UpdatePreview();
        OnPropertyChanged(nameof(CanStart));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Removes all unchecked backup sets from the list.
    /// </summary>
    private void RemoveUncheckedSets()
    {
        var unchecked_ = BackupSets.Where(b => b.IsCheckedForRestore == false).ToList();
        foreach (var item in unchecked_)
            BackupSets.Remove(item);

        NotifySelectionChanged();
    }

    #endregion
}
