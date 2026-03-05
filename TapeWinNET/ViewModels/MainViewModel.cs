using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeLibNET.Virtual;
using TapeWinNET.Converters;

using TapeWinNET.Models;
using TapeWinNET.Services;


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
    private readonly TapeService _tapeService;
    private string _windowTitle = "TapeWin - Tape Backup Manager";
    private string _statusMessage = "Ready";
    private string _busyMessage = string.Empty;
    private string _propertiesHeader = "Properties";
    private string _tableHeader = "Content";
    private bool _isBusy;
    private bool _isBackupInProgress;
    private bool _showFullPathname = false;
    private bool _showIncrementalSets = true;
    private FileListItem? _selectedFile;
    private BackupSetListItem? _selectedBackupSet;
    private TapeTreeItemViewModel? _selectedTreeItem;
    private ContentPaneType _contentType = ContentPaneType.DriveInfo;

    // Backup fields are in MainViewModel.Backup.cs

    public MainViewModel()
    {
        _tapeService = new TapeService();
        _tapeService.LogMessageReceived += OnLogMessageReceived;
        _tapeService.StatusChanged += OnStatusChanged;

        // Initialize commands
        OpenDriveCommand = new AsyncRelayCommand(OpenDriveAsync, _ => !IsBusy);
        OpenVirtualDriveCommand = new RelayCommand(ShowOpenVirtualDriveWindow, _ => !IsBusy);
        SetIoSpeedCommand = new RelayCommand(SetIoSpeed, _ => IsIoSpeedEnabled);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && _tapeService.IsDriveOpen);
        RereadTOCCommand = new AsyncRelayCommand(RereadTOCAsync, () => !IsBusy && _tapeService.IsDriveOpen);
        EjectCommand = new AsyncRelayCommand(EjectAsync, () => !IsBusy && _tapeService.IsMediaLoaded);
        FormatMediaCommand = new RelayCommand(ShowFormatMediaWindow, _ => !IsBusy && _tapeService.IsMediaLoaded);
        ExportTOCCommand = new AsyncRelayCommand(ExportTOCAsync, () => !IsBusy && _tapeService.TOC != null);
        ImportTOCCommand = new AsyncRelayCommand(ImportTOCAsync, () => !IsBusy && _tapeService.IsDriveOpen);
        NavigateToBackupSetCommand = new RelayCommand(NavigateToSelectedBackupSet, _ => SelectedBackupSet != null);
        RenameSelectedCommand = new AsyncRelayCommand(RenameSelectedAsync, () => CanRenameSelected);
        RenameMediaCommand = new AsyncRelayCommand(RenameMediaAsync, () => CanRenameMedia);
        ExitCommand = new RelayCommand(Exit);
        AboutCommand = new RelayCommand(ShowAbout);

        // Initialize backup commands (from MainViewModel.Backup.cs)
        InitializeBackupCommands();

        // Initialize restore commands (from MainViewModel.Restore.cs)
        InitializeRestoreCommands();

        // Initialize drive menu items
        InitializeDriveMenu();
    }

    /// <summary>
    /// Initializes the drive menu items for drives 0-9.
    /// TODO: Later can be enhanced to detect installed drives.
    /// </summary>
    private void InitializeDriveMenu()
    {
        for (int i = 0; i <= 9; i++)
        {
            DriveMenuItems.Add(new DriveMenuItem(
                Header: $"Drive _{i}",  // Underscore creates keyboard accelerator
                DriveNumber: i,
                Command: OpenDriveCommand
            ));
        }
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
    /// True when busy with non-backup/restore operations (shows full-window overlay).
    /// </summary>
    public bool IsGeneralBusy => IsBusy && !IsBackupInProgress && !IsRestoreInProgress;

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
        set => SetProperty(ref _selectedBackupSet, value);
        // Navigation now triggered by double-click or Enter, not selection change
    }

    public ContentPaneType ContentType
    {
        get => _contentType;
        set
        {
            if (SetProperty(ref _contentType, value))
            {
                OnPropertyChanged(nameof(IsTableVisible));
                OnPropertyChanged(nameof(IsFileTableVisible));
                OnPropertyChanged(nameof(IsBackupSetTableVisible));
            }
        }
    }

    /// <summary>Whether the table subpane is visible (Media or BackupSet selected)</summary>
    public bool IsTableVisible => ContentType != ContentPaneType.DriveInfo;

    /// <summary>Whether the files table is visible (BackupSet selected)</summary>
    public bool IsFileTableVisible => ContentType == ContentPaneType.BackupSetInfo;

    /// <summary>Whether the backup sets table is visible (Media selected)</summary>
    public bool IsBackupSetTableVisible => ContentType == ContentPaneType.MediaInfo;

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
    public ObservableCollection<LogEntry> LogMessages { get; } = [];

    #endregion

    #region Commands

    public ICommand OpenDriveCommand { get; }
    public ICommand OpenVirtualDriveCommand { get; }
    public ICommand SetIoSpeedCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RereadTOCCommand { get; }
    public ICommand EjectCommand { get; }
    public ICommand FormatMediaCommand { get; }
    public ICommand ExportTOCCommand { get; }
    public ICommand ImportTOCCommand { get; }
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
    }

    public void Cleanup()
    {
        _tapeService.Dispose();
    }

    #endregion

    #region Private Methods - Drive/Media Operations

    private async Task<bool> LoadMediaInternalAsync(int driveNumber)
    {
        bool isBusy = IsBusy;
        string busyMessage = BusyMessage;

        try
        {
            IsBusy = true;
            BusyMessage = "Loading media...";
            var success = await _tapeService.LoadMediaAsync();
            if (!success)
            {
                MessageBox.Show($"Failed to load media.\n\n{_tapeService.LastError}",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            BusyMessage = "Reading TOC...";
            success = await _tapeService.RestoreTOCAsync();
            if (!success)
            {
                var result = MessageBox.Show(
                    $"Failed to read TOC from media.\n\n{_tapeService.LastError}\n\n" +
                    "If you have a saved TOC file (.tapetoc), you can load it to access the media content.\n\n" +
                    "Would you like to load a TOC from file?",
                    "TOC Read Failed", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    success = await ImportTOCFromFileAsync();
                    if (!success)
                        return false;
                }
                else
                {
                    return false;
                }
            }
        }
        finally
        {
            IsBusy = isBusy;
            BusyMessage = busyMessage;
        }

        return true;
    }

    private async Task OpenDriveAsync(object? parameter)
    {
        int driveNumber = parameter as int? ?? 0;

        IsBusy = true;
        BusyMessage = $"Opening drive {driveNumber}...";

        try
        {
            var success = await _tapeService.OpenDriveAsync(driveNumber);
            if (!success)
            {
                MessageBox.Show($"Failed to open drive {driveNumber}.\n\n{_tapeService.LastError}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateTreeForDriveOnly(driveNumber);
                return;
            }

            success = await LoadMediaInternalAsync(driveNumber);
            if (!success)
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
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
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

        IsBusy = true;
        BusyMessage = "Refreshing...";

        try
        {
            var driveNumber = _tapeService.DriveNumber;

            if (_tapeService.TOC == null)
            {
                var success = await LoadMediaInternalAsync(driveNumber);

                if (!success)
                {
                    UpdateTreeForDriveOnly(driveNumber);
                    return;
                }
            }

            UpdateTreeFromTOC(driveNumber);
            // Select the most recent backup set
            SelectMostRecentSet();
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
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
            (toc.Description ?? "tape").Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).Trim();

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
        var driveItem = TapeTreeItemViewModel.CreateDriveItem(driveNumber, _tapeService.DeviceName);
        TreeItems.Add(driveItem);
        WindowTitle = $"TapeWin - Drive {driveNumber}";
        
        // Show drive info when only drive is available
        LoadDriveInfo();
    }

    private void UpdateTreeFromTOC(int driveNumber)
    {
        TreeItems.Clear();

        var toc = _tapeService.TOC;
        if (toc == null)
        {
            UpdateTreeForDriveOnly(driveNumber);
            return;
        }

        // Create drive node
        var driveItem = TapeTreeItemViewModel.CreateDriveItem(driveNumber, _tapeService.DeviceName);
        TreeItems.Add(driveItem);

        // Create tape/volume node
        var tocFileName = _tapeService.IsTOCFromFile
            ? System.IO.Path.GetFileName(_tapeService.TOCFilePath ?? "file")
            : null;
        var tapeItem = TapeTreeItemViewModel.CreateTapeItem(toc, driveItem, tocFileName);
        driveItem.Children.Add(tapeItem);

        // Add backup sets (from latest to oldest for consistency with alt index display)
        int totalSets = toc.Count;
        int currentVolume = toc.Volume;
        for (int i = totalSets; i >= 1; i--)
        {
            var setTOC = toc[i];
            var setItem = TapeTreeItemViewModel.CreateBackupSetItem(setTOC, i, totalSets, tapeItem,
                setTOC.Volume == currentVolume);
            tapeItem.Children.Add(setItem);
        }

        WindowTitle = $"TapeWin - {toc.Description ?? $"Volume #{toc.Volume}"}";
        StatusMessage = _tapeService.IsTOCFromFile
            ? $"\u26a0 TOC: {System.IO.Path.GetFileName(_tapeService.TOCFilePath)} | Loaded {totalSets} backup set(s)"
            : $"Loaded {totalSets} backup set(s)";
    }

    private void SelectMostRecentSet()
    {
        // Find and select the most recent (first) backup set
        if (TreeItems.Count > 0 && TreeItems[0].Children.Count > 0)
        {
            var tapeNode = TreeItems[0].Children[0];
            if (tapeNode.Children.Count > 0)
            {
                var firstSet = tapeNode.Children[0]; // Changed from [^1] to [0]
                firstSet.IsSelected = true;
                OnTreeItemSelected(firstSet);
            }
            else
            {
                // No backup sets, select the tape node to show media info
                tapeNode.IsSelected = true;
                OnTreeItemSelected(tapeNode);
            }
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
    /// Shows a rename dialog for the current media and writes the updated TOC back to tape.
    /// </summary>
    private async Task RenameMediaAsync()
    {
        var toc = _tapeService.TOC;
        if (toc == null)
            return;

        var dialog = new RenameDialog(
            "Rename Media",
            "Enter a new description for the media:",
            toc.Description ?? string.Empty)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true || dialog.NewName == toc.Description)
            return;

        IsBusy = true;
        BusyMessage = "Renaming media...";
        try
        {
            if (await _tapeService.RenameMediaAsync(dialog.NewName))
            {
                // Iterate thru TreeItems to find the tape node and update its display name
                foreach (var driveNode in TreeItems)
                {
                    foreach (var tapeNode in driveNode.Children)
                    {
                        if (tapeNode.ItemType == TreeItemType.Tape)
                        {
                            tapeNode.DisplayName = dialog.NewName;
                            break;
                        }
                    }
                }
                WindowTitle = $"TapeWin - {dialog.NewName}";
                if (_selectedTreeItem?.ItemType == TreeItemType.Tape)
                    LoadMediaInfo(); // refresh properties pane if media node is selected
            }
            else
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
    /// Shows a rename dialog for the specified backup set and writes the updated TOC back to tape.
    /// </summary>
    private async Task RenameBackupSetAsync(int setIndex)
    {
        var toc = _tapeService.TOC;
        if (toc == null)
            return;

        var setTOC = toc[setIndex];

        var dialog = new RenameDialog(
            "Rename Backup Set",
            $"Enter a new description for backup set #{setIndex} | {toc.SetIndexToAlt(setIndex)}:",
            setTOC.Description ?? string.Empty)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true || setTOC.Description == dialog.NewName)
            return;

        IsBusy = true;
        BusyMessage = "Renaming backup set...";
        try
        {
            if (await _tapeService.RenameBackupSetAsync(setIndex, dialog.NewName))
            {
                // Update tree node if it is currently selected: "Set #N | -M: description"
                if (_selectedTreeItem?.ItemType == TreeItemType.BackupSet &&
                    _selectedTreeItem.SetIndex == setIndex)
                {
                    var colonIdx = _selectedTreeItem.DisplayName.IndexOf(':');
                    _selectedTreeItem.DisplayName = colonIdx >= 0
                        ? $"{_selectedTreeItem.DisplayName[..colonIdx]}: {dialog.NewName}"
                        : dialog.NewName;
                    LoadBackupSetInfo(setIndex); // refreh properties pane
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
                                            ? $"{setNode.DisplayName[..colonIdx]}: {dialog.NewName}"
                                            : dialog.NewName;
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
            else
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

        PropertyList.Add(new PropertyItem("Device Name", _tapeService.DeviceName));
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
                    Helpers.BytesToStringLong(_tapeService.GetRemainingCapacity())));
            }
        }

        StatusMessage = "Drive information displayed";
    }

    private void LoadMediaInfo()
    {
        PropertyList.Clear();
        FileList = [];
        BackupSetList.Clear();
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
        PropertyList.Add(new PropertyItem("Name", toc.Description ?? "(unnamed)"));
        PropertyList.Add(new PropertyItem("Created On", toc.CreationTime.ToString("G")));
        PropertyList.Add(new PropertyItem("Last Saved", toc.LastSaveTime.ToString("G")));
        PropertyList.Add(new PropertyItem("Backup Sets", toc.Count.ToString()));
        PropertyList.Add(new PropertyItem("Capacity", 
            Helpers.BytesToStringLong(_tapeService.Capacity)));

        var used = toc.ComputeTotalFileSizeOnTape(_tapeService.DefaultBlockSize);
        if (!_tapeService.HasInitiatorPartition)
            used += TapeNavigator.TOCCapacity; // if TOC is in set, it consumes content space
        var remaining = _tapeService.Capacity - used; //_tapeService.GetRemainingCapacity();

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

        // Populate backup sets table in the alternative order: from latest (0) down to oldest (toc.MinSetIndex)
        TableHeader = $"Backup Sets ({toc.Count})";
        int currentVolume = toc.Volume;
        for (int alt = 0; alt >= toc.MinSetIndex; alt--)
        {
            int setIndex = toc.SetIndexToAlt(alt); // this also converts from alt to regular index
            var setTOC = toc[setIndex];
            // Not used currently, but could be for an alternative view
            BackupSetList.Add(new BackupSetListItem(setTOC, setIndex, alt, setTOC.Volume == currentVolume));
        }

        var mediaName = toc.Description ?? "Volume #" + toc.Volume;
        StatusMessage = _tapeService.IsTOCFromFile
            ? $"\u26a0 TOC: {System.IO.Path.GetFileName(_tapeService.TOCFilePath)} | Media: {mediaName} - {toc.Count} backup set(s)"
            : $"Media: {mediaName} - {toc.Count} backup set(s)";
    }

    private void LoadBackupSetInfo(int setIndex)
    {
        var toc = _tapeService.TOC;
        if (toc == null)
            return;

        PropertyList.Clear();
        FileList = [];
        BackupSetList.Clear();
        ContentType = ContentPaneType.BackupSetInfo;

        try
        {
            toc.CurrentSetIndex = setIndex;
            var setTOC = toc.CurrentSetTOC;
            int totalSets = toc.Count;
            int altIndex = toc.SetIndexToAlt(setIndex);

            PropertiesHeader = $"Backup Set #{setIndex} | {altIndex} Properties";

            // Populate backup set properties
            PropertyList.Add(new PropertyItem("Name", setTOC.Description ?? "(unnamed)"));
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

            // Populate files table
            // Get files, potentially including incremental sets
            IEnumerable<TapeFileInfo> files;
            if (ShowIncrementalSets && setTOC.Incremental)
            {
                // For incremental sets, include files from dependent sets
                var allFiles = new List<TapeFileInfo>();
                var filesBySets = toc.SelectFiles(incremental: true, filePatterns: null);
                foreach (var setFiles in filesBySets)
                {
                    if (setFiles != null)
                    {
                        allFiles.AddRange(setFiles);
                    }
                }
                files = allFiles;
                TableHeader = $"Files ({allFiles.Count} total, including incremental)";
            }
            else
            {
                files = setTOC;
                TableHeader = $"Files ({setTOC.Count})";
            }

            var newFileList = new List<FileListItem>();
            foreach (var fileInfo in files)
            {
                newFileList.Add(new FileListItem(fileInfo, ShowFullPathname));
            }
            FileList = newFileList;

            StatusMessage = $"Set #{setIndex} | #{altIndex}: {FileList.Count} file(s)";
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

    #region Private Methods — Logging

    private void AddLog(LogEntry entry)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogMessages.Add(entry);
            while (LogMessages.Count > 1000)
                LogMessages.RemoveAt(0);
        });
    }

    private void LogInfo(string msg)    => AddLog(new LogEntry(WarningLevel.Info, msg, false, DateTime.Now));
    private void LogOk(string msg)      => AddLog(new LogEntry(WarningLevel.Completed, msg, false, DateTime.Now));
    private void LogWarn(string msg)    => AddLog(new LogEntry(WarningLevel.Warning, msg, false, DateTime.Now));
    private void LogErr(string msg)     => AddLog(new LogEntry(WarningLevel.Error, msg, false, DateTime.Now));

    #endregion

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

    private void OnLogMessageReceived(object? sender, LogEntry entry)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogMessages.Add(entry);

            // Keep log size manageable
            while (LogMessages.Count > 1000)
            {
                LogMessages.RemoveAt(0);
            }
        });
    }

    private void OnStatusChanged(object? sender, string status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = status;
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
        IsBusy = true;
        BusyMessage = request.IsCreateNew ? "Creating virtual drive..." : "Opening virtual drive...";

        try
        {
            // Pass FileMode based on user's mode selection
            var success = await _tapeService.OpenVirtualDriveAsync(
                request.Capabilities,
                request.Media,
                mediaMode: request.IsCreateNew ? FileMode.Create : FileMode.Open);

            if (!success)
            {
                IsBusy = false;
                BusyMessage = string.Empty;

                if (!request.IsCreateNew)
                {
                    // "Open existing" failed - show error and re-open dialog
                    MessageBox.Show(
                        $"Failed to open existing virtual media.\n\n{_tapeService.LastError}\n\n" +
                        "Please check the file path or select 'Create new virtual media'.",
                        "Open Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    // Re-show dialog with pre-populated path, suggesting "Create new"
                    ShowOpenVirtualDriveWindowInternal(
                        prePopulatedPath: request.Media.ContentPath,
                        preSelectCreateNew: null); // Let user decide
                }
                else
                {
                    // "Create new" failed - just show error
                    MessageBox.Show(
                        $"Failed to create virtual drive.\n\n{_tapeService.LastError}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    UpdateTreeForDriveOnly(0);
                }
                return;
            }

            BusyMessage = "Loading virtual media...";
            success = await _tapeService.LoadMediaAsync();
            if (!success)
            {
                IsBusy = false;
                BusyMessage = string.Empty;

                MessageBox.Show(
                    $"Failed to load virtual media.\n\n{_tapeService.LastError}",
                    "Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                UpdateTreeForDriveOnly(0);
                return;
            }

            // Apply IO speed selected in the dialog (if any) - before any TOC operations
            //  so that the TOC operations already reflect the selected speed
            if (request.IoSpeed != null)
            {
                SelectedIoSpeed = request.IoSpeed;
            }
            // Sync IO speed UI state for the newly opened virtual drive
            NotifyIoSpeedChanged();
            
            // For "Create new", create and save the initial TOC
            if (request.IsCreateNew)
            {
                BusyMessage = "Creating initial TOC...";
                success = await _tapeService.CreateInitialTOCAsync(request.MediaName);
                if (!success)
                {
                    // Warning only - drive is still usable
                    LogWarn("Could not create initial TOC");
                }
            }

            BusyMessage = "Reading TOC...";
            success = await _tapeService.RestoreTOCAsync();
            if (!success)
            {
                if (!request.IsCreateNew)
                {
                    // Offer file-based TOC load for existing virtual media
                    var result = MessageBox.Show(
                        $"Failed to read TOC from virtual media.\n\n{_tapeService.LastError}\n\n" +
                        "If you have a saved TOC file (.tapetoc), you can load it to access the media content.\n\n" +
                        "Would you like to load a TOC from file?",
                        "TOC Read Failed", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    if (result == MessageBoxResult.Yes)
                    {
                        success = await ImportTOCFromFileAsync();
                        if (success)
                        {
                            UpdateTreeFromTOC(0);
                            SelectMostRecentSet();
                            NotifyIoSpeedChanged();
                            return;
                        }
                    }
                }
                UpdateTreeForDriveOnly(0);
                return;
            }

            UpdateTreeFromTOC(0);

            // Select the most recent backup set
            SelectMostRecentSet();

            // Log success with mode info
            var modeText = request.IsCreateNew ? "Created new" : "Opened existing";
            LogInfo($"{modeText} virtual media: {request.Media.ContentPath}");
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
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
                ? TapeNavigator.TOCCapacity : -1;

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

            BusyMessage = "Loading media...";
            if (!await _tapeService.LoadMediaAsync())
            {
                MessageBox.Show($"Failed to load virtual media.\n\n{_tapeService.LastError}",
                    "Format Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            BusyMessage = "Formatting media...";
            long initiatorPartitionSize = formatViewModel.CreateInitiatorPartition
                ? TapeNavigator.TOCCapacity : -1;

            if (!await _tapeService.FormatMediaAsync(initiatorPartitionSize, formatViewModel.MediaName))
            {
                MessageBox.Show($"Failed to format media.\n\n{_tapeService.LastError}",
                    "Format Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UpdateTreeFromTOC(_tapeService.DriveNumber);
            SelectMostRecentSet();

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

