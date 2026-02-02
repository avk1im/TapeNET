using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
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

public class MainViewModel : ViewModelBase
{
    private readonly TapeService _tapeService;
    private string _windowTitle = "TapeWin - Tape Backup Manager";
    private string _statusMessage = "Ready";
    private string _busyMessage = string.Empty;
    private string _propertiesHeader = "Properties";
    private string _tableHeader = "Content";
    private bool _isBusy;
    private bool _showFullPathname = false;
    private bool _showIncrementalSets = true;
    private FileListItem? _selectedFile;
    private BackupSetListItem? _selectedBackupSet;
    private TapeTreeItemViewModel? _selectedTreeItem;
    private ContentPaneType _contentType = ContentPaneType.DriveInfo;

    public MainViewModel()
    {
        _tapeService = new TapeService();
        _tapeService.LogMessageReceived += OnLogMessageReceived;
        _tapeService.StatusChanged += OnStatusChanged;

        // Initialize commands
        OpenDriveCommand = new AsyncRelayCommand(OpenDriveAsync, param => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && _tapeService.IsDriveOpen);
        EjectCommand = new AsyncRelayCommand(EjectAsync, () => !IsBusy && _tapeService.IsMediaLoaded);
        ExitCommand = new RelayCommand(Exit);
        AboutCommand = new RelayCommand(ShowAbout);
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
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

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
            if (SetProperty(ref _selectedBackupSet, value) && value != null)
            {
                // When a backup set is selected in the table, navigate to it
                OnBackupSetSelectedInTable(value);
            }
        }
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

    public ObservableCollection<TapeTreeItemViewModel> TreeItems { get; } = [];
    public ObservableCollection<PropertyItem> PropertyList { get; } = [];
    public ObservableCollection<FileListItem> FileList { get; } = [];
    public ObservableCollection<BackupSetListItem> BackupSetList { get; } = [];
    public ObservableCollection<string> LogMessages { get; } = [];

    #endregion

    #region Commands

    public ICommand OpenDriveCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand EjectCommand { get; }
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

    #region Private Methods

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

            BusyMessage = "Loading media...";
            success = await _tapeService.LoadMediaAsync();
            if (!success)
            {
                MessageBox.Show($"Failed to load media.\n\n{_tapeService.LastError}",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateTreeForDriveOnly(driveNumber);
                return;
            }

            BusyMessage = "Reading TOC...";
            success = await _tapeService.RestoreTOCAsync();
            if (!success)
            {
                MessageBox.Show($"Failed to read TOC.\n\n{_tapeService.LastError}",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateTreeForDriveOnly(driveNumber);
                return;
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

    private async Task RefreshAsync()
    {
        if (!_tapeService.IsDriveOpen)
            return;

        IsBusy = true;
        BusyMessage = "Refreshing...";

        try
        {
            var driveNumber = _tapeService.DriveNumber;

            var success = await _tapeService.LoadMediaAsync();
            if (!success)
            {
                MessageBox.Show($"Failed to load media.\n\n{_tapeService.LastError}",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BusyMessage = "Reading TOC...";
            success = await _tapeService.RestoreTOCAsync();
            if (!success)
            {
                MessageBox.Show($"Failed to read TOC.\n\n{_tapeService.LastError}",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateTreeFromTOC(driveNumber);
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

    private void Exit(object? parameter)
    {
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
        var tapeItem = TapeTreeItemViewModel.CreateTapeItem(toc, driveItem);
        driveItem.Children.Add(tapeItem);

        // Add backup sets (from oldest to newest for proper ordering in tree)
        int totalSets = toc.Count;
        for (int i = 1; i <= totalSets; i++)
        {
            var setTOC = toc[i];
            var setItem = TapeTreeItemViewModel.CreateBackupSetItem(setTOC, i, totalSets, tapeItem);
            tapeItem.Children.Add(setItem);
        }

        WindowTitle = $"TapeWin - {toc.Description ?? $"Volume #{toc.Volume}"}";
        StatusMessage = $"Loaded {totalSets} backup set(s)";
    }

    private void SelectMostRecentSet()
    {
        // Find and select the most recent (last) backup set
        if (TreeItems.Count > 0 && TreeItems[0].Children.Count > 0)
        {
            var tapeNode = TreeItems[0].Children[0];
            if (tapeNode.Children.Count > 0)
            {
                var lastSet = tapeNode.Children[^1];
                lastSet.IsSelected = true;
                OnTreeItemSelected(lastSet);
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

    private void LoadDriveInfo()
    {
        PropertyList.Clear();
        FileList.Clear();
        BackupSetList.Clear();
        ContentType = ContentPaneType.DriveInfo;
        PropertiesHeader = "Drive Properties";
        TableHeader = ""; // Not visible for drive

        PropertyList.Add(new PropertyItem("Device Name", _tapeService.DeviceName));
        PropertyList.Add(new PropertyItem("Drive Open", _tapeService.IsDriveOpen ? "Yes" : "No"));

        if (_tapeService.IsDriveOpen)
        {
            PropertyList.Add(new PropertyItem("Supports Multiple Partitions", 
                _tapeService.SupportsMultiplePartitions ? "Yes" : "No"));
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
                PropertyList.Add(new PropertyItem("Remaining Capacity", 
                    Helpers.BytesToStringLong(_tapeService.GetRemainingCapacity())));
            }
        }

        StatusMessage = "Drive information displayed";
    }

    private void LoadMediaInfo()
    {
        PropertyList.Clear();
        FileList.Clear();
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
        
        var remaining = _tapeService.GetRemainingCapacity();
        var used = _tapeService.Capacity - remaining;
        PropertyList.Add(new PropertyItem("Used", Helpers.BytesToStringLong(used)));
        PropertyList.Add(new PropertyItem("Remaining", Helpers.BytesToStringLong(remaining)));
        PropertyList.Add(new PropertyItem("TOC Placement", 
            _tapeService.PartitionCount > 1 ? "Partition" : "Set"));
        PropertyList.Add(new PropertyItem("Volume", $"#{toc.Volume}"));
        PropertyList.Add(new PropertyItem("Continued on Next Volume", 
            toc.ContinuedOnNextVolume ? "Yes" : "No"));

        /*
        // Populate backup sets table
        TableHeader = $"Backup Sets ({toc.Count})";
        int totalSets = toc.Count;
        for (int i = 1; i <= totalSets; i++)
        {
            var setTOC = toc[i];
            BackupSetList.Add(new BackupSetListItem(setTOC, i, toc.SetIndexToAlt(i)));
        }
        */

        // Populate backup sets table in the alternative order: from latest (0) down to oldest (toc.MinSetIndex)
        TableHeader = $"Backup Sets ({toc.Count})";
        for (int alt = 0; alt >= toc.MinSetIndex; alt--)
        {
            int setIndex = toc.SetIndexToAlt(alt); // this also converts from alt to regular index
            var setTOC = toc[setIndex];
            // Not used currently, but could be for an alternative view
            BackupSetList.Add(new BackupSetListItem(setTOC, setIndex, alt));
        }

        StatusMessage = $"Media: {toc.Description ?? "Volume #" + toc.Volume} - {toc.Count} backup set(s)";
    }

    private void LoadBackupSetInfo(int setIndex)
    {
        var toc = _tapeService.TOC;
        if (toc == null)
            return;

        PropertyList.Clear();
        FileList.Clear();
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
            PropertyList.Add(new PropertyItem("Set Index", $"{setIndex} | {altIndex}"));
            PropertyList.Add(new PropertyItem("Files", setTOC.Count.ToString("N0")));
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

            foreach (var fileInfo in files)
            {
                var item = new FileListItem(fileInfo, ShowFullPathname);
                FileList.Add(item);
            }

            StatusMessage = $"Set #{setIndex} | #{altIndex}: {FileList.Count} file(s)";
        }
        catch (Exception ex)
        {
            LogMessages.Add($"!!! Error loading backup set info: {ex.Message}");
            StatusMessage = "Error loading backup set information";
        }
    }

    private void OnBackupSetSelectedInTable(BackupSetListItem backupSetItem)
    {
        // Find the corresponding tree node and select it
        if (TreeItems.Count > 0 && TreeItems[0].Children.Count > 0)
        {
            var tapeNode = TreeItems[0].Children[0];
            var setIndex = backupSetItem.SetIndex;
            if (setIndex >= 1 && setIndex <= tapeNode.Children.Count)
            {
                var setNode = tapeNode.Children[setIndex - 1];
                setNode.IsSelected = true;
                // Don't call OnTreeItemSelected here to avoid recursion;
                //  the TreeView selection changed event will handle it
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

    private void OnLogMessageReceived(object? sender, string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            
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
}