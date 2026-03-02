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
public record RestoreRequest(
    RestoreMode Mode,
    List<int> SetIndexes,
    bool Incremental,
    List<string>? FilePatterns,
    string? TargetDirectory,
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
    private string _filePatterns = string.Empty;
    private bool _restoreToOriginal = true;
    private string _targetDirectory = string.Empty;
    private HandleExistingOption _selectedHandleExisting = HandleExistingOption.All[0]; // Keep Both

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

        // Clone the pre-selected sets into our local collection (all pre-checked)
        foreach (var item in preSelectedSets)
        {
            BackupSets.Add(item);
            item.IsCheckedForRestore = true;
        }

        // Auto-detect incremental: check if any selected set is incremental
        _incremental = preSelectedSets.Any(s => s.IsIncremental);

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

        Files = preSelectedFiles;
        foreach (var item in preSelectedFiles)
            item.IsCheckedForRestore = true;

        _incremental = isIncremental;

        BrowseTargetCommand = new RelayCommand(BrowseTarget);
        StartCommand = new RelayCommand(ExecuteStart, _ => CanStart);
        CancelCommand = new RelayCommand(_ => _onCancel());
    }

    #region Properties

    /// <summary>The backup sets available for this operation (set mode).</summary>
    public ObservableCollection<BackupSetListItem> BackupSets { get; } = [];

    /// <summary>The files available for this operation (file mode). Uses List for efficient population.</summary>
    public List<FileListItem> Files { get; } = [];

    /// <summary>Whether the sets list is visible (set mode).</summary>
    public bool IsSetsListVisible => !IsFileMode;

    /// <summary>Whether the files list is visible (file mode).</summary>
    public bool IsFilesListVisible => IsFileMode;

    /// <summary>Header text for the items GroupBox.</summary>
    public string ItemsGroupHeader => IsFileMode
        ? "Files to process (uncheck to exclude)"
        : "Backup Sets to process (uncheck to exclude)";

    /// <summary>Whether the file patterns input row is visible.</summary>
    public bool ShowFilePatterns => !IsFileMode;

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

    public string FilePatterns
    {
        get => _filePatterns;
        set => SetProperty(ref _filePatterns, value);
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
    /// Whether all items are checked.
    /// Setter checks or unchecks every item in the appropriate list.
    /// </summary>
    public bool AreAllChecked
    {
        get => IsFileMode
            ? Files.Count > 0 && Files.All(f => f.IsCheckedForRestore)
            : BackupSets.Count > 0 && BackupSets.All(b => b.IsCheckedForRestore);
        set
        {
            if (IsFileMode)
            {
                foreach (var item in Files)
                    item.IsCheckedForRestore = value;
            }
            else
            {
                foreach (var item in BackupSets)
                    item.IsCheckedForRestore = value;
            }
            OnPropertyChanged(nameof(AreAllChecked));
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
        OnPropertyChanged(nameof(AreAllChecked));
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
        List<string>? filePatterns = null;

        if (IsFileMode)
        {
            checkedIndexes = [_fileSetIndex];
            filePatterns = [.. Files
                .Where(f => f.IsCheckedForRestore)
                .Select(f => f.FullPath)];

            if (filePatterns.Count == 0)
                return;
        }
        else
        {
            checkedIndexes = [.. BackupSets
                .Where(b => b.IsCheckedForRestore)
                .Select(b => b.SetIndex)];

            // Parse file patterns: split by ';', trim, remove empty
            if (!string.IsNullOrWhiteSpace(_filePatterns))
            {
                filePatterns = [.. _filePatterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

                if (filePatterns.Count == 0)
                    filePatterns = null;
            }
        }

        // Target directory is only used in Restore mode when "Target directory" radio is selected
        string? targetDir = (_mode == RestoreMode.Restore && !_restoreToOriginal && !string.IsNullOrWhiteSpace(_targetDirectory))
            ? _targetDirectory : null;

        var request = new RestoreRequest(
            Mode: _mode,
            SetIndexes: checkedIndexes,
            Incremental: _incremental,
            FilePatterns: filePatterns,
            TargetDirectory: targetDir,
            HandleExisting: _selectedHandleExisting.Value);

        _onStart(request);
    }

    #endregion
}
