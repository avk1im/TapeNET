using Grpc.Core;
using Microsoft.Extensions.Logging;
using TapeLibNET;
using TapeLibNET.Remote;
using TapeLibNET.Virtual;

namespace TapeServiceNET;

/// <summary>
/// gRPC service implementation that forwards all RPCs to the <see cref="TapeDriveBackend"/>
/// held by the singleton <see cref="TapeDriveSession"/>.
/// <para>
/// This class is transient (one instance per request, the gRPC default).
/// All mutable state lives in the injected <see cref="TapeDriveSession"/>.
/// </para>
/// Client                                    Tape Server
/// ─────────────────────────────             ─────────────────────────────
/// TapeDrive TapeDriveGrpcService
///   └─ RemoteTapeDriveBackend    ──gRPC──►    └─ TapeDriveBackend
/// (generated client stub)                  (Win32 or Virtual)
/// </summary>
public class TapeDriveGrpcService(TapeDriveSession session, ILogger<TapeDriveGrpcService> logger)
    : TapeDriveService.TapeDriveServiceBase
{
    #region *** Helper Methods ***

    /// <summary>
    /// Captures current backend state into a <see cref="BackendState"/> message.
    /// Returns an empty state if no backend is active.
    /// </summary>
    private BackendState CaptureState()
    {
        var b = session.Backend;
        if (b == null)
            return new BackendState();

        return new BackendState
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
        };
    }

    /// <summary>
    /// Creates an <see cref="ErrorInfo"/> from the current backend error state.
    /// </summary>
    private ErrorInfo CaptureError()
    {
        var b = session.Backend;
        if (b == null)
            return new ErrorInfo();

        return new ErrorInfo
        {
            ErrorCode = b.LastError,
            ErrorMessage = b.LastErrorMessage ?? string.Empty,
        };
    }

    /// <summary>
    /// Builds an <see cref="OperationResponse"/> from a bool result, capturing error and state.
    /// </summary>
    private OperationResponse MakeResponse(bool success) => new()
    {
        Success = success,
        Error = CaptureError(),
        State = CaptureState(),
    };

    /// <summary>
    /// Ensures a backend is available, throwing an RPC error if not.
    /// </summary>
    private TapeDriveBackend RequireBackend()
    {
        return session.Backend ?? throw new RpcException(
            new Status(StatusCode.FailedPrecondition, "No backend is open. Call OpenWin32 or OpenVirtual first."));
    }

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

    public override Task<OperationResponse> OpenWin32(OpenWin32Request request, ServerCallContext context)
    {
        logger.LogInformation("OpenWin32: drive #{DriveNumber}", request.DriveNumber);

        var backend = new TapeDriveWin32Backend(session.LoggerFactory);
        bool success = backend.Open(request.DriveNumber);

        if (success)
        {
            session.SetBackend(backend);
        }
        else
        {
            // Capture error before disposing the failed backend
            var error = new ErrorInfo { ErrorCode = backend.LastError, ErrorMessage = backend.LastErrorMessage };
            backend.Dispose();
            return Task.FromResult(new OperationResponse { Success = false, Error = error, State = CaptureState() });
        }

        return Task.FromResult(MakeResponse(success));
    }

    public override Task<OperationResponse> OpenVirtual(OpenVirtualRequest request, ServerCallContext context)
    {
        logger.LogInformation("OpenVirtual: drive #{DriveNumber}, config: {Config}",
            request.DriveNumber, request.ConfigCase);

        VirtualTapeDriveBackend backend = request.ConfigCase switch
        {
            OpenVirtualRequest.ConfigOneofCase.MemoryConfig => CreateMemoryBackend(request.MemoryConfig),
            OpenVirtualRequest.ConfigOneofCase.MemoryMapConfig => CreateMemoryMapBackend(request.MemoryMapConfig),
            OpenVirtualRequest.ConfigOneofCase.FileConfig => CreateFileBackend(request.FileConfig),
            _ => VirtualTapeDriveBackend.CreateMemoryBacked(session.LoggerFactory),
        };

        bool success = backend.Open(request.DriveNumber);

        if (success)
        {
            session.SetBackend(backend);
        }
        else
        {
            var error = new ErrorInfo { ErrorCode = backend.LastError, ErrorMessage = backend.LastErrorMessage };
            backend.Dispose();
            return Task.FromResult(new OperationResponse { Success = false, Error = error, State = CaptureState() });
        }

        return Task.FromResult(MakeResponse(success));
    }

    private VirtualTapeDriveBackend CreateMemoryBackend(VirtualMemoryConfig cfg)
    {
        var caps = MapCapabilities(cfg.Capabilities);
        long contentCap = cfg.ContentCapacity > 0 ? cfg.ContentCapacity : 500L * 1024 * 1024;
        long initCap = cfg.InitiatorCapacity > 0 ? cfg.InitiatorCapacity : 16L * 1024 * 1024;
        return VirtualTapeDriveBackend.CreateMemoryBacked(session.LoggerFactory, caps, contentCap, initCap);
    }

    private VirtualTapeDriveBackend CreateMemoryMapBackend(VirtualMemoryMapConfig cfg)
    {
        var caps = MapCapabilities(cfg.Capabilities);
        long contentCap = cfg.ContentCapacity > 0 ? cfg.ContentCapacity : 4L * 1024 * 1024 * 1024;
        long initCap = cfg.InitiatorCapacity > 0 ? cfg.InitiatorCapacity : 16L * 1024 * 1024;
        return VirtualTapeDriveBackend.CreateMemoryMapBacked(session.LoggerFactory, caps, contentCap, initCap);
    }

    private VirtualTapeDriveBackend CreateFileBackend(VirtualFileConfig cfg)
    {
        var caps = MapCapabilities(cfg.Capabilities);
        var mediaMode = cfg.MediaMode != 0 ? (FileMode)cfg.MediaMode : FileMode.OpenOrCreate;
        return VirtualTapeDriveBackend.CreateFileBacked(
            session.LoggerFactory,
            cfg.ContentFilePath,
            cfg.ContentCapacity,
            string.IsNullOrEmpty(cfg.InitiatorFilePath) ? null : cfg.InitiatorFilePath,
            cfg.InitiatorCapacity,
            caps,
            mediaMode);
    }

    public override Task<OperationResponse> Close(EmptyRequest request, ServerCallContext context)
    {
        var b = session.Backend;
        if (b != null)
        {
            b.Close();
            logger.LogInformation("Close: drive closed");
        }

        return Task.FromResult(MakeResponse(true));
    }

    public override Task<OperationResponse> SetDriveParameters(SetDriveParametersRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        bool ok = b.SetDriveParameters(
            request.Compression, request.Ecc, request.DataPadding,
            request.ReportSetmarks, request.EotWarningZoneSize);
        return Task.FromResult(MakeResponse(ok));
    }

    #endregion

    #region *** Media Lifecycle ***

    public override Task<OperationResponse> LoadMedia(EmptyRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.LoadMedia()));
    }

    public override Task<OperationResponse> UnloadMedia(EmptyRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.UnloadMedia()));
    }

    public override Task<OperationResponse> SetBlockSize(SetBlockSizeRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.SetBlockSize(request.Size)));
    }

    public override Task<OperationResponse> FormatMedia(FormatMediaRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.FormatMedia(request.InitiatorPartitionSize)));
    }

    #endregion

    #region *** Read / Write ***

    public override Task<ReadResponse> Read(ReadRequest request, ServerCallContext context)
    {
        var b = RequireBackend();

        var buffer = new byte[request.Count];
        int bytesRead = b.Read(buffer, 0, request.Count, out bool tapemark, out bool eof);

        var response = new ReadResponse
        {
            // Return only the bytes actually read
            Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead),
            Tapemark = tapemark,
            Eof = eof,
            Error = CaptureError(),
            State = CaptureState(),
        };

        return Task.FromResult(response);
    }

    public override Task<WriteResponse> Write(WriteRequest request, ServerCallContext context)
    {
        var b = RequireBackend();

        var data = request.Data.ToByteArray();
        int written = b.Write(data, 0, data.Length, out bool tapemark, out bool eof);

        var response = new WriteResponse
        {
            BytesWritten = written,
            Tapemark = tapemark,
            Eof = eof,
            Error = CaptureError(),
            State = CaptureState(),
        };

        return Task.FromResult(response);
    }

    #endregion

    #region *** Positioning ***

    public override Task<OperationResponse> SetPosition(SetPositionRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.SetPosition(request.Block)));
    }

    public override Task<OperationResponse> SetPositionToPartition(SetPositionToPartitionRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(
            b.SetPositionToPartition(MapPartition(request.Partition), request.Block)));
    }

    public override Task<GetPositionResponse> GetPosition(EmptyRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(new GetPositionResponse
        {
            Position = b.GetPosition(),
            Error = CaptureError(),
            State = CaptureState(),
        });
    }

    public override Task<GetCurrentPartitionResponse> GetCurrentPartition(EmptyRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(new GetCurrentPartitionResponse
        {
            Partition = MapPartition(b.GetCurrentPartition()),
            Error = CaptureError(),
            State = CaptureState(),
        });
    }

    public override Task<OperationResponse> Rewind(EmptyRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.Rewind()));
    }

    public override Task<OperationResponse> SeekToEnd(SeekToEndRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.SeekToEnd(MapPartition(request.Partition))));
    }

    public override Task<OperationResponse> SpaceFilemarks(SpaceMarksRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.SpaceFilemarks(request.Count)));
    }

    public override Task<OperationResponse> SpaceSetmarks(SpaceMarksRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.SpaceSetmarks(request.Count)));
    }

    public override Task<OperationResponse> SpaceSequentialFilemarks(SpaceMarksRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.SpaceSequentialFilemarks(request.Count)));
    }

    #endregion

    #region *** Tapemarks ***

    public override Task<OperationResponse> WriteFilemarks(WriteMarksRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.WriteFilemarks(request.Count)));
    }

    public override Task<OperationResponse> WriteSetmarks(WriteMarksRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        return Task.FromResult(MakeResponse(b.WriteSetmarks(request.Count)));
    }

    #endregion

    #region *** Parameter Queries ***

    public override Task<DriveCapabilitiesResponse> FillDriveCapabilities(EmptyRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
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
            Error = CaptureError(),
            State = CaptureState(),
        });
    }

    public override Task<MediaParametersResponse> FillMediaParameters(EmptyRequest request, ServerCallContext context)
    {
        var b = RequireBackend();
        b.FillMediaParameters(out MediaParameters media);

        return Task.FromResult(new MediaParametersResponse
        {
            Capacity = media.Capacity,
            Remaining = media.Remaining,
            BlockSize = media.BlockSize,
            HasInitiatorPartition = media.HasInitiatorPartition,
            WriteProtected = media.WriteProtected,
            Error = CaptureError(),
            State = CaptureState(),
        });
    }

    #endregion
}
