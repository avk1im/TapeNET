using System.IO;

using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeLibNET.Services;
using TapeWinNET.Converters;
using TapeWinNET.Models;

namespace TapeWinNET.Services;

/// <summary>
/// Partial class containing backup-related operations for TapeService.
/// </summary>
public partial class TapeService
{
    /// <summary>
    /// Executes a backup operation with the specified parameters.
    /// </summary>
    /// <param name="request">Backup operation parameters (file list, options, etc.).</param>
    /// <param name="currentFileCallback">Callback to report current file being processed.</param>
    /// <param name="progressCallback">Callback for progress updates (filesProcessed, totalFiles, bytesProcessed).</param>
    /// <param name="logCallback">Callback for log messages.</param>
    /// <param name="fileErrorCallback">Callback when a file error occurs. Returns FileFailedAction. Ignored when <see cref="BackupRequest.SkipAllErrors"/> is <c>true</c>.</param>
    /// <param name="volumeFullCallback">Callback when current volume is full. Returns true to continue on next volume, false to end backup.</param>
    /// <param name="insertMediaCallback">Callback after media ejection, prompting user to insert new media. Returns true when ready, false to end backup.</param>
    /// <param name="mediaLoadRetryCallback">Optional callback when loading the next-volume media fails.
    /// Receives the error message and whether this is a retry attempt; returns true to try again, false to abort.
    /// If null, a failed load throws immediately.</param>
    /// <param name="emergencyTocSaveCallback">Callback when all attempts to save TOC to tape fail.
    /// Receives a suggested file path for emergency TOC export; returns the chosen path, or null to skip.
    /// If null, no emergency export is attempted.</param>
    /// <remarks>
    /// To abort a backup in progress, set Agent.IsAbortRequested = true.
    /// The backup agent is available via the Agent property during execution.
    /// </remarks>
    public Task<BackupResult> ExecuteBackupAsync(
        BackupRequest request,
        Action<string> currentFileCallback,
        Action<int, int, long> progressCallback,
        Action<LogEntry> logCallback,
        Func<string, string, FileFailedAction>? fileErrorCallback,
        Func<int, int, int, int, long, bool> volumeFullCallback,
        Func<int, bool> insertMediaCallback,
        Func<string, bool, bool>? mediaLoadRetryCallback = null,
        Func<string, bool, string?>? emergencyTocSaveCallback = null)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                GuiBackupProgressHandler? progressHandler = null;
                TapeFileBackupAgent? agent = null;

                // Default result for early-exit paths
                BackupResult MakeResult(bool aborted = false, bool failed = false) => new()
                {
                    FilesTotal     = progressHandler?.FilesTotal ?? 0,
                    FilesProcessed = progressHandler?.FilesProcessed ?? 0,
                    FilesSucceeded = progressHandler?.FilesSucceeded ?? 0,
                    FilesFailed    = progressHandler?.FilesFailed ?? 0,
                    FilesSkipped   = progressHandler?.FilesSkipped ?? 0,
                    BytesProcessed = agent?.BytesBackedup ?? 0,
                    WasAborted     = aborted,
                    HasFailed      = failed,
                    Success        = !failed,
                    Outcome        = aborted ? ServiceReportLevel.Failed
                                   : failed  ? ServiceReportLevel.Error
                                   :           ServiceReportLevel.Completed,
                };

                // Local log helpers for concise structured logging
#pragma warning disable CS8321 // some log helpers not used (yet), but might be later
                void log(string msg)           => logCallback(new LogEntry(WarningLevel.None, msg, false, DateTime.Now));
                void logOk(string msg)         => logCallback(new LogEntry(WarningLevel.Completed, msg, false, DateTime.Now));
                void logOkSub(string msg)      => logCallback(new LogEntry(WarningLevel.Completed, msg, true, DateTime.Now));
                void logInfo(string msg)       => logCallback(new LogEntry(WarningLevel.Info, msg, false, DateTime.Now));
                void logInfoSub(string msg)    => logCallback(new LogEntry(WarningLevel.Info, msg, true, DateTime.Now));
                void logWarn(string msg)       => logCallback(new LogEntry(WarningLevel.Warning, msg, false, DateTime.Now));
                void logFail(string msg)       => logCallback(new LogEntry(WarningLevel.Failed, msg, false, DateTime.Now));
                void logFailSub(string msg)    => logCallback(new LogEntry(WarningLevel.Failed, msg, true, DateTime.Now));
                void logErr(string msg)        => logCallback(new LogEntry(WarningLevel.Error, msg, false, DateTime.Now));
#pragma warning restore CS8321

                if (_drive == null || !_drive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    throw new InvalidOperationException("Media not loaded");
                }

                if (request.FileList.Count == 0)
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
                    bool append = request.AppendMode && _toc != null;

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
                    int appendAfterSetIndex = toc.SetIndexToStd(request.AppendAfterSetIndex); // ensure std form

                    // Capacity hint for the new set's internal list — use the known file count
                    //  for non-incremental, non-pattern backups; otherwise leave at 0 (unknown)
                    int capacityHint = !request.Incremental && !request.ListContainsPatterns ? request.FileList.Count : 0;

                    // Mode 1: Append after specific set — save TOC copy for rollback
                    if (append && appendAfterSetIndex > toc.FirstSetOnVolume && appendAfterSetIndex < toc.LastSetOnVolume)
                    {
                        logInfo($"Appending after backup set #{appendAfterSetIndex} | {toc.SetIndexToAlt(appendAfterSetIndex)}");
                        backupTOC = new TapeTOC(toc);
                        appendAfterSetUsed = true;
                        toc.CurrentSetIndex = appendAfterSetIndex + 1;
                        toc.ReplaceCurrentSetTOC(capacityHint, request.Incremental); // replace with fresh set; will trigger newSet=false below
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
                            toc.AddNewSetTOC(capacityHint, request.Incremental); // straight append: add new set
                            newSet = true;
                        }
                        else
                        {
                            toc.MarkCurrentSetIncremental(request.Incremental); // reuse replaced slot (mode 1)
                            newSet = false;
                        }
                    }
                    else
                    {
                        newSet = true; // overwrite: entire TOC created anew
                    }

                    // Configure the new backup set
                    toc.CurrentSetTOC.Description = request.Description;
                    toc.CurrentSetTOC.HashAlgorithm = request.HashAlgorithm;
                    toc.CurrentSetTOC.BlockSize = request.BlockSize;
                    toc.CurrentSetTOC.FmksMode = request.UseFilemarks;

                    logInfo($"Backup set: >{request.Description}<");
                    logInfoSub($"Block size: {Helpers.BytesToString(request.BlockSize)}");
                    logInfoSub($"Hash algorithm: {request.HashAlgorithm}");
                    logInfoSub($"Incremental: {(request.Incremental ? "Yes" : "No")}");
                    if (request.ListContainsPatterns)
                        logInfoSub($"Patterns / folders to backup: {request.FileList.Count:N0}");
                    else
                        logInfoSub($"Files to backup: {request.FileList.Count:N0}");

                    // Create progress handler (uses agent.IsAbortRequested for abort checking).
                    // Pass null fileErrorCallback when skipAllErrors is set — OnFileFailed will
                    //  then always return Skip without showing a dialog.
                    progressHandler = new GuiBackupProgressHandler(
                        agent,
                        logCallback,
                        progressCallback,
                        currentFileCallback,
                        request.SkipAllErrors ? null : fileErrorCallback);

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
                            : request.ListContainsPatterns ?
                                agent.BackupFilesToCurrentSet(newSet, request.FileList, request.IncludeSubdirectories, ignoreFailures: true, progressHandler)
                                : agent.BackupFileListToCurrentSet(newSet, request.FileList, ignoreFailures: true, progressHandler);
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
                                        
                                        // Give the user two attempts to save the TOC to a file; break if user cancels
                                        const int maxExportAttempts = 2;
                                        for (int attempt = 1; attempt <= maxExportAttempts && !emergencySaved; attempt++)
                                        {
                                            string? chosenPath = emergencyTocSaveCallback(suggestedPath, attempt > 1);

                                            if (string.IsNullOrEmpty(chosenPath))
                                            {
                                                logWarn("User declined emergency TOC export");
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
                                                if (attempt < maxExportAttempts)
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
                        logCallback(new LogEntry(headlineLevel, headlineMsg, IsSub: false, DateTime.Now));

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

                        // Retry loop: give the user at least two attempts to seat the new media
                        const int maxLoadAttempts = 2;
                        bool mediaLoaded = false;
                        for (int loadAttempt = 1; loadAttempt <= maxLoadAttempts && !mediaLoaded; loadAttempt++)
                        {
                            bool loadOk = _drive.ReloadMedia();
                            string loadError = _drive.LastErrorMessage;

                            if (loadOk && !_drive.PrepareMedia())
                            {
                                loadOk = false;
                                loadError = _drive.LastErrorMessage;
                            }

                            if (loadOk)
                            {
                                mediaLoaded = true;
                            }
                            else
                            {
                                logErr($"Couldn't load media: {loadError}");

                                bool retry = loadAttempt < maxLoadAttempts
                                    && mediaLoadRetryCallback != null
                                    && mediaLoadRetryCallback(loadError, loadAttempt > 1);

                                if (!retry)
                                    throw new InvalidOperationException($"Couldn't load media: {loadError}");

                                logInfo("Retrying media load...");
                                Status("Loading media...");
                            }
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
                    
                    var backupResult = MakeResult();

                    if (backupResult is { HasFailed: true })
                        logFail("Backup completed with failures");
                    else if (backupResult is { IsFullSuccess: true })
                        logOk("Backup completed successfully");
                    else
                        logInfo("Backup completed");

                    return backupResult;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Status("Backup failed");
                    logErr($"Backup failed: {ex.Message}");
                    return MakeResult(failed: true);
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
    /// <see cref="ServiceBackupProgressHandler"/> subclass that drives the WPF
    ///  progress bar / current-file display, and shows the <see cref="FileErrorDialog"/>
    ///  for per-file errors. All batch logging and abort handling live in the shared base.
    /// </summary>
    private class GuiBackupProgressHandler(
        TapeFileAgent agent,
        Action<LogEntry> logCallback,
        Action<int, int, long> progressCallback,
        Action<string> currentFileCallback,
        Func<string, string, FileFailedAction>? fileErrorCallback)
        : ServiceBackupProgressHandler(
              new WpfServiceHost(System.Windows.Application.Current.Dispatcher, logCallback),
              agent,
              skipAllErrors: fileErrorCallback is null)
    {
        protected override void ReportProgress(in TapeFileStatistics stats, string? currentFile = null)
        {
            if (currentFile is not null)
                currentFileCallback(currentFile);
            progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Overrides the base to show the WPF-specific <see cref="FileErrorDialog"/>
        ///  via <paramref name="fileErrorCallback"/> instead of the generic text prompt.
        ///  End-of-media errors are still silently skipped (matching base behaviour).
        /// </remarks>
        public override FileFailedAction OnFileFailed(TapeFileInfo fileInfo, TapeResult result, in TapeFileStatistics stats)
        {
            Sync(stats);
            ThrowIfAbortRequested();

            // Log the failure lines (same as base)
            logCallback(new LogEntry(WarningLevel.Failed, $"Failed: '{fileInfo.FileDescr.FullName}'", false, DateTime.Now));
            logCallback(new LogEntry(WarningLevel.Failed, $"Error: {result.ErrorMessage}", true, DateTime.Now));
            ReportProgress(stats);

            // End-of-media errors are handled by the multi-volume loop; always skip silently.
            if (result.ErrorCode == (uint)WIN32_ERROR.ERROR_END_OF_MEDIA ||
                result.ErrorCode == (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED)
            {
                return FileFailedAction.Skip;
            }

            // fileErrorCallback is null when skipAllErrors is active — always skip silently.
            if (fileErrorCallback is null)
                return FileFailedAction.Skip;

            // Show the WPF FileErrorDialog via the caller-supplied callback.
            //  The callback is already Dispatcher-marshalled at the call site.
            var action = fileErrorCallback(fileInfo.FileDescr.FullName, result.ErrorMessage);
            if (action == FileFailedAction.Abort)
            {
                logCallback(new LogEntry(WarningLevel.Warning, "Backup abort requested", false, DateTime.Now));
                throw new TapeAbortRequestedException("User requested abort");
            }
            return action;
        }
    }

    #endregion
}
