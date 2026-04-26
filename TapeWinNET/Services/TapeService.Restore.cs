using System.IO;

using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeLibNET.Services;
using TapeWinNET.Converters;
using TapeWinNET.Models;

namespace TapeWinNET.Services;

/// <summary>
/// Partial class containing restore/validate/verify operations for TapeService.
/// </summary>
public partial class TapeService
{
    /// <summary>
    /// Executes a restore, validate, or verify operation with the specified parameters.
    /// </summary>
    /// <param name="request">Restore operation parameters (mode, file selection, options, etc.).</param>
    /// <param name="currentFileCallback">Callback to report current file being processed.</param>
    /// <param name="progressCallback">Callback for progress updates (filesProcessed, totalFiles, bytesProcessed).</param>
    /// <param name="logCallback">Callback for log messages.</param>
    /// <param name="fileErrorCallback">Callback when a file error occurs. Returns FileFailedAction. Ignored when <see cref="RestoreRequest.SkipAllErrors"/> is <c>true</c>.</param>
    /// <param name="volumeChangeCallback">Callback when restore needs to continue on another volume. Receives the required volume number. Returns true to continue, false to abort.</param>
    /// <param name="insertMediaCallback">Callback after media ejection, prompting user to insert media for the specified volume. Returns true when ready, false to abort.</param>
    /// <param name="mediaLoadRetryCallback">Optional callback when loading the next-volume media fails.
    /// Receives the error message and whether this is a retry attempt; returns true to try again, false to abort.
    /// If null, a failed load throws immediately.</param>
    /// <remarks>
    /// To abort a restore in progress, set Agent.IsAbortRequested = true.
    /// The agent is available via the Agent property during execution.
    /// </remarks>
    public Task<RestoreResult> ExecuteRestoreAsync(
        RestoreRequest request,
        Action<string> currentFileCallback,
        Action<int, int, long> progressCallback,
        Action<LogEntry> logCallback,
        Func<string, string, FileFailedAction>? fileErrorCallback,
        Func<int, bool> volumeChangeCallback,
        Func<int, bool> insertMediaCallback,
        Func<string, bool, bool>? mediaLoadRetryCallback = null)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                GuiRestoreProgressHandler? progressHandler = null;
                TapeFileRestoreBaseAgent? agent = null;

                // Default result for early-exit paths
                RestoreResult MakeResult(bool aborted = false, bool failed = false) => new()
                {
                    FilesTotal     = progressHandler?.FilesTotal ?? 0,
                    FilesProcessed = progressHandler?.FilesProcessed ?? 0,
                    FilesSucceeded = progressHandler?.FilesSucceeded ?? 0,
                    FilesFailed    = progressHandler?.FilesFailed ?? 0,
                    FilesSkipped   = progressHandler?.FilesSkipped ?? 0,
                    BytesProcessed = progressHandler?.BytesProcessed ?? 0,
                    WasAborted     = aborted,
                    HasFailed      = failed,
                    Success        = !failed,
                    Outcome        = aborted ? ServiceReportLevel.Failed
                                   : failed  ? ServiceReportLevel.Error
                                   :           ServiceReportLevel.Completed,
                };

                // Local log helpers for concise structured logging
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable CS8321 // some log helpers might not be used (yet), but might be later
                void logOk(string msg)         => logCallback(new LogEntry(WarningLevel.Completed, msg, false, DateTime.Now));
                void logOkSub(string msg)      => logCallback(new LogEntry(WarningLevel.Completed, msg, true, DateTime.Now));
                void logInfo(string msg)       => logCallback(new LogEntry(WarningLevel.Info, msg, false, DateTime.Now));
                void logInfoSub(string msg)    => logCallback(new LogEntry(WarningLevel.Info, msg, true, DateTime.Now));
                void logWarn(string msg)       => logCallback(new LogEntry(WarningLevel.Warning, msg, false, DateTime.Now));
                void logWarnSub(string msg)    => logCallback(new LogEntry(WarningLevel.Warning, msg, true, DateTime.Now));
                void logFail(string msg)       => logCallback(new LogEntry(WarningLevel.Failed, msg, false, DateTime.Now));
                void logErr(string msg)        => logCallback(new LogEntry(WarningLevel.Error, msg, false, DateTime.Now));
#pragma warning restore CS8321
#pragma warning restore IDE0079 // Remove unnecessary suppression

                if (_drive == null || !_drive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    throw new InvalidOperationException("Media not loaded");
                }

                string modeName = request.Mode switch
                {
                    RestoreMode.Restore => "Restoring",
                    RestoreMode.Validate => "Validating",
                    RestoreMode.Verify => "Verifying",
                    _ => "Processing"
                };

                try
                {
                    logInfo($"{modeName} files...");
                    Status($"Preparing {modeName.ToLowerInvariant()}...");

                    if (!_drive.PrepareMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        throw new InvalidOperationException($"Couldn't prepare media: {LastError}");
                    }

                    // Create the appropriate agent based on mode
                    _agent?.Dispose();
                    agent = request.Mode switch
                    {
                        RestoreMode.Restore => new TapeFileRestoreAgentEx(
                            _drive, request.TargetDirectory, request.RecurseSubdirectories, request.HandleExisting, _toc),
                        RestoreMode.Validate => new TapeFileValidateAgent(_drive, _toc),
                        RestoreMode.Verify => new TapeFileVerifyAgent(_drive, _toc),
                        _ => throw new ArgumentOutOfRangeException(nameof(request), $"Unsupported mode {request.Mode}")
                    };
                    _agent = agent;
                    var toc = agent.TOC;

                    // Restore TOC if not already loaded
                    if (_toc == null)
                    {
                        logInfo("Restoring TOC from tape...");
                        Status("Reading TOC...");
                        var tocResult = agent.RestoreTOC();
                        if (!tocResult)
                        {
                            throw new InvalidOperationException($"Couldn't restore TOC: {tocResult.ErrorMessage}");
                        }
                        _toc = toc;
                        logOk($"TOC restored with {toc.Count} backup set(s)");
                    }

                    // Derive set indexes from the dictionary keys
                    var setIndexes = request.CheckedFilesBySet.Keys.OrderBy(i => i).ToList();

                    // Select files and build the work package
                    var combined = toc.SelectFilesFromSets(request.Incremental, request.CheckedFilesBySet);
                    int newestIdx = setIndexes.Max();
                    toc.CurrentSetIndex = newestIdx;

                    // Log compact initial summary using the assembled work package
                    var (totalFiles, perSet) = toc.GetFileCounts(combined, newestIdx);
                    logInfo($"{modeName} {totalFiles:N0} file(s) from {perSet.Count} set(s)");
                    foreach (var setIndex in setIndexes)
                    {
                        toc.CurrentSetIndex = setIndex;
                        int count = perSet.GetValueOrDefault(setIndex);
                        logInfoSub($"From set #{setIndex} | {toc.SetIndexToAlt(setIndex)}: {toc.CurrentSetTOC.Description}: {count:N0} file(s)");
                    }
                    if (request.Mode == RestoreMode.Restore && !string.IsNullOrEmpty(request.TargetDirectory))
                        logInfoSub($"Target folder: {request.TargetDirectory}");
                    toc.CurrentSetIndex = newestIdx; // restore after iterating

                    // Create progress handler.
                    // Pass null fileErrorCallback when skipAllErrors is set — OnFileFailed will
                    //  then always return Skip without showing a dialog.
                    progressHandler = new GuiRestoreProgressHandler(
                        agent,
                        totalFiles,
                        logCallback,
                        progressCallback,
                        currentFileCallback,
                        request.SkipAllErrors ? null : fileErrorCallback,
                        modeName);

                    Status($"{modeName} files...");

                    // Timing — accumulate data-processing time across multi-volume iterations;
                    //  user interaction time between volumes is excluded
                    var dataTimer = new Stopwatch();
                    long dataElapsedUs = 0;

                    // Call agent to do the actual work —
                    //  this will trigger callbacks for progress and file-level events
                    dataTimer.Start();
                    bool success = agent.RestoreFilesFromCurrentSetDown(
                        combined, ignoreFailures: true, progressHandler);
                    dataTimer.Stop();
                    dataElapsedUs += dataTimer.ElapsedMicroseconds;

                    // The agent catches TapeAbortRequestedException internally and returns false,
                    //  so we detect abort via the flag rather than catching the exception.
                    bool wasAborted = agent.IsAbortRequested;

                    // Handle multi-volume continuation
                    while (!wasAborted && !success && agent.CanResumeFromAnotherVolume)
                    {
                        int volumeNeeded = agent.VolumeToResumeFrom;
                        logInfo($"{modeName} requires Volume #{volumeNeeded} to continue");

                        // Step 1: Ask user if they want to continue
                        if (!volumeChangeCallback(volumeNeeded))
                        {
                            logInfo($"User chose to end multi-volume {modeName.ToLowerInvariant()}");
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

                        // Step 3: Ask user to insert the required volume
                        if (!insertMediaCallback(volumeNeeded))
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

                        logOk($"Media loaded, continuing {modeName.ToLowerInvariant()}...");
                        Status($"{modeName} files...");

                        // Step 5: Resume restore on the new volume
                        dataTimer.Restart();
                        success = agent.ResumeRestoreFromAnotherVolume();
                        dataTimer.Stop();
                        dataElapsedUs += dataTimer.ElapsedMicroseconds;
                        wasAborted = agent.IsAbortRequested;
                    } // while multi-volume continuation

                    // The final tally
                    var result = progressHandler.GenerateResult();

                    // Handle abort
                    if (wasAborted)
                    {
                        logWarn($"{modeName} of {result.FilesTotal:N0} file(s): aborting per user request");
                        var bytesProcessed = long.Max(result.BytesProcessed, agent.BytesRestored); // in case BatchEnd wasn't called due to abort
                        double abortSecs = dataElapsedUs / 1e6;
                        var abortParts = new List<string>(3)
                        {
                            $"Before abort: {result.FilesSucceeded:N0} succeeded",
                            $"{Helpers.BytesToString(bytesProcessed)} processed"
                        };
                        string abortRate = FormatDataRate(bytesProcessed, abortSecs);
                        if (abortRate.Length > 0) abortParts.Add(abortRate);
                        logInfoSub(string.Join(", ", abortParts));
                        Status($"{modeName} aborted");
                        return result with { WasAborted = true };
                    }

                    // Log final results — determine headline level, then emit uniform stats
                    WarningLevel headlineLevel;
                    string headlineMsg;
                    if (result.IsFullSuccess && (success || agent.CanResumeFromAnotherVolume))
                    {
                        headlineLevel = WarningLevel.Completed;
                        headlineMsg = $"{modeName} of {result.FilesTotal:N0} file(s) completed successfully";
                    }
                    else if (result.FilesFailed > 0)
                    {
                        headlineLevel = WarningLevel.Failed;
                        headlineMsg = $"{modeName} of {result.FilesTotal:N0} file(s) completed with {result.FilesFailed:N0} failed";
                    }
                    else if (result.FilesProcessed == 0)
                    {
                        headlineLevel = WarningLevel.Warning;
                        headlineMsg = $"{modeName} of {result.FilesTotal:N0} file(s) completed — no files processed";
                    }
                    else
                    {
                        headlineLevel = WarningLevel.Warning;
                        headlineMsg = $"{modeName} of {result.FilesTotal:N0} file(s) completed with issues";
                    }

                    logCallback(new LogEntry(headlineLevel, headlineMsg, false, DateTime.Now));

                    // Uniform stats sub-line (always shown when files were processed)
                    if (result.FilesProcessed > 0)
                    {
                        var parts = new List<string>(4) { $"{result.FilesSucceeded:N0} succeeded" };
                        if (result.FilesFailed > 0) parts.Add($"{result.FilesFailed:N0} failed");
                        if (result.FilesSkipped > 0) parts.Add($"{result.FilesSkipped:N0} skipped");
                        parts.Add($"{Helpers.BytesToString(result.BytesProcessed)} processed");
                        logInfoSub(string.Join(", ", parts));

                        // Timing sub-line: elapsed time and data rate
                        double dataSecs = dataElapsedUs / 1e6;
                        var timingParts = new List<string>(2) { FormatElapsed(dataSecs) };
                        string rate = FormatDataRate(result.BytesProcessed, dataSecs);
                        if (rate.Length > 0) timingParts.Add(rate);
                        logInfoSub(string.Join(", ", timingParts));
                    }
                    if (result.FilesMissing > 0)
                        logWarnSub($"{result.FilesMissing:N0} file(s) not found on tape");

                    Status($"{modeName} complete");

                    return result;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Status($"{modeName} failed");
                    logErr($"{modeName} failed: {ex.Message}");
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

    #region Helper Classes - Restore

    /// <summary>
    /// <see cref="ServiceRestoreProgressHandler"/> subclass that drives the WPF
    ///  progress bar / current-file display, and shows the <see cref="FileErrorDialog"/>
    ///  for per-file errors. All batch logging and abort handling live in the shared base.
    /// </summary>
    private class GuiRestoreProgressHandler(
        TapeFileAgent agent,
        int totalFilesToProcess,
        Action<LogEntry> logCallback,
        Action<int, int, long> progressCallback,
        Action<string> currentFileCallback,
        Func<string, string, FileFailedAction>? fileErrorCallback,
        string modeName)
        : ServiceRestoreProgressHandler(
              new WpfServiceHost(System.Windows.Application.Current.Dispatcher, logCallback),
              agent,
              totalFilesToProcess,
              skipAllErrors: fileErrorCallback is null,
              modeName)
    {
        protected override void ReportProgress(in TapeFileStatistics stats, string? currentFile = null)
        {
            if (currentFile is not null)
                currentFileCallback(currentFile);
            int total = TotalFilesToProcess > 0 ? TotalFilesToProcess : stats.FilesTotal;
            progressCallback(stats.FilesProcessed, total, stats.BytesProcessed);
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
                logCallback(new LogEntry(WarningLevel.Warning, $"{OperationName} abort requested", false, DateTime.Now));
                throw new TapeAbortRequestedException("User requested abort");
            }
            return action;
        }
    }

    #endregion
}
