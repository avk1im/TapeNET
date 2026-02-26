using System.Windows;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeLibNET.Virtual;
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
            _ => StartRestoreForSelectedSet(RestoreMode.Restore),
            _ => CanStartRestore);
        ValidateCommand = new RelayCommand(
            _ => StartRestoreForSelectedSet(RestoreMode.Validate),
            _ => CanStartRestore);
        VerifyCommand = new RelayCommand(
            _ => StartRestoreForSelectedSet(RestoreMode.Verify),
            _ => CanStartRestore);
        AbortRestoreCommand = new RelayCommand(AbortRestore, _ => IsRestoreInProgress);
    }

    /// <summary>
    /// Whether restore/validate/verify commands should be enabled.
    /// Requires: not busy, media loaded, TOC available, and a backup set selected.
    /// </summary>
    private bool CanStartRestore =>
        !IsBusy && _tapeService.IsMediaLoaded && _tapeService.TOC != null && SelectedSetIndex.HasValue;

    /// <summary>
    /// Gets the currently selected backup set index from either the tree view or the backup set list view.
    /// Returns null if no backup set is selected.
    /// </summary>
    private int? SelectedSetIndex
    {
        get
        {
            // First try: backup set selected in tree view
            if (_selectedTreeItem?.ItemType == TreeItemType.BackupSet)
                return _selectedTreeItem.SetIndex;

            // Second try: backup set selected in the backup set list view
            if (SelectedBackupSet != null)
                return SelectedBackupSet.SetIndex;

            return null;
        }
    }

    #endregion

    #region Private Methods - Restore Operations

    private void StartRestoreForSelectedSet(RestoreMode mode)
    {
        int? setIndex = SelectedSetIndex;
        if (!setIndex.HasValue)
            return;

        var toc = _tapeService.TOC;
        if (toc == null)
            return;

        // For Restore mode, prompt for target directory
        string? targetDirectory = null;
        if (mode == RestoreMode.Restore)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Target Directory for Restore"
            };

            if (dialog.ShowDialog() != true)
                return;

            targetDirectory = dialog.FolderName;
        }

        // Determine if the set is incremental and should traverse the chain
        toc.CurrentSetIndex = setIndex.Value;
        bool incremental = toc.CurrentSetTOC.Incremental;

        _ = ExecuteRestoreAsync(mode, setIndex.Value, incremental, targetDirectory);
    }

    private async Task ExecuteRestoreAsync(
        RestoreMode mode, int setIndex, bool incremental, string? targetDirectory)
    {
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
                    setIndex,
                    incremental,
                    filePatterns: null, // no file filtering in simplified UI
                    targetDirectory,
                    recurseSubdirectories: true,
                    TapeHowToHandleExisting.KeepBoth,
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
                    message =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
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
            LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] !!! {modeName} aborted by user");
            MessageBox.Show($"{modeName} was aborted.", $"{modeName} Aborted",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            LogMessages.Add($"[{DateTime.Now:HH:mm:ss}] !!! {modeName} failed: {ex.Message}");
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
