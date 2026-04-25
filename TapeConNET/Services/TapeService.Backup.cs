using System.IO;

using Windows.Win32.System.SystemServices; // Helpers

using Stopwatch = Windows.Win32.System.SystemServices.Stopwatch;

using TapeConNET.Ux;
using TapeLibNET;
using TapeLibNET.Services;

namespace TapeConNET.Services;

public partial class TapeService
{
    /// <summary>
    /// Executes a backup operation. Multi-volume continuation, file-error
    /// handling and emergency TOC recovery all prompt the user via
    /// <see cref="IConsoleUx"/>; under <c>NonInteractive</c> the safe default
    /// (abort / skip-all) is used so unattended runs never hang.
    /// </summary>
    /// <remarks>
    /// To abort a backup in progress, set <c>Agent.IsAbortRequested = true</c>
    /// (typically wired to Ctrl+C via the constructor-supplied
    /// <see cref="CancellationToken"/>).
    /// </remarks>
    public Task<BackupResult> ExecuteBackupAsync(BackupRequest options)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                GuiBackupProgressHandler? progressHandler = null;
                TapeFileBackupAgent? agent = null;
                IProgressScope? progress = null;

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

                if (options.FileList.Count == 0)
                {
                    LogInfo("No files to backup");
                    return MakeResult();
                }

                // Bridge Ctrl+C → agent.IsAbortRequested for the duration of this call
                using var ctReg = _ct.Register(() =>
                {
                    var a = _agent; if (a is not null) a.IsAbortRequested = true;
                });

                try
                {
                    LogInfo("Preparing media for backup...");
                    if (!_drive.PrepareMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        throw new InvalidOperationException($"Couldn't prepare media: {LastError}");
                    }

                    bool append = options.AppendMode && _toc != null;

                    // --- TOC preparation (mirrors WPF logic exactly) ---
                    _agent?.Dispose();
                    _agent = new TapeFileBackupAgent(_drive, _toc);
                    agent = (TapeFileBackupAgent)_agent;
                    var toc = agent.TOC;
                    TapeTOC? backupTOC = null;
                    bool appendAfterSetUsed = false;
                    int appendAfterSetIndex = toc.SetIndexToStd(options.AppendAfterSetIndex);

                    int capacityHint = !options.Incremental && !options.ListContainsPatterns
                        ? options.FileList.Count : 0;

                    // Mode 1: Append after specific set
                    if (append && appendAfterSetIndex > toc.FirstSetOnVolume && appendAfterSetIndex < toc.LastSetOnVolume)
                    {
                        LogInfo($"Appending after backup set #{appendAfterSetIndex} | {toc.SetIndexToAlt(appendAfterSetIndex)}");
                        backupTOC = new TapeTOC(toc);
                        appendAfterSetUsed = true;
                        toc.CurrentSetIndex = appendAfterSetIndex + 1;
                        toc.ReplaceCurrentSetTOC(capacityHint, options.Incremental);
                    }
                    // Mode 3: Overwrite
                    else if (!append)
                    {
                        LogInfo("Creating new backup, replacing all existing content");
                        backupTOC = new TapeTOC(toc);
                        toc.RemoveAllSets();
                    }
                    // else: Mode 2 — straight append

                    if (string.IsNullOrEmpty(toc.Description))
                        toc.Description = $"Media created {DateTime.Now}";

                    bool newSet;
                    if (append)
                    {
                        if (toc.CurrentSetTOC.Count > 0)
                        {
                            toc.AddNewSetTOC(capacityHint, options.Incremental);
                            newSet = true;
                        }
                        else
                        {
                            toc.MarkCurrentSetIncremental(options.Incremental);
                            newSet = false;
                        }
                    }
                    else
                    {
                        newSet = true;
                    }

                    toc.CurrentSetTOC.Description = options.Description;
                    toc.CurrentSetTOC.HashAlgorithm = options.HashAlgorithm;
                    toc.CurrentSetTOC.BlockSize = options.BlockSize;
                    toc.CurrentSetTOC.FmksMode = options.UseFilemarks;

                    LogInfo($"Backup set: >{options.Description}<");
                    LogInfoSub($"Block size: {Helpers.BytesToString(options.BlockSize)}");
                    LogInfoSub($"Hash algorithm: {options.HashAlgorithm}");
                    LogInfoSub($"Incremental: {(options.Incremental ? "Yes" : "No")}");
                    if (options.ListContainsPatterns)
                        LogInfoSub($"Patterns / folders to backup: {options.FileList.Count:N0}");
                    else
                        LogInfoSub($"Files to backup: {options.FileList.Count:N0}");

                    progress = _ux.BeginProgress("Backing up");
                    progressHandler = new GuiBackupProgressHandler(
                        _ux,
                        agent,
                        progress,
                        options.SkipAllErrors,
                        options.Filter);

                    // --- Backup loop (multi-volume) ---
                    bool wasAborted = false;
                    var dataTimer = new Stopwatch();
                    var tocTimer = new Stopwatch();
                    long dataElapsedUs = 0;
                    long tocElapsedUs = 0;

                    do
                    {
                        dataTimer.Restart();
                        bool result = agent.CanResumeToNextVolume
                            ? agent.ResumeBackupToNextVolume()
                            : options.ListContainsPatterns
                                ? agent.BackupFilesToCurrentSet(newSet, options.FileList, options.IncludeSubdirectories, ignoreFailures: true, progressHandler)
                                : agent.BackupFileListToCurrentSet(newSet, options.FileList, ignoreFailures: true, progressHandler);
                        dataTimer.Stop();
                        dataElapsedUs += dataTimer.ElapsedMicroseconds;

                        wasAborted = agent.IsAbortRequested;

                        bool noFilesBackedUp = toc.CurrentSetTOC.Count == 0;
                        bool skipTOCSave = false;

                        if (noFilesBackedUp)
                        {
                            if (backupTOC is not null)
                            {
                                if (!agent.Manager.ContentWritten)
                                {
                                    toc.CopyFrom(backupTOC);
                                    if (!result && !wasAborted && !agent.CanResumeToNextVolume)
                                        LogErr("No files backed up");
                                    else
                                        LogInfo("No files were backed up");
                                }
                                else
                                {
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

                            if (!agent.CanResumeToNextVolume && !agent.Navigator.TOCInvalidated)
                            {
                                skipTOCSave = true;
                                _toc = toc;
                            }
                        }

                        if (agent.CanResumeToNextVolume)
                            LogInfo($"Volume #{toc.Volume} is full - backup can continue to next volume");

                        if (!noFilesBackedUp)
                        {
                            if (appendAfterSetUsed)
                                toc.RemoveSetsAfterCurrent();

                            if (!result && !wasAborted && !agent.CanResumeToNextVolume)
                                LogFail("Some files failed to back up");
                        }

                        if (!noFilesBackedUp && !agent.CanResumeToNextVolume)
                        {
                            if (toc.ContinuedOnNextVolume)
                            {
                                toc.ContinuedOnNextVolume = false;
                                skipTOCSave = false;
                            }
                        }

                        if (!skipTOCSave)
                        {
                            tocTimer.Restart();

                            if (!wasAborted)
                                LogInfo("Backing up TOC...");
                            else
                                LogInfo("Abort requested — saving TOC to preserve media integrity...");

                            var tocResult = agent.BackupTOC();
                            if (!tocResult)
                            {
                                LogErr($"Couldn't backup TOC. Error: {tocResult.ErrorMessage}");
                                LogInfo("Attempting to enforce TOC backup...");

                                var enforceResult = agent.BackupTOC(enforce: true);
                                if (!enforceResult)
                                {
                                    LogErr("Couldn't enforce TOC backup");

                                    if (!TryEmergencyTocExport(agent, toc, options.EmergencyTocFolder))
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

                            tocTimer.Stop();
                            tocElapsedUs += tocTimer.ElapsedMicroseconds;

                            _toc = toc;
                        }

                        // Per-volume summary
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
                        _ux.Log(new LogEntry(headlineLevel, headlineMsg));

                        if (progressHandler.FilesProcessed > 0)
                        {
                            var parts = new List<string>(4) { $"{progressHandler.FilesSucceeded:N0} succeeded" };
                            if (progressHandler.FilesFailed > 0) parts.Add($"{progressHandler.FilesFailed:N0} failed");
                            if (progressHandler.FilesSkipped > 0) parts.Add($"{progressHandler.FilesSkipped:N0} skipped");
                            parts.Add($"{Helpers.BytesToString(agent.BytesBackedup)} written");
                            LogInfoSub(string.Join(", ", parts));

                            double dataSecs = dataElapsedUs / 1e6;
                            double tocSecs = tocElapsedUs / 1e6;
                            var timingParts = new List<string>(3) { FormatElapsed(dataSecs) };
                            string rate = FormatDataRate(agent.BytesBackedup, dataSecs);
                            if (rate.Length > 0) timingParts.Add(rate);
                            if (tocSecs >= 1.0) timingParts.Add($"TOC save {FormatElapsed(tocSecs)}");
                            LogInfoSub(string.Join(", ", timingParts));
                        }
                        LogInfoSub($"Remaining media capacity: {Helpers.BytesToStringLong(_drive.GetContentRemainingCapacity())}");

                        if (wasAborted)
                            break;

                        if (!agent.CanResumeToNextVolume)
                            break;

                        // Multi-volume: ask user
                        if (!_ux.Confirm(
                                $"Volume #{toc.Volume} is full. Continue backup on a new volume #{toc.Volume + 1}?",
                                defaultAnswer: false))
                        {
                            LogInfo("User chose to end multi-volume backup");
                            break;
                        }

                        LogInfo("Ejecting media...");
                        if (!_drive.UnloadMedia())
                            throw new InvalidOperationException($"Couldn't eject media: {_drive.LastErrorMessage}");
                        LogOk($"Volume #{toc.Volume} ejected");

                        if (!_ux.Confirm(
                                $"Insert blank/next media for volume #{toc.Volume + 1} and continue?",
                                defaultAnswer: true))
                        {
                            LogInfo("User cancelled media insertion");
                            break;
                        }

                        if (!LoadNextVolumeMedia())
                            throw new InvalidOperationException($"Couldn't load media: {_drive.LastErrorMessage}");

                        LogOk("Media loaded, continuing backup...");

                    } while (true);

                    // Finalize progress display
                    progress?.Complete();

                    if (wasAborted)
                    {
                        LogOk("TOC saved after abort");
                        double abortDataSecs = dataElapsedUs / 1e6;
                        var abortParts = new List<string>(3)
                        {
                            $"Before abort: {Helpers.BytesToString(agent.BytesBackedup)} written"
                        };
                        string abortRate = FormatDataRate(agent.BytesBackedup, abortDataSecs);
                        if (abortRate.Length > 0) abortParts.Add(abortRate);
                        LogInfoSub(string.Join(", ", abortParts));
                        return MakeResult(aborted: true);
                    }

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
                    LogErr($"Backup failed: {ex.Message}");
                    return MakeResult(failed: true);
                }
                finally
                {
                    progress?.Dispose();
                    _agent?.Dispose();
                    _agent = null;
                }
            }
        }, _ct);
    }

    /// <summary>
    /// Loads the next-volume media with a 2-attempt retry loop, prompting the
    /// user via <see cref="IConsoleUx"/> on failure. Returns true on success.
    /// </summary>
    private bool LoadNextVolumeMedia()
    {
        if (_drive is null) return false;

        LogInfo("Loading media...");

        const int maxLoadAttempts = 2;
        for (int attempt = 1; attempt <= maxLoadAttempts; attempt++)
        {
            bool loadOk = _drive.ReloadMedia();
            string loadError = _drive.LastErrorMessage;

            if (loadOk && !_drive.PrepareMedia())
            {
                loadOk = false;
                loadError = _drive.LastErrorMessage;
            }

            if (loadOk)
                return true;

            LogErr($"Couldn't load media: {loadError}");

            if (attempt >= maxLoadAttempts)
                return false;

            if (!_ux.Confirm("Retry loading media?", defaultAnswer: true))
                return false;

            LogInfo("Retrying media load...");
        }
        return false;
    }

    /// <summary>
    /// Attempts to export the in-memory TOC to a file as a last-resort recovery
    /// when on-tape TOC backup has failed. Mirrors the WPF emergency-export flow.
    /// </summary>
    private bool TryEmergencyTocExport(TapeFileBackupAgent agent, TapeTOC toc, string? folderHint)
    {
        LogInfo("Attempting to export TOC to file as emergency recovery...");

        string suggestedPath = BuildEmergencyTocExportPath(toc, folderHint);

        const int maxExportAttempts = 2;
        for (int attempt = 1; attempt <= maxExportAttempts; attempt++)
        {
            string chosenPath = _ux.Ask(
                attempt == 1
                    ? "Emergency TOC export path"
                    : "Emergency TOC export failed — try a different path",
                defaultValue: suggestedPath);

            if (string.IsNullOrWhiteSpace(chosenPath))
            {
                LogWarn("User declined emergency TOC export");
                return false;
            }

            var saveResult = agent.SaveTOCToFile(chosenPath);
            if (saveResult)
            {
                LogOk($"Emergency TOC exported to: {chosenPath}");
                LogInfoSub("This file can be used to recover access to the media content");
                IsTOCFromFile = true;
                TOCFilePath = chosenPath;
                return true;
            }

            LogErr($"Failed to export emergency TOC to file: {saveResult.ErrorMessage}");
            if (attempt < maxExportAttempts)
                LogInfoSub("You can try a different location...");
        }
        return false;
    }

    private static string BuildEmergencyTocExportPath(TapeTOC toc, string? folderHint = null)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(
                [.. (toc.Description ?? "tape").Select(c => invalidChars.Contains(c) ? '_' : c)]
            ).Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "tape";

        if (sanitized.Length > 60)
            sanitized = sanitized[..60];

        var fileName = $"{sanitized}_vol{toc.Volume}_{DateTime.Now:yyyyMMdd_HHmmss}{TapeFileAgent.TOCFileExtension}";
        var directory = !string.IsNullOrWhiteSpace(folderHint) && Directory.Exists(folderHint)
            ? folderHint
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(directory, fileName);
    }

    #region Helper Class — Progress handler

    /// <summary>
    /// <see cref="ServiceBackupProgressHandler"/> subclass that additionally
    /// drives a bounded <see cref="IProgressScope"/> for the console progress bar.
    /// All core logic (logging, abort, file-failed prompts) lives in the shared base.
    /// </summary>
    private sealed class GuiBackupProgressHandler(
        IConsoleUx ux,
        TapeFileAgent agent,
        IProgressScope progress,
        bool skipAllErrors,
        ITapeFileFilter? filter = null)
        : ServiceBackupProgressHandler(new ConsoleUxServiceHost(ux), agent, skipAllErrors, filter)
    {
        protected override void ReportProgress(in TapeFileStatistics stats, string? currentFile = null)
        {
            if (stats.FilesTotal > 0)
            {
                double pct = 100.0 * stats.FilesProcessed / stats.FilesTotal;
                progress.Report(pct, currentFile);
            }
            else if (currentFile is not null)
            {
                progress.Report(0, currentFile);
            }
        }
    }

    #endregion
}
