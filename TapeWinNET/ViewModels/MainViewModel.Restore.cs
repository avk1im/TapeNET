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
/// Partial class containing restore/validate/verify functionality for MainViewModel.
/// </summary>
public partial class MainViewModel
{
    #region Restore Fields

    private int _restoreProgressPercent;
    private string _restoreProgressText = string.Empty;
    private string _currentRestoreFile = string.Empty;
    private bool _isRestoreInProgress;

    #endregion

    #region Restore Properties

    /// <summary>
    /// Whether a restore/validate/verify operation is currently in progress.
    /// </summary>
    public bool IsRestoreInProgress
    {
        get => _isRestoreInProgress;
        set
        {
            if (SetProperty(ref _isRestoreInProgress, value))
            {
                OnPropertyChanged(nameof(IsGeneralBusy));
                OnPropertyChanged(nameof(IsOperationInProgress));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int RestoreProgressPercent
    {
        get => _restoreProgressPercent;
        set => SetProperty(ref _restoreProgressPercent, value);
    }

    public string RestoreProgressText
    {
        get => _restoreProgressText;
        set => SetProperty(ref _restoreProgressText, value);
    }

    public string CurrentRestoreFile
    {
        get => _currentRestoreFile;
        set => SetProperty(ref _currentRestoreFile, value);
    }

    #endregion

    #region Restore Commands

    public ICommand RestoreCommand { get; private set; } = null!;
    public ICommand ValidateCommand { get; private set; } = null!;
    public ICommand VerifyCommand { get; private set; } = null!;
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
        AbortRestoreCommand = new RelayCommand(AbortRestore, _ => IsRestoreInProgress);
    }

    private void StartRestore(RestoreMode mode)
    {
        if (HasFilesCheckedForRestore && IsFileTableVisible)
            StartRestoreForSelectedFiles(mode);
        else
            StartRestoreForSelectedSet(mode);
    }

    /// <summary>
    /// Whether restore/validate/verify commands should be enabled.
    /// Requires: not busy, media loaded, TOC available, and at least one backup set selected.
    /// </summary>
    private bool CanStartRestore =>
        !IsBusy && _tapeService.IsMediaLoaded && _tapeService.TOC != null
        && (SelectedSetIndexes.Count > 0 || HasFilesCheckedForRestore);

    /// <summary>
    /// Gets or sets whether all backup sets are checked for restore.
    /// Setter checks or unchecks every item in BackupSetList.
    /// </summary>
    public bool AreAllBackupSetsChecked
    {
        get => BackupSetList.Count > 0 && BackupSetList.All(b => b.IsCheckedForRestore);
        set
        {
            foreach (var item in BackupSetList)
                item.IsCheckedForRestore = value;
            OnPropertyChanged(nameof(AreAllBackupSetsChecked));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Gets or sets whether all files are checked for restore.
    /// Setter checks or unchecks every item in FileList.
    /// </summary>
    public bool AreAllFilesChecked
    {
        get => FileList.Count > 0 && FileList.All(f => f.IsCheckedForRestore);
        set
        {
            foreach (var item in FileList)
                item.IsCheckedForRestore = value;
            OnPropertyChanged(nameof(AreAllFilesChecked));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Whether any files are checked for restore in the file list.
    /// </summary>
    private bool HasFilesCheckedForRestore => FileList.Any(f => f.IsCheckedForRestore);

    /// <summary>Called from code-behind when a file row checkbox is toggled.</summary>
    public void OnFileCheckChanged()
    {
        OnPropertyChanged(nameof(AreAllFilesChecked));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Called from code-behind when a backup set row checkbox is toggled.</summary>
    public void OnBackupSetCheckChanged()
    {
        OnPropertyChanged(nameof(AreAllBackupSetsChecked));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Gets the set indexes selected for restore: checked sets in the backup set list,
    /// or the single set selected in the tree view.
    /// </summary>
    private List<int> SelectedSetIndexes
    {
        get
        {
            // First try: checked backup sets in the list view (multi-select via checkboxes)
            var checkedSets = BackupSetList
                .Where(b => b.IsCheckedForRestore)
                .Select(b => b.SetIndex)
                .ToList();

            if (checkedSets.Count > 0)
                return checkedSets;

            // Second try: backup set selected in tree view
            if (_selectedTreeItem?.ItemType == TreeItemType.BackupSet && _selectedTreeItem.SetIndex.HasValue)
                return [_selectedTreeItem.SetIndex.Value];

            // Third try: backup set selected (clicked) in the backup set list view
            if (SelectedBackupSet != null)
                return [SelectedBackupSet.SetIndex];

            return [];
        }
    }

    #endregion

    #region Private Methods - Restore Operations

    private void StartRestoreForSelectedSet(RestoreMode mode)
    {
        var setIndexes = SelectedSetIndexes;
        if (setIndexes.Count == 0)
            return;

        var toc = _tapeService.TOC;
        if (toc == null)
            return;

        // Gather the BackupSetListItem objects for the selected indexes
        var preSelectedSets = setIndexes
            .Select(idx => BackupSetList.FirstOrDefault(b => b.SetIndex == idx))
            .Where(b => b != null)
            .Cast<BackupSetListItem>()
            .ToList();

        // If selection came from tree view (not from checkboxes), find or create the item
        if (preSelectedSets.Count == 0 && setIndexes.Count > 0)
        {
            foreach (var idx in setIndexes)
            {
                var setTOC = toc[idx];
                var altIdx = toc.SetIndexToAlt(idx);
                preSelectedSets.Add(new BackupSetListItem(setTOC, idx, altIdx, setTOC.Volume == toc.Volume));
            }
        }

        if (preSelectedSets.Count == 0)
            return;

        var viewModel = new RestoreViewModel(
            mode,
            preSelectedSets,
            request =>
            {
                // Close the dialog
                Application.Current.Windows.OfType<RestoreWindow>().FirstOrDefault()?.Close();

                // Execute the operation with the gathered settings
                _ = ExecuteRestoreAsync(request);
            },
            () =>
            {
                Application.Current.Windows.OfType<RestoreWindow>().FirstOrDefault()?.Close();
            });

        var window = new RestoreWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private void StartRestoreForSelectedFiles(RestoreMode mode)
    {
        var toc = _tapeService.TOC;
        if (toc == null)
            return;

        // Determine the set index from the current tree selection
        int setIndex;
        if (_selectedTreeItem?.ItemType == TreeItemType.BackupSet && _selectedTreeItem.SetIndex.HasValue)
            setIndex = _selectedTreeItem.SetIndex.Value;
        else
            return;

        var checkedFiles = FileList.Where(f => f.IsCheckedForRestore).ToList();
        if (checkedFiles.Count == 0)
            return;

        toc.CurrentSetIndex = setIndex;
        var setTOC = toc.CurrentSetTOC;

        if (setTOC.Count == checkedFiles.Count)
        {
            // All files are checked —> shortcut to full set restore
            StartRestoreForSelectedSet(mode);
            return;
        }

        var viewModel = new RestoreViewModel(
            mode,
            checkedFiles,
            setIndex,
            setTOC.Incremental,
            request =>
            {
                Application.Current.Windows.OfType<RestoreWindow>().FirstOrDefault()?.Close();
                _ = ExecuteRestoreAsync(request);
            },
            () =>
            {
                Application.Current.Windows.OfType<RestoreWindow>().FirstOrDefault()?.Close();
            });

        var window = new RestoreWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    private async Task ExecuteRestoreAsync(RestoreRequest request)
    {
        var mode = request.Mode;
        var setIndexes = request.SetIndexes;
        var incremental = request.Incremental;
        var targetDirectory = request.TargetDirectory;
        var handleExisting = request.HandleExisting;
        var filePatterns = request.FilePatterns;

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

        // Sticky state for "Skip All Errors" button in the error dialog
        bool skipAllErrors = false;
        int errorCount = 0;

        try
        {
            await Task.Run(async () =>
            {
                await _tapeService.ExecuteRestoreAsync(
                    mode,
                    setIndexes,
                    incremental,
                    filePatterns,
                    targetDirectory,
                    recurseSubdirectories: true,
                    handleExisting,
                    // Progress update callback
                    (processed, total, bytes) =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            RestoreProgressPercent = total > 0 ? (int)(100.0 * processed / total) : 0;
                            RestoreProgressText = $"{processed:N0} / {total:N0} files ({Helpers.BytesToString(bytes)})";
                        });
                    },
                    // Log message callback
                    entry =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            LogMessages.Add(entry);
                            while (LogMessages.Count > 1000)
                                LogMessages.RemoveAt(0);
                        });
                    },
                    // File error callback
                    (filePath, error) =>
                    {
                        errorCount++;

                        if (skipAllErrors)
                            return FileFailedAction.Skip;

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
                                if (dialog.SkipAllErrors)
                                    skipAllErrors = true;
                            }
                        });
                        return result;
                    },
                    // Volume change callback — ask user if they want to continue on another volume
                    (volumeNeeded) =>
                    {
                        bool continueRestore = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var dialog = new MediaChangeDialog(
                                $"{modeName}: Volume Change Required",
                                $"{modeName} requires Volume #{volumeNeeded} to continue.",
                                $"Click Continue to eject the current media and insert Volume #{volumeNeeded}.",
                                $"Continue {modeName}")
                            {
                                Owner = Application.Current.MainWindow
                            };
                            if (dialog.ShowDialog() == true)
                            {
                                continueRestore = dialog.ContinueBackup;
                            }
                        });
                        return continueRestore;
                    },
                    // Insert media callback — prompt user to insert the required volume
                    (volumeNeeded) =>
                    {
                        bool mediaReady = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (_tapeService.IsVirtualDrive)
                            {
                                // Virtual drive: show OpenVirtualDriveWindow to pick existing media file
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
                                VirtualMediaDescriptor? prePopulate = null;
                                if (lastVmd != null)
                                {
                                    prePopulate = lastVmd with
                                    {
                                        ContentPath = OpenVirtualDriveViewModel.BuildVolumeFilePath(
                                            lastVmd.ContentPath, volumeNeeded)
                                    };
                                }

                                var vm = new OpenVirtualDriveViewModel(
                                    request =>
                                    {
                                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();

                                        // Insert virtual media in Open mode (existing media for restore)
                                        mediaReady = _tapeService.InsertVirtualMedia(
                                            request.Media,
                                            System.IO.FileMode.Open);
                                    },
                                    () =>
                                    {
                                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
                                        mediaReady = false;
                                    },
                                    prePopulate: prePopulate,
                                    mediaMode: System.IO.FileMode.Open,
                                    currentCapabilities: currentCaps);

                                var window = new OpenVirtualDriveWindow(vm)
                                {
                                    Owner = Application.Current.MainWindow
                                };
                                window.ShowDialog();
                            }
                            else
                            {
                                // Physical drive: show simple media change dialog
                                var dialog = new MediaChangeDialog(
                                    "Insert Media",
                                    $"The current volume has been ejected.",
                                    $"Please insert the media containing Volume #{volumeNeeded}.\n\n" +
                                    $"Click Continue when the media is in the drive.",
                                    $"Continue {modeName}",
                                    showWarning: true)
                                {
                                    Owner = Application.Current.MainWindow
                                };
                                if (dialog.ShowDialog() == true)
                                {
                                    mediaReady = dialog.ContinueBackup;
                                }
                            }
                        });
                        return mediaReady;
                    },
                    // Current file callback
                    filePath =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            CurrentRestoreFile = System.IO.Path.GetFileName(filePath);
                        });
                    }
                );
            });

            // Refresh tree after successful operation
            await RefreshAsync();

            if (errorCount > 0)
            {
                MessageBox.Show($"{modeName} completed with some errors. See log for details.",
                    $"{modeName} Complete", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show($"{modeName} completed successfully!",
                    $"{modeName} Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (TapeAbortRequestedException)
        {
            LogErr($"{modeName} aborted by user");
            MessageBox.Show($"{modeName} was aborted.", $"{modeName} Aborted",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            LogErr($"{modeName} failed: {ex.Message}");
            MessageBox.Show($"{modeName} failed.\n\n{ex.Message}", $"{modeName} Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsRestoreInProgress = false;
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
            var result = MessageBox.Show(
                "Are you sure you want to abort the current operation?",
                "Abort Operation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                agent.IsAbortRequested = true;
                BusyMessage = "Aborting...";
            }
        }
    }

    #endregion
}
