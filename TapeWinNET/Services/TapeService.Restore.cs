using System.IO;

using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;

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
    /// <param name="setIndex">Backup set index to restore from (1-based).</param>
    /// <param name="incremental">Whether to traverse the incremental chain.</param>
    /// <param name="filePatterns">Optional file filter patterns (null = all files).</param>
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
        int setIndex,
        bool incremental,
        List<string>? filePatterns,
        string? targetDirectory,
        bool recurseSubdirectories,
        TapeHowToHandleExisting handleExisting,
        Action<int, int, long> progressCallback,
        Action<string> logCallback,
        Func<string, string, FileFailedAction> fileErrorCallback,
        Func<int, bool> volumeChangeCallback,
        Func<int, bool> insertMediaCallback,
        Action<string> currentFileCallback)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
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
                    logCallback($">>> {modeName} files...");
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
                        logCallback(">>> Restoring TOC from tape...");
                        Status("Reading TOC...");
                        if (!agent.RestoreTOC())
                        {
                            throw new InvalidOperationException($"Couldn't restore TOC: {agent.LastErrorMessage}");
                        }
                        _toc = toc;
                        logCallback($"vvv TOC restored with {toc.Count} backup set(s)");
                    }

                    // Set the current backup set
                    toc.CurrentSetIndex = setIndex;
                    var setTOC = toc.CurrentSetTOC;

                    logCallback($"iii {modeName} backup set #{setIndex}: {setTOC.Description}");
                    logCallback($" ii Files in set: {setTOC.Count:N0}");
                    logCallback($" ii Block size: {Helpers.BytesToString(setTOC.BlockSize)}");
                    logCallback($" ii Hash algorithm: {setTOC.HashAlgorithm}");
                    logCallback($" ii Incremental: {(setTOC.Incremental ? "Yes" : "No")}");
                    if (incremental && setTOC.Incremental)
                        logCallback($" ii {modeName} incremental backup set including earlier dependent sets");
                    if (mode == RestoreMode.Restore && !string.IsNullOrEmpty(targetDirectory))
                        logCallback($" ii Target directory: {targetDirectory}");
                    if (filePatterns != null && filePatterns.Count > 0)
                        logCallback($" ii File patterns: {string.Join(", ", filePatterns)}");

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

                    bool result = incremental
                        ? (filePatterns != null && filePatterns.Count > 0)
                            ? agent.RestoreFilesFromCurrentSetInc(filePatterns, ignoreFailures: true, progressHandler)
                            : agent.RestoreAllFilesFromCurrentSetInc(ignoreFailures: true, progressHandler)
                        : (filePatterns != null && filePatterns.Count > 0)
                            ? agent.RestoreFilesFromCurrentSet(filePatterns, ignoreFailures: true, progressHandler)
                            : agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, progressHandler);

                    // Handle multi-volume continuation
                    while (!result && agent.CanResumeFromAnotherVolume)
                    {
                        int volumeNeeded = agent.VolumeToResumeFrom;
                        logCallback($"iii {modeName} requires Volume #{volumeNeeded} to continue");

                        // Step 1: Ask user if they want to continue
                        if (!volumeChangeCallback(volumeNeeded))
                        {
                            logCallback($"iii User chose to end multi-volume {modeName.ToLowerInvariant()}");
                            break;
                        }

                        // Step 2: Eject current media
                        logCallback(">>> Ejecting media...");
                        Status("Ejecting media...");

                        if (!_drive.UnloadMedia())
                        {
                            throw new InvalidOperationException($"Couldn't eject media: {_drive.LastErrorMessage}");
                        }

                        logCallback($"vvv Volume #{toc.Volume} ejected");

                        // Step 3: Ask user to insert the required volume
                        if (!insertMediaCallback(volumeNeeded))
                        {
                            logCallback("iii User cancelled media insertion");
                            break;
                        }

                        // Step 4: Load and prepare the new media
                        logCallback(">>> Loading media...");
                        Status("Loading media...");

                        if (!_drive.ReloadMedia())
                        {
                            throw new InvalidOperationException($"Couldn't load media: {_drive.LastErrorMessage}");
                        }

                        if (!_drive.PrepareMedia())
                        {
                            throw new InvalidOperationException($"Couldn't prepare media: {_drive.LastErrorMessage}");
                        }

                        logCallback($"vvv Media loaded, continuing {modeName.ToLowerInvariant()}...");
                        Status($"{modeName} files...");

                        // Step 5: Resume restore on the new volume
                        result = agent.ResumeRestoreFromAnotherVolume();
                    }

                    // Log final results
                    if (!result && !agent.CanResumeFromAnotherVolume && progressHandler.FilesFailed > 0)
                    {
                        logCallback($"!!! {modeName}: {progressHandler.FilesFailed:N0} file(s) of {progressHandler.FilesProcessed:N0} failed");
                    }

                    logCallback($"vvv {modeName}: {progressHandler.FilesSucceeded:N0} file(s), {Helpers.BytesToString(agent.BytesRestored)}");
                    Status($"{modeName} complete");
                    logCallback($"vvv {modeName} completed successfully");
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
                    logCallback($"!!! {modeName} failed: {ex.Message}");
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
    /// </summary>
    private class GuiRestoreProgressHandler(
        TapeFileAgent agent,
        Action<string> logCallback,
        Action<int, int, long> progressCallback,
        Action<string> currentFileCallback,
        Func<string, string, FileFailedAction> fileErrorCallback,
        string modeName) : ITapeFileNotifiable
    {
        public int FilesProcessed { get; private set; }
        public int FilesTotal { get; private set; }
        public int FilesFailed { get; private set; }
        public int FilesSucceeded { get; private set; }
        public long BytesProcessed { get; private set; }

        private void ThrowIfAbortRequested()
        {
            if (agent.IsAbortRequested)
            {
                logCallback($"!!! {modeName} aborted by user");
                throw new TapeAbortRequestedException("User requested abort");
            }
        }

        public void BatchStartStatistics(int set, int filesFound)
        {
            FilesTotal += filesFound;
            FilesProcessed = 0;
            FilesFailed = 0;
            FilesSucceeded = 0;
            BytesProcessed = 0;

            logCallback($"iii Starting {modeName.ToLowerInvariant()} of {filesFound:N0} files from set #{set}...");
            progressCallback(0, FilesTotal, 0);
        }

        public void BatchEndStatistics(int set, int filesProcessed, int filesFailed, long bytesProcessed)
        {
            FilesProcessed = filesProcessed;
            FilesFailed = filesFailed;
            BytesProcessed = bytesProcessed;

            logCallback($"iii {modeName} batch complete: {filesProcessed - filesFailed:N0} succeeded, {filesFailed:N0} failed");
            progressCallback(filesProcessed, FilesTotal, bytesProcessed);
        }

        public bool PreProcessFile(ref TapeFileDescriptor fileDescr)
        {
            ThrowIfAbortRequested();

            currentFileCallback(fileDescr.FullName);
            return true;
        }

        public bool PostProcessFile(ref TapeFileDescriptor fileDescr)
        {
            ThrowIfAbortRequested();

            FilesSucceeded++;
            FilesProcessed++;
            BytesProcessed += fileDescr.Length;

            logCallback($"vvv {Path.GetFileName(fileDescr.FullName)} ({Helpers.BytesToString(fileDescr.Length)})");
            progressCallback(FilesProcessed, FilesTotal, BytesProcessed);

            return true;
        }

        public FileFailedAction OnFileFailed(TapeFileDescriptor fileDescr, Exception ex)
        {
            ThrowIfAbortRequested();

            FilesFailed++;

            logCallback($"!!! Failed: {fileDescr.FullName}");
            logCallback($"    Error: {ex.Message}");

            progressCallback(FilesProcessed, FilesTotal, BytesProcessed);

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
                logCallback($"!!! {modeName} aborted by user");
                throw new TapeAbortRequestedException("User requested abort");
            }

            return result;
        }

        public void OnFileSkipped(TapeFileDescriptor fileDescr)
        {
            ThrowIfAbortRequested();

            logCallback($" -- Skipped: {Path.GetFileName(fileDescr.FullName)}");
        }
    }

    #endregion
}
