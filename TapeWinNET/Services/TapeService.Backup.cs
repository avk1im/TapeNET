using System.IO;

using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeWinNET.Converters;

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
    /// <param name="emergencyTocSaveCallback">Callback when all attempts to save TOC to tape fail.
    /// Receives a suggested file path for emergency TOC export; returns the chosen path, or null to skip.
    /// If null, no emergency export is attempted.</param>
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
        Action<LogEntry> logCallback,
        Func<string, string, FileFailedAction> fileErrorCallback,
        Func<int, int, int, int, long, bool> volumeFullCallback,
        Func<int, bool> insertMediaCallback,
        Action<string> currentFileCallback,
        Func<string, string?>? emergencyTocSaveCallback = null)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                // Local log helpers for concise structured logging
#pragma warning disable CS8321 // some log helpers not used (yet), but might be later
                void log(string msg)           => logCallback(new LogEntry(WarningLevel.None, msg, false, DateTime.Now));
                void logOk(string msg)         => logCallback(new LogEntry(WarningLevel.Completed, msg, false, DateTime.Now));
                void logOkSub(string msg)      => logCallback(new LogEntry(WarningLevel.Completed, msg, true, DateTime.Now));
                void logInfo(string msg)       => logCallback(new LogEntry(WarningLevel.Info, msg, false, DateTime.Now));
                void logInfoSub(string msg)    => logCallback(new LogEntry(WarningLevel.Info, msg, true, DateTime.Now));
                void logFail(string msg)       => logCallback(new LogEntry(WarningLevel.Failed, msg, false, DateTime.Now));
                void logFailSub(string msg)    => logCallback(new LogEntry(WarningLevel.Failed, msg, true, DateTime.Now));
                void logErr(string msg)        => logCallback(new LogEntry(WarningLevel.Error, msg, false, DateTime.Now));
#pragma warning restore CS8321

                if (_drive == null || !_drive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    throw new InvalidOperationException("Media not loaded");
                }

                if (fileList.Count == 0)
                {
                    logInfo("No files to backup");
                    return;
                }

                try
                {
                    logInfo("Preparing media for backup...");
                    Status("Preparing backup...");

                    if (!_drive.PrepareMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        throw new InvalidOperationException($"Couldn't prepare media: {LastError}");
                    }

                    // Determine if we need to append or overwrite
                    bool append = appendMode && _toc != null;

                    // --- TOC preparation ---
                    // Three modes:
                    //  1. Append after specific set: backup copy of TOC for rollback, empty the
                    //     target set slot, reuse it (newSet=false). Sets after it are removed on success.
                    //  2. Straight append: add a new set after existing ones (newSet=true),
                    //     or reuse the last set if it's empty (newSet=false).
                    //  3. Overwrite: remove all existing sets, write from scratch (newSet=true).

                    // Create backup agent with existing TOC if appending, store in _tapeAgent field
                    _agent?.Dispose();
                    _agent = new TapeFileBackupAgent(_drive, _toc);
                    var agent = (TapeFileBackupAgent)_agent;
                    var toc = agent.TOC;
                    TapeTOC? backupTOC = null;
                    bool appendAfterSetUsed = false;
                    appendAfterSetIndex = toc.SetIndexToStd(appendAfterSetIndex); // ensure std form

                    // Mode 1: Append after specific set — save TOC copy for rollback
                    if (append && appendAfterSetIndex > toc.FirstSetOnVolume && appendAfterSetIndex < toc.LastSetOnVolume)
                    {
                        logInfo($"Appending after backup set #{appendAfterSetIndex} | {toc.SetIndexToAlt(appendAfterSetIndex)}");
                        backupTOC = new TapeTOC(toc);
                        appendAfterSetUsed = true;
                        toc.CurrentSetIndex = appendAfterSetIndex + 1;
                        toc.EmptyCurrentSet(); // clear the target slot; will trigger newSet=false below
                    }
                    // Mode 3: Overwrite — save TOC copy for rollback
                    else if (!append)
                    {
                        logInfo("Creating new backup, replacing all existing content");
                        backupTOC = new TapeTOC(toc);
                        toc.RemoveAllSets();
                    }
                    // else: Mode 2 — straight append (no TOC modification needed here)

                    // Set up media description if empty
                    if (string.IsNullOrEmpty(toc.Description))
                    {
                        toc.Description = $"Media created {DateTime.Now}";
                    }

                    // Determine if a new set was added or an existing empty slot is reused
                    bool newSet;
                    if (append)
                    {
                        if (toc.CurrentSetTOC.Count > 0)
                        {
                            toc.AddNewSetTOC(0, incremental); // straight append: add new set
                            newSet = true;
                        }
                        else
                        {
                            toc.MarkCurrentSetIncremental(incremental); // reuse emptied slot (mode 1)
                            newSet = false;
                        }
                    }
                    else
                    {
                        newSet = true; // overwrite: entire TOC created anew
                    }

                    // Configure the new backup set
                    toc.CurrentSetTOC.Description = description;
                    toc.CurrentSetTOC.HashAlgorithm = hashAlgorithm;
                    toc.CurrentSetTOC.BlockSize = blockSize;
                    toc.CurrentSetTOC.FmksMode = useFilemarks;

                    logInfo($"Backup set: >{description}<");
                    logInfoSub($"Block size: {Helpers.BytesToString(blockSize)}");
                    logInfoSub($"Hash algorithm: {hashAlgorithm}");
                    logInfoSub($"Incremental: {(incremental ? "Yes" : "No")}");
                    if (listContainsPatterns)
                        logInfoSub($"Patterns / directories to backup: {fileList.Count:N0}");
                    else
                        logInfoSub($"Files to backup: {fileList.Count:N0}");

                    // Create progress handler (uses agent.IsAbortRequested for abort checking)
                    var progressHandler = new GuiBackupProgressHandler(
                        agent,
                        logCallback,
                        progressCallback,
                        currentFileCallback,
                        fileErrorCallback);

                    // --- Backup loop (handles multi-volume) ---
                    // After each iteration:
                    //  result=true  → all files processed successfully
                    //  result=false → abort, volume full (CanResumeToNextVolume), or hard failure
                    // In all cases, the TOC must be cleaned up and saved to tape.
                    bool wasAborted = false;
                    do
                    {
                        Status($"Backing up files...");

                        bool result = agent.CanResumeToNextVolume
                            ? agent.ResumeBackupToNextVolume()
                            : listContainsPatterns ?
                                agent.BackupFilesToCurrentSet(newSet, fileList, includeSubdirectories, ignoreFailures: true, progressHandler)
                                : agent.BackupFileListToCurrentSet(newSet, fileList, ignoreFailures: true, progressHandler);

                        // The agent catches TapeAbortRequestedException internally and returns false,
                        // so we detect abort via the flag rather than catching the exception.
                        wasAborted = agent.IsAbortRequested;

                        bool noFilesBackedUp = toc.CurrentSetTOC.Count == 0;

                        // --- TOC cleanup based on result ---

                        if (result)
                        {
                            // Success: trim sets after current if we replaced a middle set (mode 1)
                            if (appendAfterSetUsed)
                            {
                                toc.RemoveSetsAfterCurrent();
                            }

                            // Success but no files (e.g. incremental with no changes, no matching files):
                            // undo TOC modifications and return without saving TOC to tape
                            if (noFilesBackedUp)
                            {
                                logInfo("No files were backed up");
                                if (backupTOC != null)
                                    toc.CopyFrom(backupTOC); // restore original TOC
                                else
                                    toc.RemoveLastEmptySet();
                                _toc = toc;
                                return;
                            }
                        }
                        else // Backup had issues
                        {
                            if (wasAborted)
                            {
                                // Abort: undo TOC modifications if no files were written;
                                // TOC will still be saved to tape below to preserve media integrity
                                if (noFilesBackedUp)
                                {
                                    if (backupTOC != null)
                                        toc.CopyFrom(backupTOC);
                                    else
                                        toc.RemoveLastEmptySet();
                                }
                            }
                            else if (agent.CanResumeToNextVolume)
                            {
                                // Volume full: trim trailing sets (mode 1), remove empty set if no files;
                                // TOC will be saved, then user is prompted to insert next volume
                                logInfo($"Volume #{toc.Volume} is full - backup can continue to next volume");

                                if (appendAfterSetUsed)
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
                                // Hard failure (no multi-volume resume possible)
                                if (noFilesBackedUp)
                                {
                                    // No files written: restore original TOC or remove empty set
                                    if (backupTOC != null)
                                    {
                                        toc.CopyFrom(backupTOC);
                                        logErr("No files backed up");
                                    }
                                    else
                                    {
                                        toc.RemoveLastEmptySet();
                                        logErr("No files backed up");
                                    }

                                    // If TOC on tape is still valid, skip re-saving it
                                    if (!agent.Navigator.TOCInvalidated)
                                    {
                                        _toc = toc;
                                        return;
                                    }
                                    // else: TOC on tape is stale → must save below
                                }
                                else
                                {
                                    logFail($"Some files failed to back up");
                                }
                            }
                        }

                        // --- Save TOC to tape ---
                        // If we wrote content and are not continuing to another volume,
                        // clear any stale multi-volume continuation flag from a previous session
                        // (e.g. user backed up onto a middle volume of an old multi-volume chain)
                        if (!noFilesBackedUp && !agent.CanResumeToNextVolume)
                            toc.ContinuedOnNextVolume = false;

                        if (!wasAborted)
                        {
                            Status("Saving TOC...");
                            logInfo("Backing up TOC...");
                        }
                        else
                        {
                            Status("Aborting — saving TOC...");
                            logInfo("Abort requested — saving TOC to preserve media integrity...");
                        }

                        if (!agent.BackupTOC())
                        {
                            logErr($"Couldn't backup TOC. Error: {agent.LastErrorMessage}");
                            logInfo("Attempting to enforce TOC backup...");

                            if (!agent.BackupTOC(enforce: true))
                            {
                                logErr("Couldn't enforce TOC backup");

                                if (emergencyTocSaveCallback != null)
                                {
                                    logInfo("Attempting to export TOC to file as emergency recovery...");
                                    Status("Emergency TOC export...");

                                    bool emergencySaved = false;
                                    string suggestedPath = BuildEmergencyTocExportPath(toc);
                                    // give the user two attempts to save the TOC to a file; break if user cancels
                                    for (int attempt = 1; attempt <= 2 && !emergencySaved; attempt++)
                                    {
                                        string? chosenPath = emergencyTocSaveCallback(suggestedPath);

                                        if (string.IsNullOrEmpty(chosenPath))
                                        {
                                            logErr("User declined emergency TOC export");
                                            break;
                                        }

                                        if (agent.SaveTOCToFile(chosenPath))
                                        {
                                            logOk($"Emergency TOC exported to: {chosenPath}");
                                            logInfoSub("This file can be used to recover access to the media content");
                                            IsTOCFromFile = true;
                                            TOCFilePath = chosenPath;
                                            emergencySaved = true;
                                        }
                                        else
                                        {
                                            logErr($"Failed to export emergency TOC to file: {agent.LastErrorMessage}");
                                            if (attempt < 2)
                                                logInfoSub("You can try a different location...");
                                        }
                                    }

                                    if (!emergencySaved)
                                    {
                                        throw new InvalidOperationException(
                                            "TOC backup failed — media TOC is lost. " +
                                            "The backed-up files are on the media but cannot be accessed without a TOC.");
                                    }
                                }
                                else
                                {
                                    throw new InvalidOperationException($"Couldn't enforce TOC backup: {agent.LastErrorMessage}");
                                }
                            }
                            else
                            {
                                logOk("Enforced TOC backup succeeded");
                            }
                        }
                        else
                        {
                            logOk("TOC backed up successfully");
                        }

                        _toc = toc; // Update service TOC reference

                        // Log results for this volume
                        logOk($"Backed up {progressHandler.FilesSucceeded:N0} file(s) of {progressHandler.FilesProcessed:N0}, {Helpers.BytesToString(agent.BytesBackedup)}");
                        if (!result && progressHandler.FilesFailed > 0)
                        {
                            logFail($"{progressHandler.FilesFailed:N0} file(s) of {progressHandler.FilesProcessed:N0} failed to back up");
                        }
                        logOkSub($"Remaining media capacity: {Helpers.BytesToStringLong(_drive.GetContentRemainingCapacity())}");

                        // If backup was aborted, TOC has been saved — break out
                        if (wasAborted)
                            break;

                        // Check if we need to continue with multi-volume
                        if (!agent.CanResumeToNextVolume)
                            break; // Done

                        // Step 1: Ask user if they want to continue on a new volume
                        bool continueBackup = volumeFullCallback(
                            toc.Volume,
                            toc.Volume + 1,
                            progressHandler.FilesProcessed,
                            progressHandler.FilesTotal,
                            agent.BytesBackedup);

                        if (!continueBackup)
                        {
                            logInfo("User chose to end multi-volume backup");
                            break;
                        }

                        // Step 2: Eject current media
                        logInfo("Ejecting media...");
                        Status("Ejecting media...");

                        if (!_drive.UnloadMedia())
                        {
                            throw new InvalidOperationException($"Couldn't eject media: {_drive.LastErrorMessage}");
                        }

                        logOk($"Volume #{toc.Volume} ejected");

                        // Step 3: Ask user to insert new media
                        if (!insertMediaCallback(toc.Volume + 1))
                        {
                            logInfo("User cancelled media insertion");
                            break;
                        }

                        // Step 4: Load and prepare the new media
                        logInfo("Loading media...");
                        Status("Loading media...");

                        if (!_drive.ReloadMedia())
                        {
                            throw new InvalidOperationException($"Couldn't load media: {_drive.LastErrorMessage}");
                        }

                        if (!_drive.PrepareMedia())
                        {
                            throw new InvalidOperationException($"Couldn't prepare media: {_drive.LastErrorMessage}");
                        }

                        logOk("Media loaded, continuing backup...");

                    } while (true);

                    if (wasAborted)
                    {
                        logOk("TOC saved after abort");
                        Status("Backup aborted");
                        throw new TapeAbortRequestedException("User requested abort");
                    }

                    Status("Backup complete");
                    logOk("Backup completed successfully");
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
                    logErr($"Backup failed: {ex.Message}");
                    throw;
                }
                finally
                {
                    _agent?.Dispose();
                    _agent = null;
                }
            }
        });
    }

    private static string BuildEmergencyTocExportPath(TapeTOC toc)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            (toc.Description ?? "tape").Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "tape";

        // Limit length to avoid path issues
        if (sanitized.Length > 60)
            sanitized = sanitized[..60];

        var fileName = $"{sanitized}_vol{toc.Volume}_{DateTime.Now:yyyyMMdd_HHmmss}{TapeFileAgent.TOCFileExtension}";
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(directory, fileName);
    }

    #region Helper Classes

    /// <summary>
    /// Progress handler for GUI backup operations.
    /// Implements ITapeFileNotifiable to bridge between TapeFileBackupAgent and the UI.
    /// All statistics come from the library via <see cref="TapeFileStatistics"/>.
    /// </summary>
    private class GuiBackupProgressHandler(
        TapeFileAgent agent,
        Action<LogEntry> logCallback,
        Action<int, int, long> progressCallback,
        Action<string> currentFileCallback,
        Func<string, string, FileFailedAction> fileErrorCallback) : ITapeFileNotifiable
    {
        private readonly TapeFileAgent _agent = agent;
        private readonly Action<LogEntry> _logCallback = logCallback;
        private readonly Action<int, int, long> _progressCallback = progressCallback;
        private readonly Action<string> _currentFileCallback = currentFileCallback;
        private readonly Func<string, string, FileFailedAction> _fileErrorCallback = fileErrorCallback;

        // Convenience accessors — always reflect the latest library snapshot
        public int FilesProcessed { get; private set; }
        public int FilesTotal { get; private set; }
        public int FilesFailed { get; private set; }
        public int FilesSucceeded { get; private set; }
        public long BytesProcessed { get; private set; }

        private bool _abortLogged;

        private void Log(WarningLevel level, string msg, bool sub = false)
            => _logCallback(new LogEntry(level, msg, sub, DateTime.Now));

        private void Sync(in TapeFileStatistics stats)
        {
            FilesTotal = stats.FilesTotal;
            FilesProcessed = stats.FilesProcessed;
            FilesSucceeded = stats.FilesSucceeded;
            FilesFailed = stats.FilesFailed;
            BytesProcessed = stats.BytesProcessed;
        }

        private void ThrowIfAbortRequested()
        {
            if (_agent.IsAbortRequested)
            {
                if (!_abortLogged)
                {
                    _abortLogged = true;
                    Log(WarningLevel.Warning, "Backup abort requested");
                }
                throw new TapeAbortRequestedException("User requested abort");
            }
        }

        public void BatchStart(int setIndex, in TapeFileStatistics stats)
        {
            Sync(stats);
            Log(WarningLevel.Info, $"Starting backup of {stats.FilesTotal:N0} files to set #{setIndex}...");
            _progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);
        }

        public void BatchEnd(int setIndex, in TapeFileStatistics stats)
        {
            Sync(stats);
            Log(WarningLevel.Info, $"Backup batch complete: {stats.FilesSucceeded:N0} succeeded, {stats.FilesFailed:N0} failed");
            _progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);
        }

        public bool PreProcessFile(ref TapeFileDescriptor fileDescr, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            _currentFileCallback(fileDescr.FullName);
            return true;
        }

        public bool PostProcessFile(ref TapeFileDescriptor fileDescr, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            Sync(stats);

            Log(WarningLevel.Completed, $"{Path.GetFileName(fileDescr.FullName)} ({Helpers.BytesToString(fileDescr.Length)})");
            _progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);

            return true;
        }

        public FileFailedAction OnFileFailed(TapeFileDescriptor fileDescr, Exception ex, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            Sync(stats);

            Log(WarningLevel.Failed, $"Failed: {fileDescr.FullName}");
            Log(WarningLevel.Failed, $"Error: {ex.Message}", sub: true);

            _progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);

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
                if (!_abortLogged)
                {
                    _abortLogged = true;
                    Log(WarningLevel.Warning, "Backup abort requested");
                }
                throw new TapeAbortRequestedException("User requested abort");
            }

            return result;
        }

        public void OnFileSkipped(TapeFileDescriptor fileDescr, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            Sync(stats);

            Log(WarningLevel.None, $"Skipped: {Path.GetFileName(fileDescr.FullName)}", sub: true);
        }
    }

    #endregion
}
