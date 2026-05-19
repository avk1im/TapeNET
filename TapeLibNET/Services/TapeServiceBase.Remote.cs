using System.IO;
using System.Threading;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.Extensions.Logging;

using TapeLibNET.Remote;
using TapeLibNET.Virtual;

namespace TapeLibNET.Services;

// ── TapeServiceBase — Remote partial ─────────────────────────────────────────
// All members that deal with remote (gRPC) tape drives: persistent connection
//  state, open/create/insert remote drives, session-volume catalog, and the
//  remote-connection close/cleanup helpers.
// ─────────────────────────────────────────────────────────────────────────────

public partial class TapeServiceBase
{
    #region Remote connection fields
    // ── Persistent remote connection state ───────────────────────────────────
    // These survive individual drive open/create/insert-media cycles so that the
    //  server-side session and its named-volume catalog remain intact for the full
    //  duration of the remote host connection (matching the user's mental model).
    //
    //  Lifecycle:
    //   • Created on the first CreateRemoteVirtual / OpenRemoteVirtual call.
    //   • Reused for every subsequent open / create / insert-media on the same host.
    //   • Cleared by ClearRemoteConnection(), called from CloseDrive / Dispose.
    //
    //  The channel is NOT owned by any single TapeDrive; it is owned here.
    //  Individual backends created by each drive-swap use the session-attach ctor
    //  (_ownsSession=false) so that TapeDrive.Dispose() does NOT send Close RPC.

    /// <summary>gRPC channel shared across all drives opened on the same remote host.</summary>
    private GrpcChannel? _remoteChannel;

    /// <summary>Server-assigned session ID that outlives individual drive open/close cycles.</summary>
    private string? _remoteSessionId;

    /// <summary>Settings used to open the current remote connection (for reconnect if needed).</summary>
    private RemoteHostSettings? _remoteHostSettings;

    #endregion

    #region Remote read-only state
    // ── Remote read-only state ────────────────────────────────────────────────

    /// <summary>True when the current drive is a remote (gRPC) drive.</summary>
    public bool IsRemoteDrive => _drive?.Backend is RemoteTapeDriveBackend;

    #endregion

    #region Drive lifecycle: open remote physical
    // ── Drive lifecycle: open remote physical drive ───────────────────────────

    /// <summary>
    /// Opens a remote physical tape drive by number. Creates a
    ///  <see cref="RemoteTapeDriveBackend"/> from <paramref name="settings"/> using the
    ///  service's own <c>_loggerFactory</c>, then wraps it in a <see cref="TapeDrive"/>
    ///  and calls <c>ReopenDrive</c> (which issues the gRPC Open RPC internally).
    ///  Disposes the backend on failure. Mirrors <see cref="OpenDriveAsync"/>.
    /// On success fires <see cref="ServiceStateChange.DriveOpened"/>.
    /// </summary>
    /// <param name="settings">Connection settings for the remote tape service.</param>
    /// <param name="driveNumber">Remote drive number to open.</param>
    public Task<bool> OpenRemoteDriveAsync(RemoteHostSettings settings, uint driveNumber)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                LogInfo($"Opening remote drive {driveNumber} on {settings.DisplayLabel}...");

                _agent?.Dispose();
                _agent = null;
                _toc = null;
                _drive?.Dispose();

                // TapeDrive.CreateRemote builds the RemoteTapeDriveBackend internally,
                //  using our _loggerFactory so remote RPC calls are properly logged.
                _drive = TapeDrive.CreateRemote(settings, _loggerFactory);

                if (!_drive.ReopenDrive(driveNumber))
                {
                    LastError = _drive.LastErrorMessage;
                    LogErr($"Couldn't open remote drive. Error: {LastError}");
                    _drive.Dispose();
                    _drive = null;
                    return false;
                }

                DriveNumber = (int)driveNumber;
                LogOk($"Remote drive {driveNumber} opened on {settings.DisplayLabel}");
                LogInfoSub($"Device name: {_drive.DriveDeviceName}");
                _host.OnServiceStateChanged(ServiceStateChange.DriveOpened);
                return true;
            }
            catch (RpcException rpc)
            {
                LastError = FormatRpcError(rpc);
                LogErr($"gRPC error opening remote drive: {LastError}");
                _drive?.Dispose();
                _drive = null;
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception opening remote drive: {ex.Message}");
                _drive?.Dispose();
                _drive = null;
                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    /// <summary>
    /// Creates a temporary remote virtual tape drive.
    /// Creates a <see cref="RemoteTapeDriveBackend"/> from <paramref name="settings"/>,
    ///  calls <c>CreateTempVirtualAsync</c> to create and open the virtual drive on the server,
    ///  then wraps it in a <see cref="TapeDrive"/> and calls <c>ReopenDrive(0)</c> to read
    ///  drive parameters. Disposes the backend on any failure path.
    ///  Mirrors <see cref="OpenVirtualDriveAsync"/> for the local case.
    /// On success fires <see cref="ServiceStateChange.DriveOpened"/>.
    /// </summary>
    /// <param name="settings">Connection settings for the remote tape service.</param>
    /// <param name="vmd">Virtual media descriptor; stored as <c>_vmdLast</c> so
    ///  <see cref="IsInMemoryDrive"/> reports the correct value.</param>
    /// <param name="caps">Drive capabilities (null → drive default).</param>
    /// <param name="mediaName">Optional media label.</param>
    public Task<bool> CreateRemoteVirtualDriveAsync(
        RemoteHostSettings settings,
        VirtualMediaDescriptor? vmd,
        VirtualTapeDriveCapabilities? caps = null,
        string? mediaName = null)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);

            long capacityBytes = vmd?.ContentCapacity ?? 0;
            uint blockSize     = caps?.DefaultBlockSize ?? 0;
            // ContentPath is the server-side backing filename for named drives;
            //  null / "(in-memory)" for in-memory drives — server interprets null as in-memory.
            string? contentPath = (vmd is null || vmd.InMemory) ? null : vmd.ContentPath;

            try
            {
                LogInfo($"Creating remote virtual drive on {settings.DisplayLabel}...");

                RemoteTapeDriveBackend backend;

                // In-memory drives cannot be swapped to via InsertMedia (the server requires a real file path).
                //  Always use CreateTempVirtual for in-memory drives, even within a persistent session.
                bool hasPersistentSession = _remoteChannel is not null
                    && !string.IsNullOrEmpty(_remoteSessionId)
                    && _remoteHostSettings?.ChannelAddress == settings.ChannelAddress
                    && !string.IsNullOrEmpty(contentPath); // in-memory: contentPath is null → new session

                if (hasPersistentSession)
                {
                    // ── Reuse the existing session: InsertMedia to swap to the new named volume ──
                    // Build a transient backend that shares the channel/session without owning it.
                    backend = new RemoteTapeDriveBackend(_remoteChannel!, _remoteSessionId!, _loggerFactory);

                    var protoCaps = caps is VirtualTapeDriveCapabilities c ? MapVirtualCaps(c) : null;
                    bool swapOk = await backend.InsertMediaAsync(
                            contentPath!, capacityBytes, null, 0, // !string.IsNullOrEmpty(contentPath) has been checked above
                            protoCaps, mediaMode: FileMode.Create)
                        .ConfigureAwait(false);

                    if (!swapOk)
                    {
                        LastError = backend.LastErrorMessage;
                        LogErr($"Couldn't create remote virtual drive (InsertMedia). Error: {LastError}");
                        return false;
                    }
                }
                else
                {
                    bool isInMemory = string.IsNullOrEmpty(contentPath);

                    if (isInMemory)
                    {
                        // ── In-memory drive: fully-owned, independent session ──
                        // In-memory backends cannot be swapped into an existing named-volume session
                        //  (the server requires a real file path for InsertMedia), so each in-memory
                        //  drive gets its own channel+session that it fully owns.
                        // Crucially: we do NOT call ClearRemoteConnectionAsync() here — the existing
                        //  named-volume persistent session (_remoteChannel/_remoteSessionId) must
                        //  survive so the user can still switch back to a named volume.
                        backend = new RemoteTapeDriveBackend(settings, _loggerFactory);

                        if (!await backend.CreateTempVirtualAsync(capacityBytes, null, blockSize, caps)
                                .ConfigureAwait(false))
                        {
                            LastError = backend.LastErrorMessage;
                            LogErr($"Couldn't create remote in-memory virtual drive. Error: {LastError}");
                            backend.Dispose();
                            return false;
                        }
                        // backend owns its own channel+session; TapeDrive.Dispose() will close it.
                        // _remoteChannel/_remoteSessionId/_remoteHostSettings are deliberately unchanged.
                    }
                    else
                    {
                        // ── Named drive, first connection: establish a new persistent session ──
                        // Clear any stale remote connection state first.
                        await ClearRemoteConnectionAsync().ConfigureAwait(false);

                        backend = new RemoteTapeDriveBackend(settings, _loggerFactory);

                        // CreateTempVirtualAsync opens the virtual drive on the server
                        //  and establishes the session before we wrap it in TapeDrive.
                        if (!await backend.CreateTempVirtualAsync(capacityBytes, contentPath, blockSize, caps)
                                .ConfigureAwait(false))
                        {
                            LastError = backend.LastErrorMessage;
                            LogErr($"Couldn't create remote virtual drive. Error: {LastError}");
                            backend.Dispose();
                            return false;
                        }

                        // Capture the persistent connection state from the new backend.
                        // DetachOwnership() transfers channel/session ownership to _remoteChannel/_remoteSessionId
                        //  and clears _ownsChannel/_ownsSession on the original backend so it can be discarded
                        //  safely without sending Close RPC or disposing the channel.
                        _remoteChannel      = backend.DetachOwnership();
                        _remoteSessionId    = backend.SessionId;
                        _remoteHostSettings = settings;

                        // Reconstruct with shared channel + session (_ownsSession=false, _ownsChannel=false).
                        backend = new RemoteTapeDriveBackend(_remoteChannel, _remoteSessionId, _loggerFactory);
                    }
                }

                _agent?.Dispose();
                _agent = null;
                _toc = null;
                _drive?.Dispose();

                _drive = new TapeDrive(_loggerFactory, backend);

                // Backend is already open — ReopenDrive(0) skips Open() and only refreshes caps.
                if (!_drive.ReopenDrive(0))
                {
                    LastError = _drive.LastErrorMessage;
                    LogErr($"Couldn't open remote virtual drive. Error: {LastError}");
                    _drive.Dispose();
                    _drive = null;
                    return false;
                }

                // Store the VMD so IsInMemoryDrive reflects in-memory vs named correctly.
                _vmdLast = vmd;
                DriveNumber = 0;
                LogOk($"Remote virtual drive created on {settings.DisplayLabel}");
                LogInfoSub($"Device name: {_drive.DriveDeviceName}");
                _host.OnServiceStateChanged(ServiceStateChange.DriveOpened);
                return true;
            }
            catch (RpcException rpc)
            {
                LastError = FormatRpcError(rpc);
                LogErr($"gRPC error creating remote virtual drive: {LastError}");
                _drive?.Dispose();
                _drive = null;
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception creating remote virtual drive: {ex.Message}");
                _drive?.Dispose();
                _drive = null;
                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    #endregion

    #region Drive lifecycle: open remote virtual file / insert remote media
    // ── Drive lifecycle: open remote file-backed virtual drive ────────────────

    /// <summary>
    /// Opens an existing file-backed virtual tape on a remote host as the active drive.
    /// This is the remote counterpart of <see cref="OpenVirtualDriveAsync"/> (file mode):
    ///  it re-uses an existing tape file that was previously created by
    ///  <see cref="CreateRemoteVirtualDriveAsync"/> or a prior remote backup session.
    /// <para>
    ///  Used by the remote service test suite to "reopen" a tape between test phases,
    ///  mirroring how local service tests reopen <see cref="TempVirtualMedia"/> files.
    /// </para>
    /// On success fires <see cref="ServiceStateChange.DriveOpened"/>.
    /// </summary>
    /// <param name="settings">Connection settings for the remote tape service.</param>
    /// <param name="vmd">Virtual media descriptor for the existing tape file.</param>
    /// <param name="caps">Drive capabilities; null → server default.</param>
    /// <param name="mediaMode">
    ///  File open mode on the server. Use <see cref="FileMode.Create"/> to create a fresh
    ///  tape file for formatting, or <see cref="FileMode.Open"/> to reopen an existing one.
    /// </param>
    public Task<bool> OpenRemoteVirtualFileAsync(
        RemoteHostSettings settings,
        VirtualMediaDescriptor vmd,
        VirtualTapeDriveCapabilities? caps = null,
        FileMode mediaMode = FileMode.Open)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);

            try
            {
                LogInfo($"Opening remote virtual file '{Path.GetFileName(vmd.ContentPath)}' on {settings.DisplayLabel}...");

                RemoteTapeDriveBackend backend;

                bool hasPersistentSession = _remoteChannel is not null
                    && !string.IsNullOrEmpty(_remoteSessionId)
                    && _remoteHostSettings?.ChannelAddress == settings.ChannelAddress;

                if (hasPersistentSession)
                {
                    // ── Reuse the existing session: InsertMedia to swap to the requested volume ──
                    backend = new RemoteTapeDriveBackend(_remoteChannel!, _remoteSessionId!, _loggerFactory);

                    var protoCaps = caps is VirtualTapeDriveCapabilities c2 ? MapVirtualCaps(c2) : null;
                    bool swapOk = await backend.InsertMediaAsync(
                            vmd.ContentPath, vmd.ContentCapacity,
                            vmd.InitiatorPath, vmd.InitiatorPartitionCapacity,
                            protoCaps, mediaMode: mediaMode)
                        .ConfigureAwait(false);

                    if (!swapOk)
                    {
                        LastError = backend.LastErrorMessage;
                        LogErr($"Couldn't open remote virtual file (InsertMedia). Error: {LastError}");
                        return false;
                    }
                }
                else
                {
                    // ── First connection: establish a new session via OpenVirtual ──
                    await ClearRemoteConnectionAsync().ConfigureAwait(false);

                    backend = new RemoteTapeDriveBackend(settings, _loggerFactory);

                    var request = new OpenVirtualRequest
                    {
                        DriveNumber = 0,
                        FileConfig  = new VirtualFileConfig
                        {
                            ContentFilePath   = vmd.ContentPath,
                            ContentCapacity   = vmd.ContentCapacity,
                            InitiatorFilePath = vmd.InitiatorPath ?? string.Empty,
                            InitiatorCapacity = vmd.InitiatorPartitionCapacity,
                            MediaMode         = (int)mediaMode,
                            Capabilities      = caps is VirtualTapeDriveCapabilities c ? MapVirtualCaps(c) : null,
                        },
                    };

                    if (!await backend.OpenVirtualAsync(request).ConfigureAwait(false))
                    {
                        LastError = backend.LastErrorMessage;
                        LogErr($"Couldn't open remote virtual file. Error: {LastError}");
                        backend.Dispose();
                        return false;
                    }

                    // Capture and detach ownership.
                    _remoteChannel      = backend.DetachOwnership();
                    _remoteSessionId    = backend.SessionId;
                    _remoteHostSettings = settings;

                    backend = new RemoteTapeDriveBackend(_remoteChannel, _remoteSessionId, _loggerFactory);
                }

                _agent?.Dispose();
                _agent = null;
                _toc   = null;
                _drive?.Dispose();

                _drive = new TapeDrive(_loggerFactory, backend);

                // Backend is already open (gRPC session established) — ReopenDrive(0) skips
                //  Open() and only reads drive capabilities, marking IsDriveOpen = true.
                if (!_drive.ReopenDrive(0))
                {
                    LastError = _drive.LastErrorMessage;
                    LogErr($"Couldn't open remote virtual file. Error: {LastError}");
                    _drive.Dispose();
                    _drive = null;
                    return false;
                }

                _vmdLast    = vmd;
                DriveNumber = 0;
                LogOk($"Remote virtual file opened on {settings.DisplayLabel}");
                LogInfoSub($"Device name: {_drive.DriveDeviceName}");
                _host.OnServiceStateChanged(ServiceStateChange.DriveOpened);
                return true;
            }
            catch (RpcException rpc)
            {
                LastError = FormatRpcError(rpc);
                LogErr($"gRPC error opening remote virtual file: {LastError}");
                _drive?.Dispose();
                _drive = null;
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception opening remote virtual file: {ex.Message}");
                _drive?.Dispose();
                _drive = null;
                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    /// <summary>
    /// Swaps the current remote session's virtual tape for a new file-backed tape.
    /// Calls <see cref="RemoteTapeDriveBackend.InsertMediaAsync"/> on the active backend,
    ///  which issues the <c>InsertMedia</c> gRPC RPC so the server replaces the session's
    ///  backing store without closing the session.
    /// <para>
    ///  Used by <see cref="RemoteMultiVolumeServiceHost"/> to service volume-swap callbacks
    ///  in multi-volume backup/restore test sequences.
    /// </para>
    /// </summary>
    /// <param name="vmd">Virtual media descriptor for the new tape file.</param>
    /// <param name="caps">Drive capabilities; null → preserve existing.</param>
    /// <param name="mediaMode">
    ///  <see cref="FileMode"/> for the new file.
    ///  Use <see cref="FileMode.Create"/> to create a fresh tape for recording,
    ///  or <see cref="FileMode.Open"/> to re-load an already-written volume.
    /// </param>
    public Task<bool> InsertRemoteVirtualMediaAsync(
        VirtualMediaDescriptor vmd,
        VirtualTapeDriveCapabilities? caps = null,
        FileMode mediaMode = FileMode.OpenOrCreate)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_drive?.Backend is not RemoteTapeDriveBackend remoteBackend)
                {
                    LastError = "No remote virtual drive is currently open";
                    return false;
                }

                var protoCaps = caps is VirtualTapeDriveCapabilities c ? MapVirtualCaps(c) : null;
                bool ok = await remoteBackend.InsertMediaAsync(
                    vmd.ContentPath, vmd.ContentCapacity, vmd.InitiatorPath, vmd.InitiatorPartitionCapacity,
                    protoCaps, mediaMode: mediaMode)
                    .ConfigureAwait(false);

                if (!ok)
                    LastError = remoteBackend.LastErrorMessage;

                return ok;
            }
            catch (RpcException rpc)
            {
                LastError = FormatRpcError(rpc);
                LogErr($"gRPC error inserting remote media: {LastError}");
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception inserting remote media: {ex.Message}");
                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        });
    }

    /// <summary>
    /// Inserts new virtual media into a remote virtual drive by issuing an <c>InsertMedia</c> gRPC RPC.
    /// <para>
    /// Must be called from inside a media-change callback where the worker thread already
    ///  holds the <see cref="_operationLock"/>; this method does <b>not</b> acquire it.
    ///  Use <see cref="InsertRemoteVirtualMediaAsync"/> for external callers.
    /// </para>
    /// </summary>
    public bool InsertRemoteVirtualMedia(
        VirtualMediaDescriptor vmd,
        VirtualTapeDriveCapabilities? caps = null,
        FileMode mediaMode = FileMode.OpenOrCreate)
    {
        if (_drive?.Backend is not RemoteTapeDriveBackend remoteBackend)
        {
            LastError = "No remote virtual drive is currently open";
            return false;
        }

        try
        {
            LogInfo("Inserting remote virtual media...");
            LogInfoSub($"Content file: >{vmd.ContentPath}<");
            if (vmd.InitiatorPath is not null)
                LogInfoSub($"Initiator file: >{vmd.InitiatorPath}<");
            LogInfoSub($"Media mode: {mediaMode}");

            var protoCaps = caps is VirtualTapeDriveCapabilities c ? MapVirtualCaps(c) : null;
            bool ok = remoteBackend.InsertMediaAsync(
                vmd.ContentPath, vmd.ContentCapacity, vmd.InitiatorPath, vmd.InitiatorPartitionCapacity,
                protoCaps, mediaMode: mediaMode)
                .GetAwaiter().GetResult();

            if (!ok)
                LastError = remoteBackend.LastErrorMessage;
            else
                LogOk("Remote virtual media inserted");

            return ok;
        }
        catch (RpcException rpc)
        {
            LastError = FormatRpcError(rpc);
            LogErr($"gRPC error inserting remote media: {LastError}");
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LogErr($"Exception inserting remote media: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Maps a <see cref="VirtualTapeDriveCapabilities"/> to the proto <see cref="VirtualCapabilities"/> message.
    /// </summary>
    private static VirtualCapabilities MapVirtualCaps(VirtualTapeDriveCapabilities caps) => new()
    {
        MinBlockSize               = caps.MinBlockSize,
        MaxBlockSize               = caps.MaxBlockSize,
        DefaultBlockSize           = caps.DefaultBlockSize,
        SupportsSetmarks           = caps.SupportsSetmarks,
        SupportsSeqFilemarks       = caps.SupportsSeqFilemarks,
        SupportsInitiatorPartition = caps.SupportsInitiatorPartition,
        SupportsCompression        = caps.SupportsCompression,
    };

    #endregion

    #region Remote: session volume catalog
    // ── Remote: session volume catalog ───────────────────────────────────────

    /// <summary>
    /// Returns the named (file-backed) temporary volumes that have been created or inserted
    ///  during the current remote session, ordered by creation time.
    /// Returns an empty list if no remote session is active or the session has no named volumes.
    /// </summary>
    public async Task<IReadOnlyList<RemoteVirtualVolumeInfo>> ListRemoteSessionVolumesAsync(
        CancellationToken ct = default)
    {
        // Prefer the persistent channel+session directly, so the catalog is available even
        //  when _drive has been closed between volume swaps or during the "Open existing" flow.
        if (_remoteChannel is not null && !string.IsNullOrEmpty(_remoteSessionId))
        {
            var catalogBackend = new RemoteTapeDriveBackend(_remoteChannel, _remoteSessionId, _loggerFactory);
            return await catalogBackend.ListSessionVolumesAsync(ct).ConfigureAwait(false);
        }

        if (_drive?.Backend is RemoteTapeDriveBackend driveBackend)
            return await driveBackend.ListSessionVolumesAsync(ct).ConfigureAwait(false);

        return [];
    }

    #endregion

    #region Remote: close / cleanup
    // ── Remote: close / cleanup ───────────────────────────────────────────────

    /// <summary>
    /// Closes the persistent remote connection (channel + session) if one is open.
    /// Call this when the user explicitly disconnects from the remote host so that
    ///  the server-side session is terminated and the catalog is cleaned up.
    /// Does nothing if no remote connection is active.
    /// </summary>
    /// <remarks>
    /// This is separate from <see cref="CloseAsync"/> which only closes the current
    ///  <see cref="TapeDrive"/> wrapper. The remote connection outlives individual
    ///  drive-swap operations so the server-side volume catalog remains intact.
    /// </remarks>
    public async Task CloseRemoteConnectionAsync()
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Dispose the current drive first (sends no Close RPC — backend is non-session-owning)
            _agent?.Dispose(); _agent = null;
            _toc = null;
            _drive?.Dispose(); _drive = null;

            await ClearRemoteConnectionAsync().ConfigureAwait(false);
            _host.OnServiceStateChanged(ServiceStateChange.DriveClosed);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Tears down the persistent remote channel and session without acquiring the lock.
    /// Must only be called from within a locked context or from <see cref="Dispose"/>.
    /// </summary>
    private async Task ClearRemoteConnectionAsync()
    {
        // Atomically take ownership of the channel so a concurrent Dispose() call cannot
        //  race to null it between our null-check and our .Dispose() call.
        var channel = Interlocked.Exchange(ref _remoteChannel, null);
        if (channel is null) return;

        var sessionId = _remoteSessionId;
        _remoteSessionId    = null;
        _remoteHostSettings = null;

        // Send the Close RPC on the persistent session so the server cleans up promptly.
        if (!string.IsNullOrEmpty(sessionId))
        {
            try
            {
                var client = new TapeDriveService.TapeDriveServiceClient(channel);
                var opts = new Grpc.Core.CallOptions(
                    headers: new Grpc.Core.Metadata { { "x-tape-session-id", sessionId } });
                await client.CloseAsync(new EmptyRequest(), opts).ResponseAsync.ConfigureAwait(false);
            }
            catch { /* best-effort — server may already be gone */ }
        }

        channel.Dispose();
    }

    /// <summary>
    /// Best-effort synchronous cleanup of the persistent remote channel and session.
    /// Called from <see cref="Dispose"/> which cannot await; sends Close RPC synchronously
    ///  then disposes the channel. Atomically takes ownership of the channel to avoid racing
    ///  a concurrent <see cref="ClearRemoteConnectionAsync"/> call.
    /// </summary>
    internal void DisposeRemoteConnection()
    {
        var channel = Interlocked.Exchange(ref _remoteChannel, null);
        if (channel is null) return;

        var sessionId = _remoteSessionId;
        _remoteSessionId    = null;
        _remoteHostSettings = null;

        if (!string.IsNullOrEmpty(sessionId))
        {
            try
            {
                var client = new TapeDriveService.TapeDriveServiceClient(channel);
                var opts = new Grpc.Core.CallOptions(
                    headers: new Grpc.Core.Metadata { { "x-tape-session-id", sessionId } });
                client.Close(new EmptyRequest(), opts);
            }
            catch { /* best-effort */ }
        }

        channel.Dispose();
    }

    #endregion
}
