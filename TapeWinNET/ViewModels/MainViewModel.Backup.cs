using System.Windows;
using System.Windows.Input;

using TapeWinNET.Converters;
using TapeWinNET.Models;

using TapeLibNET;
using TapeLibNET.Services;
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

    // True while the TOC save sub-phase is not in progress; bound to the Abort Backup button's IsEnabled
    private bool _isAbortBackupEnabled = true;

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

    /// <summary>
    /// False during the TOC save sub-phase of a backup, disabling the Abort button.
    /// Reset to <see langword="true"/> when the TOC save completes or when no backup
    ///  operation is in progress.
    /// </summary>
    public bool IsAbortBackupEnabled
    {
        get => _isAbortBackupEnabled;
        set => SetProperty(ref _isAbortBackupEnabled, value);
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

    private void OnStartBackup(BackupFormData request)
    {
        // Close the BackupWindow before starting the operation
        Application.Current.Windows.OfType<BackupWindow>().FirstOrDefault()?.Close();
        _ = ExecuteBackupAsync(request);
    }

    private async Task ExecuteBackupAsync(BackupFormData request)
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

        BackupResult? operationResult = null;

        try
        {
            operationResult = await _tapeService.ExecuteBackupAsync(
                new BackupRequest(
                    FileList: fileList,
                    ListContainsPatterns: false,  // files are already resolved
                    Description: request.Description,
                    IncludeSubdirectories: request.IncludeSubdirectories,
                    Incremental: request.Incremental,
                    BlockSize: request.BlockSize,
                    HashAlgorithm: request.HashAlgorithm,
                    AppendMode: request.AppendMode,
                    AppendAfterSetIndex: request.AppendAfterSetIndex,
                    SkipAllErrors: request.SkipAllErrors,
                    MediaName: request.MediaName)
                {
                    NoMultivolume = request.NoMultivolume,
                });

            // Refresh tree after backup to keep TOCView in sync with the (possibly modified) TOC.
            // Refresh might throw if TOC has been spoiled.
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
            else if (operationResult is { IsFullSuccess: false })
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
            // Catch catastrophic failures not handled by TapeService.ExecuteBackupAsync.

            // Refresh even on failure — TOC may have been modified before the error.
            // Refresh might throw if TOC has been spoiled.
            try { await RefreshAsync(); } catch { /* ignore */ }
            LogErr($"Backup failed: {ex.Message}");
            MessageBox.Show($"Backup failed.\n\n{ex.Message}", "Backup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBackupInProgress = false;
            IsAbortBackupEnabled = true;  // always re-enable for next backup session
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
