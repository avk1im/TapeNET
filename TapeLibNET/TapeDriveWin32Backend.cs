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
public class TapeDriveWin32Backend : TapeDriveBackend
{
    #region *** Private Fields ***

    private SafeFileHandle m_driveHandle = new();
    private uint m_driveNumber = 0;
    private TAPE_GET_DRIVE_PARAMETERS? m_driveParams = null;
    private TAPE_GET_MEDIA_PARAMETERS? m_mediaParams = null;

    #endregion

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

    #endregion

    #region *** Constructor ***

    public TapeDriveWin32Backend(ILoggerFactory loggerFactory) : base(loggerFactory)
    {
    }

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
        if (!InvokeWithRetry(RefreshDriveParams))
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

        if (!InvokeWithRetry(LoadMediaInternalImmediate))
        {
            LogErrorAsWarning("Failed to load media using immediate version");

            if (!InvokeWithRetry(LoadMediaInternalBlocking))
            {
                LogErrorAsDebug("Failed to load media using blocking version");
                return false;
            }
        }

        m_logger.LogTrace("{Prefix}: Media loaded", LogPrefix);
        return true;
    }

    private bool LoadMediaInternal(bool immediate = false)
    {
        SetError(PInvoke.PrepareTape(m_driveHandle, PREPARE_TAPE_OPERATION.TAPE_LOAD, immediate));
        // TODO: sometimes this call just hangs. Consider adding a timeout mechanism.
        //  Can try to pass true for bImmediate - and poll GetTapeStatus until it's ready.

        if (WentOK)
            RefreshMediaParams();

        return WentOK;
    }

    private bool LoadMediaInternalImmediate() => LoadMediaInternal(true);
    private bool LoadMediaInternalBlocking() => LoadMediaInternal(false);


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
            LoadMediaInternal(); // Refresh media parameters
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

        uint read = 0;
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

        uint written = 0;
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

        SetError(PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 0,
            Helpers.LoDWORD(block), Helpers.HiDWORD(block), false));

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
            PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 1, 0, 0, false);
        }

        SetError(PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, win32Partition,
            Helpers.LoDWORD(block), Helpers.HiDWORD(block), false));

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

        SetError(PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_REWIND, 0, 0, 0, false));

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Rewound", LogPrefix);
        else
            LogErrorAsDebug("Failed to rewind");

        return WentOK;
    }

    public override bool SeekToEnd(MediaPartition partition)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        uint win32Partition = MapPartitionToWin32(partition);
        SetError(PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_END_OF_DATA, win32Partition, 0, 0, false));

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

        SetError(PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_FILEMARKS, 0,
            Helpers.LoDWORD(count), Helpers.HiDWORD(count), false));

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

        SetError(PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_SETMARKS, 0,
            Helpers.LoDWORD(count), Helpers.HiDWORD(count), false));

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

        SetError(PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_SEQUENTIAL_FMKS, 0,
            Helpers.LoDWORD(count), Helpers.HiDWORD(count), false));

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

        SetError(PInvoke.WriteTapemark(m_driveHandle, TAPEMARK_TYPE.TAPE_FILEMARKS, count, false));

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

        SetError(PInvoke.WriteTapemark(m_driveHandle, TAPEMARK_TYPE.TAPE_SETMARKS, count, false));

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
            m_mediaParams = mediaParams;
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
            // partition 1 = initiator, partition 2 = content
            return (partition == MediaPartition.Initiator) ? 2U : 1U;
        }

        if (partition == MediaPartition.Initiator)
        {
            LogErrorAsWarning("Request to map initiator partition while none exists -> using content partition instead",
                nameof(MapPartitionToWin32));
        }

        // partition 1 = common
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
        if (win32Partition == 0)
            return MediaPartition.Current;
        if (HasInitiatorPartition)
        {
            // partition 1 = initiator, partition 2 = content
            return (win32Partition == 2) ? MediaPartition.Initiator : MediaPartition.Content;
        }
        // partition 1 = common
        return MediaPartition.Content;
    }

    private bool InvokeWithRetry(Func<bool> func)
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

    #endregion
}