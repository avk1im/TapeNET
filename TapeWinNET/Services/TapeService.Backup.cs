using System.IO;

using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeWinNET.Converters;
using TapeWinNET.Models;

namespace TapeWinNET.Services;

/// <summary>
/// Summary statistics returned by a backup operation.
/// Allows the caller to distinguish full success, partial failure, abort,
///  and "no files backed up" without cross-boundary exceptions.
/// </summary>
public record BackupOperationResult(
    int FilesTotal,
    int FilesProcessed,
    int FilesSucceeded,
    int FilesFailed,
    int FilesSkipped,
    long BytesWritten)
{
    /// <summary>Whether the user aborted the operation.</summary>
    public bool WasAborted { get; init; }

    /// <summary>Whether the operation completed without any issues.</summary>
    public bool IsFullSuccess => !WasAborted && FilesFailed == 0 && FilesSkipped == 0 && FilesProcessed > 0;
}

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
    public Task<BackupOperationResult> ExecuteBackupAsync(
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
                GuiBackupProgressHandler? progressHandler = null;
                TapeFileBackupAgent? agent = null;

                // Default result for early-exit paths
                BackupOperationResult MakeResult(bool aborted = false) => new(
                    progressHandler?.FilesTotal ?? 0,
                    progressHandler?.FilesProcessed ?? 0,
                    progressHandler?.FilesSucceeded ?? 0,
                    progressHandler?.FilesFailed ?? 0,
                    progressHandler?.FilesSkipped ?? 0,
                    agent?.BytesBackedup ?? 0)
                { WasAborted = aborted };

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
                    return MakeResult();
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
                    agent = (TapeFileBackupAgent)_agent;
                    var toc = agent.TOC;
                    TapeTOC? backupTOC = null;
                    bool appendAfterSetUsed = false;
                    appendAfterSetIndex = toc.SetIndexToStd(appendAfterSetIndex); // ensure std form

                    // Capacity hint for the new set's internal list — use the known file count
                    //  for non-incremental, non-pattern backups; otherwise leave at 0 (unknown)
                    int capacityHint = !incremental && !listContainsPatterns ? fileList.Count : 0;

                    // Mode 1: Append after specific set — save TOC copy for rollback
                    if (append && appendAfterSetIndex > toc.FirstSetOnVolume && appendAfterSetIndex < toc.LastSetOnVolume)
                    {
                        logInfo($"Appending after backup set #{appendAfterSetIndex} | {toc.SetIndexToAlt(appendAfterSetIndex)}");
                        backupTOC = new TapeTOC(toc);
                        appendAfterSetUsed = true;
                        toc.CurrentSetIndex = appendAfterSetIndex + 1;
                        toc.ReplaceCurrentSetTOC(capacityHint, incremental); // replace with fresh set; will trigger newSet=false below
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
                            toc.AddNewSetTOC(capacityHint, incremental); // straight append: add new set
                            newSet = true;
                        }
                        else
                        {
                            toc.MarkCurrentSetIncremental(incremental); // reuse replaced slot (mode 1)
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
                        logInfoSub($"Patterns / folders to backup: {fileList.Count:N0}");
                    else
                        logInfoSub($"Files to backup: {fileList.Count:N0}");

                    // Create progress handler (uses agent.IsAbortRequested for abort checking)
                    progressHandler = new GuiBackupProgressHandler(
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

                    // Timing — accumulate data and TOC times separately across multi-volume iterations;
                    //  user interaction time between volumes is excluded
                    var dataTimer = new Stopwatch();
                    var tocTimer = new Stopwatch();
                    long dataElapsedUs = 0;
                    long tocElapsedUs = 0;

                    do
                    {
                        Status($"Backing up files...");

                        dataTimer.Restart();
                        bool result = agent.CanResumeToNextVolume
                            ? agent.ResumeBackupToNextVolume()
                            : listContainsPatterns ?
                                agent.BackupFilesToCurrentSet(newSet, fileList, includeSubdirectories, ignoreFailures: true, progressHandler)
                                : agent.BackupFileListToCurrentSet(newSet, fileList, ignoreFailures: true, progressHandler);
                        dataTimer.Stop();
                        dataElapsedUs += dataTimer.ElapsedMicroseconds;

                        // The agent catches TapeAbortRequestedException internally and returns false,
                        // so we detect abort via the flag rather than catching the exception.
                        wasAborted = agent.IsAbortRequested;

                        bool noFilesBackedUp = toc.CurrentSetTOC.Count == 0;
                        bool skipTOCSave = false;

                        // --- TOC cleanup based on result ---

                        // 1. Handle "no files backed up" uniformly, regardless of outcome.
                        //    The structural TOC repair is the same in every case:
                        //     - If we have a rollback TOC and nothing was physically written,
                        //       restore the original TOC (safe revert).
                        //     - If content was physically written (partial file I/O) but no file
                        //       completed, the old sets' data may be overwritten — keep the
                        //       (empty) new set and trim stale trailing sets.
                        //     - If there's no rollback TOC, just remove the empty trailing set.
                        //    skipTOCSave is set when the tape's TOC is still valid AND we're not
                        //    continuing to the next volume.
                        if (noFilesBackedUp)
                        {
                            if (backupTOC != null)
                            {
                                if (!agent.Manager.ContentWritten)
                                {
                                    toc.CopyFrom(backupTOC); // safe revert
                                    if (!result && !wasAborted && !agent.CanResumeToNextVolume)
                                        logErr("No files backed up");
                                    else
                                        logInfo("No files were backed up");
                                }
                                else
                                {
                                    // Content was physically written (partial file) —
                                    //  cannot revert, old sets' data may be overwritten.
                                    //  Keep the (empty) new set; trim trailing sets if mode 1.
                                    if (appendAfterSetUsed)
                                        toc.RemoveSetsAfterCurrent();
                                    logErr("No files backed up — previous set data may be lost");
                                }
                            }
                            else
                            {
                                toc.RemoveLastEmptySet();
                                if (!result && !wasAborted && !agent.CanResumeToNextVolume)
                                    logErr("No files backed up");
                                else
                                    logInfo("No files were backed up");
                            }

                            // If TOC on tape is still valid and we're not continuing to
                            //  the next volume, we can skip re-saving it
                            if (!agent.CanResumeToNextVolume && !agent.Navigator.TOCInvalidated)
                            {
                                skipTOCSave = true;
                                _toc = toc;
                            }
                        } // if (noFilesBackedUp)

                        // 2. Log volume-full status (applies regardless of file count)
                        if (agent.CanResumeToNextVolume)
                            logInfo($"Volume #{toc.Volume} is full - backup can continue to next volume");

                        // 3. Handle outcome-specific cleanup when files were backed up:
                        //    trim stale trailing sets (mode 1) and log failure summary.
                        //    When noFilesBackedUp, all TOC repair was already done above.
                        if (!noFilesBackedUp)
                        {
                            if (appendAfterSetUsed)
                                toc.RemoveSetsAfterCurrent();

                            if (!result && !wasAborted && !agent.CanResumeToNextVolume)
                                logFail("Some files failed to back up");
                        }

                        // --- Save TOC to tape ---
                        // If we wrote content and are not continuing to another volume,
                        // clear any stale multi-volume continuation flag from a previous session
                        // (e.g. user backed up onto a middle volume of an old multi-volume chain)
                        if (!noFilesBackedUp && !agent.CanResumeToNextVolume)
                        {
                            if (toc.ContinuedOnNextVolume)
                            { 
                                toc.ContinuedOnNextVolume = false;
                                skipTOCSave = false; // must save to clear the flag on tape
                            }
                        }

                        if (!skipTOCSave)
                        {
                            tocTimer.Restart();

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

                            var tocResult = agent.BackupTOC();
                            if (!tocResult)
                            {
                                logErr($"Couldn't backup TOC. Error: {tocResult.ErrorMessage}");
                                logInfo("Attempting to enforce TOC backup...");

                                var enforceResult = agent.BackupTOC(enforce: true);
                                if (!enforceResult)
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

                                            var saveResult = agent.SaveTOCToFile(chosenPath);
                                            if (saveResult)
                                            {
                                                logOk($"Emergency TOC exported to: {chosenPath}");
                                                logInfoSub("This file can be used to recover access to the media content");
                                                IsTOCFromFile = true;
                                                TOCFilePath = chosenPath;
                                                emergencySaved = true;
                                            }
                                            else
                                            {
                                                logErr($"Failed to export emergency TOC to file: {saveResult.ErrorMessage}");
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
                                        throw new InvalidOperationException($"Couldn't enforce TOC backup: {enforceResult.ErrorMessage}");
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

                            tocTimer.Stop();
                            tocElapsedUs += tocTimer.ElapsedMicroseconds;

                            _toc = toc; // Update service TOC reference
                        } // if (!skipTOCSave)

                        // Log results for this volume — headline level + uniform stats
                        WarningLevel headlineLevel;
                        string headlineMsg;
                        if (progressHandler.FilesFailed > 0)
                        {
                            headlineLevel = WarningLevel.Failed;
                            headlineMsg = $"Backed up {progressHandler.FilesTotal:N0} file(s) with {progressHandler.FilesFailed:N0} failed";
                        }
                        else if (progressHandler.FilesSkipped > 0)
                        {
                            headlineLevel = WarningLevel.Warning;
                            headlineMsg = $"Backed up {progressHandler.FilesTotal:N0} file(s) with {progressHandler.FilesSkipped:N0} skipped";
                        }
                        else
                        {
                            headlineLevel = WarningLevel.Completed;
                            headlineMsg = $"Backed up {progressHandler.FilesTotal:N0} file(s) successfully";
                        }
                        logCallback(new LogEntry(headlineLevel, headlineMsg, false, DateTime.Now));

                        // Uniform stats sub-line
                        if (progressHandler.FilesProcessed > 0)
                        {
                            var parts = new List<string>(4) { $"{progressHandler.FilesSucceeded:N0} succeeded" };
                            if (progressHandler.FilesFailed > 0) parts.Add($"{progressHandler.FilesFailed:N0} failed");
                            if (progressHandler.FilesSkipped > 0) parts.Add($"{progressHandler.FilesSkipped:N0} skipped");
                            parts.Add($"{Helpers.BytesToString(agent.BytesBackedup)} written");
                            logInfoSub(string.Join(", ", parts));

                            // Timing sub-line: elapsed time, data rate, and TOC save time
                            double dataSecs = dataElapsedUs / 1e6;
                            double tocSecs = tocElapsedUs / 1e6;
                            var timingParts = new List<string>(3) { FormatElapsed(dataSecs) };
                            string rate = FormatDataRate(agent.BytesBackedup, dataSecs);
                            if (rate.Length > 0) timingParts.Add(rate);
                            if (tocSecs >= 1.0) timingParts.Add($"TOC save {FormatElapsed(tocSecs)}");
                            logInfoSub(string.Join(", ", timingParts));
                        }
                        logInfoSub($"Remaining media capacity: {Helpers.BytesToStringLong(_drive.GetContentRemainingCapacity())}");

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
                        // Log timing even on abort
                        double abortDataSecs = dataElapsedUs / 1e6;
                        var abortParts = new List<string>(3)
                        {
                            $"Before abort: {Helpers.BytesToString(agent.BytesBackedup)} written"
                        };
                        string abortRate = FormatDataRate(agent.BytesBackedup, abortDataSecs);
                        if (abortRate.Length > 0) abortParts.Add(abortRate);
                        logInfoSub(string.Join(", ", abortParts));
                        Status("Backup aborted");
                        return MakeResult(aborted: true);
                    }

                    Status("Backup complete");
                    logOk("Backup completed successfully");
                    return MakeResult();
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
                [.. (toc.Description ?? "tape").Select(c => invalidChars.Contains(c) ? '_' : c)]
            ).Trim();

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
    /// Per-batch statistics are derived as deltas from successive snapshots.
    /// </summary>
    private class GuiBackupProgressHandler(
        TapeFileAgent agent,
        Action<LogEntry> logCallback,
        Action<int, int, long> progressCallback,
        Action<string> currentFileCallback,
        Func<string, string, FileFailedAction> fileErrorCallback) : ITapeFileNotifiable
    {
        // Convenience accessors — always reflect the latest library snapshot
        public int FilesProcessed { get; private set; }
        public int FilesTotal { get; private set; }
        public int FilesFailed { get; private set; }
        public int FilesSucceeded { get; private set; }
        public int FilesSkipped { get; private set; }
        public long BytesProcessed { get; private set; }

        /// <summary>Snapshot taken at each BatchStart for computing per-batch deltas.</summary>
        private TapeFileStatistics _batchStartSnapshot;
        private bool _abortLogged;

        private void Log(WarningLevel level, string msg, bool sub = false)
            => logCallback(new LogEntry(level, msg, sub, DateTime.Now));

        private void Sync(in TapeFileStatistics stats)
        {
            FilesTotal = stats.FilesTotal;
            FilesProcessed = stats.FilesProcessed;
            FilesSucceeded = stats.FilesSucceeded;
            FilesFailed = stats.FilesFailed;
            FilesSkipped = stats.FilesSkipped;
            BytesProcessed = stats.BytesProcessed;
        }

        private void ThrowIfAbortRequested()
        {
            if (agent.IsAbortRequested)
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
            _batchStartSnapshot = stats; // capture snapshot for computing per-batch deltas in BatchEnd
            Sync(stats);
            var toc = agent.TOC;

            Log(WarningLevel.Info, $"Set #{setIndex} | {toc.SetIndexToAlt(setIndex)}: starting backup...");
            progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);
        }

        public void BatchEnd(int setIndex, in TapeFileStatistics stats)
        {
            Sync(stats);
            var toc = agent.TOC;

            // Compute per-batch statistics as delta from the snapshot taken at BatchStart
            var batch = stats.Delta(in _batchStartSnapshot);

            // Build a concise per-batch summary
            var level = batch.FilesFailed > 0 ? WarningLevel.Failed
                      : batch.FilesSkipped > 0 ? WarningLevel.Warning
                      : WarningLevel.Completed;
            var parts = new List<string>(3) { $"{batch.FilesSucceeded:N0} succeeded" };
            if (batch.FilesFailed > 0) parts.Add($"{batch.FilesFailed:N0} failed");
            if (batch.FilesSkipped > 0) parts.Add($"{batch.FilesSkipped:N0} skipped");

            Log(level, $"Set #{setIndex} | {toc.SetIndexToAlt(setIndex)} complete: {string.Join(", ", parts)}");
            progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);
        }

        public bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            currentFileCallback(fileInfo.FileDescr.FullName);
            return true;
        }

        public bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            Sync(stats);

            Log(WarningLevel.Completed, $"'{Path.GetFileName(fileInfo.FileDescr.FullName)}' {Helpers.BytesToString(fileInfo.FileDescr.Length)}", sub: true);
            progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);

            return true;
        }

        public FileFailedAction OnFileFailed(TapeFileInfo fileInfo, TapeResult result, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            Sync(stats);

            Log(WarningLevel.Failed, $"Failed: '{fileInfo.FileDescr.FullName}'");
            Log(WarningLevel.Failed, $"Error: {result.ErrorMessage}", sub: true);

            progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);

            // Don't show file error dialog for end-of-media errors —
            // the multi-volume logic in BackupFilesToCurrentSet handles these
            if (result.ErrorCode == (uint)WIN32_ERROR.ERROR_END_OF_MEDIA ||
                result.ErrorCode == (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED)
            {
                return FileFailedAction.Skip;
            }

            // Show error dialog via callback - the callback handles sticky choices (e.g. Skip All)
            var action = fileErrorCallback(fileInfo.FileDescr.FullName, result.ErrorMessage);

            if (action == FileFailedAction.Abort)
            {
                if (!_abortLogged)
                {
                    _abortLogged = true;
                    Log(WarningLevel.Warning, "Backup abort requested");
                }
                throw new TapeAbortRequestedException("User requested abort");
            }

            return action;
        }

        public void OnFileSkipped(TapeFileInfo fileInfo, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            Sync(stats);

            Log(WarningLevel.None, $"Skipped: {Path.GetFileName(fileInfo.FileDescr.FullName)}", sub: true);
        }
    }

    #endregion
}
