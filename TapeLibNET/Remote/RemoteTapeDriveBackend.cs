using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using TapeLibNET.Virtual;

namespace TapeLibNET.Remote;

/// <summary>
/// Tape drive backend that forwards all operations to a remote
/// <see cref="TapeDriveGrpcService"/> via gRPC. Transparently substitutes
/// for <see cref="TapeDriveWin32Backend"/> or <see cref="Virtual.VirtualTapeDriveBackend"/>
/// in the <see cref="TapeDrive"/> constructor.
/// <para>
/// All backend properties are served from a cached <see cref="BackendState"/>
/// snapshot that is refreshed with every RPC response (piggybacked).
/// </para>
/// Client                                    Tape Server
/// ─────────────────────────────             ─────────────────────────────
/// TapeDrive TapeDriveGrpcService
///  └─ RemoteTapeDriveBackend    ──gRPC──►    └─ TapeDriveBackend
///       (generated client stub)                  (Win32 or Virtual)
/// </summary>
public class RemoteTapeDriveBackend : TapeDriveBackend
{
    #region *** Private Fields ***

    private readonly GrpcChannel _channel;
    private readonly TapeDriveService.TapeDriveServiceClient _client;
    private readonly bool _ownsChannel;

    // Session ID issued by the server on Open* and echoed in x-tape-session-id metadata
    // on every subsequent call. Empty string means no session is established yet.
    private string _sessionId = string.Empty;

    // Cached property snapshot, refreshed on every RPC response
    private BackendState _state = new();

    // Background timer that sends periodic Ping RPCs to keep the session alive while the
    // client is idle (e.g. interactive user taking a break). Started after Open*, stopped
    // in Close/Dispose. Null when no session is established.
    private Timer? _pingTimer;

    // How often to send keepalive pings. Chosen so that at least two pings arrive within
    // the server-side IdleTimeout (default 30 min), giving ample margin.
    private static readonly TimeSpan PingInterval = TimeSpan.FromMinutes(9);

    #endregion

    #region *** Constructors ***

    /// <summary>
    /// Creates a remote backend using the supplied connection settings.
    /// This is the primary constructor — it derives the channel URI and transport
    /// security from <paramref name="settings"/>.
    /// </summary>
    /// <param name="settings">Host, port, TLS and certificate settings.</param>
    /// <param name="loggerFactory">Logger factory for logging.</param>
    public RemoteTapeDriveBackend(RemoteHostSettings settings, ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        _channel = GrpcChannel.ForAddress(settings.ChannelAddress, settings.BuildChannelOptions());
        _client = new TapeDriveService.TapeDriveServiceClient(_channel);
        _ownsChannel = true;

        m_logger.LogTrace("RemoteTapeDriveBackend created for {Address}", settings.ChannelAddress);
    }

    /// <summary>
    /// Convenience shim: creates a plain-HTTP remote backend from individual host and port values.
    /// Equivalent to <c>new RemoteTapeDriveBackend(new RemoteHostSettings(host, port), loggerFactory)</c>.
    /// </summary>
    /// <param name="host">Hostname or IP address of the tape service.</param>
    /// <param name="port">gRPC port (default 50551).</param>
    /// <param name="loggerFactory">Logger factory for logging.</param>
    public RemoteTapeDriveBackend(string host, int port, ILoggerFactory loggerFactory)
        : this(new RemoteHostSettings(host, port), loggerFactory) { }

    /// <summary>
    /// Creates a remote backend from an existing gRPC channel (for testing or shared channels).
    /// </summary>
    public RemoteTapeDriveBackend(GrpcChannel channel, ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        _channel = channel;
        _client = new TapeDriveService.TapeDriveServiceClient(channel);
        _ownsChannel = false;
    }

    #endregion


    #region *** State Synchronization ***

    // Header key that carries the session ID on every call after Open
    private const string SessionIdHeader = "x-tape-session-id";

    /// <summary>
    /// Updates the cached state and error from an <see cref="OperationResponse"/>.
    /// </summary>
    private bool Sync(OperationResponse response)
    {
        SyncState(response.State);
        SyncError(response.Error);
        return response.Success;
    }

    /// <summary>
    /// Updates the cached state from a response's <see cref="BackendState"/>.
    /// </summary>
    private void SyncState(BackendState? state)
    {
        if (state != null)
            _state = state;
    }

    /// <summary>
    /// Copies error information from the remote response into local error state.
    /// </summary>
    private void SyncError(ErrorInfo? error)
    {
        if (error == null || error.ErrorCode == 0)
            ResetError();
        else
            SetError(error.ErrorCode, error.ErrorMessage);
    }

    /// <summary>
    /// Returns <see cref="CallOptions"/> carrying the current session ID in metadata.
    /// Must only be called after a successful Open* call has established <see cref="_sessionId"/>.
    /// </summary>
    private CallOptions WithSession() => new(
        headers: new Grpc.Core.Metadata { { SessionIdHeader, _sessionId } });

    private CallOptions WithSession(CancellationToken ct) => new(
        headers: new Grpc.Core.Metadata { { SessionIdHeader, _sessionId } },
        cancellationToken: ct);

    #endregion

    #region *** State Properties (from cache) ***

    public override bool IsOpen => _state.IsOpen;
    public override bool HasMedia => _state.HasMedia;
    public override string DeviceName => _state.DeviceName ?? string.Empty;
    public override string Vendor  => _state.Vendor ?? string.Empty;
    public override string Product => _state.Product ?? string.Empty;
    public override uint DriveNumber => _state.DriveNumber;

    #endregion

    #region *** Drive & Media Properties (from cache) ***

    public override uint BlockSize => _state.BlockSize;
    public override uint MinBlockSize => _state.MinBlockSize;
    public override uint MaxBlockSize => _state.MaxBlockSize;
    public override uint DefaultBlockSize => _state.DefaultBlockSize;
    public override long Capacity => _state.Capacity;
    public override long Remaining => _state.Remaining;
    public override long Position => _state.Position;
    public override bool SupportsInitiatorPartition => _state.SupportsInitiatorPartition;
    public override bool HasInitiatorPartition => _state.HasInitiatorPartition;
    public override bool SupportsSetmarks => _state.SupportsSetmarks;
    public override bool SupportsSeqFilemarks => _state.SupportsSeqFilemarks;

    #endregion

    #region *** Connection Properties ***

    /// <summary>The remote service address this backend is connected to.</summary>
    public string RemoteAddress => _channel.Target;

    #endregion

    #region *** Drive Discovery (sessionless) ***

    /// <summary>
    /// Probes the remote host for available Win32 tape drives 0...<paramref name="maxDrive"/> without
    /// opening any of them. Safe to call before <see cref="Open"/> — no session required.
    /// </summary>
    /// <param name="maxDrive">Highest drive number to probe (default 9).</param>
    /// <returns>Read-only list of Win32 drive numbers that exist on the remote host.</returns>
    public IReadOnlyList<uint> ProbeDrives(uint maxDrive = 9)
    {
        var response = _client.ProbeDrives(new ProbeDrivesRequest { MaxDrive = maxDrive });
        return [.. response.DriveNumbers];
    }

    /// <summary>
    /// Creates a temporary virtual tape drive on the remote host for testing.
    /// Unnamed drives (empty <paramref name="mediaName"/>) are in-memory and vanish when the session closes.
    /// Named drives are file-backed in the server's temp folder and are deleted on <see cref="Close"/>.
    /// Safe to call before any <see cref="Open"/> — establishes a new session on success.
    /// </summary>
    /// <param name="capacityBytes">Total tape capacity in bytes (0 → server default of 500 MB).</param>
    /// <param name="mediaName">Optional media name; null or empty string creates an in-memory drive.</param>
    /// <param name="blockSize">Default block size in bytes (0 → drive default).</param>
    /// <param name="caps">Drive capabilities (null → server uses <c>WithFilemarksOnlyLargeBlocks</c>).</param>
    /// <returns>True if the temporary drive was created and opened successfully.</returns>
    public bool CreateTempVirtual(long capacityBytes = 0, string? name = null,
        uint blockSize = 0, VirtualTapeDriveCapabilities? caps = null)
    {
        var request = new CreateTempVirtualRequest
        {
            CapacityBytes = capacityBytes > 0 ? (ulong)capacityBytes : 0UL,
            Name          = name ?? string.Empty,
            BlockSize     = blockSize,
        };

        if (caps.HasValue)
        {
            request.Caps = new VirtualCapabilities
            {
                MinBlockSize             = caps.Value.MinBlockSize,
                MaxBlockSize             = caps.Value.MaxBlockSize,
                DefaultBlockSize         = caps.Value.DefaultBlockSize,
                SupportsSetmarks         = caps.Value.SupportsSetmarks,
                SupportsSeqFilemarks     = caps.Value.SupportsSeqFilemarks,
                SupportsInitiatorPartition = caps.Value.SupportsInitiatorPartition,
                SupportsCompression      = caps.Value.SupportsCompression,
            };
        }

        var call = _client.CreateTempVirtualAsync(request);
        var headers = call.ResponseHeadersAsync.GetAwaiter().GetResult();
        _sessionId = headers.GetValue(SessionIdHeader) ?? string.Empty;
        var response = call.ResponseAsync.GetAwaiter().GetResult();
        bool ok = Sync(response);
        if (ok)
            StartPingTimer();
        return ok;
    }

    /// <summary>
    /// Asynchronously creates a temporary virtual drive on the remote service.
    /// <para>
    /// Pass <paramref name="name"/> as <c>null</c> (the default) for a memory-backed drive
    /// that leaves no files on the server. Provide a non-empty name to get a temp-file-backed
    /// drive that persists media content and is cleaned up when the session closes.
    /// </para>
    /// </summary>
    /// <param name="capacityBytes">Maximum capacity in bytes (0 → server default).</param>
    /// <param name="name">Drive name; <c>null</c> or empty → anonymous memory-backed drive.</param>
    /// <param name="blockSize">Default block size in bytes (0 → drive default).</param>
    /// <param name="caps">Drive capabilities (null → server uses <c>WithFilemarksOnlyLargeBlocks</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the temporary drive was created and opened successfully.</returns>
    public async Task<bool> CreateTempVirtualAsync(long capacityBytes = 0, string? name = null,
        uint blockSize = 0, VirtualTapeDriveCapabilities? caps = null, CancellationToken ct = default)
    {
        var request = new CreateTempVirtualRequest
        {
            CapacityBytes = capacityBytes > 0 ? (ulong)capacityBytes : 0UL,
            Name          = name ?? string.Empty,
            BlockSize     = blockSize,
        };

        if (caps.HasValue)
        {
            request.Caps = new VirtualCapabilities
            {
                MinBlockSize               = caps.Value.MinBlockSize,
                MaxBlockSize               = caps.Value.MaxBlockSize,
                DefaultBlockSize           = caps.Value.DefaultBlockSize,
                SupportsSetmarks           = caps.Value.SupportsSetmarks,
                SupportsSeqFilemarks       = caps.Value.SupportsSeqFilemarks,
                SupportsInitiatorPartition = caps.Value.SupportsInitiatorPartition,
                SupportsCompression        = caps.Value.SupportsCompression,
            };
        }

        var call = _client.CreateTempVirtualAsync(request, cancellationToken: ct);
        var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
        _sessionId = headers.GetValue(SessionIdHeader) ?? string.Empty;
        var response = await call.ResponseAsync.ConfigureAwait(false);
        bool ok = Sync(response);
        if (ok)
            StartPingTimer();
        return ok;
    }

    /// <summary>
    /// Retrieves version and host information from the remote service without opening a session.
    /// Safe to call before any <see cref="Open"/> — no session ID required.
    /// </summary>
    /// <returns>
    /// A <see cref="ServerInfoResponse"/> with <c>ServerVersion</c>, <c>ProtocolLevel</c>,
    /// and <c>HostName</c>, or <c>null</c> if the call fails (service unreachable or not a
    /// TapeNET service).
    /// </returns>
    public ServerInfoResponse? GetServerInfo()
    {
        try
        {
            return _client.GetServerInfo(new EmptyRequest());
        }
        catch (RpcException ex)
        {
            m_logger.LogWarning(ex, "GetServerInfo failed for {Address}", RemoteAddress);
            return null;
        }
    }

    #endregion

    #region *** Drive Operations ***

    /// <summary>
    /// Opens a Win32 tape drive on the remote system.
    /// </summary>
    public override bool Open(uint driveNumber)
    {
        var call = _client.OpenWin32Async(new OpenWin32Request { DriveNumber = driveNumber });
        // Response headers contain the session ID issued by the server
        var headers = call.ResponseHeadersAsync.GetAwaiter().GetResult();
        _sessionId = headers.GetValue(SessionIdHeader) ?? string.Empty;
        var response = call.ResponseAsync.GetAwaiter().GetResult();
        bool ok = Sync(response);
        if (ok)
            StartPingTimer();
        return ok;
    }

    /// <summary>
    /// Asynchronously opens a Win32 tape drive on the remote system.
    /// </summary>
    /// <param name="driveNumber">Tape drive index (0-based).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> OpenAsync(uint driveNumber, CancellationToken ct = default)
    {
        var call = _client.OpenWin32Async(new OpenWin32Request { DriveNumber = driveNumber },
            cancellationToken: ct);
        // Response headers contain the session ID issued by the server
        var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
        _sessionId = headers.GetValue(SessionIdHeader) ?? string.Empty;
        var response = await call.ResponseAsync.ConfigureAwait(false);
        bool ok = Sync(response);
        if (ok)
            StartPingTimer();
        return ok;
    }

    /// <summary>
    /// Opens a virtual tape drive on the remote system.
    /// </summary>
    /// <param name="request">The fully configured open request with backend type and parameters.</param>
    /// <returns>True if the remote backend was created and opened successfully.</returns>
    public bool OpenVirtual(OpenVirtualRequest request)
    {
        var call = _client.OpenVirtualAsync(request);
        // Response headers contain the session ID issued by the server
        var headers = call.ResponseHeadersAsync.GetAwaiter().GetResult();
        _sessionId = headers.GetValue(SessionIdHeader) ?? string.Empty;
        var response = call.ResponseAsync.GetAwaiter().GetResult();
        bool ok = Sync(response);
        if (ok)
            StartPingTimer();
        return ok;
    }

    /// <summary>
    /// Asynchronously opens a virtual tape drive on the remote system.
    /// </summary>
    /// <param name="request">The fully configured open request with backend type and parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> OpenVirtualAsync(OpenVirtualRequest request, CancellationToken ct = default)
    {
        var call = _client.OpenVirtualAsync(request, cancellationToken: ct);
        // Response headers contain the session ID issued by the server
        var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
        _sessionId = headers.GetValue(SessionIdHeader) ?? string.Empty;
        var response = await call.ResponseAsync.ConfigureAwait(false);
        bool ok = Sync(response);
        if (ok)
            StartPingTimer();
        return ok;
    }

    public override void Close()
    {
        StopPingTimer();

        // Skip the RPC if we never obtained a session (Open failed) or it was already closed.
        if (!string.IsNullOrEmpty(_sessionId))
        {
            var response = _client.Close(new EmptyRequest(), WithSession());
            _sessionId = string.Empty;
            Sync(response);
        }
    }

    /// <summary>
    /// Asynchronously closes the current remote session and releases server-side resources.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        StopPingTimer();

        // Skip the RPC if we never obtained a session (Open failed) or it was already closed.
        if (!string.IsNullOrEmpty(_sessionId))
        {
            var call = _client.CloseAsync(new EmptyRequest(), WithSession(ct));
            _sessionId = string.Empty;
            Sync(await call.ResponseAsync.ConfigureAwait(false));
        }
    }

    public override bool SetDriveParameters(bool compression, bool ecc, bool dataPadding,
        bool reportSetmarks, uint eotWarningZoneSize)
    {
        var response = _client.SetDriveParameters(new SetDriveParametersRequest
        {
            Compression = compression,
            Ecc = ecc,
            DataPadding = dataPadding,
            ReportSetmarks = reportSetmarks,
            EotWarningZoneSize = eotWarningZoneSize,
        }, WithSession());
        return Sync(response);
    }

    #endregion

    #region *** Media Operations ***

    public override bool LoadMedia()
    {
        var response = _client.LoadMedia(new EmptyRequest(), WithSession());
        return Sync(response);
    }

    public override bool UnloadMedia()
    {
        var response = _client.UnloadMedia(new EmptyRequest(), WithSession());
        return Sync(response);
    }

    public override bool SetBlockSize(uint size)
    {
        var response = _client.SetBlockSize(new SetBlockSizeRequest { Size = size }, WithSession());
        return Sync(response);
    }

    public override bool FormatMedia(long initiatorPartitionSize = -1)
    {
        var response = _client.FormatMedia(new FormatMediaRequest
        {
            InitiatorPartitionSize = initiatorPartitionSize,
        }, WithSession());
        return Sync(response);
    }

    #endregion

    #region *** Read / Write ***

    public override int Read(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        var response = _client.Read(new ReadRequest { Count = count }, WithSession());
        SyncState(response.State);
        SyncError(response.Error);

        tapemark = response.Tapemark;
        eof = response.Eof;

        int bytesRead = response.Data.Length;
        if (bytesRead > 0)
            response.Data.CopyTo(buffer, offset);

        return bytesRead;
    }

    public override int Write(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        // Extract the segment to send — avoid sending the entire buffer
        var data = ByteString.CopyFrom(buffer, offset, count);

        var response = _client.Write(new WriteRequest { Data = data }, WithSession());
        SyncState(response.State);
        SyncError(response.Error);

        tapemark = response.Tapemark;
        eof = response.Eof;

        return response.BytesWritten;
    }

    #endregion

    #region *** Positioning ***

    public override bool SetPosition(long block)
    {
        var response = _client.SetPosition(new SetPositionRequest { Block = block }, WithSession());
        return Sync(response);
    }

    public override bool SetPositionToPartition(MediaPartition partition, long block)
    {
        var response = _client.SetPositionToPartition(new SetPositionToPartitionRequest
        {
            Partition = MapPartition(partition),
            Block = block,
        }, WithSession());
        return Sync(response);
    }

    public override long GetPosition()
    {
        var response = _client.GetPosition(new EmptyRequest(), WithSession());
        SyncState(response.State);
        SyncError(response.Error);
        return response.Position;
    }

    public override MediaPartition GetCurrentPartition()
    {
        var response = _client.GetCurrentPartition(new EmptyRequest(), WithSession());
        SyncState(response.State);
        SyncError(response.Error);
        return MapPartition(response.Partition);
    }

    public override bool Rewind()
    {
        var response = _client.Rewind(new EmptyRequest(), WithSession());
        return Sync(response);
    }

    public override bool SeekToEnd(MediaPartition partition)
    {
        var response = _client.SeekToEnd(new SeekToEndRequest
        {
            Partition = MapPartition(partition),
        }, WithSession());
        return Sync(response);
    }

    public override bool SpaceFilemarks(int count)
    {
        var response = _client.SpaceFilemarks(new SpaceMarksRequest { Count = count }, WithSession());
        return Sync(response);
    }

    public override bool SpaceSetmarks(int count)
    {
        var response = _client.SpaceSetmarks(new SpaceMarksRequest { Count = count }, WithSession());
        return Sync(response);
    }

    public override bool SpaceSequentialFilemarks(int count)
    {
        var response = _client.SpaceSequentialFilemarks(new SpaceMarksRequest { Count = count }, WithSession());
        return Sync(response);
    }

    #endregion

    #region *** Tapemarks ***

    public override bool WriteFilemarks(uint count)
    {
        var response = _client.WriteFilemarks(new WriteMarksRequest { Count = count }, WithSession());
        return Sync(response);
    }

    public override bool WriteSetmarks(uint count)
    {
        var response = _client.WriteSetmarks(new WriteMarksRequest { Count = count }, WithSession());
        return Sync(response);
    }

    #endregion

    #region *** Parameter Queries ***

    public override void FillDriveCapabilities(out DriveCapabilities parameters)
    {
        var response = _client.FillDriveCapabilities(new EmptyRequest(), WithSession());
        SyncState(response.State);
        SyncError(response.Error);

        parameters = new DriveCapabilities(
            response.MinimumBlockSize,
            response.MaximumBlockSize,
            response.DefaultBlockSize,
            response.SupportsCompression,
            response.SupportsEcc,
            response.SupportsPadding,
            response.SupportsSetmarks,
            response.SupportsSeqFilemarks,
            response.SupportsInitiatorPartition);
    }

    public override void FillMediaParameters(out MediaParameters parameters)
    {
        var response = _client.FillMediaParameters(new EmptyRequest(), WithSession());
        SyncState(response.State);
        SyncError(response.Error);

        parameters = new MediaParameters(
            response.Capacity,
            response.Remaining,
            response.BlockSize,
            response.HasInitiatorPartition,
            response.WriteProtected);
    }

    #endregion

    #region *** Partition Mapping ***

    private static ProtoMediaPartition MapPartition(MediaPartition p) => p switch
    {
        MediaPartition.Content => ProtoMediaPartition.MediaPartitionContent,
        MediaPartition.Initiator => ProtoMediaPartition.MediaPartitionInitiator,
        _ => ProtoMediaPartition.MediaPartitionCurrent,
    };

    private static MediaPartition MapPartition(ProtoMediaPartition p) => p switch
    {
        ProtoMediaPartition.MediaPartitionContent => MediaPartition.Content,
        ProtoMediaPartition.MediaPartitionInitiator => MediaPartition.Initiator,
        _ => MediaPartition.Current,
    };

    #endregion

    #region *** Keepalive Ping ***

    private void StartPingTimer()
    {
        _pingTimer = new Timer(SendPing, null, PingInterval, PingInterval);
    }

    private void StopPingTimer()
    {
        _pingTimer?.Dispose();
        _pingTimer = null;
    }

    private void SendPing(object? _)
    {
        if (string.IsNullOrEmpty(_sessionId)) return;
        try
        {
            _client.Ping(new EmptyRequest(), WithSession());
            m_logger.LogTrace("Keepalive ping sent for session {SessionId}", _sessionId);
        }
        catch (Exception ex)
        {
            // A failed ping is non-fatal — log it and let the next interval try again.
            // If the server has already reaped the session, the next real operation will
            // surface a proper RpcException with NotFound/Unauthenticated.
            m_logger.LogWarning(ex, "Keepalive ping failed for session {SessionId}", _sessionId);
        }
    }

    #endregion

    #region *** Dispose ***

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            StopPingTimer();

        // base.Dispose calls Close(), which sends the gRPC Close RPC and needs the
        //  channel to be alive — so we MUST call base first, then dispose the channel.
        base.Dispose(disposing);

        if (disposing && _ownsChannel)
            _channel.Dispose();
    }

    #endregion
}
