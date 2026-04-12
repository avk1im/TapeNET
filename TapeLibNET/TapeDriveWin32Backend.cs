using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.SystemServices;

namespace TapeLibNET;

/// <summary>
/// Win32 implementation of TapeDriveBackend using Windows Tape API.
/// </summary>
public class TapeDriveWin32Backend(ILoggerFactory loggerFactory) : TapeDriveBackend(loggerFactory)
{
    #region *** Private Fields ***

    private SafeFileHandle m_driveHandle = new();
    private uint m_driveNumber = 0;
    private TAPE_GET_DRIVE_PARAMETERS? m_driveParams = null;
    private TAPE_GET_MEDIA_PARAMETERS? m_mediaParams = null;

    // Runtime-detected opcodes that require blocking mode (bImmediate=false).
    // QUIRK: Some drives (e.g. DLT-V4) reject bImmediate=true for certain
    //  operations with ERROR_INVALID_FUNCTION. Discovered at runtime and
    //  cached for all subsequent calls until the drive is closed.
    private readonly HashSet<TAPE_POSITION_METHOD> m_blockingPositionOps = [];
    private readonly HashSet<PREPARE_TAPE_OPERATION> m_blockingPrepareOps = [];
    private readonly HashSet<TAPEMARK_TYPE> m_blockingTapemarkOps = [];

    #endregion

#if DEBUG
    #region *** Failure Simulation ***

    /// <summary>
    /// When enabled, simulates I/O failures in <see cref="Read"/> and <see cref="Write"/>
    /// by returning 0 bytes and setting <see cref="WIN32_ERROR.ERROR_IO_DEVICE"/>.
    /// </summary>
    public FailureSimulator SimulateIOFailures { get; } = new();

    /// <summary>
    /// When enabled, simulates timeout failures in <see cref="PollForCompletion"/>
    /// by returning <see cref="Helpers.WIN32_ERROR_WAIT_TIMEOUT"/> immediately
    /// instead of actually polling the drive.
    /// </summary>
    public FailureSimulator SimulateTimeoutFailures { get; } = new();

    #endregion
#endif

    #region *** Private Constants ***

    private const int c_maxRetries = 4;
    private const int c_retryDelayMs = 1000;

    private static readonly WIN32_ERROR[] c_retryableErrors = [
        WIN32_ERROR.ERROR_BUS_RESET,
        WIN32_ERROR.ERROR_MEDIA_CHANGED,
        WIN32_ERROR.ERROR_NOT_READY,
    ];

    private static readonly WIN32_ERROR[] c_endOfFileErrors = [
        WIN32_ERROR.ERROR_FILEMARK_DETECTED,
        WIN32_ERROR.ERROR_SETMARK_DETECTED,
        WIN32_ERROR.ERROR_END_OF_MEDIA,
        WIN32_ERROR.ERROR_NO_DATA_DETECTED,
        WIN32_ERROR.ERROR_HANDLE_EOF,
    ];

    private static readonly WIN32_ERROR[] c_tapemarkErrors = [
        WIN32_ERROR.ERROR_FILEMARK_DETECTED,
        WIN32_ERROR.ERROR_SETMARK_DETECTED,
        WIN32_ERROR.ERROR_END_OF_MEDIA,
    ];

    // Errors treated as "still in progress" during PollForCompletion.
    // QUIRK DLT-V4: GetTapeStatus may return ERROR_IO_DEVICE instead of
    //  ERROR_NOT_READY while a bImmediate operation is still executing.
    private static readonly WIN32_ERROR[] c_waitableErrors = [
        WIN32_ERROR.ERROR_NOT_READY,
        WIN32_ERROR.ERROR_IO_DEVICE,
    ];

    private const int c_maxPollDelayMs = 500;

    #endregion

    #region *** State Properties ***

    public override bool IsOpen => !m_driveHandle.IsInvalid && !m_driveHandle.IsClosed && m_driveParams != null;
    public override bool HasMedia => IsOpen && m_mediaParams != null;
    public override string DeviceName => $"\\\\.\\TAPE{m_driveNumber}";
    public override uint DriveNumber => m_driveNumber;

    #endregion

    #region *** Drive & Media Properties ***

    public override uint BlockSize => m_mediaParams?.BlockSize ?? 0U;
    public override uint MinBlockSize => m_driveParams?.MinimumBlockSize ?? 0U;
    public override uint MaxBlockSize => m_driveParams?.MaximumBlockSize ?? 0U;
    public override uint DefaultBlockSize => m_driveParams?.DefaultBlockSize ?? 0U;
    public override long Capacity => m_mediaParams?.Capacity ?? 0L;
    public override long Remaining => m_mediaParams?.Remaining ?? 0L;
    public override long Position => GetPosition();
    public override bool SupportsInitiatorPartition => m_driveParams?.MaximumPartitionCount > 1 && CreatesInitiatorPartitions;
    public override bool HasInitiatorPartition => (m_mediaParams?.PartitionCount ?? 0) > 1;
    public override bool SupportsSetmarks => m_driveParams?.SupportsSetmarks ?? false;
    public override bool SupportsSeqFilemarks => m_driveParams?.SupportsSeqFilemarks ?? false;

    private bool CreatesInitiatorPartitions => m_driveParams?.CreatesInitiatorPartitions ?? false;
    private bool CreatesFixedPartitions => m_driveParams?.CreatesFixedPartitions ?? false;
    private bool CreatesSelectPartitions => m_driveParams?.CreatesSelectPartitions ?? false;

    #endregion

    #region *** Drive Operations ***

    public override bool Open(uint driveNumber)
    {
        Close();

        m_driveNumber = driveNumber;

        m_driveHandle = PInvoke.CreateFile(
            DeviceName,
            (uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE),
            FILE_SHARE_MODE.FILE_SHARE_NONE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DEVICE,
            null);

        SetErrorFromPInvoke();

        if (WentBad)
        {
            LogErrorAsDebug("Failed to open drive");
            return false;
        }

        // Fill drive parameters with retry logic
        if (!Op(RefreshDriveParams).WithRetry().Run())
        {
            LogErrorAsDebug("Failed to fill drive parameters");
            return false;
        }

        m_logger.LogTrace("{Prefix}: Opened", LogPrefix);
        return true;
    }

    public override void Close()
    {
        m_driveHandle.Close();
        m_driveParams = null;
        m_mediaParams = null;
        m_blockingPositionOps.Clear();
        m_blockingPrepareOps.Clear();
        m_blockingTapemarkOps.Clear();
    }

    public override bool SetDriveParameters(bool compression, bool ecc, bool dataPadding, bool reportSetmarks, uint eotWarningZoneSize)
    {
        if (!IsOpen)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_HANDLE);
            return false;
        }

        TAPE_SET_DRIVE_PARAMETERS driveParamsToSet;
        driveParamsToSet.Compression = compression;
        driveParamsToSet.ECC = ecc;
        driveParamsToSet.DataPadding = dataPadding;
        driveParamsToSet.ReportSetmarks = reportSetmarks;
        driveParamsToSet.EOTWarningZoneSize = eotWarningZoneSize;

        unsafe
        {
            SetError(PInvoke.SetTapeParameters(m_driveHandle, TAPE_INFORMATION_TYPE.SET_TAPE_DRIVE_INFORMATION, &driveParamsToSet));
        }

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Set drive parameters", LogPrefix);
        else
            LogErrorAsDebug("Failed to set drive parameters");

        return WentOK;
    }

    #endregion

    #region *** Media Operations ***

    public override bool LoadMedia()
    {
        if (!IsOpen)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_HANDLE);
            return false;
        }

        // Try polled (bImmediate) mode with retry
        if (!Op(() => InvokePrepareTape(PREPARE_TAPE_OPERATION.TAPE_LOAD)).WithRetry().WithPoll().Run())
        {
            // Timeout is final — don't fall back
            if (LastErrorWin32 == Helpers.WIN32_ERROR_WAIT_TIMEOUT)
            {
                LogErrorAsDebug("Failed to load media (timed out)");
                return false;
            }

            // Other error — fall back to blocking mode with retry
            LogErrorAsWarning("Failed to load media using polled mode, falling back to blocking");

            if (!Op(() => InvokePrepareTapeBlocking(PREPARE_TAPE_OPERATION.TAPE_LOAD)).WithRetry().Run())
            {
                LogErrorAsDebug("Failed to load media using blocking mode");
                return false;
            }
        }

        RefreshMediaParams();
        m_logger.LogTrace("{Prefix}: Media loaded", LogPrefix);
        return true;
    }


    public override bool UnloadMedia()
    {
        if (!IsOpen)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_HANDLE);
            return false;
        }

        SetError(PInvoke.PrepareTape(m_driveHandle, PREPARE_TAPE_OPERATION.TAPE_UNLOAD, false));

        if (WentOK)
        {
            m_mediaParams = null;
            m_logger.LogTrace("{Prefix}: Media unloaded", LogPrefix);
        }
        else
            LogErrorAsDebug("Failed to unload media");

        return WentOK;
    }

    public override bool SetBlockSize(uint size)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        TAPE_SET_MEDIA_PARAMETERS mediaParamsToSet;
        mediaParamsToSet.BlockSize = size;

        unsafe
        {
            SetError(PInvoke.SetTapeParameters(m_driveHandle, TAPE_INFORMATION_TYPE.SET_TAPE_MEDIA_INFORMATION, &mediaParamsToSet));
        }

        if (WentOK)
        {
            RefreshMediaParams();
            m_logger.LogTrace("{Prefix}: Block size set to {Size}", LogPrefix, BlockSize);
        }
        else
            LogErrorAsDebug("Failed to set block size");

        return WentOK;
    }

    public override bool FormatMedia(long initiatorPartitionSize = -1)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        m_logger.LogTrace("{Prefix}: Formatting media", LogPrefix);

        if (initiatorPartitionSize > 0L && SupportsInitiatorPartition)
        {
            SetError(PInvoke.CreateTapePartition(m_driveHandle,
                CREATE_TAPE_PARTITION_METHOD.TAPE_INITIATOR_PARTITIONS,
                2 /*one initiator partition + one content partition*/, (uint)initiatorPartitionSize / (1024 * 1024) /*MB*/));
        }
        else
        {
            // Create single partition based on drive capabilities
            if (CreatesFixedPartitions)
                SetError(PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_FIXED_PARTITIONS,
                    0 /*ignored*/, 0 /*ignored*/));
            else if (CreatesSelectPartitions)
                SetError(PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_SELECT_PARTITIONS,
                    1 /*one common partition*/, 0 /*ignored*/));
            else if (CreatesInitiatorPartitions)
                SetError(PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_INITIATOR_PARTITIONS,
                    1 /*one common partition*/, 0 /*ignored for single partition*/));
            else
                SetError(WIN32_ERROR.NO_ERROR); // the drive doesn't support / need partitioning / formatting
        }

        if (WentOK)
        {
            // Re-load media to refresh parameters after formatting
            Op(() => InvokePrepareTape(PREPARE_TAPE_OPERATION.TAPE_LOAD)).WithRetry().WithPoll().Run();
            RefreshMediaParams();
            m_logger.LogTrace("{Prefix}: Formatted media", LogPrefix);
        }
        else
            LogErrorAsDebug("Failed to format media");

        return WentOK;
    }

    #endregion

    #region *** Read/Write Operations ***

    public override int Read(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        tapemark = false;
        eof = false;

#if DEBUG
        if (SimulateIOFailures.ShouldFailNow())
        {
            SetError(WIN32_ERROR.ERROR_IO_DEVICE);
            m_logger.LogWarning("{Prefix}: SIMULATED I/O read failure (counter {Counter})",
                LogPrefix, SimulateIOFailures.Counter);
            return 0;
        }
#endif

        uint read;
        bool bOK;

        unsafe
        {
            bOK = PInvoke.ReadFile(m_driveHandle, buffer.AsSpan(offset, count), out read,
                ref Unsafe.NullRef<System.Threading.NativeOverlapped>());
        }

        if (bOK)
            ResetError();
        else
            SetErrorFromPInvoke();

        if (WentBad)
        {
            if (c_tapemarkErrors.Contains(LastErrorWin32))
            {
                tapemark = true;
                LogErrorAsTrace("Read encountered tapemark");
            }

            if (c_endOfFileErrors.Contains(LastErrorWin32))
            {
                eof = true;
                LogErrorAsTrace("Read encountered EOF");
                ResetError(); // EOF is not an error for reads
            }

            if (!tapemark && !eof)
                LogErrorAsDebug("Read failed");
        }

        return (int)read;
    }

    public override int Write(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        tapemark = false;
        eof = false;

#if DEBUG
        if (SimulateIOFailures.ShouldFailNow())
        {
            SetError(WIN32_ERROR.ERROR_IO_DEVICE);
            m_logger.LogWarning("{Prefix}: SIMULATED I/O write failure (counter {Counter})",
                LogPrefix, SimulateIOFailures.Counter);
            return 0;
        }
#endif

        uint written;
        bool bOK;

        unsafe
        {
            bOK = PInvoke.WriteFile(m_driveHandle, buffer.AsSpan(offset, count), out written,
                ref Unsafe.NullRef<System.Threading.NativeOverlapped>());
        }

        if (bOK)
            ResetError();
        else
            SetErrorFromPInvoke();

        if (WentBad)
        {
            if (c_tapemarkErrors.Contains(LastErrorWin32))
            {
                tapemark = true;
                LogErrorAsTrace("Write encountered tapemark");
            }

            if (c_endOfFileErrors.Contains(LastErrorWin32))
            {
                eof = true;
                LogErrorAsTrace("Write encountered EOF");
                // Do NOT reset error for writes - EOF is significant
            }

            if (!tapemark && !eof)
                LogErrorAsDebug("Write failed");
        }

        return (int)written;
    }

    #endregion

    #region *** Positioning Operations ***

    public override bool SetPosition(long block)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 0, block)).WithRetry().WithPoll().Run();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Moved to block {Block}", LogPrefix, block);
        else
            LogErrorAsDebug("Failed to set position");

        return WentOK;
    }

    public override bool SetPositionToPartition(MediaPartition partition, long block)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        uint win32Partition = MapPartitionToWin32(partition);

        // QUIRK Sony AIT: must go to partition 1 before partition 2+
        if (win32Partition > 1)
        {
            Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 1, 0)).WithRetry().WithPoll().Run();
        }

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, win32Partition, block)).WithRetry().WithPoll().Run();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Moved to partition {Partition} block {Block}", LogPrefix, partition, block);
        else
            LogErrorAsDebug("Failed to move to partition");

        return WentOK;
    }

    public override long GetPosition()
    {
        if (!HasMedia)
            return -1;

        SetError(PInvoke.GetTapePosition(m_driveHandle, TAPE_POSITION_TYPE.TAPE_LOGICAL_POSITION,
            out _, out uint blockLow, out uint blockHigh));

        return WentOK ? Helpers.MakeLong(blockLow, blockHigh) : -1;
    }

    public override MediaPartition GetCurrentPartition()
    {
        if (!HasMedia)
            return MediaPartition.Current;

        SetError(PInvoke.GetTapePosition(m_driveHandle, TAPE_POSITION_TYPE.TAPE_LOGICAL_POSITION,
            out uint win32Partition, out _, out _));

        return WentOK ? MapWin32ToPartition(win32Partition) : MediaPartition.Current;

    }

    public override bool Rewind()
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_REWIND, 0 /*partition ignored*/, 0)).WithRetry().WithPoll().Run();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Rewound", LogPrefix);
        else
            LogErrorAsDebug("Failed to rewind");

        return WentOK;
    }

    public override bool SeekToEnd(MediaPartition partition = MediaPartition.Current)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        uint win32Partition = MapPartitionToWin32(partition);
        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_SPACE_END_OF_DATA, win32Partition, 0)).WithRetry().WithPoll().Run();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Seeked to end of partition {Partition}", LogPrefix, partition);
        else
            LogErrorAsDebug("Failed to seek to end");

        return WentOK;
    }

    public override bool SpaceFilemarks(int count)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_SPACE_FILEMARKS, 0, count)).WithRetry().WithPoll().Run();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Spaced {Count} filemark(s)", LogPrefix, count);
        else
            LogErrorAsDebug("Failed to space filemarks");

        return WentOK;
    }

    public override bool SpaceSetmarks(int count)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        if (count == 0)
        {
            ResetError();
            return true;
        }

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_SPACE_SETMARKS, 0, count)).WithRetry().WithPoll().Run();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Spaced {Count} setmark(s)", LogPrefix, count);
        else
            LogErrorAsDebug("Failed to space setmarks");

        return WentOK;
    }

    public override bool SpaceSequentialFilemarks(int count)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_SPACE_SEQUENTIAL_FMKS, 0, count)).WithRetry().WithPoll().Run();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Spaced {Count} sequential filemark(s)", LogPrefix, count);
        else
            LogErrorAsDebug("Failed to space sequential filemarks");

        return WentOK;
    }

    #endregion

    #region *** Tapemark Operations ***

    public override bool WriteFilemarks(uint count)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        Op(() => InvokeWriteTapemark(TAPEMARK_TYPE.TAPE_FILEMARKS, count)).WithRetry().WithPoll().Run();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Wrote {Count} filemark(s)", LogPrefix, count);
        else
            LogErrorAsDebug("Failed to write filemarks");

        return WentOK;
    }

    public override bool WriteSetmarks(uint count)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        Op(() => InvokeWriteTapemark(TAPEMARK_TYPE.TAPE_SETMARKS, count)).WithRetry().WithPoll().Run();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Wrote {Count} setmark(s)", LogPrefix, count);
        else
            LogErrorAsDebug("Failed to write setmarks");

        return WentOK;
    }

    #endregion

    #region *** Parameter Queries ***

    public override void FillDriveCapabilities(out DriveCapabilities parameters)
    {
        RefreshDriveParams();

        parameters = m_driveParams != null
            ? new DriveCapabilities(
                m_driveParams.Value.MinimumBlockSize,
                m_driveParams.Value.MaximumBlockSize,
                m_driveParams.Value.DefaultBlockSize,
                m_driveParams.Value.HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_COMPRESSION),
                m_driveParams.Value.HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ECC),
                m_driveParams.Value.HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_PADDING),
                m_driveParams.Value.SupportsSetmarks,
                m_driveParams.Value.SupportsSeqFilemarks,
                m_driveParams.Value.MaximumPartitionCount > 1 && m_driveParams.Value.CreatesInitiatorPartitions)
            : default;
    }

    public override void FillMediaParameters(out MediaParameters parameters)
    {
        RefreshMediaParams();

        parameters = m_mediaParams != null
            ? new MediaParameters(
                m_mediaParams.Value.Capacity,
                m_mediaParams.Value.Remaining,
                m_mediaParams.Value.BlockSize,
                m_mediaParams.Value.PartitionCount > 1,
                m_mediaParams.Value.WriteProtected)
            : default;
    }

    #endregion

    #region *** Private Helpers ***

    // === Fluent Operation Builder ===

    /// <summary>
    /// Lightweight fluent builder for composing retry and poll behaviors on tape operations.
    /// Usage: <c>Op(action).WithRetry().WithPoll().Run()</c>
    /// </summary>
    private readonly struct TapeOp(TapeDriveWin32Backend backend, Func<bool> action)
    {
        private readonly TapeDriveWin32Backend m_backend = backend;
        private readonly Func<bool> m_action = action;

        /// <summary>Wraps the action with retry logic for transient bus/media errors.</summary>
        public TapeOp WithRetry() { var a = m_action; var b = m_backend; return new(b, () => b.ApplyRetry(a)); }

        /// <summary>Appends GetTapeStatus polling after a successful bImmediate call.</summary>
        public TapeOp WithPoll() { var a = m_action; var b = m_backend; return new(m_backend, () =>
        {
            if (!a()) return false;
            return b.PollForCompletion();
        }); }

        /// <summary>Executes the composed operation chain.</summary>
        public bool Run() => m_action();
    }

    private TapeOp Op(Func<bool> action) => new(this, action);

    // === Retry & Poll Helpers ===

    private bool ApplyRetry(Func<bool> func)
    {
        int retryCount = 0;
        bool result;

        do
        {
            result = func();

            if (!result && c_retryableErrors.Contains(LastErrorWin32))
            {
                retryCount++;
                m_logger.LogWarning("Retrying upon error: 0x{Error:X8} >{ErrorMessage}<; retry count: {Count}",
                    LastError, LastErrorMessage, retryCount);
                Thread.Sleep(c_retryDelayMs);
            }
            else
                break;

        } while (retryCount < c_maxRetries);

        return result;
    }

    /// <summary>
    /// Polls GetTapeStatus with exponential backoff until the drive reports completion or timeout.
    /// Returns true if the underlying operation succeeded, false on error or timeout.
    /// </summary>
    private bool PollForCompletion()
    {
#if DEBUG
        if (SimulateTimeoutFailures.ShouldFailNow())
        {
            SetError(Helpers.WIN32_ERROR_WAIT_TIMEOUT);
            m_logger.LogWarning("{Prefix}: SIMULATED timeout failure (counter {Counter})",
                LogPrefix, SimulateTimeoutFailures.Counter);
            return false;
        }
#endif

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int delayMs = -1; // -1 = first iteration, no delay

        while (true)
        {
            SetError(PInvoke.GetTapeStatus(m_driveHandle));

            if (!c_waitableErrors.Contains(LastErrorWin32))
                return WentOK; // Completed (NO_ERROR) or failed with a real error

            if (OperationTimeout != Timeout.InfiniteTimeSpan && sw.Elapsed >= OperationTimeout)
            {
                SetError(Helpers.WIN32_ERROR_WAIT_TIMEOUT);
                LogErrorAsWarning("Operation timed out");
                return false;
            }

            delayMs = NextPollDelay(delayMs);
            Thread.Sleep(delayMs);
        }
    }

    /// <summary>Exponential backoff: yield → 1ms → 4 → 16 → … → 500ms cap.</summary>
    private static int NextPollDelay(int current) =>
        current < 0 ? 0               // first: yield (Sleep(0))
        : current == 0 ? 1            // then: 1ms
        : Math.Min(current * 4, c_maxPollDelayMs); // quadruple until cap

    // === Immediate-or-Blocking Dispatch ===

    /// <summary>
    /// Invokes a tape operation, preferring bImmediate=true. If the driver
    /// rejects immediate mode with ERROR_INVALID_FUNCTION, falls back to
    /// blocking and remembers the opcode for direct-blocking on future calls.
    /// </summary>
    private bool InvokeImmediateOrBlocking<TOpcode>(
        TOpcode opcode, HashSet<TOpcode> blockingOpcodes,
        Func<bool, bool> invoke) where TOpcode : notnull
    {
        if (!blockingOpcodes.Contains(opcode))
        {
            if (invoke(true))
                return true;

            if (LastErrorWin32 != WIN32_ERROR.ERROR_INVALID_FUNCTION)
                return false; // Real error — propagate

            // QUIRK: Drive rejected bImmediate=true for this operation.
            blockingOpcodes.Add(opcode);
            m_logger.LogInformation("{Prefix}: {Opcode} requires blocking mode — remembered for future calls",
                LogPrefix, opcode);
        }

        return invoke(false);
    }

    // === Invoke Helpers ===

    /// <summary>Invokes SetTapePosition with automatic immediate-to-blocking fallback.</summary>
    private bool InvokeSetPosition(TAPE_POSITION_METHOD method, uint partition, long offset) =>
        InvokeImmediateOrBlocking(method, m_blockingPositionOps, immediate =>
        {
            SetError(PInvoke.SetTapePosition(m_driveHandle, method, partition,
                Helpers.LoDWORD(offset), Helpers.HiDWORD(offset), immediate));
            return WentOK;
        });

    /// <summary>Invokes PrepareTape with automatic immediate-to-blocking fallback.</summary>
    private bool InvokePrepareTape(PREPARE_TAPE_OPERATION operation) =>
        InvokeImmediateOrBlocking(operation, m_blockingPrepareOps, immediate =>
        {
            SetError(PInvoke.PrepareTape(m_driveHandle, operation, immediate));
            return WentOK;
        });

    /// <summary>Invokes PrepareTape with bImmediate=false (blocking).
    /// Used by <see cref="LoadMedia"/> for explicit blocking fallback on timeout.</summary>
    private bool InvokePrepareTapeBlocking(PREPARE_TAPE_OPERATION operation)
    {
        SetError(PInvoke.PrepareTape(m_driveHandle, operation, false));
        return WentOK;
    }

    /// <summary>Invokes WriteTapemark with automatic immediate-to-blocking fallback.</summary>
    private bool InvokeWriteTapemark(TAPEMARK_TYPE type, uint count) =>
        InvokeImmediateOrBlocking(type, m_blockingTapemarkOps, immediate =>
        {
            SetError(PInvoke.WriteTapemark(m_driveHandle, type, count, immediate));
            return WentOK;
        });

    // === Parameter & Mapping Helpers ===

    private bool RefreshDriveParams()
    {
        if (m_driveHandle.IsInvalid || m_driveHandle.IsClosed)
            return false;

        TAPE_GET_DRIVE_PARAMETERS driveParams;
        uint retSize;

        unsafe
        {
            retSize = (uint)sizeof(TAPE_GET_DRIVE_PARAMETERS);
            SetError(PInvoke.GetTapeParameters(m_driveHandle, GET_TAPE_DRIVE_PARAMETERS_OPERATION.GET_TAPE_DRIVE_INFORMATION,
                ref retSize, new Span<byte>(&driveParams, (int)retSize)));
        }

        if (WentOK)
            m_driveParams = driveParams;
        else
            LogErrorAsDebug("Failed to get drive parameters");

        return WentOK;
    }

    private bool RefreshMediaParams()
    {
        if (!IsOpen)
            return false;

        TAPE_GET_MEDIA_PARAMETERS mediaParams;
        uint retSize;

        unsafe
        {
            retSize = (uint)sizeof(TAPE_GET_MEDIA_PARAMETERS);
            SetError(PInvoke.GetTapeParameters(m_driveHandle, GET_TAPE_DRIVE_PARAMETERS_OPERATION.GET_TAPE_MEDIA_INFORMATION,
                ref retSize, new Span<byte>(&mediaParams, (int)retSize)));
        }

        if (WentOK)
        {
            // QUIRK on AIT drives: Capacity may be smaller than Remaining. Check & fix
            if (mediaParams.Capacity < mediaParams.Remaining)
            {
                m_logger.LogTrace("Media parameters quirk detected: Remaining ({Remaining}) > Capacity ({Capacity}) - adjusted Capcity",
                    Helpers.BytesToStringLong(mediaParams.Remaining), Helpers.BytesToStringLong(mediaParams.Capacity));
                mediaParams.Capacity = mediaParams.Remaining;
            }

            m_mediaParams = mediaParams;
        }
        else
            LogErrorAsDebug("Failed to get media parameters");

        return WentOK;
    }

    private uint MapPartitionToWin32(MediaPartition partition)
    {
        if (partition == MediaPartition.Current)
            return 0U; // In Win32 0 means "use current partition"

        if (HasInitiatorPartition)
        {
            // In Win32 partition 2 = initiator, partition 1 = content
            return (partition == MediaPartition.Initiator) ? 2U : 1U;
        }

        if (partition == MediaPartition.Initiator)
        {
            LogErrorAsWarning("Request to map initiator partition while none exists -> using content partition instead",
                nameof(MapPartitionToWin32));
        }

        // In Win32 partition 1 = common
        return 1U;

        /*
        // Win32: partition 2 = initiator (if exists), partition 1 = content // FIXME: check if not vice versa!
        //  So content is always in partition 1
        if (partition == MediaPartition.Content)
            return 1U;

        if (!HasInitiatorPartition)
        {
            LogErrorAsWarning("Request to map initiator partition while none exists -> using content partition instead",
                nameof(MapPartitionToWin32));
            return 1U; // return content partition if no initiator partition exists
        }

        return 2U;
        */
    }

    private MediaPartition MapWin32ToPartition(uint win32Partition)
    {
        if (win32Partition == 0U)
            return MediaPartition.Current;
        if (HasInitiatorPartition)
        {
            // In Win32 partition 2 = initiator, partition 1 = content
            return (win32Partition == 2U) ? MediaPartition.Initiator : MediaPartition.Content;
        }
        // In Win32 partition 1 = common
        return MediaPartition.Content;
    }

    #endregion
}