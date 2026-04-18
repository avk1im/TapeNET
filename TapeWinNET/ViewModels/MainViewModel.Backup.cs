using System.Windows;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeWinNET.Converters;
using TapeWinNET.Models;

using TapeLibNET;
using TapeLibNET.Virtual;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Partial class containing backup-related functionality for MainViewModel.
/// </summary>
public partial class MainViewModel
{
    #region Backup Fields

    // Backup progress properties
    private int _backupProgressPercent;
    private string _backupProgressText = string.Empty;
    private string _currentBackupFile = string.Empty;

    #endregion

    #region Backup Properties

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

    #endregion

    #region Backup Commands

    public ICommand NewBackupCommand { get; private set; } = null!;
    public ICommand AbortBackupCommand { get; private set; } = null!;

    /// <summary>
    /// Initializes backup-related commands. Called from constructor.
    /// </summary>
    private void InitializeBackupCommands()
    {
        NewBackupCommand = new RelayCommand(ShowNewBackupWindow, _ => !IsBusy && _tapeService.IsMediaLoaded && !_tapeService.IsTOCFromFile);
        AbortBackupCommand = new RelayCommand(AbortBackup, _ => IsBackupInProgress);
    }

    #endregion

    #region Private Methods - Backup Operations

    private void ShowNewBackupWindow(object? parameter)
    {
        ShowNewBackupWindow(paths: null);
    }

    /// <summary>
    /// Opens the BackupWindow, optionally pre-populated with the given paths
    ///  (e.g. from a drag-drop onto the MainWindow).
    /// </summary>
    internal void ShowNewBackupWindow(string[]? paths)
    {
        var viewModel = new BackupViewModel(
            _tapeService,
            OnStartBackup,
            () =>
            {
                Application.Current.Windows.OfType<BackupWindow>().FirstOrDefault()?.Close();
            });

        var window = new BackupWindow(viewModel, paths)
        {
            Owner = Application.Current.MainWindow
        };

        window.ShowDialog();
    }

    private void OnStartBackup(BackupRequest request)
    {
        // Close the BackupWindow before starting the operation
        Application.Current.Windows.OfType<BackupWindow>().FirstOrDefault()?.Close();
        _ = ExecuteBackupAsync(request);
    }

    private async Task ExecuteBackupAsync(BackupRequest request)
    {
        // Convert checked TapeFileInfo list to string paths for TapeService
        var fileList = request.CheckedFiles
            .Select(tfi => tfi.FileDescr.FullName)
            .ToList();

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

        // Sticky state for "Skip All Errors" button in the error dialog
        bool skipAllErrors = false;
        int errorCount = 0;
        BackupOperationResult? operationResult = null;

        try
        {
            await Task.Run(async () =>
            {
                operationResult = await _tapeService.ExecuteBackupAsync(
                    fileList,
                    listContainsPatterns: false,  // files are already resolved
                    request.Description,
                    request.IncludeSubdirectories,
                    request.Incremental,
                    request.BlockSize,
                    request.HashAlgorithm,
                    request.AppendMode,
                    request.AppendAfterSetIndex,
                    request.UseFilemarks,
                    // Current file callback
                    filePath =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            CurrentBackupFile = System.IO.Path.GetFileName(filePath);
                        });
                    },
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
                    entry => AddLog(entry),
                    // File error callback - returns FileFailedAction
                    (filePath, error) =>
                    {
                        errorCount++;

                        if (skipAllErrors)
                            return FileFailedAction.Skip;

                        FileFailedAction result = FileFailedAction.Skip;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var dialog = new FileErrorDialog(filePath, error, "Backup")
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
                    // Volume full callback - ask user if they want to continue on next volume
                    (currentVolume, nextVolume, filesBackedUp, totalFiles, bytesBackedUp) =>
                    {
                        bool continueBackup = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var dialog = new MediaChangeDialog(
                                "Volume Full",
                                $"Volume #{currentVolume} is full.\n" +
                                $"Backed up {filesBackedUp:N0} of {totalFiles:N0} files " +
                                $"({Helpers.BytesToString(bytesBackedUp)}) so far.",
                                $"Click Continue to eject this volume and continue the backup on a new media volume.",
                                "Continue to Next Volume")
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
                    // Insert media callback - prompt user to insert new media after ejection
                    (nextVolume) =>
                    {
                        bool mediaReady = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (_tapeService.IsVirtualDrive)
                            {
                                // Virtual drive: show OpenVirtualDriveWindow in newMediaOnly mode
                                // to let user configure the new volume's file and capacity
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

                                // Pre-populate with previous media's values, volume-decorated path
                                var lastVmd = _tapeService.LastVMD;
                                VirtualMediaDescriptor? prePopulate = null;
                                if (lastVmd != null)
                                {
                                    prePopulate = lastVmd with
                                    {
                                        ContentPath = OpenVirtualDriveViewModel.BuildVolumeFilePath(
                                            lastVmd.ContentPath, nextVolume)
                                    };
                                }

                                var vm = new OpenVirtualDriveViewModel(
                                    request =>
                                    {
                                        // Close dialog
                                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();

                                        // Insert the new virtual media (no lock — worker is blocked here)
                                        mediaReady = _tapeService.InsertVirtualMedia(
                                            request.Media,
                                            System.IO.FileMode.Create);
                                    },
                                    () =>
                                    {
                                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
                                        mediaReady = false;
                                    },
                                    prePopulate: prePopulate,
                                    mediaMode: System.IO.FileMode.Create,
                                    currentCapabilities: currentCaps,
                                    currentIoSpeed: _selectedIoSpeed);

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
                                    "Insert New Media",
                                    $"The current volume has been ejected.",
                                    $"Please insert a formatted media for Volume #{nextVolume}.\n\n" +
                                    $"Click Continue when the new media is in the drive.",
                                    "Continue Backup",
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
                    // Media load retry callback — when ReloadMedia/PrepareMedia fails after inserting the next volume.
                    //  First invocation: explain the failure and ask the user to re-seat the media.
                    //  Second invocation: shorter follow-up prompt.
                    (loadError, isRetry) =>
                    {
                        bool retry = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            string info = !isRetry
                                ? $"The drive could not load the media.\n\nError: {loadError}\n\n" +
                                  "Make sure the media is properly inserted. Retry?"
                                : $"Loading media failed again.\n\nError: {loadError}\n\n" +
                                  "Try re-seating the media. Retry?";

                            var answer = MessageBox.Show(
                                info,
                                "Media Load Failed",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning,
                                MessageBoxResult.Yes);

                            retry = answer == MessageBoxResult.Yes;
                        });
                        return retry;
                    },
                    // Emergency TOC export callback — when all tape TOC writes fail.
                    //  First invocation: warn the user and ask for confirmation before showing the save dialog.
                    //  Second invocation (retry after a failed file-save): show a shorter prompt.
                    (suggestedPath, isRetry) =>
                    {
                        string? chosenPath = null;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            string info = !isRetry
                                ? "The TOC could not be saved to media. Without a TOC, the files on media cannot be accessed.\n\n" +
                                  "This is your last chance to export the TOC to a file for file recovery.\n\n" +
                                  "Do you want to choose a save location now?"
                                : "Exporting TOC failed. Try a different location?";

                            var answer = MessageBox.Show(
                                info,
                                "Emergency TOC Export",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning,
                                MessageBoxResult.Yes);

                            if (answer != MessageBoxResult.Yes)
                                return; // user declined — chosenPath stays null

                            var dialog = new Microsoft.Win32.SaveFileDialog
                            {
                                Title = "Emergency TOC Export — Choose Save Location",
                                Filter = $"Tape TOC files (*{TapeFileAgent.TOCFileExtension})|*{TapeFileAgent.TOCFileExtension}|All files (*.*)|*.*",
                                FileName = System.IO.Path.GetFileName(suggestedPath),
                                InitialDirectory = System.IO.Path.GetDirectoryName(suggestedPath) ?? "",
                                OverwritePrompt = true,
                            };
                            if (dialog.ShowDialog() == true)
                                chosenPath = dialog.FileName;
                        });
                        return chosenPath;
                    }
                );
            });

            // Refresh tree after backup
            //  to keep TOCView in sync with the (possibly modified) TOC.
            //  Refresh might throw if TOC has been spoiled
            try { await RefreshAsync(); } catch { /* ignore */ }

            // Determine outcome from the result record
            if (operationResult is { HasFailed: true })
            {
                LogErr("Backup failed");
                MessageBox.Show("Backup failed. See log for details.", "Backup Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else if (operationResult is { WasAborted: true })
            {
                LogErr("Backup aborted by user");
                MessageBox.Show("Backup was aborted.", "Backup Aborted",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (errorCount > 0 || operationResult is { IsFullSuccess: false })
            {
                MessageBox.Show("Backup completed with some errors. See log for details.", "Backup Complete",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show("Backup completed successfully!", "Backup Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            // Catch catstrophic failures not handled by TapeService.ExecuteBackupAsync.
            
            // Refresh even on failure — TOC may have been modified before the error.
            //  Refresh might throw if TOC has been spoiled
            try { await RefreshAsync(); } catch { /* ignore */ }
            LogErr($"Backup failed: {ex.Message}");
            MessageBox.Show($"Backup failed.\n\n{ex.Message}", "Backup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBackupInProgress = false;
            IsBusy = false;
            BusyMessage = string.Empty;
            BackupProgressText = string.Empty;
            CurrentBackupFile = string.Empty;
        }
    }

#if _OLDCODE
    // ─────────────────────────────────────────────────
    //  Legacy backup flow (NewBackupSetWindow + BackupPreviewWindow)
    // ─────────────────────────────────────────────────

    private void ShowNewBackupWindow_Legacy(object? parameter)
    {
        var viewModel = new NewBackupSetViewModel(
            _tapeService,
            OnStartBackup_Legacy,
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

    private void OnStartBackup_Legacy(NewBackupSetViewModel backupViewModel)
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
            _ = ExecuteBackupAsync_Legacy(backupViewModel, backupViewModel.GetPatterns(), listContainsPatterns: true);
        }
    }

    private void ShowBackupPreview(NewBackupSetViewModel backupViewModel, Window? parentWindow)
    {
        // Build complete file list for preview
        var patterns = backupViewModel.GetPatterns();
        var fileList = TapeFileBackupAgent.BuildFileNameList(patterns, backupViewModel.IncludeSubdirectories);

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
                _ = ExecuteBackupAsync_Legacy(backupViewModel, selectedFiles, listContainsPatterns: false);
            },
            () =>
            {
                // Back - close preview, keep New Backup Set window open
                Application.Current.Windows.OfType<BackupPreviewWindow>().FirstOrDefault()?.Close();
            });

        var previewWindow = new BackupPreviewWindow(previewViewModel)
        {
            Owner = parentWindow ?? Application.Current.MainWindow
        };

        previewWindow.ShowDialog();
    }

    private async Task ExecuteBackupAsync_Legacy(NewBackupSetViewModel backupViewModel,
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

        // Sticky state for "Skip All Errors" button in the error dialog
        bool skipAllErrors = false;
        int errorCount = 0;

#if DEBUG
        //agent.SimulateFileFailures.Enabled = true; // set on agent instance after creation
#endif

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
                    entry => AddLog(entry),
                    // File error callback - returns FileFailedAction
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
                    // Volume full callback - ask user if they want to continue on next volume
                    (currentVolume, nextVolume, filesBackedUp, totalFiles, bytesBackedUp) =>
                    {
                        bool continueBackup = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var dialog = new MediaChangeDialog(
                                "Volume Full",
                                $"Volume #{currentVolume} is full.\n" +
                                $"Backed up {filesBackedUp:N0} of {totalFiles:N0} files " +
                                $"({Helpers.BytesToString(bytesBackedUp)}) so far.",
                                $"Click Continue to eject this volume and continue the backup on a new media volume.",
                                "Continue to Next Volume")
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
                    // Insert media callback - prompt user to insert new media after ejection
                    (nextVolume) =>
                    {
                        bool mediaReady = false;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (_tapeService.IsVirtualDrive)
                            {
                                // Virtual drive: show OpenVirtualDriveWindow in newMediaOnly mode
                                // to let user configure the new volume's file and capacity
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

                                // Pre-populate with previous media's values, volume-decorated path
                                var lastVmd = _tapeService.LastVMD;
                                VirtualMediaDescriptor? prePopulate = null;
                                if (lastVmd != null)
                                {
                                    prePopulate = lastVmd with
                                    {
                                        ContentPath = OpenVirtualDriveViewModel.BuildVolumeFilePath(
                                            lastVmd.ContentPath, nextVolume)
                                    };
                                }

                                var vm = new OpenVirtualDriveViewModel(
                                    request =>
                                    {
                                        // Close dialog
                                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();

                                        // Insert the new virtual media (no lock — worker is blocked here)
                                        mediaReady = _tapeService.InsertVirtualMedia(
                                            request.Media,
                                            System.IO.FileMode.Create);
                                    },
                                    () =>
                                    {
                                        Application.Current.Windows.OfType<OpenVirtualDriveWindow>().FirstOrDefault()?.Close();
                                        mediaReady = false;
                                    },
                                    prePopulate: prePopulate,
                                    mediaMode: System.IO.FileMode.Create,
                                    currentCapabilities: currentCaps,
                                    currentIoSpeed: _selectedIoSpeed);

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
                                    "Insert New Media",
                                    $"The current volume has been ejected.",
                                    $"Please insert a formatted media for Volume #{nextVolume}.\n\n" +
                                    $"Click Continue when the new media is in the drive.",
                                    "Continue Backup",
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
                            CurrentBackupFile = System.IO.Path.GetFileName(filePath);
                        });
                    },
                    // Emergency TOC export callback — when all tape TOC writes fail
                    suggestedPath =>
                    {
                        string? chosenPath = null;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var dialog = new Microsoft.Win32.SaveFileDialog
                            {
                                Title = "Emergency TOC Export",
                                Filter = $"Tape TOC files (*{TapeFileAgent.TOCFileExtension})|*{TapeFileAgent.TOCFileExtension}|All files (*.*)|*.*",
                                FileName = System.IO.Path.GetFileName(suggestedPath),
                                InitialDirectory = System.IO.Path.GetDirectoryName(suggestedPath) ?? "",
                                OverwritePrompt = true,
                            };
                            if (dialog.ShowDialog() == true)
                                chosenPath = dialog.FileName;
                        });
                        return chosenPath;
                    }
                );
            });

            // Refresh tree after successful backup
            await RefreshAsync();

            if (errorCount > 0)
            {
                MessageBox.Show("Backup completed with some errors. See log for details.", "Backup Complete",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show("Backup completed successfully!", "Backup Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (TapeAbortRequestedException)
        {
            LogErr("Backup aborted by user");
            MessageBox.Show("Backup was aborted.", "Backup Aborted",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            LogErr($"Backup failed: {ex.Message}");
            MessageBox.Show($"Backup failed.\n\n{ex.Message}", "Backup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBackupInProgress = false;
            IsBusy = false;
            BusyMessage = string.Empty;
            BackupProgressText = string.Empty;
            CurrentBackupFile = string.Empty;
        }
    }
#endif // _OLDCODE

    private void AbortBackup(object? parameter)
    {
        var agent = _tapeService.Agent;
        if (agent != null)
        {
            var result = MessageBox.Show(
                "Are you sure you want to abort the backup?\n\nFiles already backed up will remain on the media.",
                "Abort Backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                agent.IsAbortRequested = true;
                BusyMessage = "Aborting backup...";
            }
        }
    }

    #endregion
}
