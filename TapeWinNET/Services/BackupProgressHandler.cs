using System.Windows;
using TapeLibNET;

namespace TapeWinNET.Services;

/// <summary>
/// Handles backup progress events and user interaction via ITapeFileNotifiable.
/// Bridges between the TapeFileBackupAgent and the WPF UI.
/// </summary>
public class BackupProgressHandler : ITapeFileNotifiable
{
    private readonly Action<string> _logMessage;
    private readonly Action<int, int, long> _updateProgress;
    private readonly Func<string, string, FileFailedAction> _showFileError;
    private bool _skipAllErrors;

    public int FilesProcessed { get; private set; }
    public int FilesTotal { get; private set; }
    public int FilesFailed { get; private set; }
    public int FilesSucceeded { get; private set; }
    public long BytesProcessed { get; private set; }

    /// <summary>
    /// Creates a new BackupProgressHandler.
    /// </summary>
    /// <param name="logMessage">Action to log a message to the UI.</param>
    /// <param name="updateProgress">Action to update progress (filesProcessed, filesTotal, bytesProcessed).</param>
    /// <param name="showFileError">Function to show file error dialog. Returns FileFailedAction.</param>
    public BackupProgressHandler(
        Action<string> logMessage,
        Action<int, int, long> updateProgress,
        Func<string, string, FileFailedAction> showFileError)
    {
        _logMessage = logMessage;
        _updateProgress = updateProgress;
        _showFileError = showFileError;
    }

    public void BatchStartStatistics(int set, int filesFound)
    {
        FilesTotal = filesFound;
        FilesProcessed = 0;
        FilesFailed = 0;
        FilesSucceeded = 0;
        BytesProcessed = 0;
        _skipAllErrors = false;

        _logMessage($"iii Starting backup of {filesFound:N0} files to set #{set}...");
        _updateProgress(0, filesFound, 0);
    }

    public void BatchEndStatistics(int set, int filesProcessed, int filesFailed, long bytesProcessed)
    {
        FilesProcessed = filesProcessed;
        FilesFailed = filesFailed;
        BytesProcessed = bytesProcessed;

        _logMessage($"iii Backup batch complete: {filesProcessed - filesFailed:N0} succeeded, {filesFailed:N0} failed");
        _updateProgress(filesProcessed, FilesTotal, bytesProcessed);
    }

    public bool PreProcessFile(ref TapeFileDescriptor fileDescr)
    {
        _logMessage($" ii Backing up: {fileDescr.FullName}");
        return true;
    }

    public bool PostProcessFile(ref TapeFileDescriptor fileDescr)
    {
        FilesSucceeded++;
        FilesProcessed++;
        BytesProcessed += fileDescr.Length;

        _logMessage($"vvv OK: {System.IO.Path.GetFileName(fileDescr.FullName)} ({Windows.Win32.System.SystemServices.Helpers.BytesToString(fileDescr.Length)})");
        _updateProgress(FilesProcessed, FilesTotal, BytesProcessed);

        return true;
    }

    public FileFailedAction OnFileFailed(TapeFileDescriptor fileDescr, Exception ex)
    {
        FilesFailed++;
        FilesProcessed++;

        _logMessage($"!!! Failed: {fileDescr.FullName}");
        _logMessage($"    Error: {ex.Message}");

        _updateProgress(FilesProcessed, FilesTotal, BytesProcessed);

        // If user chose to skip all errors, don't show dialog
        if (_skipAllErrors)
            return FileFailedAction.Skip;

        // Show error dialog on UI thread
        FileFailedAction result = FileFailedAction.Skip;
        bool applyToAll = false;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new FileErrorDialog(fileDescr.FullName, ex.Message)
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true)
            {
                result = dialog.Result;
                applyToAll = dialog.ApplyToAll;
            }
        });

        if (applyToAll && result == FileFailedAction.Skip)
        {
            _skipAllErrors = true;
        }

        if (result == FileFailedAction.Abort)
        {
            _logMessage("!!! Backup aborted by user");
        }

        return result;
    }

    public void OnFileSkipped(TapeFileDescriptor fileDescr)
    {
        _logMessage($" -- Skipped: {System.IO.Path.GetFileName(fileDescr.FullName)}");
    }
}