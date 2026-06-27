using System.IO;

using Windows.Win32.System.SystemServices; // Helpers, Stopwatch

using Stopwatch = Windows.Win32.System.SystemServices.Stopwatch;

using TapeLibNET;

namespace TapeLibNET.Services;

public partial class TapeServiceBase
{
    // ── Backup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a backup operation.
    /// <para>
    /// Multi-volume continuation, file-error handling, and emergency TOC export
    ///  prompts are all routed through <see cref="ITapeServiceHost"/> so every
    ///  app presents its own UI without duplicating state-machine logic.
    /// </para>
    /// </summary>
    /// <remarks>
    /// To abort a running backup set <c>Agent.IsAbortRequested = true</c>;
    ///  the Ctrl+C bridge in CLI subclasses and the abort-button handler in WPF
    ///  already do this via <see cref="TapeFileAgent.IsAbortRequested"/>.
    /// </remarks>
    public Task<BackupResult> ExecuteBackupAsync(BackupRequest request)
    {
        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);

        return Task.Run(async () =>
        {
            // 1. Wait for the semaphore
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // 2. Run the core backup synchronously
                var result = ExecuteBackupCore(request);

                // 3. If requested, automatically eject the media while STILL holding the lock
                if (request.EjectWhenDone)
                {
                    LogInfo("Ejecting media after backup...");
                    // Use the core method bypassing the outer semaphore check of EjectMediaAsync
                    EjectMediaCore();
                }

                return result;
            }
            finally
            {
                // 4. Release lock and notify host
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        }, OperationCancellationToken);
    }

    // Runs synchronously inside the semaphore — no async/await needed here.
    private BackupResult ExecuteBackupCore(BackupRequest request)
    {
        ServiceBackupProgressHandler? progressHandler = null;
        TapeFileBackupAgent? agent = null;

        // Factory for early-exit result paths before the progress handler is set up
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

        if (_drive is null || !_drive.IsMediaLoaded)
        {
            LastError = "Media not loaded";
            throw new InvalidOperationException("Media not loaded");
        }

        if (request.FileList.Count == 0)
        {
            LogInfo("No files to backup");
            return MakeResult();
        }

        // Bridge OperationCancellationToken → agent abort flag (CLI Ctrl+C, WPF abort button)
        using var ctReg = OperationCancellationToken.Register(() =>
        {
            var a = _agent; if (a is not null) a.IsAbortRequested = true;
        });

        try
        {
            LogInfo("Preparing media for backup...");
            OnStatusUpdate("Preparing backup...");

            if (!_drive.PrepareMedia())
            {
                LastError = _drive.LastErrorMessage;
                throw new InvalidOperationException($"Couldn't prepare media: {LastError}");
            }

            bool append = request.AppendMode && _toc != null;

            // --- TOC preparation ---
            // Three modes:
            //  1. Append after specific set: backup copy of TOC for rollback, empty the
            //     target set slot, reuse it (newSet=false). Sets after it are removed on success.
            //  2. Straight append: add a new set after existing ones (newSet=true),
            //     or reuse the last set if it's empty (newSet=false).
            //  3. Overwrite: remove all existing sets, write from scratch (newSet=true).

            _agent?.Dispose();
            _agent = new TapeFileBackupAgent(_drive, _toc);
            agent = (TapeFileBackupAgent)_agent;
            var toc = agent.TOC;
            TapeTOC? backupTOC = null;
            bool appendAfterSetUsed = false;
            int appendAfterSetIndex = toc.SetIndexToStd(request.AppendAfterSetIndex); // ensure std form

            // Capacity hint for the new set's internal list — use the known file count
            //  for non-incremental, non-pattern backups; otherwise leave at 0 (unknown)
            int capacityHint = !request.Incremental && !request.ListContainsPatterns
                ? request.FileList.Count : 0;

            // Mode 1: Append after specific set — save TOC copy for rollback
            if (append && appendAfterSetIndex > toc.FirstSetOnVolume && appendAfterSetIndex < toc.LastSetOnVolume)
            {
                LogInfo($"Appending after backup set #{appendAfterSetIndex} | {toc.SetIndexToAlt(appendAfterSetIndex)}");
                backupTOC = new TapeTOC(toc);
                appendAfterSetUsed = true;
                toc.CurrentSetIndex = appendAfterSetIndex + 1;
                toc.ReplaceCurrentSetTOC(capacityHint, request.Incremental);
            }
            // Mode 3: Overwrite — save TOC copy for rollback
            else if (!append)
            {
                LogInfo("Creating new backup, replacing all existing content");
                backupTOC = new TapeTOC(toc);
                toc.RemoveAllSets();
                toc.Volume = 1; // reset volume to 1 (volume indexing starts from 1)
            }
            // else: Mode 2 — straight append (no TOC modification needed here)

            // Set up media description:
            //  - Overwrite mode: use the caller-supplied name (if any), otherwise the default.
            //  - Append mode: keep the existing description; only fill in if still empty.
            if (!append || string.IsNullOrEmpty(toc.Description))
                toc.Description = !string.IsNullOrWhiteSpace(request.MediaName)
                    ? request.MediaName
                    : DefaultNewMediaName;

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
            toc.CurrentSetTOC.Description       = request.Description;
            toc.CurrentSetTOC.HashAlgorithm     = request.HashAlgorithm;
            toc.CurrentSetTOC.BlockSize         = request.BlockSize;
            toc.CurrentSetTOC.Compression       = request.Compression;
            toc.CurrentSetTOC.CompressionLevel  = request.CompressionLevel;

            LogInfo($"Backup set: >{request.Description}<");
            LogInfoSub($"Block size: {Helpers.BytesToString(request.BlockSize)}");
            LogInfoSub($"Hash algorithm: {request.HashAlgorithm}");
            LogInfoSub($"Compression: {CompressionPreset.DisplayName(request.Compression, request.CompressionLevel)}");
            LogInfoSub($"Incremental: {(request.Incremental ? "Yes" : "No")}");
            if (request.ListContainsPatterns)
                LogInfoSub($"Patterns / folders to backup: {request.FileList.Count:N0}");
            else
                LogInfoSub($"Files to backup: {request.FileList.Count:N0}");

            // Create progress handler via the overridable factory
            progressHandler = CreateBackupProgressHandler(agent, request.SkipAllErrors, request.Filter);

            OnStatusUpdate("Backing up files...");

            // --- Backup loop (handles multi-volume) ---
            // After each iteration:
            //  result=true  → all files processed successfully
            //  result=false → abort, volume full (CanResumeToNextVolume), or hard failure
            // In all cases, the TOC must be cleaned up and saved to tape.
            bool wasAborted = false;

            // Timing — accumulate data and TOC times separately across multi-volume iterations;
            //  user interaction time between volumes is excluded
            var  dataTimer     = new Stopwatch();
            var  tocTimer      = new Stopwatch();
            long dataElapsedUs = 0;
            long tocElapsedUs  = 0;

            do
            {
                dataTimer.Restart();
                bool result = agent.CanResumeToNextVolume
                    ? agent.ResumeBackupToNextVolume()
                    : request.ListContainsPatterns
                        ? agent.BackupFilesToCurrentSet(newSet, request.FileList, request.IncludeSubdirectories,
                              ignoreFailures: true, progressHandler)
                        : agent.BackupFileListToCurrentSet(newSet, request.FileList,
                              ignoreFailures: true, progressHandler);
                dataTimer.Stop();
                dataElapsedUs += dataTimer.ElapsedMicroseconds;

                // The agent catches TapeAbortRequestedException internally and returns false,
                //  so abort is detected via the flag rather than catching the exception.
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
                                LogErr("No files backed up");
                            else
                                LogInfo("No files were backed up");
                        }
                        else
                        {
                            // Content was physically written (partial file) —
                            //  cannot revert, old sets' data may be overwritten.
                            //  Keep the (empty) new set; trim trailing sets if mode 1.
                            if (appendAfterSetUsed)
                                toc.RemoveSetsAfterCurrent();
                            LogErr("No files backed up — previous set data may be lost");
                        }
                    }
                    else
                    {
                        toc.RemoveLastEmptySet();
                        if (!result && !wasAborted && !agent.CanResumeToNextVolume)
                            LogErr("No files backed up");
                        else
                            LogInfo("No files were backed up");
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
                    LogInfo($"Volume #{toc.Volume} is full - backup can continue to next volume");

                // 3. Handle outcome-specific cleanup when files were backed up:
                //    trim stale trailing sets (mode 1) and log failure summary.
                //    When noFilesBackedUp, all TOC repair was already done above.
                if (!noFilesBackedUp)
                {
                    if (appendAfterSetUsed)
                        toc.RemoveSetsAfterCurrent();

                    if (!result && !wasAborted && !agent.CanResumeToNextVolume)
                        LogFail("Some files failed to back up");
                }

                // --- Save TOC to tape ---
                // If we wrote content and are not continuing to another volume,
                //  clear any stale multi-volume continuation flag from a previous session
                //  (e.g. user backed up onto a middle volume of an old multi-volume chain)
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
                    // Notify host that TOC save is starting so the UI can disable abort
                    _host.OnServiceStateChanged(ServiceStateChange.TOCSaveStarted);
                    try
                    {
                        tocTimer.Restart();

                        if (!wasAborted)
                        {
                            OnStatusUpdate("Saving TOC...");
                            LogInfo("Backing up TOC...");
                        }
                        else
                        {
                            OnStatusUpdate("Aborting — saving TOC...");
                            LogInfo("Abort requested — saving TOC to preserve media integrity...");
                        }

                        var tocResult = agent.BackupTOC();
                        if (!tocResult)
                        {
                            LogErr($"Couldn't backup TOC. Error: {tocResult.ErrorMessage}");
                            LogInfo("Attempting to enforce TOC backup...");

                            var enforceResult = agent.BackupTOC(enforce: true);
                            if (!enforceResult)
                            {
                                LogErr("Couldn't enforce TOC backup");
                                LogInfo("Attempting to export TOC to file as emergency recovery...");
                                OnStatusUpdate("Emergency TOC export...");

                                bool emergencySaved = false;
                                string suggestedPath = BuildEmergencyTocExportPath(toc, request.EmergencyTocFolder);

                                // Give the user two attempts to save the TOC to a file; break if user cancels
                                const int maxExportAttempts = 2;
                                for (int attempt = 1; attempt <= maxExportAttempts && !emergencySaved; attempt++)
                                {
                                    string? chosenPath = _host.OnEmergencyTocExportConfirm(suggestedPath, attempt > 1);

                                    if (string.IsNullOrEmpty(chosenPath))
                                    {
                                        LogWarn("User declined emergency TOC export");
                                        break;
                                    }

                                    var saveResult = agent.SaveTOCToFile(chosenPath);
                                    if (saveResult)
                                    {
                                        LogOk($"Emergency TOC exported to: {chosenPath}");
                                        LogInfoSub("This file can be used to recover access to the media content");
                                        IsTOCFromFile = true;
                                        TOCFilePath = chosenPath;
                                        emergencySaved = true;
                                    }
                                    else
                                    {
                                        LogErr($"Failed to export emergency TOC to file: {saveResult.ErrorMessage}");
                                        if (attempt < maxExportAttempts)
                                            LogInfoSub("You can try a different location...");
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
                                LogOk("Enforced TOC backup succeeded");
                            }
                        }
                        else
                        {
                            LogOk("TOC backed up successfully");
                        }

                        _toc = toc; // update service TOC reference
                    } // try (TOC save)
                    finally
                    {
                        tocTimer.Stop();
                        tocElapsedUs += tocTimer.ElapsedMicroseconds;

                        // Re-enable abort now that TOC save is complete (or threw)
                        _host.OnServiceStateChanged(ServiceStateChange.TOCSaveEnded);
                    }
                } // if (!skipTOCSave)

                // Log results for this volume — headline level + uniform stats
                ServiceReportLevel headlineLevel;
                string headlineMsg;
                if (progressHandler.FilesFailed > 0)
                {
                    headlineLevel = ServiceReportLevel.Failed;
                    headlineMsg = $"Backed up {progressHandler.FilesTotal:N0} file(s) with {progressHandler.FilesFailed:N0} failed";
                }
                else if (progressHandler.FilesSkipped > 0)
                {
                    headlineLevel = ServiceReportLevel.Warning;
                    headlineMsg = $"Backed up {progressHandler.FilesTotal:N0} file(s) with {progressHandler.FilesSkipped:N0} skipped";
                }
                else
                {
                    headlineLevel = ServiceReportLevel.Completed;
                    headlineMsg = $"Backed up {progressHandler.FilesTotal:N0} file(s) successfully";
                }
                _host.Report(headlineLevel, headlineMsg);

                // Uniform stats sub-line
                if (progressHandler.FilesProcessed > 0)
                {
                    var parts = new List<string>(4) { $"{progressHandler.FilesSucceeded:N0} succeeded" };
                    if (progressHandler.FilesFailed  > 0) parts.Add($"{progressHandler.FilesFailed:N0} failed");
                    if (progressHandler.FilesSkipped > 0) parts.Add($"{progressHandler.FilesSkipped:N0} skipped");
                    parts.Add($"{Helpers.BytesToString(agent.BytesBackedup)} written");
                    LogInfoSub(string.Join(", ", parts));

                    // Timing sub-line: elapsed time, data rate, and TOC save time
                    double dataSecs = dataElapsedUs / 1e6;
                    double tocSecs  = tocElapsedUs  / 1e6;
                    var timingParts = new List<string>(3) { FormatElapsed(dataSecs) };
                    string rate = FormatDataRate(agent.BytesBackedup, dataSecs);
                    if (rate.Length > 0) timingParts.Add(rate);
                    if (tocSecs >= 1.0) timingParts.Add($"TOC save {FormatElapsed(tocSecs)}");
                    LogInfoSub(string.Join(", ", timingParts));
                }
                LogInfoSub($"Remaining media capacity: {Helpers.BytesToStringLong(_drive.GetContentRemainingCapacity())}");

                // If backup was aborted, TOC has been saved — break out
                if (wasAborted)
                    break;

                // Check if we need to continue with multi-volume
                if (!agent.CanResumeToNextVolume)
                    break; // Done

                // If the caller opted out of multi-volume, end here after the current volume
                if (request.NoMultivolume)
                {
                    LogInfo("Multi-volume continuation skipped (no-multivolume mode)");
                    break;
                }

                // Step 1: Ask user if they want to continue on a new volume
                if (!_host.OnVolumeFullConfirm(toc.Volume, toc.Volume + 1,
                        progressHandler.FilesProcessed, progressHandler.FilesTotal, agent.BytesBackedup))
                {
                    LogInfo("User chose to end multi-volume backup");
                    break;
                }

                // Step 2: Eject current media
                LogInfo("Ejecting media...");
                OnStatusUpdate("Ejecting media...");

                if (!_drive.UnloadMedia())
                    throw new InvalidOperationException($"Couldn't eject media: {_drive.LastErrorMessage}");

                LogOk($"Volume #{toc.Volume} ejected");

                // Step 3: Ask user to insert new media
                if (!_host.OnInsertNewMediaConfirm(toc.Volume + 1))
                {
                    LogInfo("User cancelled media insertion");
                    break;
                }

                // Step 4: Load and prepare the new media (with retry)
                LogInfo("Loading media...");
                OnStatusUpdate("Loading media...");

                const int maxLoadAttempts = 2;
                bool mediaLoaded = false;
                for (int loadAttempt = 1; loadAttempt <= maxLoadAttempts && !mediaLoaded; loadAttempt++)
                {
                    bool loadOk    = _drive.ReloadMedia();
                    string loadErr = _drive.LastErrorMessage;

                    if (loadOk && !_drive.PrepareMedia())
                    {
                        loadOk  = false;
                        loadErr = _drive.LastErrorMessage;
                    }

                    if (loadOk)
                    {
                        mediaLoaded = true;
                    }
                    else
                    {
                        LogErr($"Couldn't load media: {loadErr}");

                        bool retry = loadAttempt < maxLoadAttempts
                            && _host.OnMediaLoadRetryConfirm(loadErr, loadAttempt > 1);

                        if (!retry)
                            throw new InvalidOperationException($"Couldn't load media: {loadErr}");

                        LogInfo("Retrying media load...");
                        OnStatusUpdate("Loading media...");
                    }
                }

                LogOk("Media loaded, continuing backup...");

            } while (true);

            progressHandler.CompleteProgress();

            if (wasAborted)
            {
                LogOk("TOC saved after abort");
                // Log timing even on abort
                double abortDataSecs = dataElapsedUs / 1e6;
                var abortParts = new List<string>(3)
                {
                    $"Before abort: {Helpers.BytesToString(agent.BytesBackedup)} written"
                };
                string abortRate = FormatDataRate(agent.BytesBackedup, abortDataSecs);
                if (abortRate.Length > 0) abortParts.Add(abortRate);
                LogInfoSub(string.Join(", ", abortParts));
                OnStatusUpdate("Backup aborted");
                return MakeResult(aborted: true);
            }

            OnStatusUpdate("Backup complete");

            var backupResult = MakeResult();

            if (backupResult is { HasFailed: true })
                LogFail("Backup completed with failures");
            else if (backupResult is { IsFullSuccess: true })
                LogOk("Backup completed successfully");
            else
                LogInfo("Backup completed");

            return backupResult;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            OnStatusUpdate("Backup failed");
            LogErr($"Backup failed: {ex.Message}");
            return MakeResult(failed: true);
        }
        finally
        {
            progressHandler?.DisposeProgress();
            _agent?.Dispose();
            _agent = null;
        }
    }

    // ── Protected factory hook ────────────────────────────────────────────────

    /// <summary>
    /// Creates the progress handler for a backup operation.
    /// <para>
    /// The base implementation returns a plain <see cref="ServiceBackupProgressHandler"/>
    ///  that logs through <see cref="_host"/> and applies the optional file filter.
    ///  Subclasses override this to add a progress-bar display (CLI: <c>IProgressScope</c>;
    ///  WPF: <see cref="WpfServiceHost.UpdateBackupProgress"/> calls).
    /// </para>
    /// </summary>
    protected virtual ServiceBackupProgressHandler CreateBackupProgressHandler(
        TapeFileBackupAgent agent, bool skipAllErrors, ITapeFileFilter? filter)
        => new(_host, agent, skipAllErrors, filter);

    // ── Static helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a suggested file path for an emergency TOC export.
    /// The file name is derived from the media description, volume number, and
    ///  current timestamp. The directory uses <paramref name="folderHint"/> if
    ///  it exists, otherwise falls back to <see cref="Environment.SpecialFolder.MyDocuments"/>.
    /// </summary>
    private static string BuildEmergencyTocExportPath(TapeTOC toc, string? folderHint = null)
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

        var fileName  = $"{sanitized}_vol{toc.Volume}_{DateTime.Now:yyyyMMdd_HHmmss}{TapeFileAgent.TOCFileExtension}";
        var directory = !string.IsNullOrWhiteSpace(folderHint) && Directory.Exists(folderHint)
            ? folderHint
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(directory, fileName);
    }
}
