using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeLibNET.Virtual;
using TapeLibNET.Services;
using TapeWinNET.Converters;

using TapeWinNET.Controls;
using TapeWinNET.Models;
using TapeWinNET.Services;
using TapeWinNET.Utils;


namespace TapeWinNET.ViewModels;

/// <summary>
/// Defines what type of content is displayed in the content pane.
/// </summary>
public enum ContentPaneType
{
    /// <summary>Drive selected: properties only, no table</summary>
    DriveInfo,
    /// <summary>Media selected: properties + backup sets table</summary>
    MediaInfo,
    /// <summary>Backup set selected: properties + files table</summary>
    BackupSetInfo
}

/// <summary>
/// Represents a menu item for opening a specific tape drive.
/// </summary>
public record DriveMenuItem(string Header, int DriveNumber, ICommand Command);

public partial class MainViewModel : ViewModelBase
{
    private const int MruMaxCount = 4;

    private readonly TapeService _tapeService;
    private readonly MruFileList _virtualDriveMru = new("VirtualDriveMru.json", MruMaxCount);
    private readonly AppSettings _settings = AppSettings.LoadFromFile();
    private string _windowTitle = "TapeWin - Tape Backup Manager";
    private string _statusMessage = "Ready";
    private string _busyMessage = string.Empty;
    private string _propertiesHeader = "Properties";
    private string _tableHeader = "Content";
    private bool _isBusy;
    private bool _isBackupInProgress;
    private bool _isTOCLoadInProgress;
    // Set to true when the user explicitly cancels a TOC load, so ReadTOCWithUIAsync
    //  can suppress the failure dialog that would otherwise appear.
    private bool _isTOCLoadCancelled;
    private bool _isTOCAbortPending;
    private bool _showFullPathname = false;
    private bool _showIncrementalSets = true;
    private FileListItem? _selectedFile;
    private BackupSetListItem? _selectedBackupSet;
    private TapeTreeItemViewModel? _selectedTreeItem;
    private ContentPaneType _contentType = ContentPaneType.DriveInfo;

    // Media usage bar
    private bool _showUsageBar;

    // TOC view model — owns BackupSetViews with per-set FilteredFileList,
    //  checked state, filter state, and FileListItem dictionaries.
    private TOCView? _tocView;
    private BackupSetView? _currentSetView;

    // Backup fields are in MainViewModel.Backup.cs

    public MainViewModel()
    {
        _tapeService = new TapeService(Application.Current.Dispatcher, this);
        _tapeService.StatusChanged += OnStatusChanged;

        // Initialize commands
        OpenDriveCommand = new AsyncRelayCommand(OpenDriveAsync, _ => !IsBusy);
        OpenDriveByNameCommand = new AsyncRelayCommand(OpenDriveByNameAsync, () => !IsBusy);
        OpenVirtualDriveCommand = new RelayCommand(ShowOpenVirtualDriveWindow, _ => !IsBusy);
        OpenRecentVirtualDriveCommand = new AsyncRelayCommand(OpenRecentVirtualDriveAsync, _ => !IsBusy);
        SetIoSpeedCommand = new RelayCommand(SetIoSpeed, _ => IsIoSpeedEnabled);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && _tapeService.IsDriveOpen);
        RereadTOCCommand = new AsyncRelayCommand(RereadTOCAsync, () => !IsBusy && _tapeService.IsDriveOpen);
        EjectCommand = new AsyncRelayCommand(EjectAsync, () => !IsBusy && _tapeService.IsMediaLoaded);
        FormatMediaCommand = new RelayCommand(ShowFormatMediaWindow, _ => !IsBusy && _tapeService.IsMediaLoaded);
        DeleteBackupSetsCommand = new RelayCommand(ShowDeleteBackupSetsWindow, _ => !IsBusy && _tapeService.IsMediaLoaded && !_tapeService.IsTOCFromFile && (_tapeService.TOC?.Count ?? 0) > 0);
        ExportTOCCommand = new AsyncRelayCommand(ExportTOCAsync, () => !IsBusy && _tapeService.TOC != null);
        ImportTOCCommand = new AsyncRelayCommand(ImportTOCAsync, () => !IsBusy && _tapeService.IsDriveOpen);
        NavigateToBackupSetCommand = new RelayCommand(NavigateToSelectedBackupSet, _ => SelectedBackupSet != null);
        UsageBar = new MediaUsageBarPresenter(_tapeService, SelectBackupSetByIndex);
        RenameSelectedCommand = new AsyncRelayCommand(RenameSelectedAsync, () => CanRenameSelected);
        RenameMediaCommand = new AsyncRelayCommand(RenameMediaAsync, () => CanRenameMedia);
        ExitCommand = new RelayCommand(Exit);
        AboutCommand = new RelayCommand(ShowAbout);

        AbortTOCLoadCommand = new RelayCommand(_ => AbortTOCLoad());

        // Initialize log commands (from MainViewModel.Log.cs)
        InitializeLogCommands();

        // Initialize backup commands (from MainViewModel.Backup.cs)
        InitializeBackupCommands();

        // Initialize restore commands (from MainViewModel.Restore.cs)
        InitializeRestoreCommands();

        // Initialize drive menu items
        InitializeDriveMenu();

        // Initialize MRU virtual drive menu items
        RefreshRecentVirtualDriveMenu();

        // Restore view options from settings
        _showUsageBar = _settings.ShowUsageBar;
    }

    private void InitializeDriveMenu()
    {
        // Drive 0 is always available as the most common default
        DriveMenuItems.Add(new DriveMenuItem(
            Header: "Drive _0",
            DriveNumber: 0,
            Command: OpenDriveCommand));

        // "Specify..." lets the user enter a device name directly
        DriveMenuItems.Add(new DriveMenuItem(
            Header: "_Specify...",
            DriveNumber: -1,
            Command: OpenDriveByNameCommand));

        // Probe drives 1-9 in the background and insert any found ones before "Specify..."
        Task.Run(() =>
        {
            for (int i = 1; i <= 9; i++)
            {
                if (TapeDrive.ProbeWin32((uint)i))
                {
                    int driveNum = i;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // Insert before the last item ("Specify...")
                        int insertIndex = DriveMenuItems.Count - 1;
                        DriveMenuItems.Insert(insertIndex, new DriveMenuItem(
                            Header: $"Drive _{driveNum}",
                            DriveNumber: driveNum,
                            Command: OpenDriveCommand));
                    });
                }
            }
        });
    }

    #region Properties

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetProperty(ref _windowTitle, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string BusyMessage
    {
        get => _busyMessage;
        set => SetProperty(ref _busyMessage, value);
    }

    public string PropertiesHeader
    {
        get => _propertiesHeader;
        set => SetProperty(ref _propertiesHeader, value);
    }

    public string TableHeader
    {
        get => _tableHeader;
        set => SetProperty(ref _tableHeader, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsGeneralBusy));
                OnPropertyChanged(nameof(IsIoSpeedEnabled));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsBackupInProgress
    {
        get => _isBackupInProgress;
        set
        {
            if (SetProperty(ref _isBackupInProgress, value))
            {
                OnPropertyChanged(nameof(IsGeneralBusy));
                OnPropertyChanged(nameof(IsOperationInProgress));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// True while the TOC is being read from media (shows cancellable overlay).
    /// </summary>
    public bool IsTOCLoadInProgress
    {
        get => _isTOCLoadInProgress;
        set
        {
            if (SetProperty(ref _isTOCLoadInProgress, value))
            {
                OnPropertyChanged(nameof(IsGeneralBusy));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// True after the user presses Cancel on the TOC overlay, until the TOC
    ///  read actually stops. Disables the Cancel button as visual feedback.
    /// </summary>
    public bool IsTOCAbortPending
    {
        get => _isTOCAbortPending;
        private set
        {
            if (SetProperty(ref _isTOCAbortPending, value))
                OnPropertyChanged(nameof(IsTOCCancelEnabled));
        }
    }

    /// <summary>False once Cancel has been pressed; disables the button to prevent double-clicks.</summary>
    public bool IsTOCCancelEnabled => !_isTOCAbortPending;

    /// <summary>
    /// True when busy with non-backup/restore/TOC-load operations (shows full-window overlay).
    /// </summary>
    public bool IsGeneralBusy => IsBusy && !IsBackupInProgress && !IsRestoreInProgress && !IsTOCLoadInProgress;

    /// <summary>
    /// True when any tape operation (backup or restore/validate/verify) is in progress.
    /// </summary>
    public bool IsOperationInProgress => IsBackupInProgress || IsRestoreInProgress;

    // BackupProgressPercent, BackupProgressText, CurrentBackupFile properties are in MainViewModel.Backup.cs
    // RestoreProgressPercent, RestoreProgressText, CurrentRestoreFile, IsRestoreInProgress properties are in MainViewModel.Restore.cs

    public bool ShowFullPathname
    {
        get => _showFullPathname;
        set
        {
            if (SetProperty(ref _showFullPathname, value))
            {
                RefreshCurrentView();
            }
        }
    }

    public bool ShowIncrementalSets
    {
        get => _showIncrementalSets;
        set
        {
            if (SetProperty(ref _showIncrementalSets, value))
            {
                RefreshCurrentView();
            }
        }
    }

    public FileListItem? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value);
    }

    public BackupSetListItem? SelectedBackupSet
    {
        get => _selectedBackupSet;
        set
        {
            if (SetProperty(ref _selectedBackupSet, value))
            {
                NotifyCommandTextChanged();
                UsageBar.UpdateHighlight(value?.SetIndex);
            }
        }
        // Navigation now triggered by double-click or Enter, not selection change
    }

    public ContentPaneType ContentType
    {
        get => _contentType;
        set
        {
            if (value != ContentPaneType.BackupSetInfo)
                _currentSetView = null; // important to prevent backup set-only UI changes e.g. table header

            if (SetProperty(ref _contentType, value))
            {
                OnPropertyChanged(nameof(IsTableVisible));
                OnPropertyChanged(nameof(IsFileTableVisible));
                OnPropertyChanged(nameof(IsBackupSetTableVisible));
                OnPropertyChanged(nameof(IsUsageBarVisible));
            }
        }
    }

    /// <summary>Whether the table subpane is visible (Media or BackupSet selected)</summary>
    public bool IsTableVisible => ContentType != ContentPaneType.DriveInfo;

    /// <summary>Whether the files table is visible (BackupSet selected)</summary>
    public bool IsFileTableVisible => ContentType == ContentPaneType.BackupSetInfo;

    /// <summary>Whether the backup sets table is visible (Media selected)</summary>
    public bool IsBackupSetTableVisible => ContentType == ContentPaneType.MediaInfo;

    #region Media Usage Bar

    /// <summary>
    /// Whether the media usage bar is shown. Toggled via the View menu;
    ///  persisted in <see cref="AppSettings"/>.
    /// </summary>
    public bool ShowUsageBar
    {
        get => _showUsageBar;
        set
        {
            if (SetProperty(ref _showUsageBar, value))
                OnPropertyChanged(nameof(IsUsageBarVisible));
        }
    }

    /// <summary>
    /// True when the usage bar should be displayed: only meaningful (and shown)
    ///  when a media node is selected and the bar is enabled in View options.
    /// </summary>
    public bool IsUsageBarVisible => ShowUsageBar && ContentType == ContentPaneType.MediaInfo;

    /// <summary>
    /// Owns the segment list, capacity, and click command bound to the
    ///  <c>MediaUsageBarControl</c> in MainWindow.
    /// </summary>
    public MediaUsageBarPresenter UsageBar { get; }

    #endregion // Media Usage Bar

    /// <summary>Menu items for the Open Drive submenu.</summary>
    public ObservableCollection<DriveMenuItem> DriveMenuItems { get; } = [];

    /// <summary>Available IO speed options for the virtual drive.</summary>
    public IoSpeedOption[] IoSpeedOptions { get; } = IoSpeedOption.All;

    /// <summary>Whether IO speed simulation controls should be visible (virtual drive is open).</summary>
    public bool IsIoSpeedVisible => _tapeService.IsVirtualDrive;

    /// <summary>Whether IO speed simulation controls should be enabled (not busy).</summary>
    public bool IsIoSpeedEnabled => _tapeService.IsVirtualDrive && !IsBusy;

    private IoSpeedOption _selectedIoSpeed = IoSpeedOption.Unlimited;

    /// <summary>Currently selected IO speed simulation rate.</summary>
    public IoSpeedOption SelectedIoSpeed
    {
        get => _selectedIoSpeed;
        set
        {
            if (SetProperty(ref _selectedIoSpeed, value) && value != null)
            {
                _tapeService.SetVirtualIoRate(value.BytesPerSecond, value.LocateBytesPerSecond, value.SearchBytesPerSecond, value.SeekOverheadMs);
            }
        }
    }

    public ObservableCollection<TapeTreeItemViewModel> TreeItems { get; } = [];
    public ObservableCollection<PropertyItem> PropertyList { get; } = [];

    private List<FileListItem> _fileList = [];
    public List<FileListItem> FileList
    {
        get => _fileList;
        private set => SetProperty(ref _fileList, value);
    }

    public ObservableCollection<BackupSetListItem> BackupSetList { get; } = [];
    // LogMessages is in MainViewModel.Log.cs

    /// <summary>Menu items for the Recent Virtual Drives submenu.</summary>
    public ObservableCollection<DriveMenuItem> RecentVirtualDriveMenuItems { get; } = [];

    /// <summary>Whether the Recent Virtual Drives submenu should be visible.</summary>
    public bool HasRecentVirtualDrives => RecentVirtualDriveMenuItems.Count > 0;

    #endregion

    #region File Filter Properties

    /// <summary>Whether a file filter is currently applied.</summary>
    public bool IsFileFilterActive => _currentSetView?.FilteredFiles.IsFiltered ?? false;

    /// <summary>Number of files currently displayed (after filtering).</summary>
    public int FileFilteredCount => _currentSetView?.FilteredFiles.Count ?? FileList.Count;

    /// <summary>Total number of files before filtering.</summary>
    public int FileTotalCount => _currentSetView?.FilteredFiles.SourceCount ?? FileList.Count;

    /// <summary>The <see cref="FilteredFileList"/> the View layer should wire as
    ///  <see cref="Controls.FileFilterPane.FilterTarget"/>. Null when no backup set
    ///  is loaded.</summary>
    public FilteredFileList? ActiveFilterTarget => _currentSetView?.FilteredFiles;

    /// <summary>Opaque delegate that restores the filter pane’s UI state for the
    ///  current backup set. Set after <c>LoadBackupSetInfo</c> when the set had
    ///  a saved filter; consumed by the View layer and then cleared.</summary>
    public Func<Task>? PendingFilterRestore { get; set; }

    /// <summary>
    /// Called by the View layer after a direct-mode filter apply/disable.
    ///  Stores the restore delegate on the current <see cref="BackupSetView"/>,
    ///  rebuilds the display list, and refreshes all filter-related bindings.
    /// </summary>
    public async Task OnFilterStateChanged(Func<Task>? restoreAction)
    {
        if (_currentSetView is null)
            return;

        _currentSetView.SavedFilterState = restoreAction;
        FileList = _currentSetView.BuildFileItemList(ShowFullPathname);
        NotifyFilterPropertiesChanged();
        await Task.CompletedTask;
    }

    /// <summary>Resets the ViewModel-side filter state (used when loading new content).</summary>
    private void ClearFileFilter()
    {
        _currentSetView = null;
    }

    /// <summary>
    /// Recomputes <see cref="TableHeader"/> from the current total, filtered,
    /// and selected file counts. Format examples:
    /// <list type="bullet">
    ///   <item>"Files (61)"</item>
    ///   <item>"Files (61 → 11 filtered)"</item>
    ///   <item>"Files (61 → 5 selected)"</item>
    ///   <item>"Files (61 → 11 filtered → 5 selected)"</item>
    ///   <item>"Files (61 incl. incremental → 11 filtered → 5 selected)"</item>
    /// </list>
    /// </summary>
    private void UpdateFileTableHeader()
    {
        int totalCount = FileTotalCount;
        string total = (_currentSetView?.IsIncrementalView ?? false)
            ? $"{totalCount:N0} incl. incremental"
            : $"{totalCount:N0}";

        bool hasFilter = IsFileFilterActive;
        int filteredCount = FileFilteredCount;
        int checkedCount = _currentSetView?.FilteredFiles.CheckedCount ?? 0;

        if (!hasFilter && checkedCount == 0)
        {
            TableHeader = $"Files ({total})";
            return;
        }

        var header = $"Files ({total}";
        if (hasFilter)
            header += $" → {filteredCount:N0} filtered";
        if (checkedCount > 0)
            header += $" → {checkedCount:N0} selected";
        header += ")";

        TableHeader = header;
    }

    /// <summary>Fires PropertyChanged for all filter-related properties.</summary>
    private void NotifyFilterPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsFileFilterActive));
        OnPropertyChanged(nameof(FileFilteredCount));
        OnPropertyChanged(nameof(FileTotalCount));
        OnPropertyChanged(nameof(AreAllFilesChecked));
        UpdateFileTableHeader();
        CommandManager.InvalidateRequerySuggested();
    }

    #endregion

    #region Commands

    public ICommand OpenDriveCommand { get; }
    public ICommand OpenDriveByNameCommand { get; }
    public ICommand OpenVirtualDriveCommand { get; }
    public ICommand OpenRecentVirtualDriveCommand { get; }
    public ICommand SetIoSpeedCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RereadTOCCommand { get; }
    public ICommand EjectCommand { get; }
    public ICommand FormatMediaCommand { get; }
    public ICommand DeleteBackupSetsCommand { get; }
    public ICommand ExportTOCCommand { get; }
    public ICommand ImportTOCCommand { get; }
    public ICommand AbortTOCLoadCommand { get; private set; } = null!;
    // NewBackupCommand and AbortBackupCommand are in MainViewModel.Backup.cs
    // RestoreCommand, ValidateCommand, VerifyCommand, AbortRestoreCommand are in MainViewModel.Restore.cs
    public ICommand NavigateToBackupSetCommand { get; }

    public ICommand RenameSelectedCommand { get; }
    public ICommand RenameMediaCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand AboutCommand { get; }

    #endregion

    #region Public Methods

    public async Task InitializeAsync(int driveNumber)
    {
        await OpenDriveAsync(driveNumber);
    }

    public void OnTreeItemSelected(TapeTreeItemViewModel item)
    {
        _selectedTreeItem = item;

        switch (item.ItemType)
        {
            case TreeItemType.Drive:
                LoadDriveInfo();
                break;
            case TreeItemType.Tape:
                LoadMediaInfo();
                break;
            case TreeItemType.BackupSet:
                if (item.SetIndex.HasValue)
                {
                    LoadBackupSetInfo(item.SetIndex.Value);
                }
                break;
        }

        // Selection change may affect dynamic command menu text
        NotifyCommandTextChanged();
    }

    public void Cleanup()
    {
        _logFlushTimer?.Stop();
        StopMirroring();
        _tapeService.Dispose();
    }

    /// <summary>The loaded settings instance — View layer reads this to restore window layout.</summary>
    public AppSettings Settings => _settings;

    /// <summary>
    /// If the last session used a physical drive, this holds the drive number.
    /// The View layer should prompt the user before reopening.
    /// Null if last session was virtual or no previous session.
    /// </summary>
    public int? StartupPhysicalDriveNumber =>
        _settings.LastDriveNumber.HasValue && !_settings.LastDriveWasVirtual
            ? _settings.LastDriveNumber
            : null;

    /// <summary>
    /// Saves the current application state (last drive info).
    /// Call before passing to the View layer's <see cref="AppSettings"/> save.
    /// </summary>
    public void SaveSettings()
    {
        if (_tapeService.IsDriveOpen)
        {
            _settings.LastDriveNumber = _tapeService.DriveNumber;
            _settings.LastDriveWasVirtual = _tapeService.IsVirtualDrive;
        }
        else
        {
            _settings.LastDriveNumber = null;
            _settings.LastDriveWasVirtual = false;
        }
    }

    #endregion

    #region Private Methods - Drive/Media Operations

    // ─────────────────────────────────────────────────
    //  ABC primitives — single-spot drive/media/TOC operations.
    //  Composed by the higher-level command methods below.
    //
    //   A — Open drive (physical / virtual; future: remote)
    //   B — Load media (unified, not abortable)
    //   C — Read TOC  (unified, abortable via dedicated overlay)
    //
    //  Each operation has two layers:
    //   *CoreAsync   – sets busy state, calls the service, swallows exceptions,
    //                  restores prior state. Returns success only.
    //   *WithUIAsync – wraps Core with the standard MessageBox failure dialog.
    //                  Callers needing custom recovery use Core directly.
    // ─────────────────────────────────────────────────

    /// <summary>
    /// Wraps an async service call with general busy state management.
    /// Captures and restores prior IsBusy/BusyMessage so the helpers nest safely
    ///  inside an outer busy scope (e.g. FormatVirtualDriveAsync).
    /// </summary>
    private async Task<bool> RunBusyAsync(string busyMessage, Func<Task<bool>> action)
    {
        bool prevBusy = IsBusy;
        string prevMsg = BusyMessage;
        IsBusy = true;
        BusyMessage = busyMessage;
        try
        {
            try { return await action(); }
            catch { return false; }
        }
        finally
        {
            IsBusy = prevBusy;
            BusyMessage = prevMsg;
        }
    }

    // A — Open drive

    private Task<bool> OpenPhysicalDriveCoreAsync(int driveNumber) =>
        RunBusyAsync($"Opening drive {driveNumber}...",
            () => _tapeService.OpenDriveAsync(driveNumber));

    private Task<bool> OpenVirtualDriveCoreAsync(VirtualDriveOpenRequest request, FileMode mediaMode)
    {
        var msg = request.Media.InMemory ? "Creating in-memory virtual drive..."
            : mediaMode == FileMode.Create ? "Creating virtual drive..."
            : "Opening virtual drive...";
        return RunBusyAsync(msg,
            () => _tapeService.OpenVirtualDriveAsync(request.Capabilities, request.Media, mediaMode));
    }

    // B — Load media

    private Task<bool> LoadMediaCoreAsync(string busyMessage = "Loading media...") =>
        RunBusyAsync(busyMessage, () => _tapeService.LoadMediaAsync());

    // C — Read TOC (abortable; uses dedicated overlay via IsTOCLoadInProgress)

    private async Task<bool> ReadTOCCoreAsync(string busyMessage = "Reading TOC...")
    {
        bool prevBusy = IsBusy;
        string prevMsg = BusyMessage;
        _isTOCLoadCancelled = false;
        IsTOCAbortPending = false;
        IsBusy = true;
        BusyMessage = busyMessage;
        IsTOCLoadInProgress = true;
        try
        {
            try { return await _tapeService.RestoreTOCAsync(); }
            catch { return false; }
        }
        finally
        {
            IsTOCLoadInProgress = false;
            IsTOCAbortPending = false;
            IsBusy = prevBusy;
            BusyMessage = prevMsg;
        }
    }

    // ── UI wrappers — standard MessageBox failure dialogs ──

    private async Task<bool> OpenPhysicalDriveWithUIAsync(int driveNumber)
    {
        var success = await OpenPhysicalDriveCoreAsync(driveNumber);
        if (!success)
        {
            MessageBox.Show($"Failed to open drive {driveNumber}.\n\n{_tapeService.LastError}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return success;
    }

    private async Task<bool> LoadMediaWithUIAsync(
        string busyMessage = "Loading media...",
        string failureSubject = "media")
    {
        var success = await LoadMediaCoreAsync(busyMessage);
        if (!success)
        {
            MessageBox.Show($"Failed to load {failureSubject}.\n\n{_tapeService.LastError}",
                "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return success;
    }

    /// <summary>
    /// Reads the TOC; on failure offers to load a saved .tapetoc file (unless
    ///  <paramref name="offerFileImportOnFailure"/> is false, e.g. for a freshly
    ///  created virtual drive where no prior TOC could exist). Silent on user-cancel.
    /// </summary>
    private async Task<bool> ReadTOCWithUIAsync(
        string busyMessage = "Reading TOC...",
        bool offerFileImportOnFailure = true)
    {
        var success = await ReadTOCCoreAsync(busyMessage);
        if (success)
            return true;

        // User-cancelled: silent exit — no error dialog.
        if (_isTOCLoadCancelled)
            return false;

        if (!offerFileImportOnFailure)
            return false;

        var result = MessageBox.Show(
            $"Failed to read TOC from media.\n\n{_tapeService.LastError}\n\n" +
            "If you have a saved TOC file (.tapetoc), you can load it to access the media content.\n\n" +
            "Would you like to load a TOC from file?",
            "TOC Read Failed", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        return result == MessageBoxResult.Yes && await ImportTOCFromFileAsync();
    }

    private void AbortTOCLoad()
    {
        BusyMessage = "Aborting TOC load...";
        _isTOCLoadCancelled = true;
        IsTOCAbortPending = true;
        _tapeService.AbortTOCLoad();
    }

    // ─────────────────────────────────────────────────
    //  High-level command methods — compose ABC helpers
    // ─────────────────────────────────────────────────

    private async Task OpenDriveAsync(object? parameter)
    {
        int driveNumber = parameter as int? ?? 0;

        if (!await OpenPhysicalDriveWithUIAsync(driveNumber))
        {
            UpdateTreeForDriveOnly(driveNumber);
            return;
        }

        if (!await LoadMediaWithUIAsync() || !await ReadTOCWithUIAsync())
        {
            UpdateTreeForDriveOnly(driveNumber);
            NotifyIoSpeedChanged();
            return;
        }

        UpdateTreeFromTOC(driveNumber);
        // Select the most recent backup set
        SelectMostRecentSet();
        NotifyIoSpeedChanged();
    }

    private async Task OpenDriveByNameAsync()
    {
        var dialog = new AskDialog(
            "Open Drive",
            "Enter the tape device name:",
            @"\\.\TAPE0")
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true)
            return;

        var name = dialog.Answer;

        // Try to extract drive number from device name like \\.\TAPE0
        if (name.StartsWith(@"\\.\TAPE", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name[@"\\.\TAPE".Length..], out int driveNum))
        {
            await OpenDriveAsync(driveNum);
        }
        else if (int.TryParse(name, out driveNum))
        {
            await OpenDriveAsync(driveNum);
        }
        else
        {
            MessageBox.Show(
                $"Invalid device name: {name}\n\nExpected format: \\\\.\\TAPE0",
                "Open Drive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RereadTOCAsync()
    {
        if (!_tapeService.IsDriveOpen)
            return;

        if (!_tapeService.Reset())
        {
            MessageBox.Show("Cannot reload while an operation is in progress.",
                "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!_tapeService.IsDriveOpen)
            return;

        var driveNumber = _tapeService.DriveNumber;

        if (_tapeService.TOC == null)
        {
            if (!await LoadMediaWithUIAsync() || !await ReadTOCWithUIAsync())
            {
                UpdateTreeForDriveOnly(driveNumber);
                return;
            }
        }

        UpdateTreeFromTOC(driveNumber);
        // Select the most recent backup set
        SelectMostRecentSet();
    }

    private async Task EjectAsync()
    {
        IsBusy = true;
        BusyMessage = "Ejecting media...";

        try
        {
            var success = await _tapeService.EjectMediaAsync();
            if (!success)
            {
                MessageBox.Show($"Failed to eject media.\n\n{_tapeService.LastError}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                // Clear tree except drive node
                var driveNumber = _tapeService.DriveNumber;
                UpdateTreeForDriveOnly(driveNumber);
            }
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task ExportTOCAsync()
    {
        var toc = _tapeService.TOC;
        if (toc == null)
            return;

        var suggestedName = BuildTOCFileName(toc);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export TOC to File",
            Filter = $"Tape TOC files (*{TapeFileAgent.TOCFileExtension})|*{TapeFileAgent.TOCFileExtension}|All files (*.*)|*.*",
            FileName = suggestedName,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        IsBusy = true;
        BusyMessage = "Exporting TOC...";

        try
        {
            var success = await _tapeService.ExportTOCToFileAsync(dialog.FileName);
            if (success)
            {
                MessageBox.Show($"TOC exported successfully to:\n{dialog.FileName}",
                    "Export TOC", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Failed to export TOC.\n\n{_tapeService.LastError}",
                    "Export TOC", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task ImportTOCAsync()
    {
        if (_tapeService.TOC != null)
        {
            var result = MessageBox.Show(
                "This media already has a valid TOC loaded.\n\n" +
                "Are you sure you want to replace it with a TOC from file?",
                "Import TOC", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;
        }

        if (await ImportTOCFromFileAsync())
        {
            UpdateTreeFromTOC(_tapeService.DriveNumber);
            SelectMostRecentSet();
        }
    }

    /// <summary>
    /// Shows an OpenFileDialog for .tapetoc files and loads the selected TOC.
    /// Shared by the Import TOC menu command and the TOC-load-failure recovery path.
    /// </summary>
    private async Task<bool> ImportTOCFromFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import TOC from File",
            Filter = $"Tape TOC files (*{TapeFileAgent.TOCFileExtension})|*{TapeFileAgent.TOCFileExtension}|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog() != true)
            return false;

        IsBusy = true;
        BusyMessage = "Importing TOC from file...";

        try
        {
            var success = await _tapeService.ImportTOCFromFileAsync(dialog.FileName);
            if (!success)
            {
                MessageBox.Show($"Failed to import TOC from file.\n\n{_tapeService.LastError}",
                    "Import TOC", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return success;
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private static string BuildTOCFileName(TapeTOC toc)
    {
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var sanitized = new string(
            [.. (toc.Description ?? "tape").Select(c => invalidChars.Contains(c) ? '_' : c)]
            ).Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "tape";

        if (sanitized.Length > 60)
            sanitized = sanitized[..60];

        return $"{sanitized}_vol{toc.Volume}{TapeFileAgent.TOCFileExtension}";
    }

    #endregion

    // Backup Operations region is in MainViewModel.Backup.cs

    #region Private Methods - Menu Commands

    private void SetIoSpeed(object? parameter)
    {
        if (parameter is IoSpeedOption option)
            SelectedIoSpeed = option;
    }

    private void Exit(object? parameter)
    {
        if (IsOperationInProgress)
        {
            var result = MessageBox.Show(
                "An operation is in progress. Are you sure you want to exit?\n\nThe operation will be aborted.",
                "Exit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            var agent = _tapeService.Agent;
            if (agent != null)
                agent.IsAbortRequested = true;
        }

        Application.Current.Shutdown();
    }

    private void ShowAbout(object? parameter)
    {
        var aboutWindow = new AboutWindow
        {
            Owner = Application.Current.MainWindow
        };
        aboutWindow.ShowDialog();
    }

    #endregion

    #region Private Methods - Tree/View Management

    private void UpdateTreeForDriveOnly(int driveNumber)
    {
        TreeItems.Clear();
        _tocView = null;
        _currentSetView = null;
        var driveItem = TapeTreeItemViewModel.CreateDriveItem(driveNumber, _tapeService.DeviceName);
        TreeItems.Add(driveItem);
        WindowTitle = $"TapeWin - Drive {driveNumber}";

        // Show drive info when only drive is available
        LoadDriveInfo();
    }

    private void UpdateTreeFromTOC(int driveNumber)
    {
        TreeItems.Clear();
        _currentSetView = null;

        var toc = _tapeService.TOC;
        if (toc == null)
        {
            UpdateTreeForDriveOnly(driveNumber);
            return;
        }

        if (_tocView?.TOC == toc)
            _tocView.Refresh();
        else
            _tocView = new TOCView(toc);

        // Create drive node
        var driveItem = TapeTreeItemViewModel.CreateDriveItem(driveNumber, _tapeService.DeviceName);
        TreeItems.Add(driveItem);

        // Create tape/volume node
        var tocFileName = _tapeService.IsTOCFromFile
            ? System.IO.Path.GetFileName(_tapeService.TOCFilePath ?? "file")
            : null;
        var tapeItem = TapeTreeItemViewModel.CreateTapeItem(toc, driveItem, tocFileName,
            isInMemory: _tapeService.IsInMemoryDrive);
        driveItem.Children.Add(tapeItem);

        // Add backup sets (from latest to oldest for consistency with alt index display)
        int totalSets = toc.Count;
        for (int i = totalSets; i >= 1; i--)
        {
            var setItem = TapeTreeItemViewModel.CreateBackupSetItem(toc, i, tapeItem);
            tapeItem.Children.Add(setItem);
        }

        WindowTitle = $"TapeWin - {toc.Description ?? $"Volume #{toc.Volume}"}";

        // Status message: TOC-from-file warning takes precedence over in-memory info
        if (_tapeService.IsTOCFromFile)
            StatusMessage = $"\u26a0 TOC: {System.IO.Path.GetFileName(_tapeService.TOCFilePath)} | Loaded {totalSets} backup set(s)";
        else if (_tapeService.IsInMemoryDrive)
            StatusMessage = $"\u2139 In-memory \u2013 cannot be saved | Loaded {totalSets} backup set(s)";
        else
            StatusMessage = $"Loaded {totalSets} backup set(s)";
    }

    private void SelectMostRecentSet()
    {
        // Select the tape/media node to show the backup sets table overview
        if (TreeItems.Count > 0 && TreeItems[0].Children.Count > 0)
        {
            var tapeNode = TreeItems[0].Children[0];
            tapeNode.IsSelected = true;
            OnTreeItemSelected(tapeNode);
        }
        else if (TreeItems.Count > 0)
        {
            // Only drive node available
            TreeItems[0].IsSelected = true;
            OnTreeItemSelected(TreeItems[0]);
        }
    }

    /// <summary>Whether the rename command should be enabled.</summary>
    private bool CanRenameSelected =>
        !IsBusy && _tapeService.TOC != null &&
        (_selectedTreeItem?.ItemType is TreeItemType.Tape or TreeItemType.BackupSet);

    /// <summary>Whether the rename media command should be enabled.</summary>
    private bool CanRenameMedia =>
        !IsBusy && _tapeService.TOC != null;

    /// <summary>Dispatches to <see cref="RenameMediaAsync"/> or <see cref="RenameBackupSetAsync"/>
    /// based on the currently selected tree item.</summary>
    private async Task RenameSelectedAsync()
    {
        if (_selectedTreeItem?.ItemType == TreeItemType.Tape)
            await RenameMediaAsync();
        else if (_selectedTreeItem?.ItemType == TreeItemType.BackupSet && _selectedTreeItem.SetIndex.HasValue)
            await RenameBackupSetAsync(_selectedTreeItem.SetIndex.Value);
    }

    /// <summary>
    /// Prompts for a new media name (via the service host) and writes the updated TOC back to tape.
    /// </summary>
    private async Task RenameMediaAsync()
    {
        IsBusy = true;
        BusyMessage = "Renaming media...";
        try
        {
            if (await _tapeService.RenameMediaAsync())
            {
                var newName = _tapeService.TOC?.Description ?? string.Empty;
                // Iterate thru TreeItems to find the tape node and update its display name
                foreach (var driveNode in TreeItems)
                {
                    foreach (var tapeNode in driveNode.Children)
                    {
                        if (tapeNode.ItemType == TreeItemType.Tape)
                        {
                            tapeNode.DisplayName = newName;
                            break;
                        }
                    }
                }
                WindowTitle = $"TapeWin - {newName}";
                if (_selectedTreeItem?.ItemType == TreeItemType.Tape)
                    LoadMediaInfo(); // refresh properties pane if media node is selected
            }
            else if (_tapeService.LastError is not null)
            {
                MessageBox.Show($"Failed to rename media.\n\n{_tapeService.LastError}",
                    "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    /// <summary>
    /// Prompts for a new backup-set description (via the service host) and writes the updated TOC back to tape.
    /// </summary>
    private async Task RenameBackupSetAsync(int setIndex)
    {
        IsBusy = true;
        BusyMessage = "Renaming backup set...";
        try
        {
            if (await _tapeService.RenameBackupSetAsync(setIndex))
            {
                var newName = _tapeService.TOC?[setIndex].Description ?? string.Empty;
                // Update tree node if it is currently selected: "Set #N | -M: description"
                if (_selectedTreeItem?.ItemType == TreeItemType.BackupSet &&
                    _selectedTreeItem.SetIndex == setIndex)
                {
                    var colonIdx = _selectedTreeItem.DisplayName.IndexOf(':');
                    _selectedTreeItem.DisplayName = colonIdx >= 0
                        ? $"{_selectedTreeItem.DisplayName[..colonIdx]}: {newName}"
                        : newName;
                    LoadBackupSetInfo(setIndex); // refresh properties pane
                }
                else
                {
                    // Iterate thru all tree nodes to find and rename the node for this backup set
                    foreach (var driveNode in TreeItems)
                    {
                        foreach (var tapeNode in driveNode.Children)
                        {
                            if (tapeNode.ItemType == TreeItemType.Tape)
                            {
                                foreach (var setNode in tapeNode.Children)
                                {
                                    if (setNode.ItemType == TreeItemType.BackupSet &&
                                        setNode.SetIndex == setIndex)
                                    {
                                        var colonIdx = setNode.DisplayName.IndexOf(':');
                                        setNode.DisplayName = colonIdx >= 0
                                            ? $"{setNode.DisplayName[..colonIdx]}: {newName}"
                                            : newName;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (_selectedTreeItem?.ItemType == TreeItemType.Tape)
                        LoadMediaInfo(); // refresh backup set table pane if media node is selected
                }
            }
            else if (_tapeService.LastError is not null)
            {
                MessageBox.Show($"Failed to rename backup set.\n\n{_tapeService.LastError}",
                    "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private void LoadDriveInfo()
    {
        PropertyList.Clear();
        FileList = [];
        BackupSetList.Clear();
        ContentType = ContentPaneType.DriveInfo;
        PropertiesHeader = "Drive Properties";
        TableHeader = ""; // Not visible for drive
        UsageBar.Clear();

        PropertyList.Add(new PropertyItem("Device Name", _tapeService.DeviceName));
        PropertyList.Add(new PropertyItem("Device Vendor", _tapeService.DeviceVendor));
        PropertyList.Add(new PropertyItem("Device Product", _tapeService.DeviceProduct));
        PropertyList.Add(new PropertyItem("Drive Open", _tapeService.IsDriveOpen ? "Yes" : "No"));

        if (_tapeService.IsDriveOpen)
        {
            PropertyList.Add(new PropertyItem("Supports Multiple Partitions", 
                _tapeService.SupportsInitiatorPartition ? "Yes" : "No"));
            PropertyList.Add(new PropertyItem("Supports Setmarks", 
                _tapeService.SupportsSetmarks ? "Yes" : "No"));
            PropertyList.Add(new PropertyItem("Supports Sequential Filemarks", 
                _tapeService.SupportsSeqFilemarks ? "Yes" : "No"));
            PropertyList.Add(new PropertyItem("Block Size (Min)", 
                Helpers.BytesToString(_tapeService.MinimumBlockSize)));
            PropertyList.Add(new PropertyItem("Block Size (Default)", 
                Helpers.BytesToString(_tapeService.DefaultBlockSize)));
            PropertyList.Add(new PropertyItem("Block Size (Max)", 
                Helpers.BytesToString(_tapeService.MaximumBlockSize)));

            PropertyList.Add(new PropertyItem("Media Loaded", 
                _tapeService.IsMediaLoaded ? "Yes" : "No"));

            if (_tapeService.IsMediaLoaded)
            {
                PropertyList.Add(new PropertyItem("Partition Count", 
                    _tapeService.PartitionCount.ToString()));
                PropertyList.Add(new PropertyItem("Capacity", 
                    Helpers.BytesToStringLong(_tapeService.Capacity)));
                PropertyList.Add(new PropertyItem("Remaining (est.)", 
                    Helpers.BytesToStringLong(_tapeService.GetRemainingCapacityFromDrive())));
            }
        }

        StatusMessage = "Drive information displayed";
    }

    private void LoadMediaInfo()
    {
        PropertyList.Clear();
        BackupSetList.Clear();
        FileList = [];
        ContentType = ContentPaneType.MediaInfo;
        PropertiesHeader = "Media Properties";

        var toc = _tapeService.TOC;
        if (toc == null)
        {
            PropertyList.Add(new PropertyItem("Status", "No TOC available"));
            TableHeader = "Backup Sets";
            StatusMessage = "No media information available";
            return;
        }

        // Populate media properties
        PropertyList.Add(new PropertyItem("Description", toc.Description ?? "(unnamed)"));
        PropertyList.Add(new PropertyItem("Created On", toc.CreationTime.ToString("G")));
        PropertyList.Add(new PropertyItem("Last Saved", toc.LastSaveTime.ToString("G")));
        PropertyList.Add(new PropertyItem("Backup Sets", toc.Count.ToString()));
        PropertyList.Add(new PropertyItem("Capacity", 
            Helpers.BytesToStringLong(_tapeService.Capacity)));

        var used = _tapeService.Used;
        var remaining = _tapeService.Remaining;

        PropertyList.Add(new PropertyItem("Used", Helpers.BytesToStringLong(used)));
        PropertyList.Add(new PropertyItem("Remaining", Helpers.BytesToStringLong(remaining)));
        PropertyList.Add(new PropertyItem("TOC Placement", 
            _tapeService.IsTOCFromFile
                ? $"File: {_tapeService.TOCFilePath}"
                : _tapeService.HasInitiatorPartition ? "Partition" : "Set",
            isHighlighted: _tapeService.IsTOCFromFile));
        PropertyList.Add(new PropertyItem("Volume", $"#{toc.Volume}"));
        PropertyList.Add(new PropertyItem("Continued on Next Volume", 
            toc.ContinuedOnNextVolume ? "Yes" : "No"));

        // Populate backup sets table (newest-first, with checked-state sync)
        _tocView ??= new TOCView(toc);
        TableHeader = $"Backup Sets ({toc.Count})";
        foreach (var item in _tocView.BuildBackupSetItemList())
            BackupSetList.Add(item);

        // Refresh the header "select all" checkbox — items may carry partial
        //  (null) checked state from per-file selections in a previous visit.
        OnPropertyChanged(nameof(AreAllBackupSetsChecked));

        var mediaName = toc.Description ?? "Volume #" + toc.Volume;
        StatusMessage = _tapeService.IsTOCFromFile
            ? $"\u26a0 TOC: {System.IO.Path.GetFileName(_tapeService.TOCFilePath)} | Media: {mediaName} - {toc.Count} backup set(s)"
            : $"Media: {mediaName} - {toc.Count} backup set(s)";

        // Build the media usage bar from the current-volume sets
        UsageBar.Rebuild();
    }

    private void LoadBackupSetInfo(int setIndex)
    {
        var toc = _tapeService.TOC;
        if (toc == null || _tocView == null)
            return;

        PropertyList.Clear();
        BackupSetList.Clear();
        ClearFileFilter();
        FileList = [];
        ContentType = ContentPaneType.BackupSetInfo;
        TableHeader = "Files"; // Reset early to avoid showing stale backup-set header
        UsageBar.Clear();

        try
        {
            toc.CurrentSetIndex = setIndex;
            var setTOC = toc.CurrentSetTOC;
            int totalSets = toc.Count;
            int altIndex = toc.SetIndexToAlt(setIndex);

            PropertiesHeader = $"Backup Set #{setIndex} | {altIndex} Properties";

            // Populate backup set properties
            PropertyList.Add(new PropertyItem("Description", setTOC.Description ?? "(unnamed)"));
            PropertyList.Add(new PropertyItem("Set Index", $"#{setIndex} | {altIndex}"));
            PropertyList.Add(new PropertyItem("Files", setTOC.Count.ToString("N0")));
            PropertyList.Add(new PropertyItem("Total File Size on Tape",
                Helpers.BytesToStringLong(setTOC.ComputeTotalFileSizeOnTape(_tapeService.DefaultBlockSize))));
            PropertyList.Add(new PropertyItem("Created On", setTOC.CreationTime.ToString("G")));
            PropertyList.Add(new PropertyItem("Last Saved", setTOC.LastSaveTime.ToString("G")));
            PropertyList.Add(new PropertyItem("Block Size", Helpers.BytesToStringLong(setTOC.BlockSize)));
            PropertyList.Add(new PropertyItem("Filemarks", setTOC.FmksMode ? "ON" : "OFF"));
            PropertyList.Add(new PropertyItem("Hash Algorithm", setTOC.HashAlgorithm.ToString()));
            PropertyList.Add(new PropertyItem("Incremental", setTOC.Incremental ? "Yes" : "No"));
            PropertyList.Add(new PropertyItem("Volume", $"#{setTOC.Volume}"));
            PropertyList.Add(new PropertyItem("Continued from Previous Volume", 
                toc.IsCurrentSetContFromPrevVolume ? "Yes, directly" :
                toc.IsCurrentSetContFromPrevVolumeInc ? "Yes, incrementally" : "No"));
            PropertyList.Add(new PropertyItem("Continued on Next Volume", 
                toc.IsCurrentSetContOnNextVolume ? "Yes" : "No"));

            // Get or create the BackupSetView (handles incremental file resolution,
            //  caching, and checked-state migration)
            var setView = _tocView.GetOrCreate(setIndex, ShowIncrementalSets);
            _currentSetView = setView;

            // Build the display list (creates FileListItem proxies as needed)
            FileList = setView.BuildFileItemList(ShowFullPathname);

            NotifyFilterPropertiesChanged();

            StatusMessage = $"Set #{setIndex} | #{altIndex}: {FileTotalCount} file(s)";

            // Queue a pending filter restore if the user previously filtered this set
            PendingFilterRestore = setView.SavedFilterState;
        }
        catch (Exception ex)
        {
            LogErr($"Error loading backup set info: {ex.Message}");
            StatusMessage = "Error loading backup set information";
        }
    }

    private void NavigateToSelectedBackupSet(object? parameter)
    {
        if (SelectedBackupSet != null)
        {
            OnBackupSetSelectedInTable(SelectedBackupSet);
        }
    }

    /// <summary>
    /// Selects the <see cref="BackupSetListItem"/> with the given set index in
    ///  <see cref="BackupSetList"/>. Wired to <see cref="UsageBar"/>'s click callback.
    /// </summary>
    private void SelectBackupSetByIndex(int setIndex)
    {
        var item = BackupSetList.FirstOrDefault(b => b.SetIndex == setIndex);
        if (item != null)
            SelectedBackupSet = item;
    }

    private void OnBackupSetSelectedInTable(BackupSetListItem backupSetItem)
    {
        // Find the corresponding tree node and select it
        if (TreeItems.Count > 0 && TreeItems[0].Children.Count > 0)
        {
            var tapeNode = TreeItems[0].Children[0];
            var setIndex = backupSetItem.SetIndex;
            int totalSets = tapeNode.Children.Count;
            // Convert setIndex to tree position (reversed order)
            int treeIndex = totalSets - setIndex;
            if (treeIndex >= 0 && treeIndex < totalSets)
            {
                var setNode = tapeNode.Children[treeIndex];
                setNode.IsSelected = true;
                // Don't call OnTreeItemSelected here to avoid recursion;
                //  the TreeView selection changed event will handle it            }
            }
        }
    }

    private void RefreshCurrentView()
    {
        if (_selectedTreeItem == null)
            return;

        switch (_selectedTreeItem.ItemType)
        {
            case TreeItemType.Drive:
                LoadDriveInfo();
                break;
            case TreeItemType.Tape:
                LoadMediaInfo();
                break;
            case TreeItemType.BackupSet:
                if (_selectedTreeItem.SetIndex.HasValue)
                {
                    LoadBackupSetInfo(_selectedTreeItem.SetIndex.Value);
                }
                break;
        }
    }

    #endregion

    // Logging region is in MainViewModel.Log.cs

    #region Private Methods - Event Handlers

    /// <summary>
    /// Syncs the IO speed UI state with the current drive.
    /// Call after opening/closing a drive to update visibility and selection.
    /// </summary>
    private void NotifyIoSpeedChanged()
    {
        // Sync selection from the backend's current rate
        _selectedIoSpeed = IoSpeedOption.FromBytesPerSecond(_tapeService.VirtualIoRateBytesPerSecond);
        OnPropertyChanged(nameof(SelectedIoSpeed));
        OnPropertyChanged(nameof(IsIoSpeedVisible));
        OnPropertyChanged(nameof(IsIoSpeedEnabled));
    }

    // OnLogMessageReceived is in MainViewModel.Log.cs

    private void OnStatusChanged(object? sender, string status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = status;

            // Mirror to BusyMessage so the progress overlay panel stays current
            if (IsBusy)
                BusyMessage = status;
        });
    }

    /// <summary>
    /// Fires batched <c>PropertyChanged</c> notifications for the property cluster
    ///  affected by <paramref name="change"/>. Called by <c>WpfServiceHost</c> on
    ///  the background worker thread; marshals to the UI thread internally.
    /// </summary>
    internal void OnServiceStateChanged(ServiceStateChange change)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (change.HasFlag(ServiceStateChange.DriveOpened) ||
                change.HasFlag(ServiceStateChange.DriveClosed))
            {
                OnPropertyChanged(nameof(IsIoSpeedVisible));
                OnPropertyChanged(nameof(IsIoSpeedEnabled));
                CommandManager.InvalidateRequerySuggested();
            }

            if (change.HasFlag(ServiceStateChange.MediaLoaded) ||
                change.HasFlag(ServiceStateChange.MediaEjected))
            {
                CommandManager.InvalidateRequerySuggested();
            }

            if (change.HasFlag(ServiceStateChange.TocChanged))
            {
                UsageBar.Rebuild();
                CommandManager.InvalidateRequerySuggested();
            }
        });
    }

    #endregion

    #region Private Methods - Virtual Drive Operations

    private void ShowOpenVirtualDriveWindow(object? parameter)
    {
        ShowOpenVirtualDriveWindowInternal(prePopulatedPath: null, preSelectCreateNew: null);
    }

    private void ShowOpenVirtualDriveWindowInternal(
        string? prePopulatedPath,
        bool? preSelectCreateNew)
    {
        var prePopulate = !string.IsNullOrWhiteSpace(prePopulatedPath)
            ? new VirtualMediaDescriptor(prePopulatedPath, 0, null, 0)
            : null;

        var viewModel = new OpenVirtualDriveViewModel(
            async (request) =>
            {
                // Close dialog first
                Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();

                // Open virtual drive with mode-specific handling
                await OpenVirtualDriveAsync(request);
            },
            () =>
            {
                Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
            },
            prePopulate: prePopulate,
            preferCreateNew: preSelectCreateNew);

        var window = new OpenVirtualDriveWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private async Task OpenVirtualDriveAsync(VirtualDriveOpenRequest request)
    {
        // A — Open virtual drive. Custom failure UI: "open existing" re-shows the
        //  dialog so the user can switch to "create new"; "create new" just errors.
        var mediaMode = request.IsCreateNew ? FileMode.Create : FileMode.Open;
        if (!await OpenVirtualDriveCoreAsync(request, mediaMode))
        {
            if (!request.IsCreateNew)
            {
                MessageBox.Show(
                    $"Failed to open existing virtual media.\n\n{_tapeService.LastError}\n\n" +
                    "Please check the file path or select 'Create new virtual media'.",
                    "Open Failed", MessageBoxButton.OK, MessageBoxImage.Warning);

                // Re-show dialog with pre-populated path, suggesting "Create new"
                ShowOpenVirtualDriveWindowInternal(
                    prePopulatedPath: request.Media.ContentPath,
                    preSelectCreateNew: null); // Let user decide
            }
            else
            {
                MessageBox.Show(
                    $"Failed to create virtual drive.\n\n{_tapeService.LastError}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateTreeForDriveOnly(0);
            }
            return;
        }

        // B — Load virtual media
        if (!await LoadMediaWithUIAsync("Loading virtual media...", "virtual media"))
        {
            UpdateTreeForDriveOnly(0);
            return;
        }

        // Apply IO speed selected in the dialog (if any) before any TOC operations
        //  so that the TOC operations already reflect the selected speed.
        if (request.IoSpeed != null)
            SelectedIoSpeed = request.IoSpeed;
        NotifyIoSpeedChanged();

        // For "Create new", create and save the initial TOC (warning only on failure)
        if (request.IsCreateNew)
        {
            var tocCreated = await RunBusyAsync("Creating initial TOC...",
                () => _tapeService.CreateInitialTOCAsync(request.MediaName));
            if (!tocCreated)
                LogWarn("Could not create initial TOC");
        }

        // C — Read TOC. For freshly created media there's no prior TOC to import,
        //  so suppress the file-import recovery prompt in that case.
        if (!await ReadTOCWithUIAsync(offerFileImportOnFailure: !request.IsCreateNew))
        {
            UpdateTreeForDriveOnly(0);
            return;
        }

        UpdateTreeFromTOC(0);
        SelectMostRecentSet();

        var modeText = request.Media.InMemory ? "Created in-memory"
            : request.IsCreateNew ? "Created new" : "Opened existing";
        LogInfo($"{modeText} virtual media: {request.Media.ContentPath}");

        // Don't add in-memory drives to MRU (they can't be reopened)
        if (!request.Media.InMemory)
            AddToVirtualDriveMru(request.Media.ContentPath);
    }

    private void AddToVirtualDriveMru(string contentPath)
    {
        _virtualDriveMru.Add(contentPath);
        RefreshRecentVirtualDriveMenu();
    }

    private void RefreshRecentVirtualDriveMenu()
    {
        RecentVirtualDriveMenuItems.Clear();

        int index = 1;
        foreach (var path in _virtualDriveMru.Items)
        {
            var display = MruFileList.AbbreviatePath(path);
            RecentVirtualDriveMenuItems.Add(new DriveMenuItem(
                Header: $"_{index} {display}",
                DriveNumber: index,
                Command: OpenRecentVirtualDriveCommand));
            index++;
        }

        OnPropertyChanged(nameof(HasRecentVirtualDrives));
    }

    private async Task OpenRecentVirtualDriveAsync(object? parameter)
    {
        int mruIndex = (parameter as int? ?? 1) - 1;
        var items = _virtualDriveMru.Items;

        if (mruIndex < 0 || mruIndex >= items.Count)
            return;

        var contentPath = items[mruIndex];

        if (!File.Exists(contentPath))
        {
            var result = MessageBox.Show(
                $"The file no longer exists:\n\n{contentPath}\n\nRemove from recent list?",
                "File Not Found", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _virtualDriveMru.Remove(contentPath);
                RefreshRecentVirtualDriveMenu();
            }
            return;
        }

        // Open the virtual drive dialog pre-populated with this path
        ShowOpenVirtualDriveWindowInternal(
            prePopulatedPath: contentPath,
            preSelectCreateNew: false);
    }

    #endregion

    #region Private Methods - Format Operations

    private void ShowFormatMediaWindow(object? parameter)
    {
        var viewModel = new FormatMediaViewModel(
            _tapeService,
            OnStartFormat,
            () => Application.Current.Windows.OfType<FormatMediaWindow>().FirstOrDefault()?.Close());

        var window = new FormatMediaWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private async void OnStartFormat(FormatMediaViewModel formatViewModel)
    {
        Application.Current.Windows.OfType<FormatMediaWindow>().FirstOrDefault()?.Close();

        var result = MessageBox.Show(
            "Are you sure you want to format the media?\n\nAll data will be permanently erased. This cannot be undone.",
            "Confirm Format",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            return;

        if (_tapeService.IsVirtualDrive)
            await FormatVirtualDriveAsync(formatViewModel);
        else
            await ExecuteFormatAsync(formatViewModel);
    }

    private async Task ExecuteFormatAsync(FormatMediaViewModel formatViewModel)
    {
        IsBusy = true;
        BusyMessage = "Formatting media...";

        try
        {
            long initiatorPartitionSize = formatViewModel.CreateInitiatorPartition
                ? TapeNavigator.DefaultTOCCapacity : -1;

            var success = await _tapeService.FormatMediaAsync(initiatorPartitionSize, formatViewModel.MediaName);

            if (!success)
            {
                MessageBox.Show($"Failed to format media.\n\n{_tapeService.LastError}",
                    "Format Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UpdateTreeFromTOC(_tapeService.DriveNumber);
            SelectMostRecentSet();

            MessageBox.Show("Media formatted successfully!", "Format Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogErr($"Format failed: {ex.Message}");
            MessageBox.Show($"Format failed.\n\n{ex.Message}", "Format Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    // ─────────────────────────────────────────────────
    //  Delete Backup Sets
    // ─────────────────────────────────────────────────

    private void ShowDeleteBackupSetsWindow(object? parameter)
    {
        var viewModel = new DeleteBackupSetsViewModel(
            _tapeService,
            OnStartDeleteBackupSets,
            () => Application.Current.Windows.OfType<DeleteBackupSetsWindow>().FirstOrDefault()?.Close());

        var window = new DeleteBackupSetsWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private void OnStartDeleteBackupSets(DeleteBackupSetsViewModel deleteViewModel)
    {
        Application.Current.Windows.OfType<DeleteBackupSetsWindow>().FirstOrDefault()?.Close();
        _ = ExecuteDeleteBackupSetsAsync(deleteViewModel.DeleteFromSetIndex);
    }

    private async Task ExecuteDeleteBackupSetsAsync(int deleteFromSetIndex)
    {
        IsBusy = true;
        BusyMessage = "Deleting backup sets...";

        try
        {
            var success = await _tapeService.DeleteBackupSetsAsync(deleteFromSetIndex);

            if (!success)
            {
                MessageBox.Show($"Failed to delete backup sets.\n\n{_tapeService.LastError}",
                    "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UpdateTreeFromTOC(_tapeService.DriveNumber);
            SelectMostRecentSet();
        }
        catch (Exception ex)
        {
            LogErr($"Delete backup sets failed: {ex.Message}");
            MessageBox.Show($"Delete failed.\n\n{ex.Message}", "Delete Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task FormatVirtualDriveAsync(FormatMediaViewModel formatViewModel)
    {
        var currentCaps = new VirtualTapeDriveCapabilities
        {
            MinBlockSize = _tapeService.MinimumBlockSize,
            MaxBlockSize = _tapeService.MaximumBlockSize,
            DefaultBlockSize = _tapeService.DefaultBlockSize,
            SupportsSetmarks = _tapeService.SupportsSetmarks,
            SupportsSeqFilemarks = _tapeService.SupportsSeqFilemarks,
            SupportsInitiatorPartition = _tapeService.SupportsInitiatorPartition,
            SupportsCompression = false,
        };

        var lastVmd = _tapeService.LastVMD;
        VirtualMediaDescriptor? newVmd = null;

        var vm = new OpenVirtualDriveViewModel(
            request =>
            {
                Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
                newVmd = request.Media;
            },
            () =>
            {
                Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
            },
            prePopulate: lastVmd,
            mediaMode: FileMode.Create,
            currentCapabilities: currentCaps,
            currentIoSpeed: _selectedIoSpeed);

        var window = new OpenVirtualDriveWindow(vm)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();

        if (newVmd == null)
            return;

        IsBusy = true;
        BusyMessage = "Creating virtual media...";

        try
        {
            if (!_tapeService.InsertVirtualMedia(newVmd, FileMode.Create))
            {
                MessageBox.Show($"Failed to create virtual media files.\n\n{_tapeService.LastError}",
                    "Format Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!await LoadMediaWithUIAsync("Loading media...", "virtual media"))
                return;

            BusyMessage = "Formatting media...";
            long initiatorPartitionSize = formatViewModel.CreateInitiatorPartition
                ? TapeNavigator.DefaultTOCCapacity : -1;

            if (!await _tapeService.FormatMediaAsync(initiatorPartitionSize, formatViewModel.MediaName))
            {
                MessageBox.Show($"Failed to format media.\n\n{_tapeService.LastError}",
                    "Format Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UpdateTreeFromTOC(_tapeService.DriveNumber);
            SelectMostRecentSet();

            AddToVirtualDriveMru(newVmd.ContentPath);

            MessageBox.Show("Virtual media formatted successfully!", "Format Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogErr($"Format failed: {ex.Message}");
            MessageBox.Show($"Format failed.\n\n{ex.Message}", "Format Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    #endregion
}

