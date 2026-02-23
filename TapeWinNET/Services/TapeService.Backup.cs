using System.IO;

using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;

namespace TapeWinNET.Services;

/// <summary>
/// Partial class containing backup-related operations for TapeService.
/// </summary>
public partial class TapeService
{
    /// <summary>
    /// Executes a backup operation with the specified parameters.
    /// </summary>
    /// <param name="fileList">List of files to backup.</param>
    /// <param name="listContainsPatterns">List of files may contain patterns and directories.</param>
    /// <param name="description">Description for the new backup set.</param>
    /// <param name="includeSubdirectories">Whether to include subdirectories.</param>
    /// <param name="incremental">Whether this is an incremental backup.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="hashAlgorithm">Hash algorithm to use.</param>
    /// <param name="appendMode">True to append after existing set, false to overwrite all.</param>
    /// <param name="appendAfterSetIndex">Set index to append after (-1 for overwrite all, or specific set index).</param>
    /// <param name="useFilemarks">Whether to use filemarks between files (blob mode if false).</param>
    /// <param name="progressCallback">Callback for progress updates (filesProcessed, totalFiles, bytesProcessed).</param>
    /// <param name="logCallback">Callback for log messages.</param>
    /// <param name="fileErrorCallback">Callback when a file error occurs. Returns FileFailedAction.</param>
    /// <param name="volumeFullCallback">Callback when current volume is full. Returns true to continue on next volume, false to end backup.</param>
    /// <param name="insertMediaCallback">Callback after media ejection, prompting user to insert new media. Returns true when ready, false to end backup.</param>
    /// <param name="currentFileCallback">Callback to report current file being processed.</param>
    /// <remarks>
    /// To abort a backup in progress, set Agent.IsAbortRequested = true.
    /// The backup agent is available via the Agent property during execution.
    /// </remarks>
    public Task ExecuteBackupAsync(
        List<string> fileList,
        bool listContainsPatterns,
        string description,
        bool includeSubdirectories,
        bool incremental,
        uint blockSize,
        TapeHashAlgorithm hashAlgorithm,
        bool appendMode,
        int appendAfterSetIndex,
        bool useFilemarks,
        Action<int, int, long> progressCallback,
        Action<string> logCallback,
        Func<string, string, FileFailedAction> fileErrorCallback,
        Func<int, int, int, int, long, bool> volumeFullCallback,
        Func<int, bool> insertMediaCallback,
        Action<string> currentFileCallback)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_drive == null || !_drive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    throw new InvalidOperationException("Media not loaded");
                }

                if (fileList.Count == 0)
                {
                    logCallback("iii No files to backup");
                    return;
                }

                try
                {
                    logCallback(">>> Preparing media for backup...");
                    Status("Preparing backup...");

                    if (!_drive.PrepareMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        throw new InvalidOperationException($"Couldn't prepare media: {LastError}");
                    }

                    // Determine if we need to append or overwrite
                    bool append = appendMode && _toc != null && _toc.Count > 0;

                    // Create backup agent with existing TOC if appending, store in _tapeAgent field
                    _agent?.Dispose();
                    _agent = new TapeFileBackupAgent(_drive, append ? _toc : null);
                    var agent = (TapeFileBackupAgent)_agent;
                    var toc = agent.TOC;

                    // Handle append after specific set
                    if (append && appendAfterSetIndex > 0 && appendAfterSetIndex < toc.Count)
                    {
                        logCallback($"iii Appending after backup set #{appendAfterSetIndex}");
                        toc.CurrentSetIndex = appendAfterSetIndex + 1;
                        toc.EmptyCurrentSet();
                    }
                    else if (!append)
                    {
                        logCallback("iii Creating new backup, replacing all existing content");
                        toc.RemoveAllSets();
                    }

                    // Set up media description if empty
                    if (string.IsNullOrEmpty(toc.Description))
                    {
                        toc.Description = $"Media created {DateTime.Now}";
                    }

                    // Determine if we need a new set
                    bool newSet;
                    if (append)
                    {
                        if (toc.CurrentSetTOC.Count > 0)
                        {
                            toc.AddNewSetTOC(0, incremental);
                            newSet = true;
                        }
                        else
                        {
                            toc.MarkCurrentSetIncremental(incremental);
                            newSet = false;
                        }
                    }
                    else
                    {
                        newSet = true;
                    }

                    // Configure the new backup set
                    toc.CurrentSetTOC.Description = description;
                    toc.CurrentSetTOC.HashAlgorithm = hashAlgorithm;
                    toc.CurrentSetTOC.BlockSize = blockSize;
                    toc.CurrentSetTOC.FmksMode = useFilemarks;

                    logCallback($"iii Backup set: >{description}<");
                    logCallback($" ii Block size: {Helpers.BytesToString(blockSize)}");
                    logCallback($" ii Hash algorithm: {hashAlgorithm}");
                    logCallback($" ii Incremental: {(incremental ? "Yes" : "No")}");
                    if (listContainsPatterns)
                        logCallback($" ii Patterns / directories to backup: {fileList.Count:N0}");
                    else
                        logCallback($" ii Files to backup: {fileList.Count:N0}");

                    // Create progress handler (uses agent.IsAbortRequested for abort checking)
                    var progressHandler = new GuiBackupProgressHandler(
                        agent,
                        logCallback,
                        progressCallback,
                        currentFileCallback,
                        fileErrorCallback);

                    // Execute backup loop (handles multi-volume)
                    do
                    {
                        Status($"Backing up files...");

                        bool result = agent.CanResumeToNextVolume
                            ? agent.ResumeBackupToNextVolume()
                            : listContainsPatterns ?
                                agent.BackupFilesToCurrentSet(newSet, fileList, includeSubdirectories, ignoreFailures: true, progressHandler)
                                : agent.BackupFileListToCurrentSet(newSet, fileList, ignoreFailures: true, progressHandler);

                        bool noFilesBackedUp = toc.CurrentSetTOC.Count == 0;

                        if (result)
                        {
                            // Handle append after set - remove sets after current
                            if (appendAfterSetIndex > 0 && appendAfterSetIndex < toc.Count)
                            {
                                toc.RemoveSetsAfterCurrent();
                            }

                            if (noFilesBackedUp)
                            {
                                logCallback("iii No files were backed up");
                                toc.RemoveLastEmptySet();
                                _toc = toc;
                                return;
                            }
                        }
                        else // Backup had issues
                        {
                            // Check for multi-volume continuation
                            if (agent.CanResumeToNextVolume)
                            {
                                logCallback($"iii Volume #{toc.Volume} is full - backup can continue to next volume");

                                if (appendAfterSetIndex > 0)
                                {
                                    toc.RemoveSetsAfterCurrent();
                                }

                                if (noFilesBackedUp)
                                {
                                    toc.RemoveLastEmptySet();
                                }
                            }
                            else
                            {
                                if (noFilesBackedUp)
                                {
                                    toc.RemoveLastEmptySet();
                                    logCallback("!!! No files backed up");

                                    if (!agent.Navigator.TOCInvalidated)
                                    {
                                        _toc = toc;
                                        return;
                                    }
                                }
                                else
                                {
                                    logCallback($"!!! Some files failed to back up");
                                }
                            }
                        }

                        // Backup TOC
                        Status("Saving TOC...");
                        logCallback(">>> Backing up TOC...");

                        if (!agent.BackupTOC())
                        {
                            logCallback($"!!! Couldn't backup TOC. Error: {agent.LastErrorMessage}");
                            logCallback(">>> Attempting to enforce TOC backup...");

                            if (!agent.BackupTOC(enforce: true))
                            {
                                throw new InvalidOperationException($"Couldn't enforce TOC backup: {agent.LastErrorMessage}");
                            }

                            logCallback("vvv Enforced TOC backup succeeded");
                        }
                        else
                        {
                            logCallback("vvv TOC backed up successfully");
                        }

                        _toc = toc; // Update service TOC reference

                        logCallback($"vvv Backed up {progressHandler.FilesSucceeded:N0} file(s), {Helpers.BytesToString(agent.BytesBackedup)}");
                        logCallback($" ii Remaining media capacity: {Helpers.BytesToStringLong(_drive.GetRemainingCapacity())}");

                        // Check if we need to continue with multi-volume
                        if (!agent.CanResumeToNextVolume)
                        {
                            break; // Done
                        }

                        // Step 1: Ask user if they want to continue on a new volume
                        bool continueBackup = volumeFullCallback(
                            toc.Volume,
                            toc.Volume + 1,
                            progressHandler.FilesProcessed,
                            progressHandler.FilesTotal,
                            agent.BytesBackedup);

                        if (!continueBackup)
                        {
                            logCallback("iii User chose to end multi-volume backup");
                            break;
                        }

                        // Step 2: Eject current media
                        logCallback(">>> Ejecting media...");
                        Status("Ejecting media...");

                        if (!_drive.UnloadMedia())
                        {
                            throw new InvalidOperationException($"Couldn't eject media: {_drive.LastErrorMessage}");
                        }

                        logCallback($"vvv Volume #{toc.Volume} ejected");

                        // Step 3: Ask user to insert new media
                        if (!insertMediaCallback(toc.Volume + 1))
                        {
                            logCallback("iii User cancelled media insertion");
                            break;
                        }

                        // Step 4: Load and prepare the new media
                        logCallback(">>> Loading media...");
                        Status("Loading media...");

                        if (!_drive.ReloadMedia())
                        {
                            throw new InvalidOperationException($"Couldn't load media: {_drive.LastErrorMessage}");
                        }

                        if (!_drive.PrepareMedia())
                        {
                            throw new InvalidOperationException($"Couldn't prepare media: {_drive.LastErrorMessage}");
                        }

                        logCallback("vvv Media loaded, continuing backup...");

                    } while (true);

                    Status("Backup complete");
                    logCallback("vvv Backup completed successfully");
                }
                catch (TapeAbortRequestedException)
                {
                    Status("Backup aborted");
                    throw; // Re-throw to be handled by caller
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Status("Backup failed");
                    logCallback($"!!! Backup failed: {ex.Message}");
                    throw;
                }
                finally
                {
                    // Clean up the backup agent
                    _agent?.Dispose();
                    _agent = null;
                }
            }
        });
    }

    #region Helper Classes

    /// <summary>
    /// Progress handler for GUI backup operations.
    /// Implements ITapeFileNotifiable to bridge between TapeFileBackupAgent and the UI.
    /// </summary>
    private class GuiBackupProgressHandler : ITapeFileNotifiable
    {
        private readonly TapeFileAgent _agent;
        private readonly Action<string> _logCallback;
        private readonly Action<int, int, long> _progressCallback;
        private readonly Action<string> _currentFileCallback;
        private readonly Func<string, string, FileFailedAction> _fileErrorCallback;

        public int FilesProcessed { get; private set; }
        public int FilesTotal { get; private set; }
        public int FilesFailed { get; private set; }
        public int FilesSucceeded { get; private set; }
        public long BytesProcessed { get; private set; }

        public GuiBackupProgressHandler(
            TapeFileAgent agent,
            Action<string> logCallback,
            Action<int, int, long> progressCallback,
            Action<string> currentFileCallback,
            Func<string, string, FileFailedAction> fileErrorCallback)
        {
            _agent = agent;
            _logCallback = logCallback;
            _progressCallback = progressCallback;
            _currentFileCallback = currentFileCallback;
            _fileErrorCallback = fileErrorCallback;
        }

        private void ThrowIfAbortRequested()
        {
            if (_agent.IsAbortRequested)
            {
                _logCallback("!!! Backup aborted by user");
                throw new TapeAbortRequestedException("User requested abort");
            }
        }

        public void BatchStartStatistics(int set, int filesFound)
        {
            FilesTotal = filesFound;
            FilesProcessed = 0;
            FilesFailed = 0;
            FilesSucceeded = 0;
            BytesProcessed = 0;

            _logCallback($"iii Starting backup of {filesFound:N0} files to set #{set}...");
            _progressCallback(0, filesFound, 0);
        }

        public void BatchEndStatistics(int set, int filesProcessed, int filesFailed, long bytesProcessed)
        {
            FilesProcessed = filesProcessed;
            FilesFailed = filesFailed;
            BytesProcessed = bytesProcessed;

            _logCallback($"iii Backup batch complete: {filesProcessed - filesFailed:N0} succeeded, {filesFailed:N0} failed");
            _progressCallback(filesProcessed, FilesTotal, bytesProcessed);
        }

        public bool PreProcessFile(ref TapeFileDescriptor fileDescr)
        {
            ThrowIfAbortRequested();

            _currentFileCallback(fileDescr.FullName);
            return true;
        }

        public bool PostProcessFile(ref TapeFileDescriptor fileDescr)
        {
            ThrowIfAbortRequested();

            FilesSucceeded++;
            FilesProcessed++;
            BytesProcessed += fileDescr.Length;

            _logCallback($"vvv {Path.GetFileName(fileDescr.FullName)} ({Helpers.BytesToString(fileDescr.Length)})");
            _progressCallback(FilesProcessed, FilesTotal, BytesProcessed);

            return true;
        }

        public FileFailedAction OnFileFailed(TapeFileDescriptor fileDescr, Exception ex)
        {
            ThrowIfAbortRequested();

            FilesFailed++;

            _logCallback($"!!! Failed: {fileDescr.FullName}");
            _logCallback($"    Error: {ex.Message}");

            _progressCallback(FilesProcessed, FilesTotal, BytesProcessed);

            // Don't show file error dialog for end-of-media errors —
            // the multi-volume logic in BackupFilesToCurrentSet handles these
            if (ex is IOException ioEx &&
                (ioEx.HResult == (int)WIN32_ERROR.ERROR_END_OF_MEDIA ||
                 ioEx.HResult == (int)WIN32_ERROR.ERROR_NO_DATA_DETECTED))
            {
                return FileFailedAction.Skip;
            }

            // Show error dialog via callback - the callback handles sticky choices (e.g. Skip All)
            var result = _fileErrorCallback(fileDescr.FullName, ex.Message);

            if (result == FileFailedAction.Abort)
            {
                _logCallback("!!! Backup aborted by user");
                throw new TapeAbortRequestedException("User requested abort");
            }

            return result;
        }

        public void OnFileSkipped(TapeFileDescriptor fileDescr)
        {
            ThrowIfAbortRequested();

            _logCallback($" -- Skipped: {Path.GetFileName(fileDescr.FullName)}");
        }
    }

    #endregion
}
