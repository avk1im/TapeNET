using System.IO;

using Microsoft.Extensions.Logging;

using Windows.Win32.System.SystemServices; // Helpers.BytesToStringLong

using TapeLibNET.Virtual;

namespace TapeLibNET.Services;

// â”€â”€ TapeServiceBase â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>
/// Shared engine that owns the <see cref="TapeDrive"/>, <see cref="TapeFileAgent"/>,
///  and cached <see cref="TapeTOC"/> and exposes drive-lifecycle operations common to
///  both TapeConNET and TapeWinNET.
/// <para>
/// Threading model: one <see cref="SemaphoreSlim"/> (1,1) guards every mutating
///  operation. Callers must never re-enter the service from the UI thread while the
///  semaphore is held (deadlock risk). All long-running methods run via
///  <see cref="Task.Run"/> so the UI thread is never blocked.
/// </para>
/// <para>
/// Subclasses (the per-app <c>TapeService</c>) may override the protected virtual
///  hooks (<see cref="LogMediaInfo"/>, <see cref="LogTOCInfo"/>) and add
///  XAML-binding faÃ§ades or console-specific partials without duplicating any state.
/// </para>
/// </summary>
public partial class TapeServiceBase : IDisposable
{
    // â”€â”€ Core fields â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    protected readonly ILoggerFactory _loggerFactory;
    protected readonly ITapeServiceHost _host;
    protected readonly SemaphoreSlim _operationLock = new(1, 1);

    protected TapeDrive? _drive;
    protected TapeFileAgent? _agent;
    protected TapeTOC? _toc;

    /// <summary>
    /// Holds the last-used <see cref="VirtualMediaDescriptor"/> so virtual-drive
    ///  features (e.g. multi-volume continuation, preset UI values) can access it
    ///  without re-opening the drive.
    /// </summary>
    protected VirtualMediaDescriptor? _vmdLast;

    private bool _disposed;

    // â”€â”€ Construction / destruction â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Initialises the service with a logger factory and a host callback interface.
    /// </summary>
    /// <param name="loggerFactory">
    ///  Used to create loggers for the underlying <see cref="TapeDrive"/> and agents.
    /// </param>
    /// <param name="host">
    ///  Host adapter for logging, prompts, and coarse state notifications.
    ///  Must not re-enter the service synchronously from the UI thread.
    /// </param>
    public TapeServiceBase(ILoggerFactory loggerFactory, ITapeServiceHost host)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    // â”€â”€ Read-only state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>True while a <see cref="TapeDrive"/> is open (physical or virtual).</summary>
    public bool IsDriveOpen => _drive?.IsDriveOpen ?? false;

    /// <summary>True while media is loaded into the open drive.</summary>
    public bool IsMediaLoaded => _drive?.IsMediaLoaded ?? false;

    /// <summary>Drive index (0-based) or 0 for virtual drives.</summary>
    public int DriveNumber { get; protected set; }

    /// <summary>OS device name reported by the open drive, or "Unknown".</summary>
    public string DeviceName => _drive?.DriveDeviceName ?? "Unknown";

    /// <summary>Human-readable message from the last failed operation; null on success.</summary>
    public string? LastError { get; protected set; }

    /// <summary>The currently loaded TOC, or <see langword="null"/> when none is available.</summary>
    public TapeTOC? TOC => _toc;

    /// <summary>True when the current TOC was loaded from a file rather than from tape.</summary>
    public bool IsTOCFromFile { get; protected set; }

    /// <summary>Full path of the TOC file when <see cref="IsTOCFromFile"/> is true; null otherwise.</summary>
    public string? TOCFilePath { get; protected set; }

    /// <summary>
    /// The running <see cref="TapeFileAgent"/> during an active operation; null otherwise.
    /// </summary>
    public TapeFileAgent? Agent => _agent;

    /// <summary>True when the running agent has been asked to abort.</summary>
    public bool IsAbortRequested => _agent?.IsAbortRequested ?? false;

    // â”€â”€ Drive capability properties â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Whether the drive supports an initiator (TOC) partition.</summary>
    public bool SupportsInitiatorPartition => _drive?.SupportsInitiatorPartition ?? false;

    /// <summary>Whether the drive supports setmarks.</summary>
    public bool SupportsSetmarks => _drive?.SupportsSetmarks ?? false;

    /// <summary>Whether the drive supports sequential filemarks.</summary>
    public bool SupportsSeqFilemarks => _drive?.SupportsSeqFilemarks ?? false;

    /// <summary>Minimum block size reported by the drive hardware.</summary>
    public uint MinimumBlockSize => _drive?.MinimumBlockSize ?? 0;

    /// <summary>Default block size reported by the drive hardware.</summary>
    public uint DefaultBlockSize => _drive?.DefaultBlockSize ?? 0;

    /// <summary>Maximum block size reported by the drive hardware.</summary>
    public uint MaximumBlockSize => _drive?.MaximumBlockSize ?? 0;

    /// <summary>Number of partitions on the currently loaded media.</summary>
    public uint PartitionCount => _drive?.PartitionCount ?? 0;

    /// <summary>True when an initiator partition exists on the loaded media.</summary>
    public bool HasInitiatorPartition => _drive?.HasInitiatorPartition ?? false;

    /// <summary>Total capacity of the content partition in bytes.</summary>
    public long Capacity => _drive?.ContentCapacity ?? 0;

    /// <summary>Estimated used bytes, accounting for TOC-in-set mode.</summary>
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

    /// <summary>Estimated remaining capacity in bytes (Capacity âˆ’ Used).</summary>
    public long Remaining => Capacity - Used;

    /// <summary>
    /// Reads the remaining capacity directly from the drive hardware (thread-safe).
    /// </summary>
    public long GetRemainingCapacityFromDrive()
    {
        // Brief lock â€” just reading a hardware register, never blocks long.
        _operationLock.Wait();
        try   { return _drive?.GetContentRemainingCapacity() ?? 0; }
        finally { _operationLock.Release(); }
    }

    /// <summary>True when the current drive is backed by a virtual tape backend.</summary>
    public bool IsVirtualDrive => _drive?.Backend is VirtualTapeDriveBackend;

    /// <summary>True when the virtual drive uses in-memory streams (no persistent files).</summary>
    public bool IsInMemoryDrive => _vmdLast?.InMemory ?? false;

    /// <summary>The last <see cref="VirtualMediaDescriptor"/> used to open or insert media.</summary>
    public VirtualMediaDescriptor? LastVMD => _vmdLast;

    /// <summary>
    /// Current IO speed simulation rate for the virtual drive, or 0 when not virtual /
    ///  when speed simulation is disabled.
    /// </summary>
    public long VirtualIoRateBytesPerSecond =>
        _drive?.Backend is VirtualTapeDriveBackend vb ? vb.IoRateBytesPerSecond : 0;

    // â”€â”€ Drive lifecycle: open physical â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Opens a physical tape drive by number. Disposes any previously open drive.
    /// On success fires <see cref="ServiceStateChange.DriveOpened"/>.
    /// </summary>
    public Task<bool> OpenDriveAsync(int driveNumber)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
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
                _host.OnServiceStateChanged(ServiceStateChange.DriveOpened);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception opening drive: {ex.Message}");
                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    // â”€â”€ Drive lifecycle: load media â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Loads (or reloads) media into the open drive.
    /// On success fires <see cref="ServiceStateChange.MediaLoaded"/>.
    /// </summary>
    public Task<bool> LoadMediaAsync()
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_drive is null) { LastError = "Drive not open"; return false; }

                LogInfo("Loading media...");

                if (!_drive.ReloadMedia())
                {
                    LastError = _drive.LastErrorMessage;
                    LogErr($"Couldn't load media. Error: {LastError}");
                    return false;
                }

                LogOk("Media loaded successfully");
                LogMediaInfo();
                _host.OnServiceStateChanged(ServiceStateChange.MediaLoaded);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception loading media: {ex.Message}");
                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    // â”€â”€ Drive lifecycle: eject media â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Ejects (unloads) the media from the open drive.
    /// Clears the cached TOC. On success fires <see cref="ServiceStateChange.MediaEjected"/>.
    /// </summary>
    public Task<bool> EjectMediaAsync()
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_drive is null) { LastError = "Drive not open"; return false; }

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
                _host.OnServiceStateChanged(ServiceStateChange.MediaEjected);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception ejecting media: {ex.Message}");
                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    // â”€â”€ Drive lifecycle: open virtual (file-backed or in-memory) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Creates and opens a virtual tape drive.
    /// Dispatches to <see cref="OpenVirtualDriveInMemoryAsync"/> for in-memory media,
    ///  or opens a file-backed virtual drive otherwise.
    /// On success fires <see cref="ServiceStateChange.DriveOpened"/>.
    /// </summary>
    public Task<bool> OpenVirtualDriveAsync(
        VirtualTapeDriveCapabilities capabilities,
        VirtualMediaDescriptor vmd,
        FileMode mediaMode = FileMode.OpenOrCreate)
    {
        if (vmd.InMemory)
            return OpenVirtualDriveInMemoryAsync(capabilities, vmd);

        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
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
                _host.OnServiceStateChanged(ServiceStateChange.DriveOpened);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception opening virtual drive: {ex.Message}");
                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    private Task<bool> OpenVirtualDriveInMemoryAsync(
        VirtualTapeDriveCapabilities capabilities,
        VirtualMediaDescriptor vmd)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
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
                _host.OnServiceStateChanged(ServiceStateChange.DriveOpened);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception opening in-memory virtual drive: {ex.Message}");
                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    /// <summary>
    /// Inserts new virtual media into the virtual drive by replacing the backing streams.
    /// <para>
    /// Must be called from inside a media-change callback where the worker thread already
    ///  holds the <see cref="_operationLock"/>; this method does <b>not</b> acquire it.
    /// </para>
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

    // â”€â”€ Misc: IO speed simulation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Sets the simulated IO, locate, and search speeds for the virtual drive.
    /// Thread-safe: acquires the lock to prevent modification during a running operation.
    /// </summary>
    /// <returns>True if the rates were set; false if not a virtual drive.</returns>
    public bool SetVirtualIoRate(
        long bytesPerSecond,
        long locateBytesPerSecond = 0,
        long searchBytesPerSecond = 0,
        int seekOverheadMs = 0)
    {
        _operationLock.Wait();
        try
        {
            if (_drive?.Backend is not VirtualTapeDriveBackend vb)
                return false;

            vb.IoRateBytesPerSecond = bytesPerSecond;
            vb.LocateRateBytesPerSecond = locateBytesPerSecond;
            vb.SearchRateBytesPerSecond = searchBytesPerSecond;
            vb.SeekOverheadMs = seekOverheadMs;

            if (bytesPerSecond == 0)
                LogInfo("IO speed simulation: unlimited");
            else
                LogInfo($"IO speed simulation: {Helpers.BytesToString(bytesPerSecond)}/s" +
                    $", locate: {Helpers.BytesToString(locateBytesPerSecond)}/s" +
                    $", search: {Helpers.BytesToString(searchBytesPerSecond)}/s" +
                    $", seek overhead: {seekOverheadMs} ms");

            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    // â”€â”€ Reset / Close â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Invalidates the cached TOC and disposes any active agent without closing
    ///  the drive. Returns false if an operation is currently in progress (lock held).
    /// </summary>
    public bool Reset()
    {
        if (!_operationLock.Wait(0))
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
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Closes the drive and releases all resources. Fires
    ///  <see cref="ServiceStateChange.DriveClosed"/> if a drive was open.
    /// </summary>
    public async Task CloseAsync()
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            bool wasOpen = _drive?.IsDriveOpen ?? false;
            _agent?.Dispose();
            _agent = null;
            _toc = null;
            _drive?.Dispose();
            _drive = null;

            if (wasOpen)
                _host.OnServiceStateChanged(ServiceStateChange.DriveClosed);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _agent?.Dispose();
        _drive?.Dispose();
        _operationLock.Dispose();

        GC.SuppressFinalize(this);
    }

    // â”€â”€ Logging shims â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    protected void LogOk(string msg)       => _host.Report(ServiceReportLevel.Completed, msg);
    protected void LogOkSub(string msg)    => _host.Report(ServiceReportLevel.Completed, msg, isSubEntry: true);
    protected void LogInfo(string msg)     => _host.Report(ServiceReportLevel.Info, msg);
    protected void LogInfoSub(string msg)  => _host.Report(ServiceReportLevel.Info, msg, isSubEntry: true);
    protected void LogWarn(string msg)     => _host.Report(ServiceReportLevel.Warning, msg);
    protected void LogWarnSub(string msg)  => _host.Report(ServiceReportLevel.Warning, msg, isSubEntry: true);
    protected void LogFail(string msg)     => _host.Report(ServiceReportLevel.Failed, msg);
    protected void LogFailSub(string msg)  => _host.Report(ServiceReportLevel.Failed, msg, isSubEntry: true);
    protected void LogErr(string msg)      => _host.Report(ServiceReportLevel.Error, msg);
    protected void LogErrSub(string msg)   => _host.Report(ServiceReportLevel.Error, msg, isSubEntry: true);

    // â”€â”€ Protected virtual hooks â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Logs drive/media capacity info after a successful media load.
    /// Override in app subclasses to add app-specific formatting.
    /// </summary>
    protected virtual void LogMediaInfo()
    {
        if (_drive is null) return;
        LogInfoSub($"Partition count: {_drive.PartitionCount}");
        LogInfoSub($"Capacity: {Helpers.BytesToStringLong(_drive.ContentCapacity)}");
        LogInfoSub($"Remaining: {Helpers.BytesToStringLong(_drive.GetContentRemainingCapacity())}");
    }

    /// <summary>
    /// Logs TOC summary info (media description, set count, creation time, etc.)
    ///  after a successful TOC load.
    /// Override in app subclasses to add app-specific formatting.
    /// </summary>
    protected virtual void LogTOCInfo()
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

    // -- TOC operations -------------------------------------------------------

    /// <summary>
    /// Cancellation token used by TOC operations. Default: <see cref="CancellationToken.None"/>.
    /// Override in subclasses to supply a host-provided token (e.g. Ctrl+C in CLI).
    /// </summary>
    protected virtual CancellationToken OperationCancellationToken => CancellationToken.None;

    /// <summary>
    /// Called at key status-change moments during TOC operations.
    /// Default is a no-op; WPF subclass overrides to push status text to the binding facade.
    /// </summary>
    protected virtual void OnStatusUpdate(string status) { }

    /// <summary>
    /// Extra log entries emitted after a successful file-TOC import.
    /// Default is a no-op; WPF subclass adds a sub-entry warning about disabled features.
    /// </summary>
    protected virtual void OnImportTOCFromFileExtra() { }

    /// <summary>
    /// Signals the in-progress TOC load to abort cooperatively.
    /// Intentionally lock-free: <see cref="RestoreTOCAsync"/> holds the lock for its entire
    ///  duration, so acquiring it here would deadlock. The volatile
    ///  <see cref="TapeFileAgent.IsAbortRequested"/> flag is safe to set from any thread.
    /// </summary>
    public void AbortTOCLoad()
    {
        var agent = _agent; // snapshot to avoid TOCTOU null-ref
        if (agent is not null)
            agent.IsAbortRequested = true;
    }

    /// <summary>Restores the TOC from tape into <see cref="TOC"/>.</summary>
    public Task<bool> RestoreTOCAsync()
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_drive is null || !_drive.IsMediaLoaded) { LastError = "Media not loaded"; return false; }

                LogInfo("Preparing media...");
                if (!_drive.PrepareMedia())
                {
                    LastError = _drive.LastErrorMessage;
                    LogErr($"Couldn't prepare media. Error: {LastError}");
                    return false;
                }

                LogInfo("Restoring TOC...");
                OnStatusUpdate("Reading TOC...");

                _agent?.Dispose();
                _agent = new TapeFileAgent(_drive, null);

                // Bridge OperationCancellationToken -> agent abort flag (CLI Ctrl+C).
                var ct = OperationCancellationToken;
                using var ctReg = ct.Register(() =>
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
                OnStatusUpdate($"TOC loaded: {_toc.Count} backup set(s)");
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
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
                _operationLock.Release();
            }
        }, OperationCancellationToken);
    }

    /// <summary>
    /// Creates and saves an initial empty TOC for newly created/formatted media.
    /// Should be called after <see cref="LoadMediaAsync"/> for new virtual media.
    /// </summary>
    public Task<bool> CreateInitialTOCAsync(string? mediaName = null)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_drive is null || !_drive.IsMediaLoaded) { LastError = "Media not loaded"; return false; }

                LogInfo("Creating initial TOC...");
                OnStatusUpdate("Creating initial TOC...");

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
                OnStatusUpdate("Initial TOC created");
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
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
                _operationLock.Release();
            }
        });
    }

    /// <summary>
    /// Formats the media (optionally creating an initiator partition) and writes an initial empty TOC.
    /// Reloads media after format to refresh parameters.
    /// </summary>
    /// <param name="initiatorPartitionSize">
    /// Use <see cref="TapeNavigator.DefaultTOCCapacity"/> for an initiator partition,
    ///  or <c>-1</c> for single-partition (TOC in set) mode.
    /// </param>
    public Task<bool> FormatMediaAsync(long initiatorPartitionSize, string? mediaName)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_drive is null || !_drive.IsMediaLoaded) { LastError = "Media not loaded"; return false; }

                _agent?.Dispose();
                _agent = null;
                _toc = null;
                IsTOCFromFile = false;
                TOCFilePath = null;

                LogInfo("Formatting media...");
                OnStatusUpdate("Formatting media...");

                if (!_drive.FormatMedia(initiatorPartitionSize))
                {
                    LastError = _drive.LastErrorMessage;
                    LogErr($"Couldn't format media. Error: {LastError}");
                    return false;
                }

                var tocPlacement = _drive.HasInitiatorPartition ? "partition" : "set";
                LogOk($"Media formatted with TOC in {tocPlacement}");

                LogInfo("Creating initial TOC...");
                OnStatusUpdate("Creating initial TOC...");

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
                OnStatusUpdate("Media formatted");
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged | ServiceStateChange.MediaLoaded);
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
                _operationLock.Release();
            }
        });
    }

    /// <summary>
    /// Imports a TOC from a file and applies it as the current TOC.
    /// Use when the on-tape TOC is missing or corrupt and a TOC file is available.
    /// Only requires the drive to be open (media need not be loaded).
    /// </summary>
    public Task<bool> ImportTOCFromFileAsync(string filePath)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_drive is null || !_drive.IsDriveOpen) { LastError = "Drive not open"; return false; }

                LogInfo($"Importing TOC from file: {filePath}");
                OnStatusUpdate("Importing TOC from file...");

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
                LogWarn("TOC imported from a file - on-tape TOC may be missing or corrupt");
                OnImportTOCFromFileExtra();
                OnStatusUpdate($"TOC from file: {_toc.Count} backup set(s)");
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
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
                _operationLock.Release();
            }
        });
    }

    /// <summary>
    /// Exports the current TOC to a file as a safety copy.
    /// </summary>
    public Task<bool> ExportTOCToFileAsync(string filePath)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_toc is null) { LastError = "No TOC loaded"; return false; }

                LogInfo($"Exporting TOC to file: {filePath}");
                OnStatusUpdate("Exporting TOC to file...");

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
                OnStatusUpdate("TOC exported to file");
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
                _operationLock.Release();
            }
        });
    }

    // â”€â”€ Internal timing / formatting helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Formats an elapsed duration as a human-readable string.</summary>
    protected static string FormatElapsed(double totalSeconds)
    {
        if (totalSeconds < 1.0) return "< 1s";
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalMinutes < 1) return $"{ts.Seconds}s";
        if (ts.TotalHours < 1) return $"{ts.Minutes}m {ts.Seconds:D2}s";
        return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s";
    }

    /// <summary>
    /// Formats a data rate as <c>"X.XX MB/s"</c>; returns an empty string
    ///  when the duration is too short or no bytes were processed.
    /// </summary>
    protected static string FormatDataRate(long bytes, double totalSeconds)
    {
        if (totalSeconds < 0.001 || bytes <= 0) return string.Empty;
        long bytesPerSecond = (long)(bytes / totalSeconds);
        return $"{Helpers.BytesToString(bytesPerSecond)}/s";
    }
}
