using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.ConstrainedExecution;
using TapeLibNET;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // Helpers, Stopwatch
using Stopwatch = Windows.Win32.System.SystemServices.Stopwatch;

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
        TapeResult agentResult = TapeResult.OK; // Note TapeResult.OK, NOT default — a default TapeResult means FAILURE

        // +-------------------------------+--------------------------------------+-----------------------------------------------+
        // | Figure                        | Scope                                | Notes                                         |
        // +-------------------------------+--------------------------------------+-----------------------------------------------+
        // | FilesTotal                    | Whole operation                      | From fileList.Count, set once                 |
        // | BytesTotal                    | Whole operation                      | Background aggregator over the full list      |
        // | FilesProcessed                | Running total across all volumes     | _stats never resets between volumes           |
        // | Succeeded                     | Running total across all volumes     | _stats never resets between volumes           |
        // | Failed                        | Running total across all volumes     | _stats never resets between volumes           |
        // | Skipped                       | Running total across all volumes     | _stats never resets between volumes           |
        // | BytesProcessed                | Running total, includes TOC bytes    | agent.BytesBackedup; BackupTOCCore adds       |
        // | (= agent.BytesBackedup)       |                                      | wstream.Length                                |
        // | dataElapsedUs                 | Running total                        | Accumulated via += across iterations          |
        // | dataIoElapsedUs               | Running total                        | Accumulated via += across iterations          |
        // | tocElapsedUs                  | Running total                        | Accumulated via += across iterations          |
        // | agentResult                   | Earliest failure of whole operation  | Failure latch carried across volume swaps     |
        // +-------------------------------+--------------------------------------+-----------------------------------------------+

        // Factory for result construction. Diagnosis defaults to OK so the early-exit paths (no drive,
        //  empty file list) produce genuinely clean results; the agent-driven paths pass what the agent
        //  reported. Before agentResult has been first set, reports all-ok with all default parametrs.
        //  Allow to override diagnostics if the default agent's doesn't fit (e.g. an exception outside the agent).
        BackupResult MakeResult(bool aborted = false, bool failed = false, TapeResult? diagnosis = null)
            => new()
            {
                Diagnosis = diagnosis ?? agentResult,
                FilesTotal = progressHandler?.FilesTotal ?? 0,
                BytesTotal = progressHandler?.BytesTotal ?? 0,
                FilesProcessed = progressHandler?.FilesProcessed ?? 0,
                FilesSucceeded = progressHandler?.FilesSucceeded ?? 0,
                FilesFailed = progressHandler?.FilesFailed ?? 0,
                FilesSkipped = progressHandler?.FilesSkipped ?? 0,
                BytesProcessed = agent?.BytesBackedup ?? 0,
                WasAborted = aborted,
                HasFailed = failed,
                Success = !failed && !(progressHandler?.SetStats.SetWriteBlocked ?? false),
                Outcome = aborted ? ServiceReportLevel.Failed
                                    : failed ? ServiceReportLevel.Error
                                        : ServiceReportLevel.Completed,
                Sets = progressHandler?.SetStats ?? new(),
            };

        if (_drive is null || !_drive.IsMediaLoaded)
        {
            LastError = "Media not loaded";
            throw new InvalidOperationException("Media not loaded");
        }

        if (request.FileList.Count == 0)
        {
            LogInfo("No files to backup");
            return MakeResult(); // treat as no error, just information
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

            // Run-scoped suppression latch for identity prompts; ProceedAlways flips it on.
            bool suppress = request.ProceedOnMediaMismatch;

            // The agent is created BEFORE the identity checkpoints so that RefreshLoadedHeader() below uses
            //  the LIVE agent — the one that owns the navigator — keeping its header presence and position
            //  coherent (§17.7). It also gives both checkpoints one `toc` to judge against.
            agent = new TapeFileBackupAgent(_drive, _toc);
            _agent?.Dispose();
            _agent = agent;
            agent.WritesMediaHeader = true; // the agent heads only at fresh-volume starts (append leaves it alone)
            agent.CorrectsSetNavigation = request.CorrectSetNavigation;  // default true
            agent.VerifiesSetHeader = request.VerifySetHeader; // default true

            var toc = agent.TOC;
            TapeTOC? backupTOC = null;
            bool appendAfterSetUsed = false;
            int appendAfterSetIndex = toc.SetIndexToStd(request.AppendAfterSetIndex); // ensure std form

            // §10.2 — re-read the BOM identity before judging it, but ONLY when overwriting.
            //  Why re-read at all: _loadedHeader is cached at load/format time, and a PREVIOUS overwrite
            //   backup in this same session invalidates it — that path calls ResetMediaId() and the ensuing
            //   content write stamps a FRESH header, while nothing refreshes the cache. Comparing the stale
            //   cache against the current TOC then reports a mismatch that does not exist on the medium.
            //  Why only when overwriting: the overwrite path rewinds to BOM anyway, so the read is free.
            //   A straight append must not pay a full rewind per operation — it otherwise needs only a short
            //   seek before the TOC (or none at all with the TOC in its own partition).
            if (!append)
                RefreshLoadedHeader();

            // Capacity hint for the new set's internal list — use the known file count
            //  for non-incremental, non-pattern backups; otherwise leave at 0 (unknown)
            int capacityHint = !request.Incremental && !request.ListContainsPatterns
                ? request.FileList.Count : 0;

            // §10.5 checkpoint 2 — Append: verify we're adding to the media we expect.
            if (append)
            {
                var apChoice = PresentVerdict(
                    EvaluateLoadedHeader(expectedSeriesId: toc.MediaId, expectedVolume: toc.Volume),
                    MediaPromptContext.VerifyRestore, suppress);
                if (apChoice == MediaMismatchChoice.Abort)
                    return MakeResult(aborted: true); // the caller decided to abort
                if (apChoice == MediaMismatchChoice.ProceedAlways)
                    suppress = true;
            }

            // Mode 1: Append after specific set — save TOC copy for rollback
            if (append && appendAfterSetIndex >= toc.FirstSetOnVolume && appendAfterSetIndex < toc.LastSetOnVolume)
            {
                LogInfo($"Appending after backup set #{appendAfterSetIndex} | {toc.SetIndexToAlt(appendAfterSetIndex)}");
                backupTOC = new TapeTOC(toc);
                appendAfterSetUsed = true;
                toc.CurrentSetIndex = appendAfterSetIndex + 1;
                toc.ReplaceCurrentSetTOC(capacityHint, request.Incremental);
            }
            // Mode 3: Overwrite — warn before destroying real content, then save a rollback copy.
            else if (!append)
            {
                // §10.5 checkpoint 1 — prompt only for a wrong-kind cartridge, a DIFFERENT series, or a TOC that
                //  still holds sets. Our own freshly-formatted / emptied media (same MediaId, no sets) is silent.
                //  Judged by the SAME evaluator that restore and append use, so the Guid.Empty "no expectation"
                //  guard and the kind check are defined exactly once; expectNoSets adds the one thing the
                //  evaluator cannot infer — that an overwrite DESTROYS whatever is already there.
                //  No expectedVolume: an overwrite resets Volume to 1 regardless, so the check would be moot.
                //  PresentVerdict short-circuits Match / Unidentified, so no null-verdict dance is needed.
                var owChoice = PresentVerdict(
                    EvaluateLoadedHeader(expectedSeriesId: toc.MediaId, expectNoSets: true),
                    MediaPromptContext.OverwriteBackup, suppress);
                if (owChoice == MediaMismatchChoice.Abort)
                    return MakeResult(aborted: true); // the caller decided to abort
                if (owChoice == MediaMismatchChoice.ProceedAlways)
                    suppress = true;

                LogInfo("Creating new backup, replacing all existing content");
                backupTOC = new TapeTOC(toc);
                toc.RemoveAllSets();
                toc.Volume = 1;         // volume indexing starts from 1
                toc.ResetMediaId();     // §10.5: fresh series id — the rewritten header gets a new MediaId
                _loadedHeader = null;   // new MediaId invalidates any previous header
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
            toc.CurrentSetTOC.Description = request.Description;
            toc.CurrentSetTOC.HashAlgorithm = request.HashAlgorithm;
            toc.CurrentSetTOC.BlockSize = request.BlockSize;
            toc.CurrentSetTOC.Compression = request.Compression;
            toc.CurrentSetTOC.CompressionLevel = request.CompressionLevel;

            int startVolume = toc.Volume;

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

            // Outcomes declared outside of the loop so that the exits after the loop can read it
            bool wasAborted = false;

            // Timing — accumulate data and TOC times separately across multi-volume iterations;
            //  user interaction time between volumes is excluded
            var dataTimer = new Stopwatch();
            var tocTimer = new Stopwatch();
            long dataElapsedUs = 0;
            long dataIoElapsedUs = 0;
            long tocElapsedUs = 0;

            do // the multivolume loop — breaks on abort, no more volumes, or user cancels
            {
                // --- Invoke the agent ---
                _drive.IoTimeCounterUs = 0; // reset I/O time counter for this volume
                dataTimer.Restart();

                agentResult = agent.CanResumeToNextVolume
                    ? agent.ResumeBackupToNextVolume()
                    : request.ListContainsPatterns
                        ? agent.BackupFilesToCurrentSet(newSet, request.FileList, request.IncludeSubdirectories,
                              ignoreFailures: true, progressHandler)
                        : agent.BackupFileListToCurrentSet(newSet, request.FileList,
                              ignoreFailures: true, progressHandler);

                dataTimer.Stop();
                dataElapsedUs += dataTimer.ElapsedMicroseconds;
                dataIoElapsedUs += _drive.IoTimeCounterUs;

                bool result = (bool)agentResult;

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
                    // Whether the MEDIUM was touched at all. The header is stamped before the content
                    // session opens, so ContentWritten alone does not answer it.
                    bool mediaUntouched = !agent.Manager.ContentWritten && !agent.MediaHeaderStamped;

                    if (backupTOC != null)
                    {
                        // The header is stamped BEFORE the content session opens, so ContentWritten alone
                        //  doesn't tell us whether the medium was modified. Reverting the TOC after a fresh
                        //  header has landed would restore the OLD MediaId against a tape carrying the NEW
                        //  one — the in-memory state would then describe a cartridge that no longer exists.
                        if (mediaUntouched)
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

                            if (agent.Manager.ContentWritten)
                            {
                                LogWarn("No files backed up — empty backup set added to preserve media structure");
                                LogWarnSub("The empty backup set may be removed (Backup | Delete Backup Sets)");
                            }
                            else // no content — only the fresh media header (+ the new empty TOC)
                            {
                                LogWarn("No files backed up — the media has been re-initialized");
                                LogWarnSub("Previous content is no longer accessible");
                            }
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

                    // Skip the save when the tape's TOC is DEMONSTRABLY still correct: nothing was
                    //  written, and both branches above have just restored the in-memory TOC to what
                    //  the tape holds. If we're continuing to the next volume, we must save the TOC
                    //  to update its ContinuedOnNextVolume flag.
                    // Deliberately NOT gated on Navigator.TOCUnlocated: that flag is true on any
                    //  FRESH navigator — it means "I have not located the TOC mark yet", not "the TOC
                    //  on tape is stale". Since the service reads the TOC with one agent and backs up
                    //  with another, it is true at the start of every backup, which would rewrite a
                    //  perfectly valid TOC after a refused overwrite (and also make "media unchanged"
                    //  report a lie!)
                    if (!agent.CanResumeToNextVolume && mediaUntouched)
                    {
                        skipTOCSave = true;
                        _toc = toc;
                    }
                } // if (noFilesBackedUp)

                // 2. Log volume-full status (applies regardless of file count)
                //    Check CanResumeToNextVolume to determine if the backup can continue on a new volume.
                if (agent.CanResumeToNextVolume)
                {
                    if (!request.NoMultivolume)
                        LogInfo($"Volume #{toc.Volume} is full - backup can continue to next volume");
                    else
                        LogInfo($"Volume #{toc.Volume} is full - backup will complete (no-multivolume mode)");
                }

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
                // ...and likewise if no-multivolume is requested, except in this case
                //  toc.ContinuedOnNextVolume cannot have gone "stale" (we only do 1 volume),
                //  hence no need to clear skipTOCSave
                if (request.NoMultivolume)
                    toc.ContinuedOnNextVolume = false;

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

                        // --- Invoke the agent to save the TOC ---
                        var tocResult = agent.BackupTOC();

                        if (!tocResult)
                        {
                            LogErr($"Couldn't backup TOC. Error: {tocResult.ErrorMessage}");

                            // A TOC failure outranks a clean file loop in the headline: every file is on
                            //  tape, and none of them is reachable without a TOC!
                            if (agentResult.Success)
                                agentResult = tocResult;

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

                                    // --- Emergency export TOC to file ---
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
                                        "TOC backup failed. It is strongly advised to immediately export TOC to file (Media | Export TOC to file). " +
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

                // Log results for this volume: first the headline, then the uniform stats
                var resultSoFar = MakeResult();

                // Our stats figures are CUMULATIVE across the volumes written so far — the agent's
                //  statistics never reset between volumes, and agentResult carries the earliest failure
                //  of the whole operation. Hence we differentiate per-volume vs. the overall outcome.
                if (agent.CanResumeToNextVolume && !request.NoMultivolume)
                {
                    // Progress notice upon this volume, not a verdict: the operation continues on the next volume.
                    LogInfo($"Volume #{toc.Volume} complete — {resultSoFar.FilesSucceeded:N0} of " +
                            $"{resultSoFar.FilesTotal:N0} file(s) written so far");
                    LogInfoSub($"{Helpers.BytesToStringLong(agent.BytesBackedupInCurrentSet)} written to this volume");
                }
                else
                {
                    // The whole operation outcome
                    ReportFileOperationOutcome(resultSoFar, "Backup");
                    ReportSetAnomalyOutcome(progressHandler.SetStats);
                }

                // Current statistics we report for each volume.
                ReportBackupStats(resultSoFar,
                    dataSecs: dataElapsedUs / 1e6, ioSecs: dataIoElapsedUs / 1e6, tocSecs: tocElapsedUs / 1e6);

                // --- Check for finish conditions ---
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

                // --- Handle media swap for multivolume continuation ---
                // Step 1: Ask user if they want to continue on a new volume
                if (!_host.OnVolumeFullConfirm(toc.Volume, toc.Volume + 1,
                        progressHandler.FilesProcessed, progressHandler.FilesTotal,
                        progressHandler.BytesProcessed, progressHandler.BytesTotal))
                {
                    LogInfo("User chose to end multi-volume backup");
                    break;
                }

                // Steps 2–5: eject, insert, load, and verify the fresh volume's identity.
                //  Retry re-runs the whole cycle; Abort / user-cancel exits the outer multi-volume loop.
                //  (The agent heads the fresh volume inside ResumeBackupToNextVolume → BeginWriteContentForCurrentSet,
                //  per §17.10 — the service never writes the continuation header itself.)
                bool cancelled = false;
                do
                {
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
                        cancelled = true;
                        break;
                    }

                    // Step 4: Load and prepare the new media (with load-retry)
                    LogInfo("Loading media...");
                    OnStatusUpdate("Loading media...");

                    const int maxLoadAttempts = 2;
                    bool mediaLoaded = false;
                    for (int loadAttempt = 1; loadAttempt <= maxLoadAttempts && !mediaLoaded; loadAttempt++)
                    {
                        bool loadOk = _drive.ReloadMedia();
                        string loadErr = _drive.LastErrorMessage;
                        if (loadOk && !_drive.PrepareMedia())
                        {
                            loadOk = false;
                            loadErr = _drive.LastErrorMessage;
                        }

                        if (loadOk)
                        {
                            mediaLoaded = true;
                        }
                        else
                        {
                            LogErr($"Couldn't load media: {loadErr}");
                            bool retryLoad = loadAttempt < maxLoadAttempts
                                && _host.OnMediaLoadRetryConfirm(loadErr, loadAttempt > 1);
                            if (!retryLoad)
                                throw new InvalidOperationException($"Couldn't load media: {loadErr}");

                            LogInfo("Retrying media load...");
                            OnStatusUpdate("Loading media...");
                        }
                    }

                    // Step 5 (§10.5 checkpoint 3): verify the continuation volume's identity.
                    //  Deliberately NOT routed through EvaluateLoadedHeader: this checkpoint INVERTS the usual
                    //  rule — a MATCHING MediaId means WrongVolume here, because a continuation needs a FRESH
                    //  cartridge, and an earlier volume of our own series is exactly what must not be overwritten.
                    //  Skip the header test if ProceedOnMediaMismatch was set by the caller.
                    if (!request.ProceedOnMediaMismatch)
                        RefreshLoadedHeader();
                    else
                        _loadedHeader = null; // treat the volume as blank
                    TapeMediaVerdict cvVerdict = _loadedHeader switch
                    {
                        null => TapeMediaVerdict.Unidentified,   // blank fresh volume — ideal
                        TapeCalibrationHeader => TapeMediaVerdict.WrongKind,
                        TapeMediaHeader m when m.MediaId == toc.MediaId => TapeMediaVerdict.WrongVolume,     // an earlier volume of THIS series
                        _ => TapeMediaVerdict.MediaIdMismatch, // a different backup
                    };

                    var cvChoice = PresentVerdict(
                        cvVerdict, MediaPromptContext.ContinuationVolume, suppress, allowRetry: true);
                    if (cvChoice == MediaMismatchChoice.Abort) { cancelled = true; break; }
                    if (cvChoice == MediaMismatchChoice.Retry) continue;   // re-eject, re-insert
                    if (cvChoice == MediaMismatchChoice.ProceedAlways) suppress = true;
                    break; // Proceed
                } while (true);

                if (cancelled)
                    break; // exit the outer multi-volume do-while

                LogOk("Media loaded, continuing backup...");

            } while (true); // the multivolume loop

            progressHandler.CompleteProgress();

            // After the loop, we need no addtl. report — the final iteration's numbers are the totals.
            //  Output just one line to finalize the multi-volume statistics.
            int volumes = toc.Volume - startVolume + 1;
            if (volumes > 0)
                LogInfoSub($"Across {volumes} volume(s)");

            if (wasAborted)
            {
                LogOk("TOC saved after abort");

                // Log timing even on abort
                double abortDataSecs = dataElapsedUs / 1e6;
                var abortParts = new List<string>(3)
                {
                    $"Before abort: {Helpers.BytesToString(progressHandler.BytesProcessed)} written"
                };
                string abortRate = FormatDataIoRate(progressHandler.BytesProcessed, abortDataSecs);
                if (abortRate.Length > 0) abortParts.Add(abortRate);
                LogInfoSub(string.Join(", ", abortParts));

                OnStatusUpdate("Backup aborted");
                return MakeResult(aborted: true);
            }

            var backupResult = MakeResult();

            if (backupResult is { HasFailed: true })
                LogFail("Backup completed with failures");
            else if (backupResult is { IsFullSuccess: true })
                LogOk("Backup completed successfully");
            else
                LogInfo("Backup completed");

            OnStatusUpdate("Backup complete");
            return backupResult;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            OnStatusUpdate("Backup failed");
            LogErr($"Backup failed: {ex.Message}");
            // The exception did not come through the agent, so build the diagnosis from it directly.
            return MakeResult(failed: true, diagnosis: TapeResult.Fail(ex)) with { ErrorException = ex };
        }
        finally
        {
            progressHandler?.DisposeProgress();
            _agent?.Dispose();
            _agent = null;
        }
    }

    // Runs synchronously inside the semaphore — no async/await needed here.
    private BackupResult ExecuteBackupCore_OLD(BackupRequest request)
    {
        ServiceBackupProgressHandler? progressHandler = null;
        TapeFileBackupAgent? agent = null;
        TapeResult agentResult = TapeResult.OK; // Note TapeResult.OK, NOT default — a default TapeResult means FAILURE

        // +-------------------------------+--------------------------------------+-----------------------------------------------+
        // | Figure                        | Scope                                | Notes                                         |
        // +-------------------------------+--------------------------------------+-----------------------------------------------+
        // | FilesTotal                    | Whole operation                      | From fileList.Count, set once                 |
        // | BytesTotal                    | Whole operation                      | Background aggregator over the full list      |
        // | FilesProcessed                | Running total across all volumes     | _stats never resets between volumes           |
        // | Succeeded                     | Running total across all volumes     | _stats never resets between volumes           |
        // | Failed                        | Running total across all volumes     | _stats never resets between volumes           |
        // | Skipped                       | Running total across all volumes     | _stats never resets between volumes           |
        // | BytesProcessed                | Running total, includes TOC bytes    | agent.BytesBackedup; BackupTOCCore adds       |
        // | (= agent.BytesBackedup)       |                                      | wstream.Length                                |
        // | dataElapsedUs                 | Running total                        | Accumulated via += across iterations          |
        // | dataIoElapsedUs               | Running total                        | Accumulated via += across iterations          |
        // | tocElapsedUs                  | Running total                        | Accumulated via += across iterations          |
        // | agentResult                   | Earliest failure of whole operation  | Failure latch carried across volume swaps     |
        // +-------------------------------+--------------------------------------+-----------------------------------------------+

        // Factory for result construction. Diagnosis defaults to OK so the early-exit paths (no drive,
        //  empty file list) produce genuinely clean results; the agent-driven paths pass what the agent
        //  reported. Before agentResult has been first set, reports all-ok with all default parametrs.
        //  Allow to override diagnostics if the default agent's doesn't fit (e.g. an exception outside the agent).
        BackupResult MakeResult(bool aborted = false, bool failed = false, TapeResult? diagnosis = null)
            => new()
            {
                Diagnosis       = diagnosis ?? agentResult,

                FilesTotal      = progressHandler?.FilesTotal ?? 0,
                BytesTotal      = progressHandler?.BytesTotal ?? 0,
                FilesProcessed  = progressHandler?.FilesProcessed ?? 0,
                FilesSucceeded  = progressHandler?.FilesSucceeded ?? 0,
                FilesFailed     = progressHandler?.FilesFailed ?? 0,
                FilesSkipped    = progressHandler?.FilesSkipped ?? 0,
                BytesProcessed  = agent?.BytesBackedup ?? 0,
                
                WasAborted      = aborted,
                HasFailed       = failed,
                Success         = !failed,
                Outcome         = aborted
                                    ? ServiceReportLevel.Failed
                                    : failed
                                        ? ServiceReportLevel.Error
                                        : ServiceReportLevel.Completed,
            };

        if (_drive is null || !_drive.IsMediaLoaded)
        {
            LastError = "Media not loaded";
            throw new InvalidOperationException("Media not loaded");
        }

        if (request.FileList.Count == 0)
        {
            LogInfo("No files to backup");
            return MakeResult(); // treat as no error, just information
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

            // Run-scoped suppression latch for identity prompts; ProceedAlways flips it on.
            bool suppress = request.ProceedOnMediaMismatch;

            // §10.5 checkpoint 2 — Append: verify we're adding to the media we expect.
            if (append)
            {
                var apChoice = PresentVerdict(
                    EvaluateLoadedHeader(expectedSeriesId: _toc?.MediaId, expectedVolume: _toc?.Volume),
                    MediaPromptContext.VerifyRestore, suppress);
                if (apChoice == MediaMismatchChoice.Abort)
                    return MakeResult(aborted: true); // the caller decided to abort
                if (apChoice == MediaMismatchChoice.ProceedAlways)
                    suppress = true;
            }

            agent = new TapeFileBackupAgent(_drive, _toc);
            _agent?.Dispose();
            _agent = agent;

            agent.WritesMediaHeader = true; // the agent heads only at fresh-volume starts (append leaves it alone)

            var toc = agent.TOC;
            TapeTOC? backupTOC = null;
            bool appendAfterSetUsed = false;
            int appendAfterSetIndex = toc.SetIndexToStd(request.AppendAfterSetIndex); // ensure std form

            // Capacity hint for the new set's internal list — use the known file count
            //  for non-incremental, non-pattern backups; otherwise leave at 0 (unknown)
            int capacityHint = !request.Incremental && !request.ListContainsPatterns
                ? request.FileList.Count : 0;

            // Mode 1: Append after specific set — save TOC copy for rollback
            if (append && appendAfterSetIndex >= toc.FirstSetOnVolume && appendAfterSetIndex < toc.LastSetOnVolume)
            {
                LogInfo($"Appending after backup set #{appendAfterSetIndex} | {toc.SetIndexToAlt(appendAfterSetIndex)}");
                backupTOC = new TapeTOC(toc);
                appendAfterSetUsed = true;
                toc.CurrentSetIndex = appendAfterSetIndex + 1;
                toc.ReplaceCurrentSetTOC(capacityHint, request.Incremental);
            }
            // Mode 3: Overwrite — warn before destroying real content, then save a rollback copy.
            else if (!append)
            {
                // §10.5 checkpoint 1 — prompt only for a wrong-kind cartridge, a DIFFERENT series, or a TOC that
                //  still holds sets. Our own freshly-formatted / emptied media (same MediaId, no sets) is silent.
                TapeMediaVerdict? owVerdict = _loadedHeader switch
                {
                    TapeCalibrationHeader => TapeMediaVerdict.WrongKind,
                    TapeMediaHeader m when m.MediaId != toc.MediaId => TapeMediaVerdict.MediaIdMismatch,
                    _ when toc.Count > 0 => TapeMediaVerdict.MediaIdMismatch,
                    _ => null,
                };
                if (owVerdict is { } ov)
                {
                    var owChoice = PresentVerdict(ov, MediaPromptContext.OverwriteBackup, suppress);
                    if (owChoice == MediaMismatchChoice.Abort)
                        return MakeResult(aborted: true); // the caller decided to abort
                    if (owChoice == MediaMismatchChoice.ProceedAlways)
                        suppress = true;
                }

                LogInfo("Creating new backup, replacing all existing content");
                backupTOC = new TapeTOC(toc);
                toc.RemoveAllSets();
                toc.Volume = 1;          // volume indexing starts from 1
                toc.ResetMediaId();      // §10.5: fresh series id — the rewritten header gets a new MediaId
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
            int startVolume = toc.Volume;

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

            // Outcomes declared outside of the loop so that the exits after the loop can read it
            bool wasAborted = false;

            // Timing — accumulate data and TOC times separately across multi-volume iterations;
            //  user interaction time between volumes is excluded
            var dataTimer     = new Stopwatch();
            var  tocTimer      = new Stopwatch();
            long dataElapsedUs = 0;
            long dataIoElapsedUs = 0;
            long tocElapsedUs  = 0;

            do // the multivolume loop — breaks on abort, no more volumes, or user cancels
            {
                // --- Invoke the agent ---
                _drive.IoTimeCounterUs = 0; // reset I/O time counter for this volume
                dataTimer.Restart();
                agentResult = agent.CanResumeToNextVolume
                    ? agent.ResumeBackupToNextVolume()
                    : request.ListContainsPatterns
                        ? agent.BackupFilesToCurrentSet(newSet, request.FileList, request.IncludeSubdirectories,
                              ignoreFailures: true, progressHandler)
                        : agent.BackupFileListToCurrentSet(newSet, request.FileList,
                              ignoreFailures: true, progressHandler);
                dataTimer.Stop();
                dataElapsedUs += dataTimer.ElapsedMicroseconds;
                dataIoElapsedUs += _drive.IoTimeCounterUs;
                bool result = (bool)agentResult;

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
                            LogWarn("No files backed up — empty backup set added to preserve media structure");
                            LogWarnSub("The empty backup set may be removed (Backup | Delete Backup Sets)");
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
                    if (!agent.CanResumeToNextVolume && !agent.Navigator.TOCUnlocated)
                    {
                        skipTOCSave = true;
                        _toc = toc;
                    }
                } // if (noFilesBackedUp)

                // 2. Log volume-full status (applies regardless of file count)
                //    Check CanResumeToNextVolume to determine if the backup can continue on a new volume.
                if (agent.CanResumeToNextVolume)
                {
                    if (!request.NoMultivolume)
                        LogInfo($"Volume #{toc.Volume} is full - backup can continue to next volume");
                    else
                        LogInfo($"Volume #{toc.Volume} is full - backup will complete (no-multivolume mode)");
                }

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
                // ...and likewise if no-multivolume is requested, except in this case
                //  toc.ContinuedOnNextVolume cannot have gone "stale" (we only do 1 volume),
                //  hence no need to clear skipTOCSave
                if (request.NoMultivolume)
                    toc.ContinuedOnNextVolume = false;

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

                        // --- Invoke the agent to save the TOC ---
                        var tocResult = agent.BackupTOC();
                        if (!tocResult)
                        {
                            LogErr($"Couldn't backup TOC. Error: {tocResult.ErrorMessage}");

                            // A TOC failure outranks a clean file loop in the headline: every file is on
                            //  tape, and none of them is reachable without a TOC!
                            if (agentResult.Success)
                                agentResult = tocResult;

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

                                    // --- Emergency export TOC to file ---
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
                                        "TOC backup failed. It is strongly advised to immediately export TOC to file (Media | Export TOC to file). " +
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

                // Log results for this volume: first the headline, then the uniform stats
                var resultSoFar = MakeResult();
                // Our stats figures are CUMULATIVE across the volumes written so far — the agent's
                //  statistics never reset between volumes, and agentResult carries the earliest failure
                //  of the whole operation. Hence we differentiate per-volume vs. the overall outcome.
                if (agent.CanResumeToNextVolume && !request.NoMultivolume)
                {
                    // Progress notice upon this volume, not a verdict: the operation continues on the next volume.
                    LogInfo($"Volume #{toc.Volume} complete — {resultSoFar.FilesSucceeded:N0} of " +
                            $"{resultSoFar.FilesTotal:N0} file(s) written so far");
                    LogInfoSub($"{Helpers.BytesToStringLong(agent.BytesBackedupInCurrentSet)} written to this volume");
                }
                else
                {
                    // The whole operation outcome
                    ReportFileOperationOutcome(resultSoFar, "Backup", agentResult);
                }
                // Current statistics we report for each volume.
                ReportBackupStats(resultSoFar,
                    dataSecs: dataElapsedUs / 1e6, ioSecs: dataIoElapsedUs / 1e6, tocSecs: tocElapsedUs / 1e6);

                // --- Check for finish conditions ---

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

                // --- Handle media swap for multivolume continuation ---

                // Step 1: Ask user if they want to continue on a new volume
                if (!_host.OnVolumeFullConfirm(toc.Volume, toc.Volume + 1,
                        progressHandler.FilesProcessed, progressHandler.FilesTotal,
                        progressHandler.BytesProcessed, progressHandler.BytesTotal))
                {
                    LogInfo("User chose to end multi-volume backup");
                    break;
                }

                // Steps 2–5: eject, insert, load, and verify the fresh volume's identity.
                //  Retry re-runs the whole cycle; Abort / user-cancel exits the outer multi-volume loop.
                //  (The agent heads the fresh volume inside ResumeBackupToNextVolume → BeginWriteContentForCurrentSet,
                //  per §17.10 — the service never writes the continuation header itself.)
                bool cancelled = false;
                do
                {
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
                        cancelled = true;
                        break;
                    }

                    // Step 4: Load and prepare the new media (with load-retry)
                    LogInfo("Loading media...");
                    OnStatusUpdate("Loading media...");
                    const int maxLoadAttempts = 2;
                    bool mediaLoaded = false;
                    for (int loadAttempt = 1; loadAttempt <= maxLoadAttempts && !mediaLoaded; loadAttempt++)
                    {
                        bool loadOk = _drive.ReloadMedia();
                        string loadErr = _drive.LastErrorMessage;
                        if (loadOk && !_drive.PrepareMedia())
                        {
                            loadOk = false;
                            loadErr = _drive.LastErrorMessage;
                        }
                        if (loadOk)
                        {
                            mediaLoaded = true;
                        }
                        else
                        {
                            LogErr($"Couldn't load media: {loadErr}");
                            bool retryLoad = loadAttempt < maxLoadAttempts
                                && _host.OnMediaLoadRetryConfirm(loadErr, loadAttempt > 1);
                            if (!retryLoad)
                                throw new InvalidOperationException($"Couldn't load media: {loadErr}");
                            LogInfo("Retrying media load...");
                            OnStatusUpdate("Loading media...");
                        }
                    }

                    // Step 5 (§10.5 checkpoint 3): verify the continuation volume's identity.
                    RefreshLoadedHeader();
                    TapeMediaVerdict cvVerdict = _loadedHeader switch
                    {
                        null => TapeMediaVerdict.Unidentified,   // blank fresh volume — ideal
                        TapeCalibrationHeader => TapeMediaVerdict.WrongKind,
                        TapeMediaHeader m when m.MediaId == toc.MediaId => TapeMediaVerdict.WrongVolume,     // an earlier volume of THIS series
                        _ => TapeMediaVerdict.MediaIdMismatch, // a different backup
                    };

                    var cvChoice = PresentVerdict(
                        cvVerdict, MediaPromptContext.ContinuationVolume, suppress, allowRetry: true);

                    if (cvChoice == MediaMismatchChoice.Abort) { cancelled = true; break; }
                    if (cvChoice == MediaMismatchChoice.Retry) continue;   // re-eject, re-insert
                    if (cvChoice == MediaMismatchChoice.ProceedAlways) suppress = true;
                    break; // Proceed
                } while (true);
                if (cancelled)
                    break; // exit the outer multi-volume do-while

                LogOk("Media loaded, continuing backup...");


            } while (true); // the multivolume loop

            progressHandler.CompleteProgress();

            // After the loop, we need no addtl. report — the final iteration's numbers are the totals.
            //  Output just one line to finalize the multi-volume statistics.
            int volumes = toc.Volume - startVolume + 1;
            if (volumes > 0)
                LogInfoSub($"Across {volumes} volume(s)");

            if (wasAborted)
            {
                LogOk("TOC saved after abort");
                // Log timing even on abort
                double abortDataSecs = dataElapsedUs / 1e6;
                var abortParts = new List<string>(3)
                {
                    $"Before abort: {Helpers.BytesToString(progressHandler.BytesProcessed)} written"
                };
                string abortRate = FormatDataIoRate(progressHandler.BytesProcessed, abortDataSecs);
                if (abortRate.Length > 0) abortParts.Add(abortRate);
                LogInfoSub(string.Join(", ", abortParts));
                OnStatusUpdate("Backup aborted");
                return MakeResult(aborted: true);
            }

            var backupResult = MakeResult();

            if (backupResult is { HasFailed: true })
                LogFail("Backup completed with failures");
            else if (backupResult is { IsFullSuccess: true })
                LogOk("Backup completed successfully");
            else
                LogInfo("Backup completed");

            OnStatusUpdate("Backup complete");

            return backupResult;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            OnStatusUpdate("Backup failed");
            LogErr($"Backup failed: {ex.Message}");
            // The exception did not come through the agent, so build the diagnosis from it directly.
            return MakeResult(failed: true, diagnosis: TapeResult.Fail(ex)) with { ErrorException = ex };
        }
        finally
        {
            progressHandler?.DisposeProgress();
            _agent?.Dispose();
            _agent = null;
        }
    } // OLD

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
