using System.IO;

using Windows.Win32.System.SystemServices; // for Helpers

using Microsoft.Extensions.Logging;
#if !DEBUG
using Microsoft.Extensions.Logging.Abstractions; // for NullLoggerFactory
#endif

using TapeLibNET;
using TapeLibNET.Virtual;

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

    public event EventHandler<string>? LogMessageReceived;
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
                    _agent?.Dispose();
                    _agent = null;
                    _toc = null;
                    _drive?.Dispose();

                    _drive = TapeDrive.CreateWin32(_loggerFactory);
                    
                    if (!_drive.ReopenDrive((uint)driveNumber))
                    {
                        LastError = _drive.LastErrorMessage;
                        Log($"!!! Couldn't open drive. Error: {LastError}");
                        return false;
                    }

                    DriveNumber = driveNumber;
                    Log($"vvv Drive {driveNumber} opened successfully");
                    Log($" ii Device name: {_drive.DriveDeviceName}");
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
                if (_drive == null)
                {
                    LastError = "Drive not open";
                    return false;
                }

                try
                {
                    Log(">>> Loading media...");
                    Status("Loading media...");

                    if (!_drive.ReloadMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        Log($"!!! Couldn't load media. Error: {LastError}");
                        return false;
                    }

                    // if multiple partitions, move to content partition to load correct media params
                    if (_drive.HasInitiatorPartition)
                    {
                        Log(">>> Moving to content partition...");
                        if (!_drive.MoveToPartition(MediaPartition.Content))
                        {
                            LastError = _drive.LastErrorMessage;
                            Log($"!!! Couldn't move to content partition. Error: {LastError}");
                            return false;
                        }
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
                if (_drive == null || !_drive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    return false;
                }

                try
                {
                    Log(">>> Preparing media...");
                    if (!_drive.PrepareMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        Log($"!!! Couldn't prepare media. Error: {LastError}");
                        return false;
                    }

                    Log(">>> Restoring TOC...");
                    Status("Reading TOC...");

                    _agent?.Dispose();
                    _agent = new TapeFileAgent(_drive, null);

                    if (!_agent.RestoreTOC())
                    {
                        LastError = _agent.LastErrorMessage;
                        Log($"!!! Couldn't restore TOC. Error: {LastError}");
                        return false;
                    }

                    _toc = _agent.TOC;
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
                    Log(">>> Creating initial TOC...");
                    Status("Creating initial TOC...");

                    var description = mediaName ?? $"Media created {DateTime.Now:yyyy-MM-dd HH:mm}";

                    _agent?.Dispose();
                    _agent = new TapeFileAgent(_drive, new TapeTOC(description));

                    if (!_agent.BackupTOC())
                    {
                        LastError = _agent.LastErrorMessage;
                        Log($"!!! Couldn't save initial TOC. Error: {LastError}");
                        return false;
                    }

                    _toc = _agent.TOC;
                    Log($"vvv Initial TOC created: {description}");
                    Status("Initial TOC created");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log($"!!! Exception creating initial TOC: {ex.Message}");
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
                    Log(">>> Ejecting media...");
                    Status("Ejecting media...");

                    _agent?.Dispose();
                    _agent = null;
                    _toc = null;

                    if (!_drive.UnloadMedia())
                    {
                        LastError = _drive.LastErrorMessage;
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
            Log($">>> Inserting virtual media...");
            Log($" ii Content file: >{vmd.ContentPath}<");
            if (vmd.InitiatorPath != null)
                Log($" ii Initiator file: >{vmd.InitiatorPath}<");
            Log($" ii Media mode: {mediaMode}");

            vb.InsertMedia(vmd.ContentPath, vmd.ContentCapacity, vmd.InitiatorPath, vmd.InitiatorPartitionCapacity, mediaMode);

            _vmdLast = vmd;

            Log("vvv Virtual media inserted");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log($"!!! Exception inserting virtual media: {ex.Message}");
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
                    Log($">>> Opening virtual drive...");
                    Log($" ii Content file: >{vmd.ContentPath}<");
                    if (vmd.InitiatorPath != null)
                        Log($" ii Initiator file: >{vmd.InitiatorPath}<");
                    Log($" ii Media mode: {mediaMode}");
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
                        Log($"!!! Failed to open virtual drive: {LastError}");
                        return false;
                    }

                    _vmdLast = vmd;

                    DriveNumber = 0;
                    Log($"vvv Virtual drive opened on file >{vmd.ContentPath}<");
                    Status("Virtual drive opened");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log($"!!! Exception opening virtual drive: {ex.Message}");
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
        _agent?.Dispose();
        _drive?.Dispose();
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
        if (_drive == null)
            return;

        Log($" ii Partition count: {_drive.PartitionCount}");
        Log($" ii Capacity: {Helpers.BytesToStringLong(_drive.Capacity)}");
        Log($" ii Remaining: {Helpers.BytesToStringLong(_drive.GetRemainingCapacity())}");
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

    // GuiBackupProgressHandler helper class is in TapeService.Backup.cs
}
