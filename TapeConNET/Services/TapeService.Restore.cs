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
    /// Executes a restore, validate, or verify operation. Multi-volume change
    /// requests and per-file error handling are routed through
    /// <see cref="IConsoleUx"/>; under <c>NonInteractive</c> the safe defaults
    /// (abort / skip-all) are used.
    /// </summary>
    public Task<RestoreResult> ExecuteRestoreAsync(RestoreRequest options)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                GuiRestoreProgressHandler? progressHandler = null;
                TapeFileRestoreBaseAgent? agent = null;
                IProgressScope? progress = null;

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

                string modeName = options.Mode switch
                {
                    RestoreMode.Restore  => "Restoring",
                    RestoreMode.Validate => "Validating",
                    RestoreMode.Verify   => "Verifying",
                    _ => "Processing"
                };

                using var ctReg = _ct.Register(() =>
                {
                    var a = _agent; if (a is not null) a.IsAbortRequested = true;
                });

                try
                {
                    LogInfo($"{modeName} files...");
                    if (!_drive.PrepareMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        throw new InvalidOperationException($"Couldn't prepare media: {LastError}");
                    }

                    _agent?.Dispose();
                    agent = options.Mode switch
                    {
                        RestoreMode.Restore => new TapeFileRestoreAgentEx(
                            _drive, options.TargetDirectory, options.RecurseSubdirectories, options.HandleExisting, _toc),
                        RestoreMode.Validate => new TapeFileValidateAgent(_drive, _toc),
                        RestoreMode.Verify   => new TapeFileVerifyAgent(_drive, _toc),
                        _ => throw new ArgumentOutOfRangeException(nameof(options))
                    };
                    _agent = agent;
                    var toc = agent.TOC;

                    if (_toc is null)
                    {
                        LogInfo("Restoring TOC from tape...");
                        var tocResult = agent.RestoreTOC();
                        if (!tocResult)
                            throw new InvalidOperationException($"Couldn't restore TOC: {tocResult.ErrorMessage}");
                        _toc = toc;
                        LogOk($"TOC restored with {toc.Count} backup set(s)");
                    }

                    var setIndexes = options.CheckedFilesBySet.Keys.OrderBy(i => i).ToList();

                    // Apply the optional FCL/wildcard selection filter to any
                    //  entry whose value is "all files in set" (null). Entries
                    //  with an explicit list are kept as-is.
                    var checkedBySet = options.CheckedFilesBySet;
                    if (options.Filter is not null)
                    {
                        checkedBySet = new Dictionary<int, IReadOnlyList<TapeFileInfo>?>(options.CheckedFilesBySet.Count);
                        foreach (var kv in options.CheckedFilesBySet)
                        {
                            if (kv.Value is not null)
                            {
                                checkedBySet[kv.Key] = kv.Value;
                                continue;
                            }
                            int stdIdx = toc.SetIndexToStd(kv.Key);
                            // SelectFiles returns null when the filter matches every file
                            //  in the set; that maps cleanly back to "all files".
                            checkedBySet[kv.Key] = toc[stdIdx].SelectFiles(options.Filter);
                        }
                    }

                    var combined = toc.SelectFilesFromSets(options.Incremental, checkedBySet);
                    int newestIdx = setIndexes.Max();
                    toc.CurrentSetIndex = newestIdx;

                    var (totalFiles, perSet) = toc.GetFileCounts(combined, newestIdx);
                    LogInfo($"{modeName} {totalFiles:N0} file(s) from {perSet.Count} set(s)");
                    foreach (var setIndex in setIndexes)
                    {
                        toc.CurrentSetIndex = setIndex;
                        int count = perSet.GetValueOrDefault(setIndex);
                        LogInfoSub($"From set #{setIndex} | {toc.SetIndexToAlt(setIndex)}: {toc.CurrentSetTOC.Description}: {count:N0} file(s)");
                    }
                    if (options.Mode == RestoreMode.Restore && !string.IsNullOrEmpty(options.TargetDirectory))
                        LogInfoSub($"Target folder: {options.TargetDirectory}");
                    toc.CurrentSetIndex = newestIdx;

                    progress = _ux.BeginProgress(modeName);
                    progressHandler = new GuiRestoreProgressHandler(
                        _ux,
                        agent,
                        progress,
                        totalFiles,
                        options.SkipAllErrors,
                        modeName);

                    var dataTimer = new Stopwatch();
                    long dataElapsedUs = 0;

                    dataTimer.Start();
                    bool success = agent.RestoreFilesFromCurrentSetDown(
                        combined, ignoreFailures: true, progressHandler);
                    dataTimer.Stop();
                    dataElapsedUs += dataTimer.ElapsedMicroseconds;

                    bool wasAborted = agent.IsAbortRequested;

                    while (!wasAborted && !success && agent.CanResumeFromAnotherVolume)
                    {
                        int volumeNeeded = agent.VolumeToResumeFrom;
                        LogInfo($"{modeName} requires Volume #{volumeNeeded} to continue");

                        if (!_ux.Confirm(
                                $"Continue {modeName.ToLowerInvariant()} on Volume #{volumeNeeded}?",
                                defaultAnswer: false))
                        {
                            LogInfo($"User chose to end multi-volume {modeName.ToLowerInvariant()}");
                            break;
                        }

                        LogInfo("Ejecting media...");
                        if (!_drive.UnloadMedia())
                            throw new InvalidOperationException($"Couldn't eject media: {_drive.LastErrorMessage}");
                        LogOk($"Volume #{toc.Volume} ejected");

                        if (!_ux.Confirm(
                                $"Insert media for volume #{volumeNeeded} and continue?",
                                defaultAnswer: true))
                        {
                            LogInfo("User cancelled media insertion");
                            break;
                        }

                        if (!LoadNextVolumeMedia())
                            throw new InvalidOperationException($"Couldn't load media: {_drive.LastErrorMessage}");

                        LogOk($"Media loaded, continuing {modeName.ToLowerInvariant()}...");

                        dataTimer.Restart();
                        success = agent.ResumeRestoreFromAnotherVolume();
                        dataTimer.Stop();
                        dataElapsedUs += dataTimer.ElapsedMicroseconds;
                        wasAborted = agent.IsAbortRequested;
                    }

                    progress?.Complete();

                    var result = progressHandler.GenerateResult();

                    if (wasAborted)
                    {
                        LogWarn($"{modeName} of {result.FilesTotal:N0} file(s): aborting per user request");
                        var bytesProcessed = long.Max(result.BytesProcessed, agent.BytesRestored);
                        double abortSecs = dataElapsedUs / 1e6;
                        var abortParts = new List<string>(3)
                        {
                            $"Before abort: {result.FilesSucceeded:N0} succeeded",
                            $"{Helpers.BytesToString(bytesProcessed)} processed"
                        };
                        string abortRate = FormatDataRate(bytesProcessed, abortSecs);
                        if (abortRate.Length > 0) abortParts.Add(abortRate);
                        LogInfoSub(string.Join(", ", abortParts));
                        return result with { WasAborted = true };
                    }

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

                    _ux.Log(new LogEntry(headlineLevel, headlineMsg));

                    if (result.FilesProcessed > 0)
                    {
                        var parts = new List<string>(4) { $"{result.FilesSucceeded:N0} succeeded" };
                        if (result.FilesFailed > 0) parts.Add($"{result.FilesFailed:N0} failed");
                        if (result.FilesSkipped > 0) parts.Add($"{result.FilesSkipped:N0} skipped");
                        parts.Add($"{Helpers.BytesToString(result.BytesProcessed)} processed");
                        LogInfoSub(string.Join(", ", parts));

                        double dataSecs = dataElapsedUs / 1e6;
                        var timingParts = new List<string>(2) { FormatElapsed(dataSecs) };
                        string rate = FormatDataRate(result.BytesProcessed, dataSecs);
                        if (rate.Length > 0) timingParts.Add(rate);
                        LogInfoSub(string.Join(", ", timingParts));
                    }
                    if (result.FilesMissing > 0)
                        LogWarnSub($"{result.FilesMissing:N0} file(s) not found on tape");

                    return result;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"{modeName} failed: {ex.Message}");
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

    #region Helper Class — Restore progress handler

    /// <summary>
    /// <see cref="ServiceRestoreProgressHandler"/> subclass that additionally
    /// drives a bounded <see cref="IProgressScope"/> for the console progress bar.
    /// All core logic (logging, abort, file-failed prompts) lives in the shared base.
    /// </summary>
    private sealed class GuiRestoreProgressHandler(
        IConsoleUx ux,
        TapeFileAgent agent,
        IProgressScope progress,
        int totalFilesToProcess,
        bool skipAllErrors,
        string modeName)
        : ServiceRestoreProgressHandler(new ConsoleUxServiceHost(ux), agent, totalFilesToProcess, skipAllErrors, modeName)
    {
        protected override void ReportProgress(in TapeFileStatistics stats, string? currentFile = null)
        {
            int total = TotalFilesToProcess > 0 ? TotalFilesToProcess : stats.FilesTotal;
            if (total > 0)
            {
                double pct = 100.0 * stats.FilesProcessed / total;
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
