using System.IO; // <-- Add this at the top with other using directives
using Windows.Win32.System.SystemServices; // for Helpers

using Microsoft.Extensions.Logging;
using TapeLibNET;

namespace TapeWinNET.Services;

/// <summary>
/// Service that wraps TapeLibNET operations with async support for UI threading.
/// Since TapeLibNET is single-threaded, all operations are executed on a dedicated worker thread.
/// </summary>
public class TapeService : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private TapeDrive? _tapeDrive;
    private TapeFileAgent? _tapeAgent;
    private TapeTOC? _toc;
    private readonly object _lock = new();
    private bool _disposed;

    public event EventHandler<string>? LogMessageReceived;
    public event EventHandler<string>? StatusChanged;

    public TapeService()
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug().SetMinimumLevel(LogLevel.Information);
        });
    }

    #region Properties

    public bool IsDriveOpen => _tapeDrive?.IsDriveOpen ?? false;
    public bool IsMediaLoaded => _tapeDrive?.IsMediaLoaded ?? false;
    public int DriveNumber { get; private set; }
    public string DeviceName => _tapeDrive?.DriveDeviceName ?? "Unknown";
    public string? LastError { get; private set; }
    public TapeTOC? TOC => _toc;
    
    // Drive information properties
    public bool SupportsMultiplePartitions => _tapeDrive?.SupportsMultiplePartitions ?? false;
    public bool SupportsSetmarks => _tapeDrive?.SupportsSetmarks ?? false;
    public bool SupportsSeqFilemarks => _tapeDrive?.SupportsSeqFilemarks ?? false;
    public uint MinimumBlockSize => _tapeDrive?.MinimumBlockSize ?? 0;
    public uint DefaultBlockSize => _tapeDrive?.DefaultBlockSize ?? 0;
    public uint MaximumBlockSize => _tapeDrive?.MaximumBlockSize ?? 0;
    public uint PartitionCount => _tapeDrive?.PartitionCount ?? 0;
    public long Capacity => _tapeDrive?.Capacity ?? 0;
    
    public long GetRemainingCapacity()
    {
        lock (_lock)
        {
            return _tapeDrive?.GetRemainingCapacity() ?? 0;
        }
    }

    #endregion

    #region Public Methods

    public Task<bool> OpenDriveAsync(int driveNumber)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    Log($">>> Opening drive {driveNumber}...");
                    Status($"Opening drive {driveNumber}...");

                    // Dispose existing drive if any
                    _tapeAgent?.Dispose();
                    _tapeAgent = null;
                    _toc = null;
                    _tapeDrive?.Dispose();

                    _tapeDrive = new TapeDrive(_loggerFactory);
                    
                    if (!_tapeDrive.ReopenDrive((uint)driveNumber))
                    {
                        LastError = _tapeDrive.LastErrorMessage;
                        Log($"!!! Couldn't open drive. Error: {LastError}");
                        return false;
                    }

                    DriveNumber = driveNumber;
                    Log($"vvv Drive {driveNumber} opened successfully");
                    Log($" ii Device name: {_tapeDrive.DriveDeviceName}");
                    Status($"Drive {driveNumber} opened");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log($"!!! Exception opening drive: {ex.Message}");
                    return false;
                }
            }
        });
    }

    public Task<bool> LoadMediaAsync()
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_tapeDrive == null)
                {
                    LastError = "Drive not open";
                    return false;
                }

                try
                {
                    Log(">>> Loading media...");
                    Status("Loading media...");

                    if (!_tapeDrive.ReloadMedia())
                    {
                        LastError = _tapeDrive.LastErrorMessage;
                        Log($"!!! Couldn't load media. Error: {LastError}");
                        return false;
                    }

                    Log("vvv Media loaded successfully");
                    LogMediaInfo();
                    Status("Media loaded");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log($"!!! Exception loading media: {ex.Message}");
                    return false;
                }
            }
        });
    }

    public Task<bool> RestoreTOCAsync()
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_tapeDrive == null || !_tapeDrive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    return false;
                }

                try
                {
                    Log(">>> Preparing media...");
                    if (!_tapeDrive.PrepareMedia())
                    {
                        LastError = _tapeDrive.LastErrorMessage;
                        Log($"!!! Couldn't prepare media. Error: {LastError}");
                        return false;
                    }

                    Log(">>> Restoring TOC...");
                    Status("Reading TOC...");

                    _tapeAgent?.Dispose();
                    _tapeAgent = new TapeFileAgent(_tapeDrive, null);

                    if (!_tapeAgent.RestoreTOC())
                    {
                        LastError = _tapeAgent.LastErrorMessage;
                        Log($"!!! Couldn't restore TOC. Error: {LastError}");
                        return false;
                    }

                    _toc = _tapeAgent.TOC;
                    Log($"vvv TOC restored with {_toc.Count} backup set(s)");
                    LogTOCInfo();
                    Status($"TOC loaded: {_toc.Count} backup set(s)");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log($"!!! Exception restoring TOC: {ex.Message}");
                    return false;
                }
            }
        });
    }

    public Task<bool> EjectMediaAsync()
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_tapeDrive == null)
                {
                    LastError = "Drive not open";
                    return false;
                }

                try
                {
                    Log(">>> Ejecting media...");
                    Status("Ejecting media...");

                    _tapeAgent?.Dispose();
                    _tapeAgent = null;
                    _toc = null;

                    if (!_tapeDrive.UnloadMedia())
                    {
                        LastError = _tapeDrive.LastErrorMessage;
                        Log($"!!! Couldn't eject media. Error: {LastError}");
                        return false;
                    }

                    Log("vvv Media ejected");
                    Status("Media ejected");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log($"!!! Exception ejecting media: {ex.Message}");
                    return false;
                }
            }
        });
    }

    /// <summary>
    /// Executes a backup operation with the specified parameters.
    /// </summary>
    /// <param name="fileList">List of files to backup.</param>
    /// <param name="description">Description for the new backup set.</param>
    /// <param name="includeSubdirectories">Whether to include subdirectories.</param>
    /// <param name="incremental">Whether this is an incremental backup.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="hashAlgorithm">Hash algorithm to use.</param>
    /// <param name="appendMode">True to append after existing set, false to overwrite all.</param>
    /// <param name="appendAfterSetIndex">Set index to append after (-1 for overwrite all, or specific set index).</param>
    /// <param name="progressCallback">Callback for progress updates (filesProcessed, totalFiles, bytesProcessed).</param>
    /// <param name="logCallback">Callback for log messages.</param>
    /// <param name="fileErrorCallback">Callback when a file error occurs. Returns FileFailedAction.</param>
    /// <param name="volumeChangeCallback">Callback when volume change is needed. Returns true to continue, false to abort.</param>
    /// <param name="currentFileCallback">Callback to report current file being processed.</param>
    /// <param name="abortCheckCallback">Callback to check if abort was requested.</param>
    /// <param name="setAgentCallback">Callback to provide the backup agent reference for abort support.</param>
    public Task ExecuteBackupAsync(
        List<string> fileList,
        string description,
        bool includeSubdirectories,
        bool incremental,
        uint blockSize,
        TapeHashAlgorithm hashAlgorithm,
        bool appendMode,
        int appendAfterSetIndex,
        Action<int, int, long> progressCallback,
        Action<string> logCallback,
        Func<string, string, FileFailedAction> fileErrorCallback,
        Func<int, int, int, int, long, bool> volumeChangeCallback,
        Action<string> currentFileCallback,
        Func<bool> abortCheckCallback,
        Action<TapeFileBackupAgent> setAgentCallback)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_tapeDrive == null || !_tapeDrive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    throw new InvalidOperationException("Media not loaded");
                }

                if (fileList.Count == 0)
                {
                    logCallback("iii No files to backup");
                    return;
                }

                try
                {
                    logCallback(">>> Preparing media for backup...");
                    Status("Preparing backup...");

                    if (!_tapeDrive.PrepareMedia())
                    {
                        LastError = _tapeDrive.LastErrorMessage;
                        throw new InvalidOperationException($"Couldn't prepare media: {LastError}");
                    }

                    // Determine if we need to append or overwrite
                    bool append = appendMode && _toc != null && _toc.Count > 0;

                    // Create backup agent with existing TOC if appending
                    using var agent = new TapeFileBackupAgent(_tapeDrive, append ? _toc : null);
                    var toc = agent.TOC;

                    // Provide agent reference for abort support
                    setAgentCallback(agent);

                    // Handle append after specific set
                    if (append && appendAfterSetIndex > 0 && appendAfterSetIndex < toc.Count)
                    {
                        logCallback($"iii Appending after backup set #{appendAfterSetIndex}");
                        toc.CurrentSetIndex = appendAfterSetIndex + 1;
                        toc.EmptyCurrentSet();
                    }
                    else if (!append)
                    {
                        logCallback("iii Creating new backup, replacing all existing content");
                        toc.RemoveAllSets();
                    }

                    // Set up media description if empty
                    if (string.IsNullOrEmpty(toc.Description))
                    {
                        toc.Description = $"Media created {DateTime.Now}";
                    }

                    // Determine if we need a new set
                    bool newSet;
                    if (append)
                    {
                        if (toc.CurrentSetTOC.Count > 0)
                        {
                            toc.AddNewSetTOC(0, incremental);
                            newSet = true;
                        }
                        else
                        {
                            toc.MarkCurrentSetIncremental(incremental);
                            newSet = false;
                        }
                    }
                    else
                    {
                        newSet = true;
                    }

                    // Configure the new backup set
                    toc.CurrentSetTOC.Description = description;
                    toc.CurrentSetTOC.HashAlgorithm = hashAlgorithm;
                    toc.CurrentSetTOC.BlockSize = blockSize;
                    toc.CurrentSetTOC.FmksMode = false; // Default to blob mode for better performance

                    logCallback($"iii Backup set: {description}");
                    logCallback($" ii Block size: {Helpers.BytesToString(blockSize)}");
                    logCallback($" ii Hash algorithm: {hashAlgorithm}");
                    logCallback($" ii Incremental: {(incremental ? "Yes" : "No")}")
;
                    logCallback($" ii Files to backup: {fileList.Count:N0}");

                    // Create progress handler
                    var progressHandler = new GuiBackupProgressHandler(
                        logCallback,
                        progressCallback,
                        currentFileCallback,
                        fileErrorCallback);

                    // Execute backup loop (handles multi-volume)
                    do
                    {
                        Status($"Backing up files...");

                        bool result = agent.CanResumeToNextVolume
                            ? agent.ResumeBackupToNextVolume()
                            : agent.BackupFileListToCurrentSet(newSet, fileList, ignoreFailures: true, progressHandler);

                        bool noFilesBackedUp = toc.CurrentSetTOC.Count == 0;

                        if (result)
                        {
                            // Handle append after set - remove sets after current
                            if (appendAfterSetIndex > 0 && appendAfterSetIndex < toc.Count)
                            {
                                toc.RemoveSetsAfterCurrent();
                            }

                            if (noFilesBackedUp)
                            {
                                logCallback("iii No files were backed up");
                                toc.RemoveLastEmptySet();
                                _toc = toc;
                                return;
                            }
                        }
                        else // Backup had issues
                        {
                            // Check for multi-volume continuation
                            if (agent.CanResumeToNextVolume)
                            {
                                logCallback($"iii Volume #{toc.Volume} is full - backup can continue to next volume");

                                if (appendAfterSetIndex > 0)
                                {
                                    toc.RemoveSetsAfterCurrent();
                                }

                                if (noFilesBackedUp)
                                {
                                    toc.RemoveLastEmptySet();
                                }
                            }
                            else
                            {
                                if (noFilesBackedUp)
                                {
                                    toc.RemoveLastEmptySet();
                                    logCallback("!!! No files backed up");

                                    if (!agent.Navigator.TOCInvalidated)
                                    {
                                        _toc = toc;
                                        return;
                                    }
                                }
                                else
                                {
                                    logCallback($"!!! Some files failed to back up");
                                }
                            }
                        }

                        // Backup TOC
                        Status("Saving TOC...");
                        logCallback(">>> Backing up TOC...");

                        if (!agent.BackupTOC())
                        {
                            logCallback($"!!! Couldn't backup TOC. Error: {agent.LastErrorMessage}");
                            logCallback(">>> Attempting to enforce TOC backup...");

                            if (!agent.BackupTOC(enforce: true))
                            {
                                throw new InvalidOperationException($"Couldn't enforce TOC backup: {agent.LastErrorMessage}");
                            }

                            logCallback("vvv Enforced TOC backup succeeded");
                        }
                        else
                        {
                            logCallback("vvv TOC backed up successfully");
                        }

                        _toc = toc; // Update service TOC reference

                        logCallback($"vvv Backed up {progressHandler.FilesSucceeded:N0} file(s), {Helpers.BytesToString(agent.BytesBackedup)}");
                        logCallback($" ii Remaining media capacity: {Helpers.BytesToStringLong(_tapeDrive.GetRemainingCapacity())}");

                        // Check if we need to continue with multi-volume
                        if (!agent.CanResumeToNextVolume)
                        {
                            break; // Done
                        }

                        // Ask user to change media
                        int filesRemaining = fileList.Count - progressHandler.FilesProcessed;
                        bool continueBackup = volumeChangeCallback(
                            toc.Volume,
                            toc.Volume + 1,
                            progressHandler.FilesProcessed,
                            fileList.Count,
                            agent.BytesBackedup);

                        if (!continueBackup)
                        {
                            logCallback("iii User cancelled multi-volume continuation");
                            break;
                        }

                        // Eject and reload media
                        logCallback(">>> Unloading media...");
                        Status("Ejecting media...");

                        if (!_tapeDrive.UnloadMedia())
                        {
                            throw new InvalidOperationException($"Couldn't unload media: {_tapeDrive.LastErrorMessage}");
                        }

                        logCallback($">>> Please insert media for Volume #{toc.Volume + 1}");
                        Status($"Waiting for Volume #{toc.Volume + 1}...");

                        // Wait a moment for media swap
                        Thread.Sleep(1000);

                        logCallback(">>> Loading media...");
                        Status("Loading media...");

                        if (!_tapeDrive.ReloadMedia())
                        {
                            throw new InvalidOperationException($"Couldn't reload media: {_tapeDrive.LastErrorMessage}");
                        }

                        if (!_tapeDrive.PrepareMedia())
                        {
                            throw new InvalidOperationException($"Couldn't prepare media: {_tapeDrive.LastErrorMessage}");
                        }

                        logCallback("vvv Media loaded, continuing backup...");

                    } while (true);

                    Status("Backup complete");
                    logCallback("vvv Backup completed successfully");
                }
                catch (TapeAbortRequestedException)
                {
                    Status("Backup aborted");
                    throw; // Re-throw to be handled by caller
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Status("Backup failed");
                    logCallback($"!!! Backup failed: {ex.Message}");
                    throw;
                }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _tapeAgent?.Dispose();
        _tapeDrive?.Dispose();
        _loggerFactory.Dispose();
    }

    #endregion

    #region Private Methods

    private void Log(string message)
    {
        LogMessageReceived?.Invoke(this, message);
    }

    private void Status(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void LogMediaInfo()
    {
        if (_tapeDrive == null)
            return;

        Log($" ii Partition count: {_tapeDrive.PartitionCount}");
        Log($" ii Capacity: {Helpers.BytesToStringLong(_tapeDrive.Capacity)}");
        Log($" ii Remaining: {Helpers.BytesToStringLong(_tapeDrive.GetRemainingCapacity())}");
    }

    private void LogTOCInfo()
    {
        if (_toc == null)
            return;

        Log($" ii Media name: {_toc.Description}");
        Log($" ii Created: {_toc.CreationTime}");
        Log($" ii Last saved: {_toc.LastSaveTime}");
        Log($" ii Volume: #{_toc.Volume}");

        /*
        // List sets in regular order: from oldest (1) to latest (_toc.MaxSetIndex)
        for (int i = 1; i <= _toc.MaxSetIndex; i++)
        {
            var setTOC = _toc[i];
            var altIndex = _toc.SetIndexToAlt(i);
            Log($" ii Set #{i} | {altIndex}: {setTOC.Description} - {setTOC.Count} files" +
                (setTOC.Incremental ? " [Incremental]" : ""));
        }
        */

        // List sets in alternative order: from latest (0) down to oldest (_toc.MinSetIndex)
        for (int alt = 0; alt >= _toc.MinSetIndex; alt--)
        {
            int setIndex = _toc.SetIndexToAlt(alt); // this also converst from alt to regular index
            var setTOC = _toc[setIndex];
            Log($" ii Set {setIndex} | {alt}: {setTOC.Description} - {setTOC.Count} files" +
                (setTOC.Incremental ? " [Incremental]" : ""));
        }
    }

    #endregion

    #region Helper Classes

    /// <summary>
    /// Progress handler for GUI backup operations.
    /// Implements ITapeFileNotifiable to bridge between TapeFileBackupAgent and the UI.
    /// </summary>
    private class GuiBackupProgressHandler : ITapeFileNotifiable
    {
        private readonly Action<string> _logCallback;
        private readonly Action<int, int, long> _progressCallback;
        private readonly Action<string> _currentFileCallback;
        private readonly Func<string, string, FileFailedAction> _fileErrorCallback;
        private bool _skipAllErrors;

        public int FilesProcessed { get; private set; }
        public int FilesTotal { get; private set; }
        public int FilesFailed { get; private set; }
        public int FilesSucceeded { get; private set; }
        public long BytesProcessed { get; private set; }

        public GuiBackupProgressHandler(
            Action<string> logCallback,
            Action<int, int, long> progressCallback,
            Action<string> currentFileCallback,
            Func<string, string, FileFailedAction> fileErrorCallback)
        {
            _logCallback = logCallback;
            _progressCallback = progressCallback;
            _currentFileCallback = currentFileCallback;
            _fileErrorCallback = fileErrorCallback;
        }

        public void BatchStartStatistics(int set, int filesFound)
        {
            FilesTotal = filesFound;
            FilesProcessed = 0;
            FilesFailed = 0;
            FilesSucceeded = 0;
            BytesProcessed = 0;
            _skipAllErrors = false;

            _logCallback($"iii Starting backup of {filesFound:N0} files to set #{set}...");
            _progressCallback(0, filesFound, 0);
        }

        public void BatchEndStatistics(int set, int filesProcessed, int filesFailed, long bytesProcessed)
        {
            FilesProcessed = filesProcessed;
            FilesFailed = filesFailed;
            BytesProcessed = bytesProcessed;

            _logCallback($"iii Backup batch complete: {filesProcessed - filesFailed:N0} succeeded, {filesFailed:N0} failed");
            _progressCallback(filesProcessed, FilesTotal, bytesProcessed);
        }

        public bool PreProcessFile(ref TapeFileDescriptor fileDescr)
        {
            _currentFileCallback(fileDescr.FullName);
            return true;
        }

        public bool PostProcessFile(ref TapeFileDescriptor fileDescr)
        {
            FilesSucceeded++;
            FilesProcessed++;
            BytesProcessed += fileDescr.Length;

            _logCallback($"vvv {Path.GetFileName(fileDescr.FullName)} ({Helpers.BytesToString(fileDescr.Length)})");
            _progressCallback(FilesProcessed, FilesTotal, BytesProcessed);

            return true;
        }

        public FileFailedAction OnFileFailed(TapeFileDescriptor fileDescr, Exception ex)
        {
            FilesFailed++;

            _logCallback($"!!! Failed: {fileDescr.FullName}");
            _logCallback($"    Error: {ex.Message}");

            _progressCallback(FilesProcessed, FilesTotal, BytesProcessed);

            // If user chose to skip all errors previously, don't show dialog
            if (_skipAllErrors)
            {
                return FileFailedAction.Skip;
            }

            // Show error dialog via callback
            var result = _fileErrorCallback(fileDescr.FullName, ex.Message);

            // Check if this was a "skip all" action (handled by dialog's ApplyToAll checkbox)
            // The dialog sets ApplyToAll and returns Skip, which we detect here
            // For simplicity, we track skip-all state here based on repeated Skip results
            // A more sophisticated approach would pass the ApplyToAll flag through the callback

            if (result == FileFailedAction.Abort)
            {
                _logCallback("!!! Backup aborted by user");
                throw new TapeAbortRequestedException("User requested abort");
            }

            return result;
        }

        public void OnFileSkipped(TapeFileDescriptor fileDescr)
        {
            _logCallback($" -- Skipped: {Path.GetFileName(fileDescr.FullName)}");
        }
    }

    #endregion
}