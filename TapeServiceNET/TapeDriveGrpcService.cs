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

    /// <summary>
    /// Converts a <see cref="VirtualTapeDriveCapabilities"/> C# record to a
    /// <see cref="VirtualCapabilities"/> proto message (reverse of <see cref="MapCapabilities"/>).
    /// </summary>
    private static VirtualCapabilities MapCapabilities(VirtualTapeDriveCapabilities caps) =>
        new()
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

            // ── Catalog: register the initial file-backed volume so restore seeding can look it up ──
            // Memory-backed and memory-map drives are never catalogued (no persistent file path).
            if (request.ConfigCase == OpenVirtualRequest.ConfigOneofCase.FileConfig
                && !string.IsNullOrEmpty(request.FileConfig.ContentFilePath))
            {
                if (registry.TryGet(sessionId, out var entry))
                {
                    var cfg  = request.FileConfig;
                    var caps = cfg.Capabilities != null
                        ? MapCapabilities(cfg.Capabilities)
                        : VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks;

                    lock (entry!.Volumes)
                    {
                        entry.Volumes.Add(new RemoteVirtualVolumeEntry(
                            Path.GetFileNameWithoutExtension(cfg.ContentFilePath),
                            cfg.ContentFilePath,
                            cfg.ContentCapacity > 0 ? cfg.ContentCapacity : backend.Capacity,
                            string.IsNullOrEmpty(cfg.InitiatorFilePath) ? null : cfg.InitiatorFilePath,
                            cfg.InitiatorCapacity,
                            caps,
                            BlockSize: backend.BlockSize,
                            DateTime.UtcNow)
                        {
                            IsCurrent   = true,
                            IsServerOwned = false, // caller provided this path; never auto-delete
                        });
                    }
                }
            }
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

    #region *** Drive Discovery (sessionless) ***

    /// <summary>
    /// Probes Win32 tape drive numbers 0 .. <c>request.MaxDrive</c> without opening them
    /// and returns those that exist. Sessionless — no session ID header is required.
    /// </summary>
    public override Task<ProbeDrivesResponse> ProbeDrives(ProbeDrivesRequest request, ServerCallContext context)
    {
        uint maxDrive = request.MaxDrive > 0 ? request.MaxDrive : 9;
        logger.LogDebug("ProbeDrives: probing drives 0..{MaxDrive}", maxDrive);

        var response = new ProbeDrivesResponse();
        for (uint i = 0; i <= maxDrive; i++)
        {
            if (TapeLibNET.TapeDrive.ProbeWin32(i))
                response.DriveNumbers.Add(i);
        }

        logger.LogDebug("ProbeDrives: found drives [{DriveList}]",
            string.Join(", ", response.DriveNumbers));
        return Task.FromResult(response);
    }

    /// <summary>
    /// Creates a temporary virtual tape drive for testing.
    /// Unnamed drives (empty <c>request.Name</c>) are memory-backed and vanish with the session.
    /// Named drives are file-backed in the server's temp folder and deleted when the session closes.
    /// </summary>
    public override async Task<OperationResponse> CreateTempVirtual(
        CreateTempVirtualRequest request, ServerCallContext context)
    {
        logger.LogInformation("CreateTempVirtual: name='{Name}', capacity={Capacity}, blockSize={BlockSize}",
            request.Name, request.CapacityBytes, request.BlockSize);

        var caps = (request.Caps == null || request.Caps.DefaultBlockSize == 0)
            ? VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks
            : MapCapabilities(request.Caps);

        long capacityBytes = request.CapacityBytes > 0 ? (long)request.CapacityBytes : 500L * 1024 * 1024;

        TapeDriveBackend backend;
        string? basePath = null; // set only for named (file-backed) drives; used by the volume catalog
        bool isServerOwned = false; // true only for server-generated temp paths

        if (string.IsNullOrEmpty(request.Name))
        {
            // Unnamed: pure in-memory drive, no cleanup needed
            var memBackend = VirtualTapeDriveBackend.CreateMemoryBacked(
                registry.LoggerFactory, caps, capacityBytes);

            if (request.BlockSize > 0)
                memBackend.SetBlockSize(request.BlockSize);

            backend = memBackend;
        }
        else
        {
            // Named: file-backed drive.
            // If the caller supplies an absolute path, use it directly (client-owned, not deleted on close).
            // If the caller supplies a bare name, construct a temp path in the system temp folder (server-owned).
            bool callerProvidedPath = Path.IsPathRooted(request.Name);

            if (callerProvidedPath)
            {
                basePath      = request.Name;
                isServerOwned = false; // caller manages the file lifetime
            }
            else
            {
                basePath = Path.Combine(Path.GetTempPath(),
                    $"tapenet_tmp_{request.Name}_{Guid.NewGuid():N}.vtape");
                isServerOwned = true; // server created this file; deleted when session closes
            }

            // No initiator partition for temp drives (keeps it simple)
            var fileBackend = VirtualTapeDriveBackend.CreateFileBacked(
                registry.LoggerFactory,
                contentFilePath: basePath,
                contentCapacity: capacityBytes,
                initiatorFilePath: null,
                capabilities: caps,
                mediaMode: FileMode.Create);

            if (request.BlockSize > 0)
                fileBackend.SetBlockSize(request.BlockSize);

            // Only wrap in TempVirtualTapeDriveBackend (auto-delete on dispose) when the server
            //  owns the file.  Client-provided absolute paths must never be auto-deleted.
            backend = isServerOwned
                ? new TempVirtualTapeDriveBackend(fileBackend, basePath, initiatorFilePath: null)
                : fileBackend;
        }

        // Use drive number 0 for all temp virtual drives (the number is cosmetic for virtual backends)
        const uint TempDriveNumber = 0;
        bool success = backend.Open(TempDriveNumber);

        if (success)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            registry.Add(sessionId, backend, context.Peer);
            await context.WriteResponseHeadersAsync(
                new Metadata { { TapeSessionConstants.SessionIdHeader, sessionId } });

            // Catalog: record named (file-backed) volumes so the client can list and re-open them.
            if (!string.IsNullOrEmpty(request.Name))
            {
                if (registry.TryGet(sessionId, out var entry))
                {
                    uint effectiveBlockSize = request.BlockSize > 0
                        ? request.BlockSize
                        : (backend as VirtualTapeDriveBackend)?.BlockSize
                          ?? (backend as TempVirtualTapeDriveBackend)?.BlockSize ?? 0;

                    lock (entry!.Volumes)
                    {
                        entry.Volumes.Add(new RemoteVirtualVolumeEntry(
                            request.Name,
                            basePath!,
                            capacityBytes,
                            null,
                            0,
                            caps,
                            effectiveBlockSize,
                            DateTime.UtcNow)
                        {
                            IsCurrent     = true,
                            IsServerOwned = isServerOwned,
                        });
                    }
                }
            }
        }
        else
        {
            var error = new ErrorInfo { ErrorCode = backend.LastError, ErrorMessage = backend.LastErrorMessage };
            backend.Dispose();
            return new OperationResponse { Success = false, Error = error };
        }

        return MakeResponse(backend, success);
    }

    /// <summary>
    /// Replaces the current session's virtual tape with a new file-backed tape without
    ///  destroying the session. Used by multi-volume sequences: the host mounts the next
    ///  tape volume by calling this instead of Close+OpenVirtual, preserving the session ID.
    /// <para>
    ///  The old backend is closed and disposed; a new <see cref="VirtualTapeDriveBackend"/>
    ///  is created from <paramref name="request"/>.FileConfig and opened on drive 0.
    ///  The session's backend entry is replaced atomically.
    /// </para>
    /// </summary>
    public override async Task<OperationResponse> InsertMedia(
        InsertMediaRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context);
        var oldBackend = scope.Backend;

        if (request.FileConfig == null || string.IsNullOrEmpty(request.FileConfig.ContentFilePath))
            return new OperationResponse
            {
                Success = false,
                Error   = new ErrorInfo { ErrorMessage = "InsertMedia requires a non-empty file_config.content_file_path" },
            };

        string newPath = request.FileConfig.ContentFilePath;
        logger.LogInformation("InsertMedia: swapping to '{Path}'", newPath);

        // ── Same-path short-circuit (edge case §5.5 #1) ─────────────────────────────
        // If the catalog already has this path flagged as current AND media is still loaded
        // (i.e. the stream is alive), the file is already mounted — no-op.
        // We must NOT short-circuit when the current backend has been unloaded (e.g. the
        //  backup engine called UnloadMedia before the user cancelled new-volume creation):
        //  in that case IsCurrent is still true in the catalog but the backing stream has
        //  been disposed, and we need the full swap path to re-open it.
        if (oldBackend.HasMedia &&
            registry.TryGet(sessionId: context.RequestHeaders.GetValue(TapeSessionConstants.SessionIdHeader)!, out var existingEntry))
        {
            lock (existingEntry!.Volumes)
            {
                if (existingEntry.Volumes.Any(v =>
                        v.IsCurrent &&
                        string.Equals(v.ContentFilePath, newPath, StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogDebug("InsertMedia: same-path no-op for '{Path}'", newPath);
                    return MakeResponse(oldBackend, true);
                }
            }
        }

        // ── Catalog: update BytesWritten on outgoing volume ──────────────────────────
        // NOTE: the backup engine calls UnloadMedia() before the host callback, which
        //  nulls m_currentMedia so Capacity and Remaining both return 0 at this point.
        //  Instead, measure the actual file size on disk — after flush/unload the content
        //  file exactly reflects what was written, and is far more reliable than in-memory counters.
        var sessionId = context.RequestHeaders.GetValue(TapeSessionConstants.SessionIdHeader)!;
        if (registry.TryGet(sessionId, out var entry))
        {
            lock (entry!.Volumes)
            {
                var outgoing = entry.Volumes.FirstOrDefault(v => v.IsCurrent);
                if (outgoing != null)
                {
                    // Prefer disk-file size: robust even after UnloadMedia resets in-memory counters.
                    long writtenBytes = 0;
                    if (!string.IsNullOrEmpty(outgoing.ContentFilePath))
                    {
                        try { writtenBytes = new FileInfo(outgoing.ContentFilePath).Length; }
                        catch { /* fall back to 0 if file is inaccessible */ }
                    }
                    outgoing.BytesWritten = writtenBytes;
                    outgoing.IsCurrent = false;
                }
            }
        }

        // Close the current tape (flush, rewind) but keep the session alive.
        // For named temp backends, use CloseWithoutDelete() so the backing file
        //  survives in the session catalog and can be re-mounted later.
        //  File cleanup is deferred to session close / idle reap via entry.Volumes.
        if (oldBackend is TempVirtualTapeDriveBackend tempBackend)
            tempBackend.CloseWithoutDelete();
        else
            oldBackend.Close();

        // Build and open the replacement backend.
        var newBackend = CreateFileBackend(request.FileConfig);
        bool success = newBackend.Open(0);

        if (!success)
        {
            var err = new ErrorInfo { ErrorCode = newBackend.LastError, ErrorMessage = newBackend.LastErrorMessage };
            newBackend.Dispose();
            // Reopen the old backend so the session is still usable (best-effort).
            oldBackend.Open(0);
            return new OperationResponse { Success = false, Error = err };
        }

        // Swap the session's backend: replace in the registry.
        // Do NOT dispose the old backend if it was a TempVirtualTapeDriveBackend —
        //  CloseWithoutDelete() already released it without removing the files.
        registry.Replace(sessionId, newBackend);
        if (oldBackend is not TempVirtualTapeDriveBackend)
            oldBackend.Dispose();

        // ── Catalog: append new volume or flip IsCurrent on an existing entry ────────
        if (registry.TryGet(sessionId, out entry))
        {
            lock (entry!.Volumes)
            {
                var existing = entry.Volumes.FirstOrDefault(v =>
                    string.Equals(v.ContentFilePath, newPath, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.IsCurrent = true;
                }
                else
                {
                    var caps = request.FileConfig.Capabilities != null
                        ? MapCapabilities(request.FileConfig.Capabilities)
                        : VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks;

                    entry.Volumes.Add(new RemoteVirtualVolumeEntry(
                        Path.GetFileNameWithoutExtension(newPath),
                        newPath,
                        request.FileConfig.ContentCapacity > 0
                            ? request.FileConfig.ContentCapacity
                            : newBackend.Capacity,
                        string.IsNullOrEmpty(request.FileConfig.InitiatorFilePath)
                            ? null : request.FileConfig.InitiatorFilePath,
                        request.FileConfig.InitiatorCapacity,
                        caps,
                        BlockSize: newBackend.BlockSize,
                        DateTime.UtcNow)
                    {
                        IsCurrent = true,
                    });
                }
            }
        }

        await Task.CompletedTask; // satisfy async signature
        return MakeResponse(newBackend, true);
    }

    /// <summary>
    /// Returns all named (file-backed) volumes created during this session.
    /// Session-scoped — requires the <c>x-tape-session-id</c> header.
    /// In-memory drives are never catalogued and will not appear in the results.
    /// </summary>
    public override Task<ListSessionVolumesResponse> ListSessionVolumes(
        EmptyRequest request, ServerCallContext context)
    {
        using var scope = GetScope(context);
        var entry = scope.Entry;

        var response = new ListSessionVolumesResponse();

        lock (entry.Volumes)
        {
            foreach (var v in entry.Volumes)
            {
                var fileConfig = new VirtualFileConfig
                {
                    ContentFilePath = v.ContentFilePath,
                    ContentCapacity = v.ContentCapacity,
                    Capabilities    = MapCapabilities(v.Capabilities),
                };
                if (v.InitiatorFilePath != null)
                {
                    fileConfig.InitiatorFilePath = v.InitiatorFilePath;
                    fileConfig.InitiatorCapacity = v.InitiatorCapacity;
                }

                response.Volumes.Add(new SessionVolumeEntry
                {
                    Name           = v.Name,
                    FileConfig     = fileConfig,
                    BlockSize      = v.BlockSize,
                    BytesWritten   = v.BytesWritten,
                    CreatedUnixUtc = new DateTimeOffset(v.CreatedUtc).ToUnixTimeSeconds(),
                    IsCurrent      = v.IsCurrent,
                });
            }
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// Returns server version, protocol level and OS host name.
    /// Sessionless — no session ID header required. Safe to call unauthenticated.
    /// </summary>
    public override Task<ServerInfoResponse> GetServerInfo(EmptyRequest request, ServerCallContext context)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string versionString = version != null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";

        var response = new ServerInfoResponse
        {
            ServerVersion = versionString,
            ProtocolLevel = 1,
            HostName      = System.Net.Dns.GetHostName(),
        };

        logger.LogDebug("GetServerInfo: version={Version}, host={Host}", versionString, response.HostName);
        return Task.FromResult(response);
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
