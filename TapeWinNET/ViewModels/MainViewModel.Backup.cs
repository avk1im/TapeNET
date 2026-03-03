using System.Windows;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeWinNET.Converters;

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
                _ = ExecuteBackupAsync(backupViewModel, selectedFiles, listContainsPatterns: false);
            },
            () =>
            {
                // Back - close preview, keep New Backup Set window open
                Application.Current.Windows.OfType<BackupPreviewWindow>().FirstOrDefault()?.Close();
            }
            /*,
            () =>
            {
                // Cancel - close both windows
                Application.Current.Windows.OfType<BackupPreviewWindow>().FirstOrDefault()?.Close();
                parentWindow?.Close();
            }*/);

        var previewWindow = new BackupPreviewWindow(previewViewModel)
        {
            Owner = parentWindow ?? Application.Current.MainWindow
        };

        previewWindow.ShowDialog();
    }

    /*
    // Not necessary - same functionality as TapeFileBackupAgent.BuildFileNameList
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
    */

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

        // Sticky state for "Skip All Errors" button in the error dialog
        bool skipAllErrors = false;
        int errorCount = 0;

#if DEBUG
        //TapeFileAgent.SimulateFailures = true;
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
                    entry =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            LogMessages.Add(entry);
                            while (LogMessages.Count > 1000)
                                LogMessages.RemoveAt(0);
                        });
                    },
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
