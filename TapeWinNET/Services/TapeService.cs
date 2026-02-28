using System.IO;

using Windows.Win32.System.SystemServices; // for Helpers

using Microsoft.Extensions.Logging;
#if !DEBUG
using Microsoft.Extensions.Logging.Abstractions; // for NullLoggerFactory
#endif

using TapeLibNET;
using TapeLibNET.Virtual;
using TapeWinNET.Converters;

namespace TapeWinNET.Services;

/// <summary>
/// Information to create or open a file-based new virtual media
/// </summary>
public record VirtualMediaDescriptor(
    string ContentPath,
    long ContentCapacity,
    string? InitiatorPath,
    long InitiatorPartitionCapacity
);


/// <summary>
/// Service that wraps TapeLibNET operations with async support for UI threading.
/// Since TapeLibNET is single-threaded, all operations are executed on a dedicated worker thread.
/// </summary>
public partial class TapeService : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private TapeDrive? _drive;
    private TapeFileAgent? _agent;
    private TapeTOC? _toc;
    private VirtualMediaDescriptor? _vmdLast = null;
        // for convenience feature: to preset values for next virtual media
    private readonly object _lock = new();
    private bool _disposed;

    public event EventHandler<LogEntry>? LogMessageReceived;
    public event EventHandler<string>? StatusChanged;


    public TapeService()
    {
#if DEBUG
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug().SetMinimumLevel(LogLevel.Trace);
        });
#else
        _loggerFactory = Debugger.IsAttached ?
            LoggerFactory.Create(builder =>
            {
                builder
                    .AddDebug()
                    .SetMinimumLevel(LogLevel.Information);
            }) :
            NullLoggerFactory.Instance;
#endif
    }

    #region Properties

    public bool IsDriveOpen => _drive?.IsDriveOpen ?? false;
    public bool IsMediaLoaded => _drive?.IsMediaLoaded ?? false;
    public int DriveNumber { get; private set; }
    public string DeviceName => _drive?.DriveDeviceName ?? "Unknown";
    public string? LastError { get; private set; }
    public TapeTOC? TOC => _toc;

    /// <summary>
    /// Returns the current tape agent if an operation is in progress, null otherwise.
    /// Use to check/set IsAbortRequested during backup or restore operations.
    /// </summary>
    public TapeFileAgent? Agent => _agent;
    public bool IsAbortRequested => _agent?.IsAbortRequested ?? false;

    // Drive information properties
    public bool SupportsInitiatorPartition => _drive?.SupportsInitiatorPartition ?? false;
    public bool SupportsSetmarks => _drive?.SupportsSetmarks ?? false;
    public bool SupportsSeqFilemarks => _drive?.SupportsSeqFilemarks ?? false;
    public uint MinimumBlockSize => _drive?.MinimumBlockSize ?? 0;
    public uint DefaultBlockSize => _drive?.DefaultBlockSize ?? 0;
    public uint MaximumBlockSize => _drive?.MaximumBlockSize ?? 0;
    public uint PartitionCount => _drive?.PartitionCount ?? 0;
    public bool HasInitiatorPartition => _drive?.HasInitiatorPartition ?? false;
    public long Capacity => _drive?.Capacity ?? 0;
    
    public long GetRemainingCapacity()
    {
        lock (_lock)
        {
            return _drive?.GetRemainingCapacity() ?? 0;
        }
    }

    /// <summary>
    /// Whether the current drive is a virtual tape drive.
    /// </summary>
    public bool IsVirtualDrive => _drive?.Backend is VirtualTapeDriveBackend;
    public VirtualMediaDescriptor? LastVMD => _vmdLast;

    /// <summary>
    /// Gets the current IO speed simulation rate for the virtual drive, or 0 if not virtual.
    /// </summary>
    public long VirtualIoRateBytesPerSecond =>
        _drive?.Backend is VirtualTapeDriveBackend vb ? vb.IoRateBytesPerSecond : 0;

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
                    LogInfo($"Opening drive {driveNumber}...");
                    Status($"Opening drive {driveNumber}...");

                    // Dispose existing drive if any
                    _agent?.Dispose();
                    _agent = null;
                    _toc = null;
                    _drive?.Dispose();

                    _drive = TapeDrive.CreateWin32(_loggerFactory);

                    if (!_drive.ReopenDrive((uint)driveNumber))
                    {
                        LastError = _drive.LastErrorMessage;
                        LogErr($"Couldn't open drive. Error: {LastError}");
                        return false;
                    }

                    DriveNumber = driveNumber;
                    LogOk($"Drive {driveNumber} opened successfully");
                    LogInfoSub($"Device name: {_drive.DriveDeviceName}");
                    Status($"Drive {driveNumber} opened");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception opening drive: {ex.Message}");
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
                if (_drive == null)
                {
                    LastError = "Drive not open";
                    return false;
                }

                try
                {
                    LogInfo("Loading media...");
                    Status("Loading media...");

                    if (!_drive.ReloadMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        LogErr($"Couldn't load media. Error: {LastError}");
                        return false;
                    }

                    // if multiple partitions, move to content partition to load correct media params
                    if (_drive.HasInitiatorPartition)
                    {
                        LogInfo("Moving to content partition...");
                        if (!_drive.MoveToPartition(MediaPartition.Content))
                        {
                            LastError = _drive.LastErrorMessage;
                            LogErr($"Couldn't move to content partition. Error: {LastError}");
                            return false;
                        }
                    }

                    LogOk("Media loaded successfully");
                    LogMediaInfo();
                    Status("Media loaded");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception loading media: {ex.Message}");
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
                if (_drive == null || !_drive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    return false;
                }

                try
                {
                    LogInfo("Preparing media...");
                    if (!_drive.PrepareMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        LogErr($"Couldn't prepare media. Error: {LastError}");
                        return false;
                    }

                    LogInfo("Restoring TOC...");
                    Status("Reading TOC...");

                    _agent?.Dispose();
                    _agent = new TapeFileAgent(_drive, null);

                    if (!_agent.RestoreTOC())
                    {
                        LastError = _agent.LastErrorMessage;
                        LogErr($"Couldn't restore TOC. Error: {LastError}");
                        return false;
                    }

                    _toc = _agent.TOC;
                    LogOk($"TOC restored with {_toc.Count} backup set(s)");
                    LogTOCInfo();
                    Status($"TOC loaded: {_toc.Count} backup set(s)");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception restoring TOC: {ex.Message}");
                    return false;
                }
                finally
                {
                    _agent?.Dispose();
                    _agent = null;
                }
            }
        });
    }

    /// <summary>
    /// Creates and saves an initial empty TOC for newly created/formatted media.
    /// Should be called after LoadMediaAsync() for new virtual media.
    /// </summary>
    /// <param name="mediaName">Description for the new media. If null, a default name is generated.</param>
    /// <returns>True if the initial TOC was created and saved successfully.</returns>
    public Task<bool> CreateInitialTOCAsync(string? mediaName = null)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_drive == null || !_drive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    return false;
                }

                try
                {
                    LogInfo("Creating initial TOC...");
                    Status("Creating initial TOC...");

                    var description = mediaName ?? $"Media created {DateTime.Now:yyyy-MM-dd HH:mm}";

                    _agent?.Dispose();
                    _agent = new TapeFileAgent(_drive, new TapeTOC(description));

                    if (!_agent.BackupTOC())
                    {
                        LastError = _agent.LastErrorMessage;
                        LogErr($"Couldn't save initial TOC. Error: {LastError}");
                        return false;
                    }

                    _toc = _agent.TOC;
                    LogOk($"Initial TOC created: {description}");
                    Status("Initial TOC created");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception creating initial TOC: {ex.Message}");
                    return false;
                }
                finally
                {
                    _agent?.Dispose();
                    _agent = null;
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
                if (_drive == null)
                {
                    LastError = "Drive not open";
                    return false;
                }

                try
                {
                    LogInfo("Ejecting media...");
                    Status("Ejecting media...");

                    _agent?.Dispose();
                    _agent = null;
                    _toc = null;

                    if (!_drive.UnloadMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        LogErr($"Couldn't eject media. Error: {LastError}");
                        return false;
                    }

                    LogOk("Media ejected");
                    Status("Media ejected");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception ejecting media: {ex.Message}");
                    return false;
                }
            }
        });
    }

    // ExecuteBackupAsync is in TapeService.Backup.cs

    /// <summary>
    /// Inserts new virtual media into the virtual drive by replacing the backing file streams.
    /// Does NOT acquire _lock — designed to be called from within insertMediaCallback
    /// where the worker thread already holds the lock and is blocked on Dispatcher.Invoke.
    /// </summary>
    public bool InsertVirtualMedia(
        VirtualMediaDescriptor vmd,
        FileMode mediaMode = FileMode.Create)
    {
        if (_drive?.Backend is not VirtualTapeDriveBackend vb)
        {
            LastError = "Not a virtual drive";
            return false;
        }

        try
        {
            LogInfo($"Inserting virtual media...");
            LogInfoSub($"Content file: >{vmd.ContentPath}<");
            if (vmd.InitiatorPath != null)
                LogInfoSub($"Initiator file: >{vmd.InitiatorPath}<");
            LogInfoSub($"Media mode: {mediaMode}");

            vb.InsertMedia(vmd.ContentPath, vmd.ContentCapacity, vmd.InitiatorPath, vmd.InitiatorPartitionCapacity, mediaMode);

            _vmdLast = vmd;

            LogOk("Virtual media inserted");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LogErr($"Exception inserting virtual media: {ex.Message}");
            return false;
        }
    }

    public Task<bool> OpenVirtualDriveAsync(
        VirtualTapeDriveCapabilities capabilities,
        VirtualMediaDescriptor vmd,
        FileMode mediaMode = FileMode.OpenOrCreate)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    LogInfo($"Opening virtual drive...");
                    LogInfoSub($"Content file: >{vmd.ContentPath}<");
                    if (vmd.InitiatorPath != null)
                        LogInfoSub($"Initiator file: >{vmd.InitiatorPath}<");
                    LogInfoSub($"Media mode: {mediaMode}");
                    Status("Opening virtual drive...");

                    var backend = VirtualTapeDriveBackend.CreateFileBacked(
                        _loggerFactory,
                        vmd.ContentPath,
                        vmd.ContentCapacity,
                        vmd.InitiatorPath,
                        vmd.InitiatorPartitionCapacity,
                        capabilities,
                        mediaMode);

                    // If we got here, the backend has been created ->
                    //  Dispose existing drive if any
                    _agent?.Dispose();
                    _agent = null;
                    _toc = null;
                    _drive?.Dispose();

                    _drive = new TapeDrive(_loggerFactory, backend);

                    if (!_drive.ReopenDrive(0))
                    {
                        LastError = _drive.LastErrorMessage;
                        LogErr($"Failed to open virtual drive: {LastError}");
                        return false;
                    }

                    _vmdLast = vmd;

                    DriveNumber = 0;
                    LogOk($"Virtual drive opened on file >{vmd.ContentPath}<");
                    Status("Virtual drive opened");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception opening virtual drive: {ex.Message}");
                    return false;
                }
            }
        });
    }

    /// <summary>
    /// Sets the simulated IO, locate, and search speeds for the virtual tape drive.
    /// Thread-safe: acquires the lock to prevent modification during a running operation.
    /// </summary>
    /// <param name="bytesPerSecond">Streaming IO rate in bytes/second, or 0 for unlimited.</param>
    /// <param name="locateBytesPerSecond">Blind-seek (locate) rate in bytes/second, or 0 for unlimited.</param>
    /// <param name="searchBytesPerSecond">Mark-scanning (search) rate in bytes/second, or 0 for unlimited.</param>
    /// <param name="seekOverheadMs">Seek overhead time in milliseconds, added to locate/search operations.</param>
    /// <returns>True if the rates were set, false if not a virtual drive.</returns>
    public bool SetVirtualIoRate(long bytesPerSecond, long locateBytesPerSecond = 0, long searchBytesPerSecond = 0, int seekOverheadMs = 0)
    {
        lock (_lock)
        {
            if (_drive?.Backend is not VirtualTapeDriveBackend vb)
                return false;

            vb.IoRateBytesPerSecond = bytesPerSecond;
            vb.LocateRateBytesPerSecond = locateBytesPerSecond;
            vb.SearchRateBytesPerSecond = searchBytesPerSecond;
            vb.SeekOverheadMs = seekOverheadMs;

            if (bytesPerSecond == 0)
            {
                LogInfo($"IO speed simulation: unlimited");
            }
            else
            {
                LogInfo($"IO speed simulation: {Helpers.BytesToString(bytesPerSecond)}/s" +
                    $", locate: {Helpers.BytesToString(locateBytesPerSecond)}/s" +
                    $", search: {Helpers.BytesToString(searchBytesPerSecond)}/s" +
                    $", seek overhead: {seekOverheadMs} ms");
            }

            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _agent?.Dispose();
        _drive?.Dispose();
        _loggerFactory.Dispose();
    }

    #endregion

    #region Private Methods — Logging

    private void Emit(WarningLevel level, string message, bool sub = false)
        => LogMessageReceived?.Invoke(this, new LogEntry(level, message, sub, DateTime.Now));

    private void Log(string msg)          => Emit(WarningLevel.None, msg);
    private void LogOk(string msg)        => Emit(WarningLevel.Completed, msg);
    private void LogOkSub(string msg)     => Emit(WarningLevel.Completed, msg, sub: true);
    private void LogInfo(string msg)      => Emit(WarningLevel.Info, msg);
    private void LogInfoSub(string msg)   => Emit(WarningLevel.Info, msg, sub: true);
    private void LogWarn(string msg)      => Emit(WarningLevel.Warning, msg);
    private void LogWarnSub(string msg)   => Emit(WarningLevel.Warning, msg, sub: true);
    private void LogFail(string msg)      => Emit(WarningLevel.Failed, msg);
    private void LogFailSub(string msg)   => Emit(WarningLevel.Failed, msg, sub: true);
    private void LogErr(string msg)       => Emit(WarningLevel.Error, msg);
    private void LogErrSub(string msg)    => Emit(WarningLevel.Error, msg, sub: true);

    private void Status(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void LogMediaInfo()
    {
        if (_drive == null)
            return;

        LogInfoSub($"Partition count: {_drive.PartitionCount}");
        LogInfoSub($"Capacity: {Helpers.BytesToStringLong(_drive.Capacity)}");
        LogInfoSub($"Remaining: {Helpers.BytesToStringLong(_drive.GetRemainingCapacity())}");
    }

    private void LogTOCInfo()
    {
        if (_toc == null)
            return;

        LogInfoSub($"Media name: {_toc.Description}");
        LogInfoSub($"Created: {_toc.CreationTime}");
        LogInfoSub($"Last saved: {_toc.LastSaveTime}");
        LogInfoSub($"Volume: #{_toc.Volume}");

        // List sets in alternative order: from latest (0) down to oldest (_toc.MinSetIndex)
        for (int alt = 0; alt >= _toc.MinSetIndex; alt--)
        {
            int setIndex = _toc.SetIndexToAlt(alt); // this also converts from alt to regular index
            var setTOC = _toc[setIndex];
            LogInfoSub($"Set {setIndex} | {alt}: {setTOC.Description} - {setTOC.Count} files" +
                (setTOC.Incremental ? " [Incremental]" : ""));
        }
    }

    #endregion

    // GuiBackupProgressHandler helper class is in TapeService.Backup.cs
}
