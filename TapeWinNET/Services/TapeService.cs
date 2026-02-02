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
}