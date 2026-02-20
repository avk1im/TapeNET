using System.Windows;
using System.Windows.Input;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;

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
        NewBackupCommand = new RelayCommand(ShowNewBackupWindow, _ => !IsBusy && _tapeService.IsMediaLoaded);
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
                    }
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
