using System.IO;

using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeWinNET.Converters;
using TapeWinNET.Models;

namespace TapeWinNET.Services;

/// <summary>
/// The three flavors of restore-like operations.
/// </summary>
public enum RestoreMode
{
    /// <summary>Writes files to a target directory.</summary>
    Restore,
    /// <summary>Reads tape data and checks CRC integrity without writing files.</summary>
    Validate,
    /// <summary>Compares tape data byte-by-byte against existing files on disk.</summary>
    Verify
}

/// <summary>
/// Summary statistics returned by a restore/validate/verify operation.
/// Allows the caller to distinguish full success, partial failure, and
///  "no files found" without needing an additional callback.
/// </summary>
public record RestoreOperationResult(
    int FilesTotal,
    int FilesProcessed,
    int FilesSucceeded,
    int FilesFailed,
    int FilesSkipped,
    long BytesProcessed)
{
    /// <summary>Files that were selected but never encountered on tape.</summary>
    public int FilesMissing => FilesTotal - FilesProcessed;

    /// <summary>Whether the operation completed without any issues.</summary>
    public bool IsFullSuccess => FilesFailed == 0 && FilesSkipped == 0 && FilesMissing == 0 && FilesProcessed > 0;

    /// <summary>
    /// Per-set dictionary of successfully processed files, populated by
    ///  <see cref="TapeService.GuiRestoreProgressHandler"/>. Used to uncheck
    ///  processed files after the operation when the user opts in.
    /// </summary>
    public Dictionary<int, List<TapeFileInfo>> ProcessedFiles { get; init; } = [];
}

/// <summary>
/// Partial class containing restore/validate/verify operations for TapeService.
/// </summary>
public partial class TapeService
{
    /// <summary>
    /// Executes a restore, validate, or verify operation with the specified parameters.
    /// </summary>
    /// <param name="mode">The restore flavor to execute.</param>
    /// <param name="checkedFilesBySet">Per-set file selection. Keys are 1-based set indexes.
    ///  A <c>null</c> value means all files in that set; a non-null list means only those files.</param>
    /// <param name="incremental">Whether to traverse the incremental chain for each set.</param>
    /// <param name="targetDirectory">Target directory for Restore mode (ignored for Validate/Verify).</param>
    /// <param name="recurseSubdirectories">Whether to recreate subdirectory structure (Restore only).</param>
    /// <param name="handleExisting">How to handle existing files (Restore only).</param>
    /// <param name="progressCallback">Callback for progress updates (filesProcessed, totalFiles, bytesProcessed).</param>
    /// <param name="logCallback">Callback for log messages.</param>
    /// <param name="fileErrorCallback">Callback when a file error occurs. Returns FileFailedAction.</param>
    /// <param name="volumeChangeCallback">Callback when restore needs to continue on another volume. Receives the required volume number. Returns true to continue, false to abort.</param>
    /// <param name="insertMediaCallback">Callback after media ejection, prompting user to insert media for the specified volume. Returns true when ready, false to abort.</param>
    /// <param name="currentFileCallback">Callback to report current file being processed.</param>
    /// <remarks>
    /// To abort a restore in progress, set Agent.IsAbortRequested = true.
    /// The agent is available via the Agent property during execution.
    /// </remarks>
    public Task<RestoreOperationResult> ExecuteRestoreAsync(
        RestoreMode mode,
        Dictionary<int, IReadOnlyList<TapeFileInfo>?> checkedFilesBySet,
        bool incremental,
        string? targetDirectory,
        bool recurseSubdirectories,
        TapeHowToHandleExisting handleExisting,
        Action<int, int, long> progressCallback,
        Action<LogEntry> logCallback,
        Func<string, string, FileFailedAction> fileErrorCallback,
        Func<int, bool> volumeChangeCallback,
        Func<int, bool> insertMediaCallback,
        Action<string> currentFileCallback)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
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

                string modeName = mode switch
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
                    TapeFileRestoreBaseAgent agent = mode switch
                    {
                        RestoreMode.Restore => new TapeFileRestoreAgentEx(
                            _drive, targetDirectory, recurseSubdirectories, handleExisting, _toc),
                        RestoreMode.Validate => new TapeFileValidateAgent(_drive, _toc),
                        RestoreMode.Verify => new TapeFileVerifyAgent(_drive, _toc),
                        _ => throw new ArgumentOutOfRangeException(nameof(mode))
                    };
                    _agent = agent;
                    var toc = agent.TOC;

                    // Restore TOC if not already loaded
                    if (_toc == null)
                    {
                        logInfo("Restoring TOC from tape...");
                        Status("Reading TOC...");
                        if (!agent.RestoreTOC())
                        {
                            throw new InvalidOperationException($"Couldn't restore TOC: {agent.LastErrorMessage}");
                        }
                        _toc = toc;
                        logOk($"TOC restored with {toc.Count} backup set(s)");
                    }

                    // Derive set indexes from the dictionary keys
                    var setIndexes = checkedFilesBySet.Keys.OrderBy(i => i).ToList();

                    // Select files and build the work package
                    var combined = toc.SelectFilesFromSets(incremental, checkedFilesBySet);
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
                    if (mode == RestoreMode.Restore && !string.IsNullOrEmpty(targetDirectory))
                        logInfoSub($"Target directory: {targetDirectory}");
                    toc.CurrentSetIndex = newestIdx; // restore after iterating

                    // Create progress handler
                    var progressHandler = new GuiRestoreProgressHandler(
                        agent,
                        totalFiles,
                        logCallback,
                        progressCallback,
                        currentFileCallback,
                        fileErrorCallback,
                        modeName);

                    Status($"{modeName} files...");

                    // Call agent to do the actual work —
                    //  this will trigger callbacks for progress and file-level events
                    bool success = agent.RestoreFilesFromCurrentSetDown(
                        combined, ignoreFailures: true, progressHandler);

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

                        if (!_drive.ReloadMedia())
                        {
                            throw new InvalidOperationException($"Couldn't load media: {_drive.LastErrorMessage}");
                        }

                        if (!_drive.PrepareMedia())
                        {
                            throw new InvalidOperationException($"Couldn't prepare media: {_drive.LastErrorMessage}");
                        }

                        logOk($"Media loaded, continuing {modeName.ToLowerInvariant()}...");
                        Status($"{modeName} files...");

                        // Step 5: Resume restore on the new volume
                        success = agent.ResumeRestoreFromAnotherVolume();
                        wasAborted = agent.IsAbortRequested;
                    } // while multi-volume continuation

                    // The final tally
                    var result = progressHandler.GenerateResult();

                    // Handle abort
                    if (wasAborted)
                    {
                        logWarn($"{modeName} of {result.FilesTotal:N0} file(s): aborting per user request");
                        var bytesProcessed = long.Max(result.BytesProcessed, agent.BytesRestored); // in case BatchEnd wasn't called due to abort
                        logInfoSub($"Before abort: {result.FilesSucceeded:N0} succeeded, {Helpers.BytesToString(bytesProcessed)} processed");
                        throw new TapeAbortRequestedException("User requested abort");
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
                    }
                    if (result.FilesMissing > 0)
                        logWarnSub($"{result.FilesMissing:N0} file(s) not found on tape");

                    Status($"{modeName} complete");

                    return result;
                }
                catch (TapeAbortRequestedException)
                {
                    Status($"{modeName} aborted");
                    throw; // Re-throw to be handled by caller
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Status($"{modeName} failed");
                    logErr($"{modeName} failed: {ex.Message}");
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

    #region Helper Classes - Restore

    /// <summary>
    /// Progress handler for GUI restore/validate/verify operations.
    /// Implements ITapeFileNotifiable to bridge between the restore agent and the UI.
    /// All statistics come from the library via <see cref="TapeFileStatistics"/>.
    /// Per-batch (per-set) statistics are derived as deltas from successive snapshots.
    /// </summary>
    private class GuiRestoreProgressHandler(
        TapeFileAgent agent,
        int TotalFilesToProcess,
        Action<LogEntry> logCallback,
        Action<int, int, long> progressCallback,
        Action<string> currentFileCallback,
        Func<string, string, FileFailedAction> fileErrorCallback,
        string modeName) : ITapeFileNotifiable
    {
        // Convenience accessors — always reflect the latest library snapshot
        public int FilesProcessed { get; private set; }
        public int FilesTotal { get; private set; }
        public int FilesFailed { get; private set; }
        public int FilesSucceeded { get; private set; }
        public int FilesSkipped { get; private set; }
        public long BytesProcessed { get; private set; }
        public Dictionary<int, List<TapeFileInfo>> ProcessedFiles { get; private set; } = [];

        /// <summary>Snapshot taken at each BatchStart for computing per-set deltas.</summary>
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
            var toc = agent.TOC;
            int setIndex = toc.CurrentSetIndex;

            // Add the file to the processed list for this set
            if (!ProcessedFiles.TryGetValue(setIndex, out List<TapeFileInfo>? value))
            {
                value = [];
                ProcessedFiles[setIndex] = value;
            }

            value.Add(fileInfo);
        }

        public void BatchStart(int setIndex, in TapeFileStatistics stats)
        {
            _batchStartSnapshot = stats; // capture snapshot for computing per-set deltas in BatchEnd
            Sync(stats);
            var toc = agent.TOC;

            Log(WarningLevel.Info, $"Set #{setIndex} | {toc.SetIndexToAlt(setIndex)}: starting {modeName.ToLowerInvariant()}...");
            progressCallback(stats.FilesProcessed, TotalFilesToProcess, stats.BytesProcessed);
        }

        public void BatchEnd(int setIndex, in TapeFileStatistics stats)
        {
            Sync(stats);
            var toc = agent.TOC;

            // Compute per-set statistics as delta from the snapshot taken at BatchStart
            var batch = stats.Delta(in _batchStartSnapshot);

            // Build a concise per-set summary
            var level = batch.FilesFailed > 0 ? WarningLevel.Failed
                      : batch.FilesSkipped > 0 ? WarningLevel.Warning
                      : WarningLevel.Completed;
            var parts = new List<string>(3) { $"{batch.FilesSucceeded:N0} succeeded" };
            if (batch.FilesFailed > 0) parts.Add($"{batch.FilesFailed:N0} failed");
            if (batch.FilesSkipped > 0) parts.Add($"{batch.FilesSkipped:N0} skipped");

            Log(level, $"Set #{setIndex} | {toc.SetIndexToAlt(setIndex)} complete: {string.Join(", ", parts)}");
            progressCallback(stats.FilesProcessed, TotalFilesToProcess, stats.BytesProcessed);
        }

        public bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
        {
            Sync(stats);
            ThrowIfAbortRequested();

            currentFileCallback(fileInfo.FileDescr.FullName);
            return true;
        }

        public bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
        {
            Sync(stats);
            ThrowIfAbortRequested();

            Log(WarningLevel.Completed, $"'{Path.GetFileName(fileInfo.FileDescr.FullName)}' {Helpers.BytesToString(fileInfo.FileDescr.Length)}", sub: true);
            progressCallback(stats.FilesProcessed, TotalFilesToProcess, stats.BytesProcessed);

            AddToProcessed(fileInfo);

            return true;
        }

        public FileFailedAction OnFileFailed(TapeFileInfo fileInfo, Exception ex, in TapeFileStatistics stats)
        {
            Sync(stats);
            ThrowIfAbortRequested();

            Log(WarningLevel.Failed, $"Failed: '{fileInfo.FileDescr.FullName}'");
            Log(WarningLevel.Failed, $"Error: {ex.Message}", sub: true);

            progressCallback(stats.FilesProcessed, TotalFilesToProcess, stats.BytesProcessed);

            // Don't show file error dialog for end-of-media errors —
            // the multi-volume logic handles these
            if (ex is IOException ioEx &&
                (ioEx.HResult == (int)WIN32_ERROR.ERROR_END_OF_MEDIA ||
                 ioEx.HResult == (int)WIN32_ERROR.ERROR_NO_DATA_DETECTED))
            {
                return FileFailedAction.Skip;
            }

            var result = fileErrorCallback(fileInfo.FileDescr.FullName, ex.Message);

            if (result == FileFailedAction.Abort)
            {
                if (!_abortLogged)
                {
                    _abortLogged = true;
                    Log(WarningLevel.Warning, $"{modeName} abort requested");
                }
                throw new TapeAbortRequestedException("User requested abort");
            }

            return result;
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
