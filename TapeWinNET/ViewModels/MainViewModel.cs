using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeLibNET.Virtual;

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

public class MainViewModel : ViewModelBase
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

    // Backup progress properties
    private int _backupProgressPercent;
    private string _backupProgressText = string.Empty;
    private string _currentBackupFile = string.Empty;
    private TapeFileBackupAgent? _currentBackupAgent;

    public MainViewModel()
    {
        _tapeService = new TapeService();
        _tapeService.LogMessageReceived += OnLogMessageReceived;
        _tapeService.StatusChanged += OnStatusChanged;

        // Initialize commands
        OpenDriveCommand = new AsyncRelayCommand(OpenDriveAsync, _ => !IsBusy);
        OpenVirtualDriveCommand = new RelayCommand(ShowOpenVirtualDriveWindow, _ => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && _tapeService.IsDriveOpen);
        EjectCommand = new AsyncRelayCommand(EjectAsync, () => !IsBusy && _tapeService.IsMediaLoaded);
        NewBackupCommand = new RelayCommand(ShowNewBackupWindow, _ => !IsBusy && _tapeService.IsMediaLoaded);
        AbortBackupCommand = new RelayCommand(AbortBackup, _ => IsBackupInProgress);
        NavigateToBackupSetCommand = new RelayCommand(NavigateToSelectedBackupSet, _ => SelectedBackupSet != null);
        ExitCommand = new RelayCommand(Exit);
        AboutCommand = new RelayCommand(ShowAbout);

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
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// True when busy with non-backup operations (shows full-window overlay).
    /// </summary>
    public bool IsGeneralBusy => IsBusy && !IsBackupInProgress;

    public int BackupProgressPercent
    {
        get => _backupProgressPercent;
        set => SetProperty(ref _backupProgressPercent, value);
    }

    public string BackupProgressText
    {
        get => _backupProgressText;
        set => SetProperty(ref _backupProgressText, value);
    }

    public string CurrentBackupFile
    {
        get => _currentBackupFile;
        set => SetProperty(ref _currentBackupFile, value);
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

    public ObservableCollection<TapeTreeItemViewModel> TreeItems { get; } = [];
    public ObservableCollection<PropertyItem> PropertyList { get; } = [];
    public ObservableCollection<FileListItem> FileList { get; } = [];
    public ObservableCollection<BackupSetListItem> BackupSetList { get; } = [];
    public ObservableCollection<string> LogMessages { get; } = [];

    #endregion

    #region Commands

    public ICommand OpenDriveCommand { get; }
    public ICommand OpenVirtualDriveCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand EjectCommand { get; }
    public ICommand NewBackupCommand { get; }
    public ICommand AbortBackupCommand { get; }
    public ICommand NavigateToBackupSetCommand { get; }
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
                MessageBox.Show($"Failed to read TOC.\n\n{_tapeService.LastError}",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
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

    #endregion

    #region Private Methods - Backup Operations

    private void ShowNewBackupWindow(object? parameter)
    {
        var viewModel = new NewBackupSetViewModel(
            _tapeService,
            OnStartBackup,
            () =>
            {
                Application.Current.Windows.OfType<NewBackupSetWindow>().FirstOrDefault()?.Close();
            });

        var window = new NewBackupSetWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };

        window.ShowDialog();
    }

    private void OnStartBackup(NewBackupSetViewModel backupViewModel)
    {
        // Get reference to New Backup Set window to close it later
        var newBackupWindow = Application.Current.Windows.OfType<NewBackupSetWindow>().FirstOrDefault();

        if (backupViewModel.PreviewFilesBeforeBackup)
        {
            // Show preview window first
            ShowBackupPreview(backupViewModel, newBackupWindow);
        }
        else
        {
            // Start backup directly
            newBackupWindow?.Close();
            _ = ExecuteBackupAsync(backupViewModel, backupViewModel.GetPatterns(), listContainsPatterns: true);
        }
    }

    private void ShowBackupPreview(NewBackupSetViewModel backupViewModel, Window? parentWindow)
    {
        // Build complete file list for preview
        var patterns = backupViewModel.GetPatterns();
        var fileList = BuildFileListFromPatterns(patterns, backupViewModel.IncludeSubdirectories);

        if (fileList.Count == 0)
        {
            MessageBox.Show("No files match the specified patterns.", "No Files",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var previewViewModel = new BackupPreviewViewModel(
            fileList,
            selectedFiles =>
            {
                // Close both windows and start backup with selected files
                Application.Current.Windows.OfType<BackupPreviewWindow>().FirstOrDefault()?.Close();
                parentWindow?.Close();
                _ = ExecuteBackupAsync(backupViewModel, selectedFiles, listContainsPatterns: false);
            },
            () =>
            {
                // Back - close preview, keep New Backup Set window open
                Application.Current.Windows.OfType<BackupPreviewWindow>().FirstOrDefault()?.Close();
            },
            () =>
            {
                // Cancel - close both windows
                Application.Current.Windows.OfType<BackupPreviewWindow>().FirstOrDefault()?.Close();
                parentWindow?.Close();
            });

        var previewWindow = new BackupPreviewWindow(previewViewModel)
        {
            Owner = parentWindow ?? Application.Current.MainWindow
        };

        previewWindow.ShowDialog();
    }

    private static List<string> BuildFileListFromPatterns(List<string> patterns, bool includeSubdirectories)
    {
        var fileList = new List<string>();

        foreach (var pattern in patterns)
        {
            try
            {
                if (TapeFileBackupAgent.HasWildcards(pattern))
                {
                    var directoryName = System.IO.Path.GetDirectoryName(pattern);
                    var fileNameWithWildcards = System.IO.Path.GetFileName(pattern);
                    var directoryPath = System.IO.Directory.Exists(directoryName)
                        ? System.IO.Path.GetFullPath(directoryName)
                        : string.IsNullOrEmpty(directoryName)
                            ? System.IO.Directory.GetCurrentDirectory()
                            : null;

                    if (!string.IsNullOrEmpty(directoryPath))
                    {
                        var searchOption = includeSubdirectories
                            ? System.IO.SearchOption.AllDirectories
                            : System.IO.SearchOption.TopDirectoryOnly;
                        fileList.AddRange(System.IO.Directory.EnumerateFiles(directoryPath, fileNameWithWildcards, searchOption));
                    }
                }
                else if (TapeFileBackupAgent.IsDirectory(pattern))
                {
                    var directoryPath = System.IO.Path.GetFullPath(pattern);
                    if (System.IO.Directory.Exists(directoryPath))
                    {
                        var searchOption = includeSubdirectories
                            ? System.IO.SearchOption.AllDirectories
                            : System.IO.SearchOption.TopDirectoryOnly;
                        fileList.AddRange(System.IO.Directory.EnumerateFiles(directoryPath, "*", searchOption));
                    }
                }
                else
                {
                    var fullPath = System.IO.Path.GetFullPath(pattern);
                    if (System.IO.File.Exists(fullPath))
                    {
                        fileList.Add(fullPath);
                    }
                }
            }
            catch
            {
                // Ignore errors during file enumeration
            }
        }

        return fileList;
    }

    private async Task ExecuteBackupAsync(NewBackupSetViewModel backupViewModel,
        List<string> fileList, bool listContainsPatterns)
    {
        if (fileList.Count == 0)
        {
            MessageBox.Show("No files to backup.", "Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        IsBackupInProgress = true;
        BusyMessage = "Preparing backup...";
        BackupProgressPercent = 0;
        BackupProgressText = "Starting...";
        CurrentBackupFile = string.Empty;

        try
        {
            await Task.Run(async () =>
            {
                await _tapeService.ExecuteBackupAsync(
                    fileList,
                    listContainsPatterns,
                    backupViewModel.Description,
                    backupViewModel.IncludeSubdirectories,
                    backupViewModel.IncrementalBackup,
                    backupViewModel.SelectedBlockSize,
                    backupViewModel.SelectedHashAlgorithm,
                    backupViewModel.AppendToSet,
                    backupViewModel.SelectedAppendOption?.SetIndex ?? -1,
                    backupViewModel.UseFilemarks,
                    // Progress update callback
                    (processed, total, bytes) =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            BackupProgressPercent = total > 0 ? (int)(100.0 * processed / total) : 0;
                            BackupProgressText = $"{processed:N0} / {total:N0} files ({Helpers.BytesToString(bytes)})";
                        });
                    },
                    // Log message callback
                    message =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
                            while (LogMessages.Count > 1000)
                                LogMessages.RemoveAt(0);
                        });
                    },
                    // File error callback - returns FileFailedAction
                    (filePath, error) =>
                    {
                        FileFailedAction result = FileFailedAction.Skip;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var dialog = new FileErrorDialog(filePath, error)
                            {
                                Owner = Application.Current.MainWindow
                            };
                            if (dialog.ShowDialog() == true)
                            {
                                result = dialog.Result;
                            }
                        });
                        return result;
                    },
                    // Volume change callback - returns true to continue, false to abort
                    (currentVolume, nextVolume, filesBackedUp, totalFiles, bytesBackedUp) =>
                    {
                        bool continueBackup = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var dialog = new MediaChangeDialog(currentVolume, nextVolume, filesBackedUp, totalFiles, bytesBackedUp)
                            {
                                Owner = Application.Current.MainWindow
                            };
                            if (dialog.ShowDialog() == true)
                            {
                                continueBackup = dialog.ContinueBackup;
                            }
                        });
                        return continueBackup;
                    },
                    // Current file callback
                    filePath =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            CurrentBackupFile = System.IO.Path.GetFileName(filePath);
                        });
                    },
                    // Abort check callback
                    () => _currentBackupAgent?.IsAbortRequested ?? false,
                    // Set agent reference callback
                    agent => _currentBackupAgent = agent
                );
            });

            // Refresh tree after successful backup
            await RefreshAsync();

            MessageBox.Show("Backup completed successfully!", "Backup Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (TapeAbortRequestedException)
        {
            LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] !!! Backup aborted by user");
            MessageBox.Show("Backup was aborted.", "Backup Aborted",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] !!! Backup failed: {ex.Message}");
            MessageBox.Show($"Backup failed.\n\n{ex.Message}", "Backup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _currentBackupAgent = null;
            IsBackupInProgress = false;
            IsBusy = false;
            BusyMessage = string.Empty;
            BackupProgressText = string.Empty;
            CurrentBackupFile = string.Empty;
        }
    }

    private void AbortBackup(object? parameter)
    {
        if (_currentBackupAgent != null)
        {
            var result = MessageBox.Show(
                "Are you sure you want to abort the backup?\n\nFiles already backed up will remain on the media.",
                "Abort Backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _currentBackupAgent.IsAbortRequested = true;
                BusyMessage = "Aborting backup...";
            }
        }
    }

    #endregion

    #region Private Methods - Menu Commands

    private void Exit(object? parameter)
    {
        if (IsBackupInProgress)
        {
            var result = MessageBox.Show(
                "A backup is in progress. Are you sure you want to exit?\n\nThe backup will be aborted.",
                "Exit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            if (_currentBackupAgent != null)
                _currentBackupAgent.IsAbortRequested = true;
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
        var tapeItem = TapeTreeItemViewModel.CreateTapeItem(toc, driveItem);
        driveItem.Children.Add(tapeItem);

        // Add backup sets (from latest to oldest for consistency with alt index display)
        int totalSets = toc.Count;
        for (int i = totalSets; i >= 1; i--)
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

        var used = toc.ComputeTotalFileSizeOnTape(_tapeService.DefaultBlockSize);
        if (!_tapeService.HasInitiatorPartition)
            used += TapeNavigator.TOCCapacity; // if TOC is in set, it consumes content space
        var remaining = _tapeService.Capacity - used; //_tapeService.GetRemainingCapacity();

        PropertyList.Add(new PropertyItem("Used", Helpers.BytesToStringLong(used)));
        PropertyList.Add(new PropertyItem("Remaining", Helpers.BytesToStringLong(remaining)));
        PropertyList.Add(new PropertyItem("TOC Placement", 
            _tapeService.HasInitiatorPartition ? "Partition" : "Set"));
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

    #region Private Methods - Event Handlers

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

    #region Private Methods - Virtual Drive Operations

    private void ShowOpenVirtualDriveWindow(object? parameter)
    {
        ShowOpenVirtualDriveWindowInternal(prePopulatedPath: null, preSelectCreateNew: null);
    }

    private void ShowOpenVirtualDriveWindowInternal(
        string? prePopulatedPath,
        bool? preSelectCreateNew)
    {
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
            prePopulatedContentPath: prePopulatedPath,
            preSelectCreateNew: preSelectCreateNew);

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
            // Pass requireExisting based on user's mode selection
            var success = await _tapeService.OpenVirtualDriveAsync(
                request.Capabilities,
                request.ContentPath,
                request.InitiatorPath,
                requireExisting: !request.IsCreateNew);

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
                        prePopulatedPath: request.ContentPath,
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

            // For "Create new", create and save the initial TOC
            if (request.IsCreateNew)
            {
                BusyMessage = "Creating initial TOC...";
                success = await _tapeService.CreateInitialTOCAsync(request.MediaName);
                if (!success)
                {
                    // Warning only - drive is still usable
                    LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] !!! Warning: Could not create initial TOC");
                }
            }

            BusyMessage = "Reading TOC...";
            success = await _tapeService.RestoreTOCAsync();
            if (!success)
            {
                // For new media, TOC read failure is expected if initial TOC creation failed
                if (!request.IsCreateNew)
                {
                    MessageBox.Show(
                        $"Failed to read virtual TOC.\n\n{_tapeService.LastError}",
                        "Warning",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                UpdateTreeForDriveOnly(0);
                return;
            }

            UpdateTreeFromTOC(0);

            // Select the most recent backup set
            SelectMostRecentSet();

            // Log success with mode info
            var modeText = request.IsCreateNew ? "Created new" : "Opened existing";
            LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] iii {modeText} virtual media: {request.ContentPath}");
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    #endregion
}

