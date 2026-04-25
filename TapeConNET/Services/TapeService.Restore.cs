using System.IO;

using Windows.Win32.System.SystemServices; // Helpers

using Stopwatch = Windows.Win32.System.SystemServices.Stopwatch;

using TapeConNET.Ux;
using TapeLibNET;

namespace TapeConNET.Services;

/// <summary>The three flavors of restore-like operations.</summary>
public enum RestoreMode
{
    /// <summary>Writes files to a target directory.</summary>
    Restore,
    /// <summary>Reads tape data and checks CRC integrity without writing files.</summary>
    Validate,
    /// <summary>Compares tape data byte-by-byte against existing files on disk.</summary>
    Verify
}

/// <summary>Summary statistics returned by a restore/validate/verify operation.</summary>
public record RestoreOperationResult(
    int FilesTotal,
    int FilesProcessed,
    int FilesSucceeded,
    int FilesFailed,
    int FilesSkipped,
    long BytesProcessed)
{
    public int FilesMissing => FilesTotal - FilesProcessed;
    public bool IsFullSuccess => !WasAborted && FilesFailed == 0 && FilesSkipped == 0 && FilesMissing == 0 && FilesProcessed > 0;
    public bool WasAborted { get; init; }
    public bool HasFailed { get; init; }

    /// <summary>
    /// Per-set dictionary of successfully processed files. Populated by the
    /// progress handler; kept to mirror the WPF result shape so callers may
    /// reuse these lists for post-operation bookkeeping.
    /// </summary>
    public Dictionary<int, List<TapeFileInfo>> ProcessedFiles { get; init; } = [];
}

/// <summary>Options bag for <see cref="TapeService.ExecuteRestoreAsync"/>.</summary>
public sealed record RestoreOptions(
    RestoreMode Mode,
    Dictionary<int, IReadOnlyList<TapeFileInfo>?> CheckedFilesBySet,
    bool Incremental,
    string? TargetDirectory,
    bool RecurseSubdirectories,
    TapeHowToHandleExisting HandleExisting,
    bool SkipAllErrors,
    ITapeFileFilter? Filter = null);

public partial class TapeService
{
    /// <summary>
    /// Executes a restore, validate, or verify operation. Multi-volume change
    /// requests and per-file error handling are routed through
    /// <see cref="IConsoleUx"/>; under <c>NonInteractive</c> the safe defaults
    /// (abort / skip-all) are used.
    /// </summary>
    public Task<RestoreOperationResult> ExecuteRestoreAsync(RestoreOptions options)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                GuiRestoreProgressHandler? progressHandler = null;
                TapeFileRestoreBaseAgent? agent = null;
                IProgressScope? progress = null;

                RestoreOperationResult MakeResult(bool aborted = false, bool failed = false) => new(
                    progressHandler?.FilesTotal ?? 0,
                    progressHandler?.FilesProcessed ?? 0,
                    progressHandler?.FilesSucceeded ?? 0,
                    progressHandler?.FilesFailed ?? 0,
                    progressHandler?.FilesSkipped ?? 0,
                    progressHandler?.BytesProcessed ?? 0)
                { WasAborted = aborted, HasFailed = failed };

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

    private sealed class GuiRestoreProgressHandler(
        IConsoleUx ux,
        TapeFileAgent agent,
        IProgressScope progress,
        int totalFilesToProcess,
        bool skipAllErrors,
        string modeName) : ITapeFileNotifiable
    {
        public int FilesProcessed { get; private set; }
        public int FilesTotal { get; private set; }
        public int FilesFailed { get; private set; }
        public int FilesSucceeded { get; private set; }
        public int FilesSkipped { get; private set; }
        public long BytesProcessed { get; private set; }
        public Dictionary<int, List<TapeFileInfo>> ProcessedFiles { get; } = [];

        private TapeFileStatistics _batchStartSnapshot;
        private bool _abortLogged;

        private void Log(WarningLevel level, string msg, bool sub = false)
            => ux.Log(new LogEntry(level, msg, sub));

        private void Sync(in TapeFileStatistics stats)
        {
            FilesTotal     = stats.FilesTotal;
            FilesProcessed = stats.FilesProcessed;
            FilesSucceeded = stats.FilesSucceeded;
            FilesFailed    = stats.FilesFailed;
            FilesSkipped   = stats.FilesSkipped;
            BytesProcessed = stats.BytesProcessed;
        }

        private void ReportProgress(in TapeFileStatistics stats, string? status = null)
        {
            int total = totalFilesToProcess > 0 ? totalFilesToProcess : stats.FilesTotal;
            if (total > 0)
            {
                double pct = 100.0 * stats.FilesProcessed / total;
                progress.Report(pct, status);
            }
            else if (status is not null)
            {
                progress.Report(0, status);
            }
        }

        public RestoreOperationResult GenerateResult() =>
            new(FilesTotal, FilesProcessed, FilesSucceeded, FilesFailed, FilesSkipped, BytesProcessed)
            {
                ProcessedFiles = ProcessedFiles
            };

        private void ThrowIfAbortRequested()
        {
            if (agent.IsAbortRequested)
            {
                if (!_abortLogged)
                {
                    _abortLogged = true;
                    Log(WarningLevel.Warning, $"{modeName} abort requested");
                }
                throw new TapeAbortRequestedException("User requested abort");
            }
        }

        private void AddToProcessed(in TapeFileInfo fileInfo)
        {
            int setIndex = agent.TOC.CurrentSetIndex;
            if (!ProcessedFiles.TryGetValue(setIndex, out var list))
            {
                list = [];
                ProcessedFiles[setIndex] = list;
            }
            list.Add(fileInfo);
        }

        public void BatchStart(int setIndex, in TapeFileStatistics stats)
        {
            _batchStartSnapshot = stats;
            Sync(stats);
            var toc = agent.TOC;
            Log(WarningLevel.Info, $"Set #{setIndex} | {toc.SetIndexToAlt(setIndex)}: starting {modeName.ToLowerInvariant()}...");
            ReportProgress(stats);
        }

        public void BatchEnd(int setIndex, in TapeFileStatistics stats)
        {
            Sync(stats);
            var toc = agent.TOC;
            var batch = stats.Delta(in _batchStartSnapshot);

            var level = batch.FilesFailed > 0 ? WarningLevel.Failed
                      : batch.FilesSkipped > 0 ? WarningLevel.Warning
                      : WarningLevel.Completed;
            var parts = new List<string>(3) { $"{batch.FilesSucceeded:N0} succeeded" };
            if (batch.FilesFailed > 0) parts.Add($"{batch.FilesFailed:N0} failed");
            if (batch.FilesSkipped > 0) parts.Add($"{batch.FilesSkipped:N0} skipped");

            Log(level, $"Set #{setIndex} | {toc.SetIndexToAlt(setIndex)} complete: {string.Join(", ", parts)}");
            ReportProgress(stats);
        }

        public bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
        {
            Sync(stats);
            ThrowIfAbortRequested();
            ReportProgress(stats, fileInfo.FileDescr.FullName);
            return true;
        }

        public bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
        {
            Sync(stats);
            ThrowIfAbortRequested();
            Log(WarningLevel.Completed, $"'{Path.GetFileName(fileInfo.FileDescr.FullName)}' {Helpers.BytesToString(fileInfo.FileDescr.Length)}", sub: true);
            ReportProgress(stats);
            AddToProcessed(fileInfo);
            return true;
        }

        public FileFailedAction OnFileFailed(TapeFileInfo fileInfo, TapeResult result, in TapeFileStatistics stats)
        {
            Sync(stats);
            ThrowIfAbortRequested();

            Log(WarningLevel.Failed, $"Failed: '{fileInfo.FileDescr.FullName}'");
            Log(WarningLevel.Failed, $"Error: {result.ErrorMessage}", sub: true);
            ReportProgress(stats);

            // ERROR_END_OF_MEDIA = 1100, ERROR_NO_DATA_DETECTED = 1104 (winerror.h)
            if (result.ErrorCode == 1100u || result.ErrorCode == 1104u)
                return FileFailedAction.Skip;

            if (skipAllErrors || ux.NonInteractive)
                return FileFailedAction.Skip;

            var choice = ux.Select(
                "File failed — choose action",
                ["Skip", "Retry", "Skip all", "Abort"],
                defaultChoice: "Skip");

            switch (choice)
            {
                case "Retry":
                    return FileFailedAction.Retry;
                case "Skip all":
                    skipAllErrors = true;
                    return FileFailedAction.Skip;
                case "Abort":
                    if (!_abortLogged)
                    {
                        _abortLogged = true;
                        Log(WarningLevel.Warning, $"{modeName} abort requested");
                    }
                    throw new TapeAbortRequestedException("User requested abort");
                default:
                    return FileFailedAction.Skip;
            }
        }

        public void OnFileSkipped(TapeFileInfo fileInfo, in TapeFileStatistics stats)
        {
            Sync(stats);
            ThrowIfAbortRequested();
            Log(WarningLevel.None, $"Skipped: {Path.GetFileName(fileInfo.FileDescr.FullName)}", sub: true);
        }
    }

    #endregion
}
