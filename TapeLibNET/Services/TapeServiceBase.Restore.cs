using System.IO;

using Windows.Win32.System.SystemServices; // Helpers, Stopwatch

using Stopwatch = Windows.Win32.System.SystemServices.Stopwatch;

using TapeLibNET;

namespace TapeLibNET.Services;

public partial class TapeServiceBase
{
    // ── Restore / Validate / Verify ───────────────────────────────────────────

    /// <summary>
    /// Executes a restore, validate, or verify operation.
    /// <para>
    /// Multi-volume continuation, file-error handling, and media-change prompts are
    ///  all routed through <see cref="ITapeServiceHost"/> so every app presents its
    ///  own UI without duplicating state-machine logic.
    /// </para>
    /// </summary>
    /// <remarks>
    /// To abort a running operation set <c>Agent.IsAbortRequested = true</c>;
    ///  the Ctrl+C bridge in CLI subclasses and the abort-button handler in WPF
    ///  already do this via <see cref="TapeFileAgent.IsAbortRequested"/>.
    /// </remarks>
    public Task<RestoreResult> ExecuteRestoreAsync(RestoreRequest request)
    {
        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return ExecuteRestoreCore(request);
            }
            finally
            {
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        }, OperationCancellationToken);
    }

    // Runs synchronously inside the semaphore — no async/await needed here.
    private RestoreResult ExecuteRestoreCore(RestoreRequest request)
    {
        ServiceRestoreProgressHandler? progressHandler = null;
        TapeFileRestoreBaseAgent? agent = null;

        // Factory for early-exit result paths before the progress handler is set up
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

        if (_drive is null || !_drive.IsMediaLoaded)
        {
            LastError = "Media not loaded";
            throw new InvalidOperationException("Media not loaded");
        }

        string modeName = request.Mode.ToVerb();

        // Bridge OperationCancellationToken → agent abort flag (CLI Ctrl+C, WPF abort button)
        using var ctReg = OperationCancellationToken.Register(() =>
        {
            var a = _agent; if (a is not null) a.IsAbortRequested = true;
        });

        try
        {
            LogInfo($"{modeName} files...");
            OnStatusUpdate($"Preparing {modeName.ToLowerInvariant()}...");

            if (!_drive.PrepareMedia())
            {
                LastError = _drive.LastErrorMessage;
                throw new InvalidOperationException($"Couldn't prepare media: {LastError}");
            }

            // Create the appropriate agent for the requested mode
            _agent?.Dispose();
            agent = request.Mode switch
            {
                RestoreMode.Restore  => new TapeFileRestoreAgentEx(
                    _drive, request.TargetDirectory, request.RecurseSubdirectories,
                    request.HandleExisting, _toc),
                RestoreMode.Validate => new TapeFileValidateAgent(_drive, _toc),
                RestoreMode.Verify   => new TapeFileVerifyAgent  (_drive, _toc),
                _ => throw new ArgumentOutOfRangeException(nameof(request), $"Unsupported mode {request.Mode}")
            };
            _agent = agent;
            var toc = agent.TOC;

            // Restore TOC from tape if not already loaded
            if (_toc is null)
            {
                LogInfo("Restoring TOC from tape...");
                OnStatusUpdate("Reading TOC...");
                var tocResult = agent.RestoreTOC();
                if (!tocResult)
                    throw new InvalidOperationException($"Couldn't restore TOC: {tocResult.ErrorMessage}");
                _toc = toc;
                LogOk($"TOC restored with {toc.Count} backup set(s)");
            }

            var setIndexes = request.CheckedFilesBySet.Keys.OrderBy(i => i).ToList();

            // Apply the optional FCL/wildcard selection filter to any entry whose value
            //  is "all files in set" (null). Entries with an explicit list are kept as-is.
            var checkedBySet = request.CheckedFilesBySet;
            if (request.Filter is not null)
            {
                checkedBySet = new Dictionary<int, IReadOnlyList<TapeFileInfo>?>(
                    request.CheckedFilesBySet.Count);
                foreach (var kv in request.CheckedFilesBySet)
                {
                    if (kv.Value is not null)
                    {
                        checkedBySet[kv.Key] = kv.Value;
                        continue;
                    }
                    int stdIdx = toc.SetIndexToStd(kv.Key);
                    // SelectFiles returns null when the filter matches every file in the
                    //  set — that maps cleanly back to "all files".
                    checkedBySet[kv.Key] = toc[stdIdx].SelectFiles(request.Filter);
                }
            }

            // Assemble the combined work package and log the initial summary
            var combined   = toc.SelectFilesFromSets(request.Incremental, checkedBySet);
            int newestIdx  = setIndexes.Max();
            toc.CurrentSetIndex = newestIdx;

            var (totalFiles, perSet) = toc.GetFileCounts(combined, newestIdx);
            LogInfo($"{modeName} {totalFiles:N0} file(s) from {perSet.Count} set(s)");
            foreach (var setIndex in setIndexes)
            {
                toc.CurrentSetIndex = setIndex;
                int count = perSet.GetValueOrDefault(setIndex);
                LogInfoSub($"From set #{setIndex} | {toc.SetIndexToAlt(setIndex)}: " +
                           $"{toc.CurrentSetTOC.Description}: {count:N0} file(s)");
            }
            if (request.Mode == RestoreMode.Restore && !string.IsNullOrEmpty(request.TargetDirectory))
                LogInfoSub($"Target folder: {request.TargetDirectory}");
            toc.CurrentSetIndex = newestIdx; // restore after iterating

            // Create progress handler via the overridable factory
            progressHandler = CreateRestoreProgressHandler(
                agent, totalFiles, request.Mode, request.SkipAllErrors);

            OnStatusUpdate($"{modeName} files...");

            // Timing: accumulate data-transfer time across multi-volume iterations;
            //  user interaction time between volumes is excluded.
            var  dataTimer     = new Stopwatch();
            long dataElapsedUs = 0;

            dataTimer.Start();
            bool success = agent.RestoreFilesFromCurrentSetDown(
                combined, ignoreFailures: true, progressHandler);
            dataTimer.Stop();
            dataElapsedUs += dataTimer.ElapsedMicroseconds;

            // The agent catches TapeAbortRequestedException internally and returns false,
            //  so abort is detected via the flag rather than catching the exception.
            bool wasAborted = agent.IsAbortRequested;

            // ── Multi-volume continuation loop ────────────────────────────────
            while (!wasAborted && !success && agent.CanResumeFromAnotherVolume)
            {
                int volumeNeeded = agent.VolumeToResumeFrom;
                LogInfo($"{modeName} requires Volume #{volumeNeeded} to continue");

                // Step 1: Ask user whether to continue on the next volume
                if (!_host.OnVolumeContinueConfirm(volumeNeeded, request.Mode))
                {
                    LogInfo($"User chose to end multi-volume {modeName.ToLowerInvariant()}");
                    break;
                }

                // Step 2: Eject the current volume
                LogInfo("Ejecting media...");
                OnStatusUpdate("Ejecting media...");
                if (!_drive.UnloadMedia())
                    throw new InvalidOperationException($"Couldn't eject media: {_drive.LastErrorMessage}");
                LogOk($"Volume #{toc.Volume} ejected");

                // Step 3: Ask user to insert the required volume (WPF: shows dialog /
                //  opens virtual-drive picker; CLI: simple confirm)
                if (!_host.OnInsertMediaConfirm(volumeNeeded, request.Mode))
                {
                    LogInfo("User cancelled media insertion");
                    break;
                }

                // Step 4: Load and prepare the new media (with retry)
                LogInfo("Loading media...");
                OnStatusUpdate("Loading media...");

                const int maxLoadAttempts = 2;
                bool mediaLoaded = false;
                for (int attempt = 1; attempt <= maxLoadAttempts && !mediaLoaded; attempt++)
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

                        bool retry = attempt < maxLoadAttempts
                            && _host.OnMediaLoadRetryConfirm(loadErr, attempt > 1);

                        if (!retry)
                            throw new InvalidOperationException($"Couldn't load media: {loadErr}");

                        LogInfo("Retrying media load...");
                        OnStatusUpdate("Loading media...");
                    }
                }

                LogOk($"Media loaded, continuing {modeName.ToLowerInvariant()}...");
                OnStatusUpdate($"{modeName} files...");

                // Step 5: Resume restore on the new volume
                dataTimer.Restart();
                success = agent.ResumeRestoreFromAnotherVolume();
                dataTimer.Stop();
                dataElapsedUs += dataTimer.ElapsedMicroseconds;
                wasAborted = agent.IsAbortRequested;
            } // while multi-volume continuation
            // ─────────────────────────────────────────────────────────────────

            var result = progressHandler.GenerateResult();

            // Handle abort path
            if (wasAborted)
            {
                LogWarn($"{modeName} of {result.FilesTotal:N0} file(s): aborting per user request");
                // BytesProcessed from progressHandler may be 0 if BatchEnd wasn't called
                var bytesProcessed = long.Max(result.BytesProcessed, agent.BytesRestored);
                double abortSecs   = dataElapsedUs / 1e6;
                var abortParts = new List<string>(3)
                {
                    $"Before abort: {result.FilesSucceeded:N0} succeeded",
                    $"{Helpers.BytesToString(bytesProcessed)} processed"
                };
                string abortRate = FormatDataRate(bytesProcessed, abortSecs);
                if (abortRate.Length > 0) abortParts.Add(abortRate);
                LogInfoSub(string.Join(", ", abortParts));
                OnStatusUpdate($"{modeName} aborted");
                return result with { WasAborted = true };
            }

            // Determine headline level and message
            ServiceReportLevel headlineLevel;
            string             headlineMsg;
            if (result.IsFullSuccess && (success || agent.CanResumeFromAnotherVolume))
            {
                headlineLevel = ServiceReportLevel.Completed;
                headlineMsg   = $"{modeName} of {result.FilesTotal:N0} file(s) completed successfully";
            }
            else if (result.FilesFailed > 0)
            {
                headlineLevel = ServiceReportLevel.Failed;
                headlineMsg   = $"{modeName} of {result.FilesTotal:N0} file(s) completed with {result.FilesFailed:N0} failed";
            }
            else if (result.FilesProcessed == 0)
            {
                headlineLevel = ServiceReportLevel.Warning;
                headlineMsg   = $"{modeName} of {result.FilesTotal:N0} file(s) completed — no files processed";
            }
            else
            {
                headlineLevel = ServiceReportLevel.Warning;
                headlineMsg   = $"{modeName} of {result.FilesTotal:N0} file(s) completed with issues";
            }

            _host.Report(headlineLevel, headlineMsg);

            // Uniform stats sub-lines (shown when at least one file was processed)
            if (result.FilesProcessed > 0)
            {
                var parts = new List<string>(4) { $"{result.FilesSucceeded:N0} succeeded" };
                if (result.FilesFailed  > 0) parts.Add($"{result.FilesFailed:N0} failed");
                if (result.FilesSkipped > 0) parts.Add($"{result.FilesSkipped:N0} skipped");
                parts.Add($"{Helpers.BytesToString(result.BytesProcessed)} processed");
                LogInfoSub(string.Join(", ", parts));

                double dataSecs = dataElapsedUs / 1e6;
                var timingParts = new List<string>(2) { FormatElapsed(dataSecs) };
                string rate     = FormatDataRate(result.BytesProcessed, dataSecs);
                if (rate.Length > 0) timingParts.Add(rate);
                LogInfoSub(string.Join(", ", timingParts));
            }
            if (result.FilesMissing > 0)
                LogWarnSub($"{result.FilesMissing:N0} file(s) not found on tape");

            OnStatusUpdate($"{modeName} complete");

            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            OnStatusUpdate($"{modeName} failed");
            LogErr($"{modeName} failed: {ex.Message}");
            return MakeResult(failed: true);
        }
        finally
        {
            _agent?.Dispose();
            _agent = null;
        }
    }

    // ── Protected factory hook ────────────────────────────────────────────────

    /// <summary>
    /// Creates the progress handler for a restore/validate/verify operation.
    /// <para>
    /// Override in app subclasses to return a specialised handler that drives
    ///  a progress bar, current-file display, etc.
    /// The handler must be constructed with <paramref name="agent"/> and
    ///  <see cref="_host"/> so its <see cref="ITapeFileNotifiable"/> callbacks
    ///  route correctly.
    /// </para>
    /// </summary>
    /// <param name="agent">The live restore agent for this operation.</param>
    /// <param name="totalFiles">Total number of files to process (for progress %).</param>
    /// <param name="mode">The restore operation mode — used to derive log and dialog strings.</param>
    /// <param name="skipAllErrors">
    ///  When <see langword="true"/> all file errors are silently skipped without prompting.
    ///  Mirrors <see cref="RestoreRequest.SkipAllErrors"/>.
    /// </param>
    protected virtual ServiceRestoreProgressHandler CreateRestoreProgressHandler(
        TapeFileRestoreBaseAgent agent, int totalFiles, RestoreMode mode, bool skipAllErrors)
        => new(_host, agent, totalFiles, skipAllErrors, mode);
}
