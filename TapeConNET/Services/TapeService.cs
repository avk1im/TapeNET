using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Windows.Win32.System.SystemServices; // Helpers.BytesToStringLong

using TapeConNET.Ux;
using TapeLibNET;
using TapeLibNET.Virtual;
using TapeLibNET.Services;

namespace TapeConNET.Services;

/// <summary>
/// Thin service layer over <c>TapeLibNET</c> mirroring TapeWinNET's
/// <c>Services.TapeService</c>: owns the live <see cref="TapeDrive"/> + cached
/// <see cref="TapeTOC"/>, exposes async drive/media/TOC operations and
/// (in the partial files) backup/restore/list.
/// </summary>
/// <remarks>
/// Key differences from the WPF version:
/// <list type="bullet">
/// <item>All console output goes through the constructor-supplied
///       <see cref="IConsoleUx"/> instead of a <c>LogMessageReceived</c> event,
///       so verb classes never need to subscribe.</item>
/// <item>The host-supplied <see cref="CancellationToken"/> is bridged to the
///       running <see cref="TapeFileAgent.IsAbortRequested"/> flag, so Ctrl+C
///       cancels operations cooperatively.</item>
/// <item>The legacy <c>Status(...)</c> calls are kept (mapped to no-ops) so
///       the WPF source can be ported with minimal diff.</item>
/// </list>
/// </remarks>
public partial class TapeService : IDisposable
{
    private readonly IConsoleUx _ux;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationToken _ct;

    private TapeDrive? _drive;
    private TapeFileAgent? _agent;
    private TapeTOC? _toc;
    private VirtualMediaDescriptor? _vmdLast;
    private readonly object _lock = new();
    private bool _disposed;

    public TapeService(IConsoleUx ux, ILoggerFactory loggerFactory, CancellationToken cancellationToken = default)
    {
        _ux = ux ?? throw new ArgumentNullException(nameof(ux));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _ct = cancellationToken;
    }

    #region Properties

    public bool IsDriveOpen => _drive?.IsDriveOpen ?? false;
    public bool IsMediaLoaded => _drive?.IsMediaLoaded ?? false;
    public int DriveNumber { get; private set; }
    public string DeviceName => _drive?.DriveDeviceName ?? "Unknown";
    public string? LastError { get; private set; }
    public TapeTOC? TOC => _toc;

    /// <summary>True when the current TOC was loaded from a file rather than from tape.</summary>
    public bool IsTOCFromFile { get; private set; }

    /// <summary>Path of the TOC file when <see cref="IsTOCFromFile"/> is true; null otherwise.</summary>
    public string? TOCFilePath { get; private set; }

    public TapeFileAgent? Agent => _agent;
    public bool IsAbortRequested => _agent?.IsAbortRequested ?? false;

    public bool SupportsInitiatorPartition => _drive?.SupportsInitiatorPartition ?? false;
    public bool SupportsSetmarks => _drive?.SupportsSetmarks ?? false;
    public bool SupportsSeqFilemarks => _drive?.SupportsSeqFilemarks ?? false;
    public uint MinimumBlockSize => _drive?.MinimumBlockSize ?? 0;
    public uint DefaultBlockSize => _drive?.DefaultBlockSize ?? 0;
    public uint MaximumBlockSize => _drive?.MaximumBlockSize ?? 0;
    public uint PartitionCount => _drive?.PartitionCount ?? 0;
    public bool HasInitiatorPartition => _drive?.HasInitiatorPartition ?? false;
    public long Capacity => _drive?.ContentCapacity ?? 0;

    public long Used
    {
        get
        {
            if (_toc is null) return 0;
            var used = _toc.ComputeTotalFileSizeOnTape(DefaultBlockSize);
            if (!HasInitiatorPartition)
                used += TapeNavigator.DefaultTOCCapacity;
            return used;
        }
    }
    public long Remaining => Capacity - Used;

    public long GetRemainingCapacityFromDrive()
    {
        lock (_lock)
            return _drive?.GetContentRemainingCapacity() ?? 0;
    }

    public bool IsVirtualDrive => _drive?.Backend is VirtualTapeDriveBackend;
    public bool IsInMemoryDrive => _vmdLast?.InMemory ?? false;
    public VirtualMediaDescriptor? LastVMD => _vmdLast;

    #endregion

    #region Public Methods — Drive lifecycle

    public Task<bool> OpenDriveAsync(int driveNumber)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    LogInfo($"Opening drive {driveNumber}...");

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
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception opening drive: {ex.Message}");
                    return false;
                }
            }
        }, _ct);
    }

    public Task<bool> LoadMediaAsync()
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_drive is null) { LastError = "Drive not open"; return false; }
                try
                {
                    LogInfo("Loading media...");
                    if (!_drive.ReloadMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        LogErr($"Couldn't load media. Error: {LastError}");
                        return false;
                    }
                    LogOk("Media loaded successfully");
                    LogMediaInfo();
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception loading media: {ex.Message}");
                    return false;
                }
            }
        }, _ct);
    }

    public Task<bool> RestoreTOCAsync()
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_drive is null || !_drive.IsMediaLoaded) { LastError = "Media not loaded"; return false; }

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
                    _agent?.Dispose();
                    _agent = new TapeFileAgent(_drive, null);

                    using var ctReg = _ct.Register(() =>
                    {
                        var a = _agent; if (a is not null) a.IsAbortRequested = true;
                    });

                    var tocResult = _agent.RestoreTOC();
                    if (!tocResult)
                    {
                        LastError = tocResult.ErrorMessage;
                        LogErr($"Couldn't restore TOC. Error: {tocResult.ErrorMessage}");
                        return false;
                    }

                    _toc = _agent.TOC;
                    IsTOCFromFile = false;
                    TOCFilePath = null;
                    LogOk($"TOC restored with {_toc.Count} backup set(s)");
                    LogTOCInfo();
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
        }, _ct);
    }

    /// <summary>
    /// Creates and saves an initial empty TOC for newly created/formatted media.
    /// </summary>
    public Task<bool> CreateInitialTOCAsync(string? mediaName = null)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_drive is null || !_drive.IsMediaLoaded) { LastError = "Media not loaded"; return false; }

                try
                {
                    LogInfo("Creating initial TOC...");
                    var description = mediaName ?? $"Media created {DateTime.Now:yyyy-MM-dd HH:mm}";

                    _agent?.Dispose();
                    _agent = new TapeFileAgent(_drive, new TapeTOC(description));

                    var initResult = _agent.BackupInitialTOC();
                    if (!initResult)
                    {
                        LastError = initResult.ErrorMessage;
                        LogErr($"Couldn't save initial TOC. Error: {LastError}");
                        return false;
                    }

                    _toc = _agent.TOC;
                    IsTOCFromFile = false;
                    TOCFilePath = null;
                    LogOk($"Initial TOC created: {description}");
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
        }, _ct);
    }

    /// <summary>
    /// Formats the media (optionally creating an initiator partition) and writes an initial empty TOC.
    /// </summary>
    /// <param name="initiatorPartitionSize">
    /// Use <see cref="TapeNavigator.DefaultTOCCapacity"/> for an initiator partition,
    /// or <c>-1</c> for single-partition (TOC in set) mode.
    /// </param>
    public Task<bool> FormatMediaAsync(long initiatorPartitionSize, string? mediaName)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_drive is null || !_drive.IsMediaLoaded) { LastError = "Media not loaded"; return false; }

                try
                {
                    _agent?.Dispose();
                    _agent = null;
                    _toc = null;
                    IsTOCFromFile = false;
                    TOCFilePath = null;

                    LogInfo("Formatting media...");
                    if (!_drive.FormatMedia(initiatorPartitionSize))
                    {
                        LastError = _drive.LastErrorMessage;
                        LogErr($"Couldn't format media. Error: {LastError}");
                        return false;
                    }

                    var tocPlacement = _drive.HasInitiatorPartition ? "partition" : "set";
                    LogOk($"Media formatted with TOC in {tocPlacement}");

                    LogInfo("Creating initial TOC...");
                    var description = mediaName ?? $"Media created {DateTime.Now:yyyy-MM-dd HH:mm}";
                    _agent = new TapeFileAgent(_drive, new TapeTOC(description));

                    var initResult = _agent.BackupInitialTOC();
                    if (!initResult)
                    {
                        LastError = initResult.ErrorMessage;
                        LogErr($"Couldn't save initial TOC. Error: {LastError}");
                        return false;
                    }

                    _toc = _agent.TOC;

                    if (!_drive.ReloadMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        LogWarn($"Couldn't reload media after format. Error: {LastError}");
                    }

                    LogOk($"Media formatted: {description}");
                    LogMediaInfo();
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception formatting media: {ex.Message}");
                    return false;
                }
                finally
                {
                    _agent?.Dispose();
                    _agent = null;
                }
            }
        }, _ct);
    }

    public Task<bool> EjectMediaAsync()
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_drive is null) { LastError = "Drive not open"; return false; }

                try
                {
                    LogInfo("Ejecting media...");
                    _agent?.Dispose();
                    _agent = null;
                    _toc = null;
                    IsTOCFromFile = false;
                    TOCFilePath = null;

                    if (!_drive.UnloadMedia())
                    {
                        LastError = _drive.LastErrorMessage;
                        LogErr($"Couldn't eject media. Error: {LastError}");
                        return false;
                    }

                    LogOk("Media ejected");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception ejecting media: {ex.Message}");
                    return false;
                }
            }
        }, _ct);
    }

    public Task<bool> ImportTOCFromFileAsync(string filePath)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_drive is null || !_drive.IsDriveOpen) { LastError = "Drive not open"; return false; }

                try
                {
                    LogInfo($"Importing TOC from file: {filePath}");
                    _agent?.Dispose();
                    _agent = new TapeFileAgent(_drive, null);

                    var loadResult = _agent.LoadTOCFromFile(filePath);
                    if (!loadResult)
                    {
                        LastError = loadResult.ErrorMessage;
                        LogErr($"Couldn't import TOC from file. Error: {loadResult.ErrorMessage}");
                        return false;
                    }

                    _toc = _agent.TOC;
                    IsTOCFromFile = true;
                    TOCFilePath = filePath;
                    LogOk($"TOC imported from file with {_toc.Count} backup set(s)");
                    LogTOCInfo();
                    LogWarn("TOC imported from a file — on-tape TOC may be missing or corrupt");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception importing TOC from file: {ex.Message}");
                    return false;
                }
                finally
                {
                    _agent?.Dispose();
                    _agent = null;
                }
            }
        }, _ct);
    }

    public Task<bool> ExportTOCToFileAsync(string filePath)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_toc is null) { LastError = "No TOC loaded"; return false; }

                try
                {
                    LogInfo($"Exporting TOC to file: {filePath}");
                    _agent?.Dispose();
                    _agent = new TapeFileAgent(_drive!, _toc);

                    var saveResult = _agent.SaveTOCToFile(filePath);
                    if (!saveResult)
                    {
                        LastError = saveResult.ErrorMessage;
                        LogErr($"Couldn't export TOC to file. Error: {saveResult.ErrorMessage}");
                        return false;
                    }

                    LogOk($"TOC exported to file: {filePath}");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception exporting TOC to file: {ex.Message}");
                    return false;
                }
                finally
                {
                    _agent?.Dispose();
                    _agent = null;
                }
            }
        }, _ct);
    }

    #endregion

    #region Public Methods — Virtual drive

    /// <summary>
    /// Inserts new virtual media into the virtual drive by replacing the backing file streams.
    /// Does NOT acquire the lock; intended to be called from within a media-change callback while
    /// the worker thread already holds the lock.
    /// </summary>
    public bool InsertVirtualMedia(VirtualMediaDescriptor vmd, FileMode mediaMode = FileMode.Create)
    {
        if (_drive?.Backend is not VirtualTapeDriveBackend vb)
        {
            LastError = "Not a virtual drive";
            return false;
        }

        try
        {
            LogInfo("Inserting virtual media...");
            LogInfoSub($"Content file: >{vmd.ContentPath}<");
            if (vmd.InitiatorPath is not null)
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
        if (vmd.InMemory)
            return OpenVirtualDriveInMemoryAsync(capabilities, vmd);

        return Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    LogInfo("Opening virtual drive...");
                    LogInfoSub($"Content file: >{vmd.ContentPath}<");
                    if (vmd.InitiatorPath is not null)
                        LogInfoSub($"Initiator file: >{vmd.InitiatorPath}<");
                    LogInfoSub($"Media mode: {mediaMode}");

                    var backend = VirtualTapeDriveBackend.CreateFileBacked(
                        _loggerFactory,
                        vmd.ContentPath,
                        vmd.ContentCapacity,
                        vmd.InitiatorPath,
                        vmd.InitiatorPartitionCapacity,
                        capabilities,
                        mediaMode);

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
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception opening virtual drive: {ex.Message}");
                    return false;
                }
            }
        }, _ct);
    }

    private Task<bool> OpenVirtualDriveInMemoryAsync(
        VirtualTapeDriveCapabilities capabilities,
        VirtualMediaDescriptor vmd)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    LogInfo("Opening in-memory virtual drive...");
                    LogInfoSub($"Content capacity: {Helpers.BytesToStringLong(vmd.ContentCapacity)}");
                    if (vmd.InitiatorPath is not null)
                        LogInfoSub($"Initiator capacity: {Helpers.BytesToStringLong(vmd.InitiatorPartitionCapacity)}");

                    var backend = VirtualTapeDriveBackend.CreateMemoryBacked(
                        _loggerFactory,
                        capabilities,
                        vmd.ContentCapacity,
                        vmd.InitiatorPartitionCapacity);

                    _agent?.Dispose();
                    _agent = null;
                    _toc = null;
                    _drive?.Dispose();

                    _drive = new TapeDrive(_loggerFactory, backend);

                    if (!_drive.ReopenDrive(0))
                    {
                        LastError = _drive.LastErrorMessage;
                        LogErr($"Failed to open in-memory virtual drive: {LastError}");
                        return false;
                    }

                    _vmdLast = vmd;
                    DriveNumber = 0;
                    LogOk("In-memory virtual drive opened");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    LogErr($"Exception opening in-memory virtual drive: {ex.Message}");
                    return false;
                }
            }
        }, _ct);
    }

    #endregion

    #region Public Methods — Misc

    public bool Reset()
    {
        if (!Monitor.TryEnter(_lock))
            return false;

        try
        {
            _agent?.Dispose();
            _agent = null;
            _toc = null;
            IsTOCFromFile = false;
            TOCFilePath = null;
            return true;
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _agent?.Dispose();
        _drive?.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion

    #region Private Methods — Logging shims

    private void Log(string msg)         => _ux.Log(WarningLevel.None, msg);
    private void LogOk(string msg)       => _ux.Log(WarningLevel.Completed, msg);
    private void LogOkSub(string msg)    => _ux.Log(new LogEntry(WarningLevel.Completed, msg, IsSub: true));
    private void LogInfo(string msg)     => _ux.Log(WarningLevel.Info, msg);
    private void LogInfoSub(string msg)  => _ux.Log(new LogEntry(WarningLevel.Info, msg, IsSub: true));
    private void LogWarn(string msg)     => _ux.Log(WarningLevel.Warning, msg);
    private void LogWarnSub(string msg)  => _ux.Log(new LogEntry(WarningLevel.Warning, msg, IsSub: true));
    private void LogFail(string msg)     => _ux.Log(WarningLevel.Failed, msg);
    private void LogFailSub(string msg)  => _ux.Log(new LogEntry(WarningLevel.Failed, msg, IsSub: true));
    private void LogErr(string msg)      => _ux.Log(WarningLevel.Error, msg);
    private void LogErrSub(string msg)   => _ux.Log(new LogEntry(WarningLevel.Error, msg, IsSub: true));

    /// <summary>
    /// No-op shim mirroring the WPF service's <c>Status(string)</c> calls.
    /// CLI uses logs for status; kept to minimize port diff.
    /// </summary>
    [Conditional("DEBUG_STATUS")]
    private static void Status(string status) { _ = status; }

    private void LogMediaInfo()
    {
        if (_drive is null) return;
        LogInfoSub($"Partition count: {_drive.PartitionCount}");
        LogInfoSub($"Capacity: {Helpers.BytesToStringLong(_drive.ContentCapacity)}");
        LogInfoSub($"Remaining: {Helpers.BytesToStringLong(_drive.GetContentRemainingCapacity())}");
    }

    private void LogTOCInfo()
    {
        if (_toc is null) return;
        LogInfoSub($"Media name: {_toc.Description}");
        LogInfoSub($"Created: {_toc.CreationTime}");
        LogInfoSub($"Last saved: {_toc.LastSaveTime}");
        LogInfoSub($"Volume: #{_toc.Volume}");

        for (int alt = 0; alt >= _toc.MinSetIndex; alt--)
        {
            int setIndex = _toc.SetIndexToAlt(alt);
            var setTOC = _toc[setIndex];
            LogInfoSub($"Set #{setIndex} | {alt}: {setTOC.Description} - {setTOC.Count} files" +
                (setTOC.Incremental ? " [Incremental]" : ""));
        }
    }

    #endregion

    #region Private Helpers — Timing & formatting

    /// <summary>Format an elapsed duration in human-readable form.</summary>
    internal static string FormatElapsed(double totalSeconds)
    {
        if (totalSeconds < 1.0) return "< 1s";
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalMinutes < 1) return $"{ts.Seconds}s";
        if (ts.TotalHours < 1) return $"{ts.Minutes}m {ts.Seconds:D2}s";
        return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s";
    }

    /// <summary>Format a data rate as <c>"X.XX MB/s"</c>; empty when too short or no bytes.</summary>
    internal static string FormatDataRate(long bytes, double totalSeconds)
    {
        if (totalSeconds < 0.001 || bytes <= 0) return string.Empty;
        long bytesPerSecond = (long)(bytes / totalSeconds);
        return $"{Helpers.BytesToString(bytesPerSecond)}/s";
    }

    #endregion
}
