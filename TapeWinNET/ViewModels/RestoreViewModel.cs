using System.Collections.ObjectModel;
using System.Windows.Input;

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
    private bool _restoreToOriginal = true;
    private bool _recurseSubdirectories;
    private string _targetDirectory = string.Empty;
    private HandleExistingOption _selectedHandleExisting = HandleExistingOption.All[0]; // Keep Both
    private string _itemsGroupHeader = string.Empty;

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

        // Populate the local collection (all pre-checked)
        foreach (var item in preSelectedSets)
        {
            BackupSets.Add(item);
            item.IsCheckedForRestore = true;
        }

        // Auto-detect incremental: check if any selected set is incremental
        _incremental = preSelectedSets.Any(s => s.IsIncremental);

        // Initialize aggregate summary
        UpdateItemsGroupHeader();

        BrowseTargetCommand = new RelayCommand(BrowseTarget);
        StartCommand = new RelayCommand(ExecuteStart, _ => CanStart);
        CancelCommand = new RelayCommand(_ => _onCancel());
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

    /// <summary>Total number of files across all checked sets.</summary>
    public int TotalFileCount
    {
        get
        {
            var checkedSets = BackupSets.Where(b => b.IsCheckedForRestore != false);
            return checkedSets.Sum(s => s.CheckedFileCount ?? s.FileCount);
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
            OnPropertyChanged(nameof(AreAllSetsChecked));
            OnPropertyChanged(nameof(TotalFileCount));
            UpdateItemsGroupHeader();
            OnPropertyChanged(nameof(CanStart));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanStart => BackupSets.Any(b => b.IsCheckedForRestore != false);

    /// <summary>Called from code-behind when a set checkbox is toggled.</summary>
    public void OnItemCheckChanged()
    {
        OnPropertyChanged(nameof(AreAllSetsChecked));
        OnPropertyChanged(nameof(TotalFileCount));
        UpdateItemsGroupHeader();
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
            HandleExisting: _selectedHandleExisting.Value);

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
                ? $"Backup sets to process ({totalFiles:N0} files)"
                : "Backup sets to process";
        else
            ItemsGroupHeader = $"Backup sets to process ({checkedSets} of {BackupSets.Count} sets \u2014 {totalFiles:N0} files)";
    }

    #endregion
}
