using Grpc.Core;
using Microsoft.Extensions.Logging;
using TapeLibNET;
using TapeLibNET.Remote;
using TapeLibNET.Virtual;

namespace TapeServiceNET;

/// <summary>
/// gRPC service implementation that forwards all RPCs to the <see cref="TapeDriveBackend"/>
/// held by the singleton <see cref="TapeDriveSessionRegistry"/>.
/// <para>
/// This class is transient (one instance per request, the gRPC default).
/// The registry maps session IDs to backends so that multiple clients can
/// each own a separate drive concurrently. Every RPC (except Open*) must
/// supply the <c>x-tape-session-id</c> header obtained from the Open* response.
/// </para>
/// Client                                    Tape Server
/// ─────────────────────────────             ─────────────────────────────
/// TapeDrive TapeDriveGrpcService
///   └─ RemoteTapeDriveBackend    ──gRPC──►    └─ TapeDriveBackend
/// (generated client stub)                  (Win32 or Virtual)
/// </summary>
public class TapeDriveGrpcService(TapeDriveSessionRegistry registry, ILogger<TapeDriveGrpcService> logger)
    : TapeDriveService.TapeDriveServiceBase
{
    #region *** Helper Methods ***

    /// <summary>
    /// Captures backend state into a <see cref="BackendState"/> message.
    /// </summary>
    private static BackendState CaptureState(TapeDriveBackend b) => new()
    {
        IsOpen = b.IsOpen,
        HasMedia = b.HasMedia,
        DeviceName = b.DeviceName,
        DriveNumber = b.DriveNumber,
        BlockSize = b.BlockSize,
        MinBlockSize = b.MinBlockSize,
        MaxBlockSize = b.MaxBlockSize,
        DefaultBlockSize = b.DefaultBlockSize,
        Capacity = b.Capacity,
        Remaining = b.Remaining,
        Position = b.Position,
        SupportsInitiatorPartition = b.SupportsInitiatorPartition,
        HasInitiatorPartition = b.HasInitiatorPartition,
        SupportsSetmarks = b.SupportsSetmarks,
        SupportsSeqFilemarks = b.SupportsSeqFilemarks,
        Vendor = b.Vendor,
        Product = b.Product,
    };

    /// <summary>
    /// Creates an <see cref="ErrorInfo"/> from the backend error state.
    /// </summary>
    private static ErrorInfo CaptureError(TapeDriveBackend b) => new()
    {
        ErrorCode = b.LastError,
        ErrorMessage = b.LastErrorMessage ?? string.Empty,
    };

    /// <summary>
    /// Builds an <see cref="OperationResponse"/> from a bool result, capturing error and state.
    /// </summary>
    private static OperationResponse MakeResponse(TapeDriveBackend b, bool success) => new()
    {
        Success = success,
        Error = CaptureError(b),
        State = CaptureState(b),
    };

    /// <summary>
    /// Extracts the session ID from the request headers and returns a <see cref="TapeDriveSessionScope"/>
    /// that protects the session from the reaper for the duration of this RPC handler.
    /// Always consume with <c>using</c>.
    /// </summary>
    private TapeDriveSessionScope GetScope(ServerCallContext context)
        => registry.RequireSession(
            context.RequestHeaders.GetValue(TapeSessionConstants.SessionIdHeader));

    /// <summary>
    /// Maps a <see cref="ProtoMediaPartition"/> enum to the TapeLibNET <see cref="MediaPartition"/>.
    /// </summary>
    private static MediaPartition MapPartition(ProtoMediaPartition p) => p switch
    {
        ProtoMediaPartition.MediaPartitionContent => MediaPartition.Content,
        ProtoMediaPartition.MediaPartitionInitiator => MediaPartition.Initiator,
        _ => MediaPartition.Current,
    };

    /// <summary>
    /// Maps a TapeLibNET <see cref="MediaPartition"/> to the proto enum.
    /// </summary>
    private static ProtoMediaPartition MapPartition(MediaPartition p) => p switch
    {
        MediaPartition.Content => ProtoMediaPartition.MediaPartitionContent,
        MediaPartition.Initiator => ProtoMediaPartition.MediaPartitionInitiator,
        _ => ProtoMediaPartition.MediaPartitionCurrent,
    };

    /// <summary>
    /// Converts a <see cref="VirtualCapabilities"/> proto message to <see cref="VirtualTapeDriveCapabilities"/>.
    /// Returns the default (WithSetmarks) if the message is null or zero-valued.
    /// </summary>
    private static VirtualTapeDriveCapabilities MapCapabilities(VirtualCapabilities? caps)
    {
        if (caps == null || caps.DefaultBlockSize == 0)
            return VirtualTapeDriveCapabilities.WithSetmarks;

        return new VirtualTapeDriveCapabilities
        {
            MinBlockSize = caps.MinBlockSize,
            MaxBlockSize = caps.MaxBlockSize,
            DefaultBlockSize = caps.DefaultBlockSize,
            SupportsSetmarks = caps.SupportsSetmarks,
            SupportsSeqFilemarks = caps.SupportsSeqFilemarks,
            SupportsInitiatorPartition = caps.SupportsInitiatorPartition,
            SupportsCompression = caps.SupportsCompression,
        };
    }

    #endregion

    #region *** Drive Lifecycle ***

    public override async Task<OperationResponse> OpenWin32(OpenWin32Request request, ServerCallContext context)
    {
        logger.LogInformation("OpenWin32: drive #{DriveNumber}", request.DriveNumber);

        var backend = new TapeDriveWin32Backend(registry.LoggerFactory);
        bool success = backend.Open(request.DriveNumber);

        if (success)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            registry.Add(sessionId, backend, context.Peer);
            await context.WriteResponseHeadersAsync(
                new Metadata { { TapeSessionConstants.SessionIdHeader, sessionId } });
        }
        else
        {
            // Capture error before disposing the failed backend
            var error = new ErrorInfo { ErrorCode = backend.LastError, ErrorMessage = backend.LastErrorMessage };
            backend.Dispose();
            return new OperationResponse { Success = false, Error = error };
        }

        return MakeResponse(backend, success);
    }

    public override async Task<OperationResponse> OpenVirtual(OpenVirtualRequest request, ServerCallContext context)
    {
        logger.LogInformation("OpenVirtual: drive #{DriveNumber}, config: {Config}",
            request.DriveNumber, request.ConfigCase);

        VirtualTapeDriveBackend backend = request.ConfigCase switch
        {
            OpenVirtualRequest.ConfigOneofCase.MemoryConfig => CreateMemoryBackend(request.MemoryConfig),
            OpenVirtualRequest.ConfigOneofCase.MemoryMapConfig => CreateMemoryMapBackend(request.MemoryMapConfig),
            OpenVirtualRequest.ConfigOneofCase.FileConfig => CreateFileBackend(request.FileConfig),
            _ => VirtualTapeDriveBackend.CreateMemoryBacked(registry.LoggerFactory),
        };

        bool success = backend.Open(request.DriveNumber);

        if (success)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            registry.Add(sessionId, backend, context.Peer);
            await context.WriteResponseHeadersAsync(
                new Metadata { { TapeSessionConstants.SessionIdHeader, sessionId } });
        }
        else
        {
            var error = new ErrorInfo { ErrorCode = backend.LastError, ErrorMessage = backend.LastErrorMessage };
            backend.Dispose();
            return new OperationResponse { Success = false, Error = error };
        }

        return MakeResponse(backend, success);
    }

    private VirtualTapeDriveBackend CreateMemoryBackend(VirtualMemoryConfig cfg)
    {
        var caps = MapCapabilities(cfg.Capabilities);
        long contentCap = cfg.ContentCapacity > 0 ? cfg.ContentCapacity : 500L * 1024 * 1024;
        long initCap = cfg.InitiatorCapacity > 0 ? cfg.InitiatorCapacity : 16L * 1024 * 1024;
        return VirtualTapeDriveBackend.CreateMemoryBacked(registry.LoggerFactory, caps, contentCap, initCap);
    }

    private VirtualTapeDriveBackend CreateMemoryMapBackend(VirtualMemoryMapConfig cfg)
    {
        var caps = MapCapabilities(cfg.Capabilities);
        long contentCap = cfg.ContentCapacity > 0 ? cfg.ContentCapacity : 4L * 1024 * 1024 * 1024;
        long initCap = cfg.InitiatorCapacity > 0 ? cfg.InitiatorCapacity : 16L * 1024 * 1024;
        return VirtualTapeDriveBackend.CreateMemoryMapBacked(registry.LoggerFactory, caps, contentCap, initCap);
    }

    private VirtualTapeDriveBackend CreateFileBackend(VirtualFileConfig cfg)
    {
        var caps = MapCapabilities(cfg.Capabilities);
        var mediaMode = cfg.MediaMode != 0 ? (FileMode)cfg.MediaMode : FileMode.OpenOrCreate;
        return VirtualTapeDriveBackend.CreateFileBacked(
            registry.LoggerFactory,
            cfg.ContentFilePath,
            cfg.ContentCapacity,
            string.IsNullOrEmpty(cfg.InitiatorFilePath) ? null : cfg.InitiatorFilePath,
            cfg.InitiatorCapacity,
            caps,
            mediaMode);
    }

    public override Task<OperationResponse> Close(EmptyRequest request, ServerCallContext context)
    {
        var id = context.RequestHeaders.GetValue(TapeSessionConstants.SessionIdHeader);

        if (!string.IsNullOrEmpty(id))
        {
            // Call backend.Close() before Remove() so the drive can flush and rewind.
            // RequireSession increments the in-flight counter; we release it immediately
            // since Remove() will dispose the backend right after.
            try
            {
                using var scope = registry.RequireSession(id);
                scope.Backend.Close();
            }
            catch (Grpc.Core.RpcException)
            {
                // Session already gone (e.g. reaped between the client's last Ping and Close).
                // Nothing left to clean up — fall through to the success response.
            }

            registry.Remove(id);
        }

        logger.LogInformation("Close: session {SessionId} closed", id ?? "<none>");
        return Task.FromResult(new OperationResponse { Success = true });
    }

    public override Task<OperationResponse> SetDriveParameters(SetDriveParametersRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        bool ok = b.SetDriveParameters(
            request.Compression, request.Ecc, request.DataPadding,
            request.ReportSetmarks, request.EotWarningZoneSize);
        return Task.FromResult(MakeResponse(b, ok));
    }

    #endregion

    #region *** Media Lifecycle ***

    public override Task<OperationResponse> LoadMedia(EmptyRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.LoadMedia()));
    }

    public override Task<OperationResponse> UnloadMedia(EmptyRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.UnloadMedia()));
    }

    public override Task<OperationResponse> SetBlockSize(SetBlockSizeRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.SetBlockSize(request.Size)));
    }

    public override Task<OperationResponse> FormatMedia(FormatMediaRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.FormatMedia(request.InitiatorPartitionSize)));
    }

    #endregion

    #region *** Read / Write ***

    public override Task<ReadResponse> Read(ReadRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;

        var buffer = new byte[request.Count];
        int bytesRead = b.Read(buffer, 0, request.Count, out bool tapemark, out bool eof);

        var response = new ReadResponse
        {
            // Return only the bytes actually read
            Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead),
            Tapemark = tapemark,
            Eof = eof,
            Error = CaptureError(b),
            State = CaptureState(b),
        };

        return Task.FromResult(response);
    }

    public override Task<WriteResponse> Write(WriteRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;

        var data = request.Data.ToByteArray();
        int written = b.Write(data, 0, data.Length, out bool tapemark, out bool eof);

        var response = new WriteResponse
        {
            BytesWritten = written,
            Tapemark = tapemark,
            Eof = eof,
            Error = CaptureError(b),
            State = CaptureState(b),
        };

        return Task.FromResult(response);
    }

    #endregion

    #region *** Positioning ***

    public override Task<OperationResponse> SetPosition(SetPositionRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.SetPosition(request.Block)));
    }

    public override Task<OperationResponse> SetPositionToPartition(SetPositionToPartitionRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b,
            b.SetPositionToPartition(MapPartition(request.Partition), request.Block)));
    }

    public override Task<GetPositionResponse> GetPosition(EmptyRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(new GetPositionResponse
        {
            Position = b.GetPosition(),
            Error = CaptureError(b),
            State = CaptureState(b),
        });
    }

    public override Task<GetCurrentPartitionResponse> GetCurrentPartition(EmptyRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(new GetCurrentPartitionResponse
        {
            Partition = MapPartition(b.GetCurrentPartition()),
            Error = CaptureError(b),
            State = CaptureState(b),
        });
    }

    public override Task<OperationResponse> Rewind(EmptyRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.Rewind()));
    }

    public override Task<OperationResponse> SeekToEnd(SeekToEndRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.SeekToEnd(MapPartition(request.Partition))));
    }

    public override Task<OperationResponse> SpaceFilemarks(SpaceMarksRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.SpaceFilemarks(request.Count)));
    }

    public override Task<OperationResponse> SpaceSetmarks(SpaceMarksRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.SpaceSetmarks(request.Count)));
    }

    public override Task<OperationResponse> SpaceSequentialFilemarks(SpaceMarksRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.SpaceSequentialFilemarks(request.Count)));
    }

    #endregion

    #region *** Tapemarks ***

    public override Task<OperationResponse> WriteFilemarks(WriteMarksRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.WriteFilemarks(request.Count)));
    }

    public override Task<OperationResponse> WriteSetmarks(WriteMarksRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        return Task.FromResult(MakeResponse(b, b.WriteSetmarks(request.Count)));
    }

    #endregion

    #region *** Parameter Queries ***

    public override Task<DriveCapabilitiesResponse> FillDriveCapabilities(EmptyRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        b.FillDriveCapabilities(out DriveCapabilities caps);

        return Task.FromResult(new DriveCapabilitiesResponse
        {
            MinimumBlockSize = caps.MinimumBlockSize,
            MaximumBlockSize = caps.MaximumBlockSize,
            DefaultBlockSize = caps.DefaultBlockSize,
            SupportsCompression = caps.SupportsCompression,
            SupportsEcc = caps.SupportsEcc,
            SupportsPadding = caps.SupportsPadding,
            SupportsSetmarks = caps.SupportsSetmarks,
            SupportsSeqFilemarks = caps.SupportsSeqFilemarks,
            SupportsInitiatorPartition = caps.SupportsInitiatorPartition,
            Error = CaptureError(b),
            State = CaptureState(b),
        });
    }

    public override Task<MediaParametersResponse> FillMediaParameters(EmptyRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context); var b = scope.Backend;
        b.FillMediaParameters(out MediaParameters media);

        return Task.FromResult(new MediaParametersResponse
        {
            Capacity = media.Capacity,
            Remaining = media.Remaining,
            BlockSize = media.BlockSize,
            HasInitiatorPartition = media.HasInitiatorPartition,
            WriteProtected = media.WriteProtected,
            Error = CaptureError(b),
            State = CaptureState(b),
        });
    }

    #endregion

    #region *** Session Keepalive ***

    /// <summary>
    /// No-op heartbeat called by the client on a background timer while idle.
    /// Touching <see cref="TapeDriveSessionEntry.LastActivity"/> is done inside
    /// <see cref="TapeDriveSessionRegistry.RequireSession"/>, so the reaper clock resets
    /// automatically just by entering the scope.
    /// </summary>
    public override Task<EmptyResponse> Ping(EmptyRequest request, ServerCallContext context)
    {
        // Acquiring (and immediately releasing) the scope is sufficient — RequireSession
        // already bumps LastActivity, preventing a reap between now and the next Ping.
        using var _ = GetScope(context);
        return Task.FromResult(new EmptyResponse());
    }

    #endregion
}
