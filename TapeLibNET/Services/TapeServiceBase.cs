using System.IO;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Windows.Win32.System.SystemServices; // Helpers.BytesToStringLong

using TapeLibNET.Remote;
using TapeLibNET.Virtual;

namespace TapeLibNET.Services;

// ── TapeServiceBase ───────────────────────────────────────────────────────────

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
///  XAML-binding façades or console-specific partials without duplicating any state.
/// </para>
/// </summary>
/// <remarks>
/// Initialises the service with a logger factory and a host callback interface.
/// </remarks>
/// <param name="loggerFactory">
///  Used to create loggers for the underlying <see cref="TapeDrive"/> and agents.
/// </param>
/// <param name="host">
///  Host adapter for logging, prompts, and coarse state notifications.
///  Must not re-enter the service synchronously from the UI thread.
/// </param>
public partial class TapeServiceBase(ILoggerFactory loggerFactory, ITapeServiceHost host) : IDisposable
{

    #region Core fields
    // ── Core fields ───────────────────────────────────────────────────────────

    protected readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    protected readonly ITapeServiceHost _host = host ?? throw new ArgumentNullException(nameof(host));
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

    // Remote connection fields (_remoteChannel, _remoteSessionId, _remoteHostSettings)
    //  are declared in TapeServiceBase.Remote.cs.

    private bool _disposed;

    #endregion
    #region Construction / destruction

    #endregion

    #region Read-only state
    // ── Read-only state ───────────────────────────────────────────────────────

    /// <summary>True while a <see cref="TapeDrive"/> is open (physical or virtual).</summary>
    public bool IsDriveOpen => _drive?.IsDriveOpen ?? false;

    /// <summary>True while media is loaded into the open drive.</summary>
    public bool IsMediaLoaded => _drive?.IsMediaLoaded ?? false;

    /// <summary>Drive index (0-based) or 0 for virtual drives.</summary>
    public int DriveNumber { get; protected set; }

    /// <summary>OS device name reported by the open drive, or "Unknown".</summary>
    public string DeviceName => _drive?.DriveDeviceName ?? "Unknown";

    /// <summary>Drive vendor name, can be empty</summary>
    public string DeviceVendor => _drive?.DriveVendor ?? string.Empty;

    /// <summary>Drive product name, can be empty</summary>
    public string DeviceProduct => _drive?.DriveProduct ?? string.Empty;

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

    #endregion

    #region Drive capability properties
    // ── Drive capability properties ───────────────────────────────────────────

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

    ///<summary>Whether the drive supports hardware compression</summary>
    public bool SupportsCompression => _drive?.SupportsCompression ?? false;

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

    /// <summary>
    /// Estimated remaining capacity in bytes (Capacity − Used).
    /// <para>For LTO drives, trust drive reporting. For others, compute</para>
    /// </summary>
    public long Remaining => IsLtoDrive
        ? GetRemainingCapacityFromDrive()
        : Capacity - Used;

    /// <summary>
    /// Reads the remaining capacity directly from the drive hardware (thread-safe).
    /// </summary>
    public long GetRemainingCapacityFromDrive()
    {
        // Brief lock — just reading a hardware register, never blocks long.
        _operationLock.Wait();
        try   { return _drive?.GetContentRemainingCapacity() ?? 0; }
        finally { _operationLock.Release(); }
    }

    /// <summary>True when the current drive is backed by a virtual tape backend or a remote backend
    ///  (i.e. not a local physical drive — should not be offered for auto-reopen on startup).</summary>
    public bool IsVirtualDrive => _drive?.Backend is VirtualTapeDriveBackend
                                                  or RemoteTapeDriveBackend;

    /// <summary>True when the virtual drive uses in-memory streams (no persistent files).</summary>
    public bool IsInMemoryDrive => _vmdLast?.InMemory ?? false;

    /// <summary>True when the drive is a physical LTO drive</summary>
    public bool IsLtoDrive => _drive?.Backend is TapeDriveWin32Backend wbe && wbe.IsLto
        || _drive?.Backend is RemoteTapeDriveBackend rbe && rbe.IsLto;

    /// <summary>The last <see cref="VirtualMediaDescriptor"/> used to open or insert media.</summary>
    public VirtualMediaDescriptor? LastVMD => _vmdLast;

    /// <summary>
    /// Current IO speed simulation rate for the virtual drive, or 0 when not virtual /
    ///  when speed simulation is disabled.
    /// </summary>
    public long VirtualIoRateBytesPerSecond =>
        _drive?.Backend is VirtualTapeDriveBackend vb ? vb.IoRate.BytesPerSecond : 0;

    #endregion

    #region Drive lifecycle: open physical
    // ── Drive lifecycle: open physical ────────────────────────────────────────

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

    #endregion

    #region Remote drive lifecycle  [see TapeServiceBase.Remote.cs]
    // ── All remote drive members are defined in TapeServiceBase.Remote.cs ───────────
    // Includes: OpenRemoteDriveAsync, CreateRemoteVirtualDriveAsync,
    //            OpenRemoteVirtualFileAsync, InsertRemoteVirtualMediaAsync,
    //            InsertRemoteVirtualMedia, ListRemoteSessionVolumesAsync,
    //            CloseRemoteConnectionAsync, ClearRemoteConnectionAsync,
    //            DisposeRemoteConnection.
    #endregion

    #region Drive lifecycle: load media
    // ── Drive lifecycle: load media ───────────────────────────────────────────

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
            catch (RpcException rpc)
            {
                LastError = FormatRpcError(rpc);
                LogErr($"gRPC error loading media: {LastError}");
                return false;
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

    #endregion

    #region Drive lifecycle: eject media
    // ── Drive lifecycle: eject media ──────────────────────────────────────────

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
                return EjectMediaCore();
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    // Helper method runs synchronously inside the semaphore  - no async / await needed here.
    private bool EjectMediaCore()
    {
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
        catch (RpcException rpc)
        {
            LastError = FormatRpcError(rpc);
            LogErr($"gRPC error ejecting media: {LastError}");
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LogErr($"Exception ejecting media: {ex.Message}");
            return false;
        }
    }
    
    #endregion

    #region Drive lifecycle: open virtual
    // ── Drive lifecycle: open virtual (file-backed or in-memory) ─────────────

    /// <summary>
    /// Creates and opens a virtual tape drive.
    /// Dispatches to <see cref="OpenVirtualDriveInMemoryAsync"/> for in-memory media,
    ///  or opens a file-backed virtual drive otherwise.
    /// On success fires <see cref="ServiceStateChange.DriveOpened"/>.
    /// </summary>
    public Task<bool> OpenVirtualDriveAsync(
        VirtualTapeDriveCapabilities capabilities,
        VirtualMediaDescriptor vmd,
        FileMode mediaMode = FileMode.OpenOrCreate,
        VirtualTapeDriveIoRate? ioRate = null)
    {
        if (vmd.InMemory)
            return OpenVirtualDriveInMemoryAsync(capabilities, vmd, ioRate);

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

                if (ioRate.HasValue)
                    backend.IoRate = ioRate.Value;

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
        VirtualMediaDescriptor vmd,
        VirtualTapeDriveIoRate? ioRate = null)
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

                // For content capacity < 2 GB, use CreateMemoryBackend, otherwise CreateMemoryMapBackend
                var backend = vmd.ContentCapacity < 2L * 1024 * 1024 * 1024
                    ? VirtualTapeDriveBackend.CreateMemoryBacked(
                        _loggerFactory,
                        capabilities,
                        vmd.ContentCapacity,
                        vmd.InitiatorPartitionCapacity)
                    : VirtualTapeDriveBackend.CreateMemoryMapBacked(
                        _loggerFactory,
                        capabilities,
                        vmd.ContentCapacity,
                        vmd.InitiatorPartitionCapacity);

                if (ioRate.HasValue)
                    backend.IoRate = ioRate.Value;

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

    // InsertRemoteVirtualMedia is defined in TapeServiceBase.Remote.cs.

    #endregion

    #region Misc: IO speed simulation
    // ── Misc: IO speed simulation ─────────────────────────────────────────────

    /// <summary>
    /// Sets the simulated IO, locate, and search speeds for the virtual drive.
    /// Thread-safe: acquires the lock to prevent modification during a running operation.
    /// </summary>
    /// <returns>True if the rates were set; false if not a virtual drive.</returns>
    public bool SetVirtualIoRate(VirtualTapeDriveIoRate rate)
    {
        _operationLock.Wait();
        try
        {
            if (_drive?.Backend is not VirtualTapeDriveBackend vb)
                return false;

            vb.IoRate = rate;

            if (!vb.IsIoThrottled && !vb.IsMovementThrottled)
                LogInfo("IO rate simulation: unlimited");
            else
                LogInfo($"IO rate simulation: {Helpers.BytesToString(rate.BytesPerSecond)}/s" +
                    $", locate: {Helpers.BytesToString(rate.LocateBytesPerSecond)}/s" +
                    $", search: {Helpers.BytesToString(rate.SearchBytesPerSecond)}/s" +
                    $", seek overhead: {rate.SeekOverheadMs} ms");

            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    #endregion

    #region Reset / Close
    // ── Reset / Close ─────────────────────────────────────────────────────────

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

        // Best-effort synchronous cleanup of the persistent remote connection (defined in TapeServiceBase.Remote.cs).
        DisposeRemoteConnection();

        _operationLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Logging shims
    // ── Logging shims ─────────────────────────────────────────────────────────

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

    /// <summary>
    /// Formats a <see cref="RpcException"/> into a concise user-facing string,
    ///  mapping well-known gRPC status codes to descriptive phrases.
    /// </summary>
    protected static string FormatRpcError(RpcException rpc) => rpc.StatusCode switch
    {
        StatusCode.Unavailable      => $"Remote service unavailable: {rpc.Status.Detail}",
        StatusCode.NotFound         => $"Session not found (server may have restarted): {rpc.Status.Detail}",
        StatusCode.Unauthenticated  => $"Authentication required: {rpc.Status.Detail}",
        StatusCode.PermissionDenied => $"Permission denied: {rpc.Status.Detail}",
        StatusCode.DeadlineExceeded => $"Operation timed out: {rpc.Status.Detail}",
        StatusCode.Cancelled        => $"Operation cancelled: {rpc.Status.Detail}",
        _                           => $"gRPC {rpc.StatusCode}: {rpc.Status.Detail}",
    };

    #endregion

    #region Protected virtual hooks
    // ── Protected virtual hooks ───────────────────────────────────────────────

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

    /// <summary>
    /// Creates a file filter from a list of raw patterns (e.g. wildcards or FCL
    ///  expressions) when <see cref="ListRequest.Filter"/> is not supplied.
    /// Base returns <see langword="null"/> (no filtering); app subclasses that have
    ///  access to an FCL adapter override this to return the appropriate filter.
    /// Used by <see cref="ListContentsAsync"/> and, when implemented, by backup/restore
    ///  operations.
    /// </summary>
    protected virtual ITapeFileFilter? CreatePatternFilter(IReadOnlyList<string> patterns) => null;

    #endregion // Protected virtual hooks

    #region TOC operations
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
        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
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
            catch (RpcException rpc)
            {
                LastError = FormatRpcError(rpc);
                LogErr($"gRPC error restoring TOC: {LastError}");
                return false;
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
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        }, OperationCancellationToken);
    }

    /// <summary>
    /// Creates and saves an initial empty TOC
    /// Should be called after <see cref="LoadMediaAsync"/> for new virtual media.
    /// </summary>
    public Task<bool> CreateInitialTOCAsync(string? mediaName = null)
    {
        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_drive is null || !_drive.IsMediaLoaded) { LastError = "Media not loaded"; return false; }

                LogInfo("Creating initial TOC...");
                OnStatusUpdate("Creating initial TOC...");

                var description = mediaName ?? DefaultNewMediaName;
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
            catch (RpcException rpc)
            {
                LastError = FormatRpcError(rpc);
                LogErr($"gRPC error creating initial TOC: {LastError}");
                return false;
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
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    /// <summary>
    /// Formats the media
    /// Reloads media after format to refresh parameters.
    /// </summary>
    /// <param name="initiatorPartitionSize">
    /// Use <see cref="TapeNavigator.DefaultTOCCapacity"/> for an initiator partition,
    ///  or <c>-1</c> for single-partition (TOC in set) mode.
    /// </param>
    public Task<bool> FormatMediaAsync(long initiatorPartitionSize, string? mediaName)
    {
        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
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

                var description = mediaName ?? DefaultNewMediaName;
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
            catch (RpcException rpc)
            {
                LastError = FormatRpcError(rpc);
                LogErr($"gRPC error formatting media: {LastError}");
                return false;
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
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    /// <summary>
    /// Imports a TOC from a file
    /// Use when the on-tape TOC is missing or corrupt and a TOC file is available.
    /// Only requires the drive to be open (media need not be loaded).
    /// </summary>
    public Task<bool> ImportTOCFromFileAsync(string filePath)
    {
        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
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
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    /// <summary>
    /// Exports the current TOC
    /// </summary>
    public Task<bool> ExportTOCToFileAsync(string filePath)
    {
        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
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
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    /// <summary>
    /// Renames the media
    /// </summary>
    public async Task<bool> RenameMediaAsync(string newName)
    {
        if (_toc is null || _drive is null)
        {
            LastError = "No media loaded";
            return false;
        }

        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
        return await Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                LogInfo($"Renaming media to: {newName}");
                _toc.Description = newName;

                _agent = new TapeFileAgent(_drive, _toc);
                var tocResult = _agent.BackupTOC();
                if (!tocResult)
                {
                    LastError = tocResult.ErrorMessage;
                    LogErr($"Failed to write TOC to media: {tocResult.ErrorMessage}");
                    return false;
                }

                LogOk($"Media renamed to: {newName}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception renaming media: {ex.Message}");
                return false;
            }
            finally
            {
                _agent?.Dispose();
                _agent = null;
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    /// <summary>
    /// Asks the host for a new media name
    ///  then renames the media if the user confirmed.
    /// Returns <see langword="false"/> when the user cancelled or the rename failed.
    /// </summary>
    public async Task<bool> RenameMediaAsync()
    {
        if (_toc is null)
        {
            LastError = "No media loaded";
            return false;
        }

        var newName = _host.OnAskMediaName(_toc.Description ?? string.Empty);
        if (newName is null || newName == _toc.Description)
            return false;

        return await RenameMediaAsync(newName);
    }

    /// <summary>
    /// Renames a backup set by updating the set TOC description and writing the TOC back to tape.
    /// </summary>
    public async Task<bool> RenameBackupSetAsync(int setIndex, string newName)
    {
        if (_toc is null || _drive is null)
        {
            LastError = "No media loaded";
            return false;
        }

        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
        return await Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var setTOC = _toc[setIndex];
                LogInfo($"Renaming backup set #{setIndex} to: {newName}");
                setTOC.Description = newName;

                _agent = new TapeFileAgent(_drive, _toc);
                var tocResult = _agent.BackupTOC();
                if (!tocResult)
                {
                    LastError = tocResult.ErrorMessage;
                    LogErr($"Failed to write TOC to media: {tocResult.ErrorMessage}");
                    return false;
                }

                LogOk($"Backup set #{setIndex} renamed to: {newName}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception renaming backup set: {ex.Message}");
                return false;
            }
            finally
            {
                _agent?.Dispose();
                _agent = null;
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    /// <summary>
    /// Asks the host for a new backup-set description
    ///  <see cref="ITapeServiceHost.OnAskBackupSetName"/>, then renames the set if confirmed.
    /// Returns <see langword="false"/> when the user cancelled or the rename failed.
    /// </summary>
    /// <param name="setIndex">Standard (1-based) index of the set to rename.</param>
    public async Task<bool> RenameBackupSetAsync(int setIndex)
    {
        if (_toc is null)
        {
            LastError = "No media loaded";
            return false;
        }

        var setTOC = _toc[setIndex];
        int altIndex = _toc.SetIndexToAlt(setIndex);
        var newName = _host.OnAskBackupSetName(setIndex, altIndex, setTOC.Description ?? string.Empty);
        if (newName is null || newName == setTOC.Description)
            return false;

        return await RenameBackupSetAsync(setIndex, newName);
    }

    /// <summary>
    /// Deletes backup sets starting from
    ///  set on the volume. Physically overwrites the tape past the last retained set to move the
    ///  end-of-data marker, then updates the TOC on tape.
    /// </summary>
    /// <param name="deleteFromSetIndex">Standard (1-based) index of the first set to delete.</param>
    public async Task<bool> DeleteBackupSetsAsync(int deleteFromSetIndex)
    {
        if (_toc is null || _drive is null)
        {
            LastError = "No media loaded";
            return false;
        }

        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
        return await Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var toc = _toc;
                deleteFromSetIndex = toc.SetIndexToStd(deleteFromSetIndex);

                int lastSet = toc.LastSetOnVolume;
                int setsToDelete = lastSet - deleteFromSetIndex + 1;
                LogInfo($"Deleting {setsToDelete} backup set(s) from #{deleteFromSetIndex} | {toc.SetIndexToAlt(deleteFromSetIndex)}...");
                OnStatusUpdate("Deleting backup sets...");

                // Set the current set to the first one to delete —
                //  this is the precondition for DeleteSetsFromCurrentSetUp()
                toc.CurrentSetIndex = deleteFromSetIndex;

                _agent = new TapeFileAgent(_drive, toc);
                var result = _agent.DeleteSetsFromCurrentSetUp();
                if (!result)
                {
                    LastError = result.ErrorMessage;
                    LogErr($"Failed to delete backup sets: {result.ErrorMessage}");
                    return false;
                }

                LogOk($"Deleted {setsToDelete} backup set(s) — TOC saved");
                OnStatusUpdate($"Deleted {setsToDelete} backup set(s)");
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception deleting backup sets: {ex.Message}");
                return false;
            }
            finally
            {
                _agent?.Dispose();
                _agent = null;
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    #endregion // TOC operations

    #region Timing / formatting helpers
    // ── Timing / formatting helpers ──────────────────────────────────────────

    /// <summary>
    /// Default name for newly created media, based on the current date/time.
    /// Used across all apps and dialogs that need to pre-populate a media name.
    /// </summary>
    public static string DefaultNewMediaName => $"Media created {DateTime.Now:yyyy-MM-dd HH:mm}";

    /// <summary>Formats an elapsed duration as a human-readable string.</summary>
    public static string FormatElapsed(double totalSeconds)
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
    public static string FormatDataRate(long bytes, double totalSeconds)
    {
        if (totalSeconds < 0.001 || bytes <= 0) return string.Empty;
        long bytesPerSecond = (long)(bytes / totalSeconds);
        return $"{Helpers.BytesToString(bytesPerSecond)}/s";
    }
    #endregion
}


