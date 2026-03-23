using System.IO;

using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeWinNET.Converters;

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
/// Partial class containing restore/validate/verify operations for TapeService.
/// </summary>
public partial class TapeService
{
    /// <summary>
    /// Executes a restore, validate, or verify operation with the specified parameters.
    /// </summary>
    /// <param name="mode">The restore flavor to execute.</param>
    /// <param name="setIndexes">Backup set indexes to restore from (1-based). Multiple sets are combined into a single pass.</param>
    /// <param name="incremental">Whether to traverse the incremental chain for each set.</param>
    /// <param name="fileFilter">Optional file filter (null = all files).</param>
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
    public Task ExecuteRestoreAsync(
        RestoreMode mode,
        List<int> setIndexes,
        bool incremental,
        ITapeFileFilter? fileFilter,
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
#pragma warning disable CS8321 // some log helpers not used (yet), but might be later
                void logOk(string msg)         => logCallback(new LogEntry(WarningLevel.Completed, msg, false, DateTime.Now));
                void logOkSub(string msg)      => logCallback(new LogEntry(WarningLevel.Completed, msg, true, DateTime.Now));
                void logInfo(string msg)       => logCallback(new LogEntry(WarningLevel.Info, msg, false, DateTime.Now));
                void logInfoSub(string msg)    => logCallback(new LogEntry(WarningLevel.Info, msg, true, DateTime.Now));
                void logFail(string msg)       => logCallback(new LogEntry(WarningLevel.Failed, msg, false, DateTime.Now));
                void logErr(string msg)        => logCallback(new LogEntry(WarningLevel.Error, msg, false, DateTime.Now));
#pragma warning restore CS8321

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

                    // Log info about the sets being processed
                    foreach (var setIndex in setIndexes)
                    {
                        toc.CurrentSetIndex = setIndex;
                        var setTOC = toc.CurrentSetTOC;

                        logInfo($"{modeName} backup set #{setIndex} | {toc.SetIndexToAlt(setIndex)}: {setTOC.Description}");
                        logInfoSub($"Files in set: {setTOC.Count:N0}");
                        logInfoSub($"Block size: {Helpers.BytesToString(setTOC.BlockSize)}");
                        logInfoSub($"Hash algorithm: {setTOC.HashAlgorithm}");
                        logInfoSub($"Incremental: {(setTOC.Incremental ? "Yes" : "No")}");
                        if (incremental && setTOC.Incremental)
                            logInfoSub($"{modeName} incremental backup set including earlier dependent sets");
                    }
                    if (mode == RestoreMode.Restore && !string.IsNullOrEmpty(targetDirectory))
                        logInfoSub($"Target directory: {targetDirectory}");
                    if (fileFilter != null)
                        logInfoSub($"File filter active");

                    // Create progress handler
                    var progressHandler = new GuiRestoreProgressHandler(
                        agent,
                        logCallback,
                        progressCallback,
                        currentFileCallback,
                        fileErrorCallback,
                        modeName);

                    // Execute the restore operation
                    Status($"{modeName} files...");

                    bool result = agent.RestoreFilesFromSets(
                        setIndexes, incremental, fileFilter,
                        ignoreFailures: true, progressHandler);

                    // The agent catches TapeAbortRequestedException internally and returns false,
                    // so we detect abort via the flag rather than catching the exception.
                    bool wasAborted = agent.IsAbortRequested;

                    // Handle multi-volume continuation
                    while (!wasAborted && !result && agent.CanResumeFromAnotherVolume)
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
                        result = agent.ResumeRestoreFromAnotherVolume();
                        wasAborted = agent.IsAbortRequested;
                    }

                    // Handle abort
                    if (wasAborted)
                    {
                        logOk($"{modeName}: {progressHandler.FilesSucceeded:N0} file(s) succeeded before abort, {Helpers.BytesToString(agent.BytesRestored)}");
                        throw new TapeAbortRequestedException("User requested abort");
                    }

                    // Log final results
                    if (!result && !agent.CanResumeFromAnotherVolume && progressHandler.FilesFailed > 0)
                    {
                        logFail($"{modeName}: {progressHandler.FilesFailed:N0} file(s) of {progressHandler.FilesProcessed:N0} failed");
                    }
                    logOk($"{modeName}: {progressHandler.FilesSucceeded:N0} file(s) of {progressHandler.FilesProcessed:N0} succeeded, {Helpers.BytesToString(agent.BytesRestored)}");
                    Status($"{modeName} complete");
                    if (progressHandler.FilesFailed > 0)
                        logFail($"{modeName} completed with errors");
                    else
                        logOk($"{modeName} completed successfully");
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
    /// </summary>
    private class GuiRestoreProgressHandler(
        TapeFileAgent agent,
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
        public long BytesProcessed { get; private set; }

        private bool _abortLogged;

        private void Log(WarningLevel level, string msg, bool sub = false)
            => logCallback(new LogEntry(level, msg, sub, DateTime.Now));

        private void Sync(in TapeFileStatistics stats)
        {
            FilesTotal = stats.FilesTotal;
            FilesProcessed = stats.FilesProcessed;
            FilesSucceeded = stats.FilesSucceeded;
            FilesFailed = stats.FilesFailed;
            BytesProcessed = stats.BytesProcessed;
        }

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

        public void BatchStart(int setIndex, in TapeFileStatistics stats)
        {
            Sync(stats);
            var toc = agent.TOC;
            Log(WarningLevel.Info, $"Starting {modeName.ToLowerInvariant()} of {stats.FilesTotal:N0} files from set #{setIndex} | {toc.SetIndexToAlt(setIndex)}...");
            progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);
        }

        public void BatchEnd(int setIndex, in TapeFileStatistics stats)
        {
            Sync(stats);
            var toc = agent.TOC;
            Log(WarningLevel.Info, $"{modeName} from set #{setIndex} | {toc.SetIndexToAlt(setIndex)} complete: {stats.FilesSucceeded:N0} succeeded, {stats.FilesFailed:N0} failed");
            progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);
        }

        public bool PreProcessFile(ref TapeFileDescriptor fileDescr, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            currentFileCallback(fileDescr.FullName);
            return true;
        }

        public bool PostProcessFile(ref TapeFileDescriptor fileDescr, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            Sync(stats);

            Log(WarningLevel.Completed, $"{Path.GetFileName(fileDescr.FullName)} ({Helpers.BytesToString(fileDescr.Length)})");
            progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);

            return true;
        }

        public FileFailedAction OnFileFailed(TapeFileDescriptor fileDescr, Exception ex, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            Sync(stats);

            Log(WarningLevel.Failed, $"Failed: {fileDescr.FullName}");
            Log(WarningLevel.Failed, $"Error: {ex.Message}", sub: true);

            progressCallback(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed);

            // Don't show file error dialog for end-of-media errors —
            // the multi-volume logic handles these
            if (ex is IOException ioEx &&
                (ioEx.HResult == (int)WIN32_ERROR.ERROR_END_OF_MEDIA ||
                 ioEx.HResult == (int)WIN32_ERROR.ERROR_NO_DATA_DETECTED))
            {
                return FileFailedAction.Skip;
            }

            var result = fileErrorCallback(fileDescr.FullName, ex.Message);

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

        public void OnFileSkipped(TapeFileDescriptor fileDescr, in TapeFileStatistics stats)
        {
            ThrowIfAbortRequested();
            Sync(stats);

            Log(WarningLevel.None, $"Skipped: {Path.GetFileName(fileDescr.FullName)}", sub: true);
        }
    }

    #endregion
}
