using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.SystemServices;

namespace TapeLibNET;

/// <summary>
/// Win32 implementation of <see cref="TapeDriveBackend"/> using the Windows Tape Backup API.
/// The only class in TapeLibNET that issues P/Invoke calls to the tape driver.
/// <para>
/// Handles hardware quirks discovered at runtime (e.g. DLT-V4 rejecting <c>bImmediate</c>,
/// DAT-320 premature polling completion) by caching per-opcode blocking-mode overrides.
/// </para>
/// </summary>
public partial class TapeDriveWin32Backend(ILoggerFactory loggerFactory) : TapeDriveBackend(loggerFactory)
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

    // QUIRK DAT-320: GetTapePosition may return ERROR_INVALID_FUNCTION when
    //  called too soon after a bImmediate operation, even after polling reports
    //  completion. Detected on first occurrence and enables pre-yield + retry.
    private bool m_positionQueryNeedsRetry;

    // QUIRK DAT-320: SetTapePosition(bImmediate=true) for ANY operation may
    //  silently fail — the call returns NO_ERROR and GetTapeStatus polling
    //  completes, but the drive has not physically finished the operation.
    //  Observed with TAPE_REWIND (position stays at old block), partition
    //  switches (reads return wrong-partition data), and potentially any
    //  long-distance seek. The driver's bImmediate implementation appears
    //  fundamentally unreliable — it reports completion prematurely.
    //  Every immediate-mode SetTapePosition is verified via a raw
    //  GetTapePosition probe. On first verification failure, this flag
    //  flips to true and ALL future positioning uses bImmediate=false.
    private bool m_setPositionNeedsBlocking;

    // LTO generation detection (probed once in Open via SCSI INQUIRY).
    //  -1 = not yet probed / probe failed; 0 = not LTO; >= 1 = LTO generation.
    private int m_ltoGeneration = -1;
    private string m_ltoVendor = string.Empty;
    private string m_ltoProduct = string.Empty;

    // LTO partition numbering flag — set during Open, cleared in Close.
    //  When true, account for LTO QUIRK in partition numbering!
    private bool m_useLtoPartitionSchema; // SetPositionToPartition → LOCATE(10)

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

    private const int c_maxPollDelayMs = 1000;
    private const int c_maxQueryRetries = 10;

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
    #endregion

    #region *** State Properties ***

    public override bool IsOpen => !m_driveHandle.IsInvalid && !m_driveHandle.IsClosed && m_driveParams != null;
    public override bool HasMedia => IsOpen && m_mediaParams != null;
    public override string DeviceName => $"\\\\.\\TAPE{m_driveNumber}";
    public override uint DriveNumber => m_driveNumber;
    public override string Vendor => m_ltoVendor;
    public override string Product => m_ltoProduct;

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

        // Probe & fill LTO information and configure LTO dispatch flags accordingly!
        ProbeForLtoInformation();
    
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
        m_positionQueryNeedsRetry = false;
        m_setPositionNeedsBlocking = false;
        m_ltoGeneration = -1;
        m_useLtoPartitionSchema = false;
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

        if (m_useLtoPartitionSchema)
        {
            // LTO FORMAT MEDIUM requires the tape to be at Initiator (if present) & BOT
            if (HasInitiatorPartition && !SetPositionToPartition(MediaPartition.Initiator, 0L))
            {
                LogErrorAsWarning("FORMAT LTO MEDIUM: failed to position to initiator partition");
                // Do NOT give up yet -- LTO format MAY still succeed
                // return false;
            }
            if (!Rewind())
            {
                LogErrorAsDebug("FORMAT LTO MEDIUM: failed to rewind before formatting");
                // If rewind failed, then format won't work with LTO-5+
                return false;
            }

            if (!PollForCompletion()) // ensure rewind completed
                LogErrorAsWarning("FORMAT LTO MEDIUM: polling failed after rewind");
        }

        if (m_useLtoPartitionSchema)
        {
            uint formatError = (uint)WIN32_ERROR.NO_ERROR;

            if (initiatorPartitionSize > 0L && SupportsInitiatorPartition)
            {
                // LTO-5+ creates much larger initoiator partition than we request -- it will silently increase the actual size
                formatError = PInvoke.CreateTapePartition(m_driveHandle,
                    CREATE_TAPE_PARTITION_METHOD.TAPE_INITIATOR_PARTITIONS,
                    2 /*one initiator partition + one content partition*/, (uint)initiatorPartitionSize / (1024 * 1024) /*MB*/);
            }
            else
            {
                // LTO-5+ supports only TAPE_SELECT_PARTITIONS
                //  Do NOT atteempt TAPE_FIXED_PARTITIONS: not only will it fail, but retrying with TAPE_SELECT_PARTITIONS might fail, too!
                formatError = PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_SELECT_PARTITIONS,
                    1 /*one common partition*/, 0 /*ignored*/);
            }
            m_logger.LogTrace("FORMAT LTO MEDIUM: CreateTapePartition returned 0x{Error:X8} for LTO format", formatError);

            // We now need to wait for completion
            if (!PollForCompletion())
            {
                LogErrorAsWarning("FORMAT LTO MEDIUM: polling failed after format");
                SetError(formatError); // preserve original format error for caller
            }

            if (WentOK)
                if (!Rewind())
                    LogErrorAsWarning("FORMAT LTO MEDIUM: rewind failed after format");
        }
        else // non-LTO-5+
        {
            if (initiatorPartitionSize > 0L && SupportsInitiatorPartition)
            {
                SetError(PInvoke.CreateTapePartition(m_driveHandle,
                    CREATE_TAPE_PARTITION_METHOD.TAPE_INITIATOR_PARTITIONS,
                    2 /*one initiator partition + one content partition*/, (uint)initiatorPartitionSize / (1024 * 1024) /*MB*/));
            }
            else
            {
                // Create single partition based on drive capabilities, with fallback chain.
                bool partitioned = false;

                if (CreatesFixedPartitions)
                {
                    SetError(PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_FIXED_PARTITIONS,
                        0 /*ignored*/, 0 /*ignored*/));
                    partitioned = WentOK;
                    if (WentBad)
                        m_logger.LogInformation("{Prefix}: TAPE_FIXED_PARTITIONS rejected for single partition — falling back to TAPE_SELECT_PARTITIONS", LogPrefix);
                }

                if (!partitioned && CreatesSelectPartitions)
                {
                    SetError(PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_SELECT_PARTITIONS,
                        1 /*one common partition*/, 0 /*ignored*/));
                    partitioned = WentOK;
                    if (WentBad)
                        m_logger.LogInformation("{Prefix}: TAPE_SELECT_PARTITIONS rejected for single partition — falling back to TAPE_INITIATOR_PARTITIONS", LogPrefix);
                }

                if (!partitioned && CreatesInitiatorPartitions)
                {
                    SetError(PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_INITIATOR_PARTITIONS,
                        1 /*one common partition*/, 0 /*ignored for single partition*/));
                    partitioned = WentOK;
                }

                if (!partitioned && !CreatesFixedPartitions && !CreatesSelectPartitions && !CreatesInitiatorPartitions)
                    SetError(WIN32_ERROR.NO_ERROR); // the drive doesn't support / need partitioning / formatting
            }
        }

        if (WentOK)
        {
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

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 0, block)).WithRetry().Run();

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

        // LTO-5+: partition switching must go through SCSI LOCATE(10) // VALIDATE: not necessary
        //if (m_useLtoPartitionSchema)
        //    return SetPositionToPartitionLto(partition, block);

        uint win32Partition = MapPartitionToWin32(partition);

        // QUIRK Sony AIT: must go to partition 1 before partition 2+
        if (win32Partition > 1)
        {
            Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 1, 0)).WithRetry().Run();
        }

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, win32Partition, block)).WithRetry().Run();

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

        return InvokeGetTapePosition(out _, out uint blockLow, out uint blockHigh)
            ? Helpers.MakeLong(blockLow, blockHigh)
            : -1;
    }

    public override MediaPartition GetCurrentPartition()
    {
        if (!HasMedia)
            return MediaPartition.Current;

        return InvokeGetTapePosition(out uint win32Partition, out _, out _)
            ? MapWin32ToPartition(win32Partition)
            : MediaPartition.Current;
    }

    public override bool Rewind()
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_REWIND, 0 /*partition ignored*/, 0)).WithRetry().Run();

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
        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_SPACE_END_OF_DATA, win32Partition, 0)).WithRetry().Run();

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

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_SPACE_FILEMARKS, 0, count)).WithRetry().Run();

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

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_SPACE_SETMARKS, 0, count)).WithRetry().Run();

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

        Op(() => InvokeSetPosition(TAPE_POSITION_METHOD.TAPE_SPACE_SEQUENTIAL_FMKS, 0, count)).WithRetry().Run();

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

    /// <summary>Exponential backoff: yield → 1ms → 4 → 16 → … → 1000ms cap.</summary>
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

    /// <summary>
    /// Self-contained SetTapePosition dispatch: issues the command, polls for
    /// completion, and verifies via GetTapePosition.
    /// <para>
    /// Callers use <c>Op(() => InvokeSetPosition(...)).WithRetry().Run()</c> — no
    /// <c>.WithPoll()</c> needed because polling is handled internally. This is
    /// necessary because the verification probe must run <em>after</em> polling
    /// but <em>before</em> the caller sees success.
    /// </para>
    /// <para>
    /// Three layers of quirk handling, each discovered at runtime:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>DLT-V4 per-opcode rejection:</b> Some drives reject bImmediate=true
    /// for specific opcodes (e.g. TAPE_SPACE_FILEMARKS) with ERROR_INVALID_FUNCTION
    /// from SetTapePosition itself. Handled by <see cref="InvokeImmediateOrBlocking{TOpcode}"/>,
    /// which remembers the opcode and uses blocking for that opcode only.
    /// </description></item>
    /// <item><description>
    /// <b>DAT-320 premature completion (this method's verification):</b> The driver
    /// accepts bImmediate=true and GetTapeStatus polling reports NO_ERROR, but the
    /// drive has not physically finished. The verification probe catches this in
    /// two ways: (a) GetTapePosition returns ERROR_INVALID_FUNCTION (observed for
    /// partition switches), or (b) GetTapePosition returns NO_ERROR but with stale
    /// position data that doesn't match the expected result (observed for rewind
    /// where position stays at the old block instead of becoming 0). Position-value
    /// verification is applied for operations with predictable targets: TAPE_REWIND
    /// (must be 0) and TAPE_LOGICAL_BLOCK (must equal the requested offset).
    /// On first verification failure of either kind, <see cref="m_setPositionNeedsBlocking"/>
    /// flips permanently and ALL future positioning uses bImmediate=false.
    /// </description></item>
    /// <item><description>
    /// <b>DAT-320 position query settle time:</b> Even after blocking operations,
    /// GetTapePosition may need a brief settle delay. Handled separately by
    /// <see cref="InvokeGetTapePosition"/> with its own first-strike flag.
    /// </description></item>
    /// </list>
    /// </summary>
    private bool InvokeSetPosition(TAPE_POSITION_METHOD method, uint partition, long offset)
    {
        // Fast path: drive is known to need blocking for all positioning.
        // Bypasses InvokeImmediateOrBlocking entirely — the per-opcode HashSet
        // is irrelevant once the driver is globally untrustworthy.
        if (m_setPositionNeedsBlocking)
        {
            SetError(PInvoke.SetTapePosition(m_driveHandle, method, partition,
                Helpers.LoDWORD(offset), Helpers.HiDWORD(offset), false));
            if (!WentOK) return false;
            return PollForCompletion();
        }

        // Try immediate mode with DLT-V4 per-opcode fallback
        bool issued = InvokeImmediateOrBlocking(method, m_blockingPositionOps, immediate =>
        {
            SetError(PInvoke.SetTapePosition(m_driveHandle, method, partition,
                Helpers.LoDWORD(offset), Helpers.HiDWORD(offset), immediate));
            return WentOK;
        });
        if (!issued) return false;

        // Poll for completion
        if (!PollForCompletion()) return false;

        // Permanent supervision: verify that the drive genuinely completed the
        // operation. Uses raw PInvoke — NOT InvokeGetTapePosition — because the
        // retry wrapper would mask the ERROR_INVALID_FUNCTION we need to detect.
        // On well-behaved drives this is a single cheap metadata query per call.
        var probeResult = (WIN32_ERROR)PInvoke.GetTapePosition(
            m_driveHandle, TAPE_POSITION_TYPE.TAPE_LOGICAL_POSITION,
            out uint probePart, out uint probeLow, out uint probeHigh);

        if (probeResult == WIN32_ERROR.ERROR_INVALID_FUNCTION)
        {
            // The drive isn't truly ready — detected via hard failure.
            // Observed on DAT-320 for partition switches.
            return SwitchToBlocking(method, partition, offset,
                "GetTapePosition returned ERROR_INVALID_FUNCTION");
        }

        if (probeResult != WIN32_ERROR.NO_ERROR)
        {
            // Unexpected error from GetTapePosition — propagate as-is.
            SetError(probeResult);
            return false;
        }

        // GetTapePosition returned NO_ERROR — but is the position correct?
        // QUIRK DAT-320: For TAPE_REWIND the firmware returns NO_ERROR with
        //  the OLD position (e.g. 9 instead of 0) because the physical rewind
        //  hasn't completed. For operations with known target positions, compare
        //  the probe result to catch this stale-data variant.
        long probePosition = Helpers.MakeLong(probeLow, probeHigh);
        long? expectedPosition = method switch
        {
            TAPE_POSITION_METHOD.TAPE_REWIND => 0,
            TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK => offset,
            _ => null // Can't predict result for spacing operations
        };

        if (expectedPosition != null && probePosition != expectedPosition)
        {
            // Position mismatch — the drive reported success but hasn't
            // physically reached the target. Same root cause, different symptom.
            return SwitchToBlocking(method, partition, offset,
                $"position mismatch (expected {expectedPosition}, got {probePosition})");
        }

        // For TAPE_LOGICAL_BLOCK with an explicit partition (> 0), also verify
        // the drive actually switched partitions.
        if (method == TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK && partition > 0 && probePart != partition)
        {
            return SwitchToBlocking(method, partition, offset,
                $"partition mismatch (expected {partition}, got {probePart})");
        }

        // Verification passed — immediate mode is functioning for this call.
        return true;
    }

    /// <summary>
    /// First-strike handler: permanently switches all positioning to blocking mode
    /// and re-issues the failed operation in blocking mode.
    /// </summary>
    private bool SwitchToBlocking(TAPE_POSITION_METHOD method, uint partition, long offset, string reason)
    {
        m_setPositionNeedsBlocking = true;
        m_logger.LogInformation(
            "{Prefix}: {Method} verification failed ({Reason}) — ALL positioning switched to blocking mode for this session",
            LogPrefix, method, reason);

        // Re-issue in blocking mode — this is the one-time cost of detection
        SetError(PInvoke.SetTapePosition(m_driveHandle, method, partition,
            Helpers.LoDWORD(offset), Helpers.HiDWORD(offset), false));
        if (!WentOK) return false;
        return PollForCompletion();
    }

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

    /// <summary>
    /// Queries tape position with automatic retry for drives that return
    /// ERROR_INVALID_FUNCTION when queried too soon after a bImmediate operation.
    /// Uses the same exponential backoff as <see cref="PollForCompletion"/>.
    /// </summary>
    private bool InvokeGetTapePosition(out uint win32Partition, out uint blockLow, out uint blockHigh)
    {
        // Pre-yield for known-quirky drives to prevent the first failed attempt
        if (m_positionQueryNeedsRetry)
            Thread.Sleep(0);

        SetError(PInvoke.GetTapePosition(m_driveHandle, TAPE_POSITION_TYPE.TAPE_LOGICAL_POSITION,
            out win32Partition, out blockLow, out blockHigh));

        if (WentOK || LastErrorWin32 != WIN32_ERROR.ERROR_INVALID_FUNCTION)
            return WentOK;

        // First strike — remember for future pre-yields
        if (!m_positionQueryNeedsRetry)
        {
            m_positionQueryNeedsRetry = true;
            m_logger.LogInformation("{Prefix}: GetTapePosition needs post-operation settle time \u2014 remembered for future calls",
                LogPrefix);
        }

        // Retry with exponential backoff (reuses NextPollDelay: 1ms \u2192 4 \u2192 16 \u2192 ...)
        int delayMs = 0;
        for (int attempt = 0; attempt < c_maxQueryRetries; attempt++)
        {
            delayMs = NextPollDelay(delayMs);
            Thread.Sleep(delayMs);

            SetError(PInvoke.GetTapePosition(m_driveHandle, TAPE_POSITION_TYPE.TAPE_LOGICAL_POSITION,
                out win32Partition, out blockLow, out blockHigh));

            if (WentOK || LastErrorWin32 != WIN32_ERROR.ERROR_INVALID_FUNCTION)
            {
                m_logger.LogInformation("{Prefix}: GetTapePosition succeeded after {Attempt} retries and up to {Delay}ms delay",
                    LogPrefix, attempt + 1, delayMs);
                return WentOK;
            }
        }

        m_logger.LogWarning("{Prefix}: GetTapePosition failed after {MaxRetries} retries and up to {Delay}ms delay",
            LogPrefix, c_maxQueryRetries, delayMs);
        return false;
    }

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

            // trace media capacity and remaining
            m_logger.LogTrace("Refreshed media parmeters: Capacity {Capacity}, Remaining {Remaining}",
                Helpers.BytesToString(m_mediaParams?.Capacity ?? 0L), Helpers.BytesToString(m_mediaParams?.Remaining ?? 0L));
        }
        else
            LogErrorAsDebug("Failed to get media parameters");



        return WentOK;
    }

    #endregion // *** Private Helpers ***

    #region *** Partition Mapping Helpers ***

    private uint MapPartitionToWin32(MediaPartition partition)
    {
        if (partition == MediaPartition.Current)
            return 0U; // In Win32 0 means "use current partition"

        if (HasInitiatorPartition)
        {
            // In Win32 partition 2 = initiator, partition 1 = content
            //  QUIRK LTO-5+: it's vice versa!
            return false //m_useLtoPartitionSchema
                ? (partition == MediaPartition.Initiator) ? 1U : 2U
                : (partition == MediaPartition.Initiator) ? 2U : 1U;
        }

        if (partition == MediaPartition.Initiator)
        {
            LogErrorAsWarning("Request to map initiator partition while none exists -> using content partition instead",
                nameof(MapPartitionToWin32));
        }

        // In Win32 partition 1 = common
        return 1U;
    }

    private MediaPartition MapWin32ToPartition(uint win32Partition)
    {
        if (win32Partition == 0U)
            return MediaPartition.Current;
        if (HasInitiatorPartition)
        {
            // In Win32 partition 2 = initiator, partition 1 = content
            //  QUIRK LTO-5+: it's vice versa!
            return false //m_useLtoPartitionSchema
                ? (win32Partition == 2U) ? MediaPartition.Content : MediaPartition.Initiator
                : (win32Partition == 2U) ? MediaPartition.Initiator : MediaPartition.Content;
        }
        // In Win32 partition 1 = common
        return MediaPartition.Content;
    }

    #endregion // *** Partition Mapping Helpers ***
}