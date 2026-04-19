using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

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

    // Cached property snapshot, refreshed on every RPC response
    private BackendState _state = new();

    #endregion

    #region *** Constructors ***

    /// <summary>
    /// Creates a remote backend connecting to the specified host and port.
    /// </summary>
    /// <param name="host">Hostname or IP address of the tape service.</param>
    /// <param name="port">gRPC port (default 50551).</param>
    /// <param name="loggerFactory">Logger factory for logging.</param>
    public RemoteTapeDriveBackend(string host, int port, ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        var address = $"http://{host}:{port}";
        _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            // Tape I/O can transfer large blocks — allow up to 16 MB messages
            MaxReceiveMessageSize = 16 * 1024 * 1024,
            MaxSendMessageSize = 16 * 1024 * 1024,
        });
        _client = new TapeDriveService.TapeDriveServiceClient(_channel);
        _ownsChannel = true;

        m_logger.LogTrace("RemoteTapeDriveBackend created for {Address}", address);
    }

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

    #endregion

    #region *** State Properties (from cache) ***

    public override bool IsOpen => _state.IsOpen;
    public override bool HasMedia => _state.HasMedia;
    public override string DeviceName => _state.DeviceName ?? string.Empty;
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

    #region *** Drive Operations ***

    /// <summary>
    /// Opens a Win32 tape drive on the remote system.
    /// </summary>
    public override bool Open(uint driveNumber)
    {
        var response = _client.OpenWin32(new OpenWin32Request { DriveNumber = driveNumber });
        return Sync(response);
    }

    /// <summary>
    /// Opens a virtual tape drive on the remote system.
    /// </summary>
    /// <param name="request">The fully configured open request with backend type and parameters.</param>
    /// <returns>True if the remote backend was created and opened successfully.</returns>
    public bool OpenVirtual(OpenVirtualRequest request)
    {
        var response = _client.OpenVirtual(request);
        return Sync(response);
    }

    public override void Close()
    {
        var response = _client.Close(new EmptyRequest());
        Sync(response);
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
        });
        return Sync(response);
    }

    #endregion

    #region *** Media Operations ***

    public override bool LoadMedia()
    {
        var response = _client.LoadMedia(new EmptyRequest());
        return Sync(response);
    }

    public override bool UnloadMedia()
    {
        var response = _client.UnloadMedia(new EmptyRequest());
        return Sync(response);
    }

    public override bool SetBlockSize(uint size)
    {
        var response = _client.SetBlockSize(new SetBlockSizeRequest { Size = size });
        return Sync(response);
    }

    public override bool FormatMedia(long initiatorPartitionSize = -1)
    {
        var response = _client.FormatMedia(new FormatMediaRequest
        {
            InitiatorPartitionSize = initiatorPartitionSize,
        });
        return Sync(response);
    }

    #endregion

    #region *** Read / Write ***

    public override int Read(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        var response = _client.Read(new ReadRequest { Count = count });
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

        var response = _client.Write(new WriteRequest { Data = data });
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
        var response = _client.SetPosition(new SetPositionRequest { Block = block });
        return Sync(response);
    }

    public override bool SetPositionToPartition(MediaPartition partition, long block)
    {
        var response = _client.SetPositionToPartition(new SetPositionToPartitionRequest
        {
            Partition = MapPartition(partition),
            Block = block,
        });
        return Sync(response);
    }

    public override long GetPosition()
    {
        var response = _client.GetPosition(new EmptyRequest());
        SyncState(response.State);
        SyncError(response.Error);
        return response.Position;
    }

    public override MediaPartition GetCurrentPartition()
    {
        var response = _client.GetCurrentPartition(new EmptyRequest());
        SyncState(response.State);
        SyncError(response.Error);
        return MapPartition(response.Partition);
    }

    public override bool Rewind()
    {
        var response = _client.Rewind(new EmptyRequest());
        return Sync(response);
    }

    public override bool SeekToEnd(MediaPartition partition)
    {
        var response = _client.SeekToEnd(new SeekToEndRequest
        {
            Partition = MapPartition(partition),
        });
        return Sync(response);
    }

    public override bool SpaceFilemarks(int count)
    {
        var response = _client.SpaceFilemarks(new SpaceMarksRequest { Count = count });
        return Sync(response);
    }

    public override bool SpaceSetmarks(int count)
    {
        var response = _client.SpaceSetmarks(new SpaceMarksRequest { Count = count });
        return Sync(response);
    }

    public override bool SpaceSequentialFilemarks(int count)
    {
        var response = _client.SpaceSequentialFilemarks(new SpaceMarksRequest { Count = count });
        return Sync(response);
    }

    #endregion

    #region *** Tapemarks ***

    public override bool WriteFilemarks(uint count)
    {
        var response = _client.WriteFilemarks(new WriteMarksRequest { Count = count });
        return Sync(response);
    }

    public override bool WriteSetmarks(uint count)
    {
        var response = _client.WriteSetmarks(new WriteMarksRequest { Count = count });
        return Sync(response);
    }

    #endregion

    #region *** Parameter Queries ***

    public override void FillDriveCapabilities(out DriveCapabilities parameters)
    {
        var response = _client.FillDriveCapabilities(new EmptyRequest());
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
        var response = _client.FillMediaParameters(new EmptyRequest());
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

    #region *** Dispose ***

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsChannel)
        {
            _channel.Dispose();
        }

        base.Dispose(disposing);
    }

    #endregion
}
