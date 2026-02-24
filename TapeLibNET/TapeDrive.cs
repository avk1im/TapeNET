using System.Diagnostics;
using System.Text;
using Windows.Win32.Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TapeLibNET;

/// <summary>
/// Platform-agnostic tape drive controller that delegates to a TapeDriveBackend.
/// </summary>
public class TapeDrive : ErrorManageableBase, IDisposable
{
    #region *** Private Fields ***

    private readonly TapeDriveBackend m_backend;
    private DriveCapabilities? m_driveParams = null;
    private MediaParameters? m_mediaParams = null;

    #endregion

    #region *** Private Constants ***

    private const uint c_defaultBlockSize = 16 * 1024; // 16 KB
    private const int c_gapFileLength = 64;

    #endregion

    #region *** Constructors ***

    public TapeDrive(ILoggerFactory loggerFactory, TapeDriveBackend backend)
        : base(loggerFactory.CreateLogger<TapeDrive>())
    {
        m_backend = backend;
    }

    public TapeDrive(TapeDriveBackend backend)
        : this(backend.LoggerFactory, backend)
    {
    }

    /// <summary>Creates TapeDrive2 with Win32 backend.</summary>
    public static TapeDrive CreateWin32(ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        return new TapeDrive(loggerFactory, new TapeDriveWin32Backend(loggerFactory));
    }

    #endregion

    #region *** Properties ***

    protected override string LogPrefix => $"Drive #{DriveNumber}";

    /// <summary>The underlying backend implementation (Win32 or Virtual).</summary>
    public TapeDriveBackend Backend => m_backend;

    public uint DriveNumber => m_backend.DriveNumber;
    public string DriveDeviceName => m_backend.DeviceName;

    public bool IsDriveOpen => m_backend.IsOpen && m_driveParams != null;
    public bool IsMediaLoaded => IsDriveOpen && m_mediaParams != null;

    public bool SupportsInitiatorPartition => m_driveParams?.SupportsInitiatorPartition ?? false;
    public bool HasInitiatorPartition => m_mediaParams?.HasInitiatorPartition ?? false;
    public uint PartitionCount => HasInitiatorPartition ? 2U : 1U;
    public bool SupportsSetmarks => m_driveParams?.SupportsSetmarks ?? false;
    public bool SupportsSeqFilemarks => m_driveParams?.SupportsSeqFilemarks ?? false;
    public uint MinimumBlockSize => m_driveParams?.MinimumBlockSize ?? 0U;
    public uint MaximumBlockSize => m_driveParams?.MaximumBlockSize ?? 0U;
    public uint DefaultBlockSize => m_driveParams?.DefaultBlockSize ?? 0U;

    public uint BlockSize => m_mediaParams?.BlockSize ?? 0U;
    internal long BlockCounter => GetCurrentBlock();
    public long Capacity => m_mediaParams?.Capacity ?? 0L;

    public long GetRemainingCapacity()
    {
        if (!RefreshMediaParams())
            return -1;

        Debug.Assert(m_mediaParams != null);
        return m_mediaParams.Value.Remaining;
    }

    public long ByteCounter { get; internal set; } = 0L;

    public ILoggerFactory LoggerFactory => m_backend.LoggerFactory;

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append("Drive name: ").Append(DriveDeviceName);
        sb.Append("\nOpen?: ").Append(IsDriveOpen);

        sb.Append("\nDrive parameters: ");
        if (m_driveParams != null)
            sb.Append('\n').Append(m_driveParams.ToString());
        else
            sb.Append("<not filled>");

        sb.Append("\nMedia parameters: ");
        if (m_mediaParams != null)
            sb.Append('\n').Append(m_mediaParams.ToString());
        else
            sb.Append("<not filled>");

        return sb.ToString();
    }

    #endregion

    #region *** Disposing ***

    private bool m_disposed = false;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!m_disposed)
        {
            m_logger.LogTrace("{Prefix}: Disposing", LogPrefix);

            if (disposing)
            {
                m_backend.Dispose();
            }

            m_disposed = true;
        }
    }

    ~TapeDrive()
    {
        Dispose(disposing: false);
    }

    #endregion

    #region *** Direct Read/Write ***

    public int WriteDirect(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        m_backend.CheckForRW(nameof(WriteDirect), buffer, offset, count);

        int blocksToWrite = count / (int)BlockSize;
        int toWrite = blocksToWrite * (int)BlockSize;

        tapemark = false;
        eof = false;

        if (toWrite == 0)
            return 0;

        int written = m_backend.Write(buffer, offset, toWrite, out tapemark, out eof);
        SyncErrorFrom(m_backend);

        if (WentBad)
        {
            if (tapemark)
                LogErrorAsTrace("Write encountered tapemark");
            if (eof)
                LogErrorAsTrace("Write encountered EOF");
            if (!tapemark && !eof)
                LogErrorAsDebug("Write failed");
        }

        ByteCounter += written;
        return written;
    }

    public int ReadDirect(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        m_backend.CheckForRW(nameof(ReadDirect), buffer, offset, count);

        int blocksToRead = count / (int)BlockSize;
        int toRead = blocksToRead * (int)BlockSize;

        tapemark = false;
        eof = false;

        if (toRead == 0)
            return 0;

        int read = m_backend.Read(buffer, offset, toRead, out tapemark, out eof);
        SyncErrorFrom(m_backend);

        if (WentBad)
        {
            if (tapemark)
                LogErrorAsTrace("Read encountered tapemark");
            if (eof)
            {
                LogErrorAsTrace("Read encountered EOF");
                ResetError();
            }
            if (!tapemark && !eof)
                LogErrorAsDebug("Read failed");
        }

        ByteCounter += read;
        return read;
    }

    internal void CheckForRW(string methodName) => m_backend.CheckForRW(methodName);
    internal void CheckForRW(string methodName, byte[] buffer, int offset, int count) =>
        m_backend.CheckForRW(methodName, buffer, offset, count);

    #endregion

    #region *** Drive & Media Operations ***

    public bool ReopenDrive(uint driveNumber = 0, bool unconditionally = true)
    {
        if (!unconditionally && IsDriveOpen)
            return true;

        m_logger.LogTrace("{Prefix}: Reopening", LogPrefix);

        CloseDrive();

        if (!m_backend.Open(driveNumber))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to open drive");
            return false;
        }

        if (!RefreshDriveCaps())
        {
            LogErrorAsDebug("Failed to fill drive parameters");
            return false;
        }

        SetOptimalDriveParams();

        m_logger.LogTrace("{Prefix}: Drive reopened", LogPrefix);
        return IsDriveOpen;
    }

    public void CloseDrive()
    {
        m_logger.LogTrace("{Prefix}: Closing", LogPrefix);

        m_backend.Close();
        m_driveParams = null;
        m_mediaParams = null;

        m_logger.LogTrace("{Prefix}: Closed", LogPrefix);
    }

    public bool ReloadMedia(bool unconditionally = true)
    {
        if (!unconditionally && IsMediaLoaded)
            return true;

        if (!IsDriveOpen)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_HANDLE);
            return false;
        }

        if (!m_backend.LoadMedia())
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to load media");
            return false;
        }

        RefreshMediaParams();
        m_logger.LogTrace("{Prefix}: Media loaded", LogPrefix);
        return IsMediaLoaded;
    }

    public bool UnloadMedia()
    {
        if (!IsDriveOpen)
            return false;

        if (!m_backend.UnloadMedia())
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to unload media");
            return false;
        }

        m_mediaParams = null;
        m_logger.LogTrace("{Prefix}: Media unloaded", LogPrefix);
        return true;
    }

    public bool PrepareMedia()
    {
        if (!IsMediaLoaded)
            return false;

        ResetError();

        SetOptimalMediaParams();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Media prepared", LogPrefix);
        else
            LogErrorAsDebug("Failed to prepare media");

        return WentOK;
    }

    public bool FormatMedia(long initiatorPartitionSize = -1)
    {
        if (!IsMediaLoaded)
            return false;

        m_logger.LogTrace("{Prefix}: Formatting media", LogPrefix);

        if (!m_backend.FormatMedia(initiatorPartitionSize))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to format media");
            return false;
        }

        // Reload after format
        if (WentOK)
            m_backend.LoadMedia();
        if (WentOK)
            RefreshMediaParams();
        if (WentOK)
            PrepareMedia();

        if (WentOK)
            m_logger.LogTrace("{Prefix}: Formatted media", LogPrefix);
        else
            LogErrorAsDebug("Failed to format media");

        return WentOK;
    }

    internal bool SetBlockSize(uint size)
    {
        if (!IsMediaLoaded)
            return false;

        if (size == 0)
            size = DefaultBlockSize;
        else if (size > MaximumBlockSize)
            size = MaximumBlockSize;
        else if (size < MinimumBlockSize)
            size = MinimumBlockSize;

        size = Math.Min(size, int.MaxValue);

        if (BlockSize == size)
            return true;

        if (!m_backend.SetBlockSize(size))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to set block size");
            return false;
        }

        RefreshMediaParams();
        m_logger.LogTrace("{Prefix}: Block size set to {Size}", LogPrefix, BlockSize);
        return WentOK;
    }

    #endregion

    #region *** Partition Operations ***

    public bool MoveToPartition(MediaPartition partition, long block = 0)
    {
        if (!IsMediaLoaded)
            return false;

        m_logger.LogTrace("{Prefix}: Moving to partition {Partition}", LogPrefix, partition);

        if (!m_backend.SetPositionToPartition(partition, block))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to move to partition");
            return false;
        }

        // parameters may differ for another partition, e.g. Capacity -> refresh them
        RefreshMediaParams();

        m_logger.LogTrace("{Prefix}: Moved to partition {Partition}", LogPrefix, partition);
        return true;
    }

    #endregion

    #region *** Tapemark Operations ***

    public bool MoveToNextFilemark(int count = 1)
    {
        if (!IsMediaLoaded)
            return false;

        if (!m_backend.SpaceFilemarks(count))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to move to filemark(s)");
            return false;
        }

        m_logger.LogTrace("{Prefix}: Moved by {Count} filemark(s)", LogPrefix, count);
        return true;
    }

    public bool WriteFilemark(uint count = 1)
    {
        if (!IsMediaLoaded)
            return false;

        if (!m_backend.WriteFilemarks(count))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to write filemark(s)");
            return false;
        }

        m_logger.LogTrace("{Prefix}: Wrote {Count} filemark(s)", LogPrefix, count);
        return true;
    }

    public bool MovePastSeqFilemarks(int count)
    {
        if (!IsMediaLoaded)
            return false;

        if (!m_backend.SpaceSequentialFilemarks(count))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to move past seq filemarks");
            return false;
        }

        m_logger.LogTrace("{Prefix}: Moved past {Count} seq filemark(s)", LogPrefix, count);
        return true;
    }

    public bool MoveToNextSetmark(int count = 1)
    {
        if (!IsMediaLoaded)
            return false;

        if (count == 0)
        {
            ResetError();
            return true;
        }

        if (!m_backend.SpaceSetmarks(count))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to move to setmark(s)");
            return false;
        }

        m_logger.LogTrace("{Prefix}: Moved by {Count} setmark(s)", LogPrefix, count);
        return true;
    }

    public bool WriteSetmark(uint count = 1)
    {
        if (!IsMediaLoaded)
            return false;

        if (!m_backend.WriteSetmarks(count))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to write setmark(s)");
            return false;
        }

        m_logger.LogTrace("{Prefix}: Wrote {Count} setmark(s)", LogPrefix, count);
        return true;
    }

    public bool WriteGapFile()
    {
        if (!IsMediaLoaded)
            return false;

        int length = Math.Max((int)MinimumBlockSize, c_gapFileLength);
        byte[] buffer = new byte[length];

        uint blockSize = BlockSize;
        SetBlockSize((uint)length);

        int result = WriteDirect(buffer, 0, length, out _, out _);

        SetBlockSize(blockSize);

        if (WentOK && result == length)
            m_logger.LogTrace("{Prefix}: Wrote gap file: {Bytes} bytes", LogPrefix, length);
        else
            LogErrorAsDebug("Failed to write gap file");

        return WentOK && result == length;
    }

    #endregion

    #region *** Tape Moving ***

    public bool Rewind()
    {
        if (!IsMediaLoaded)
            return false;

        if (!m_backend.Rewind())
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to rewind");
            return false;
        }

        m_logger.LogTrace("{Prefix}: Rewound", LogPrefix);
        return true;
    }

    public bool FastforwardToEnd(MediaPartition partition = MediaPartition.Content)
    {
        if (!IsMediaLoaded)
            return false;

        if (!m_backend.SeekToEnd(partition))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to fast forward");
            return false;
        }

        m_logger.LogTrace("{Prefix}: Fast forwarded to end", LogPrefix);
        return true;
    }

    public bool MoveToBlock(long block)
    {
        if (!IsMediaLoaded)
            return false;

        if (block == BlockCounter)
            return true;

        if (block < 0)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            return false;
        }

        if (!m_backend.SetPosition(block))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to move to block");
            return false;
        }

        m_logger.LogTrace("{Prefix}: Moved to block {Block}", LogPrefix, block);
        return true;
    }

    public long GetCurrentBlock()
    {
        if (!IsMediaLoaded)
            return -1;

        long position = m_backend.GetPosition();
        SyncErrorFrom(m_backend);

        return WentOK ? position : -1;
    }

    public MediaPartition GetCurrentPartition()
    {
        if (!IsMediaLoaded)
            return MediaPartition.Current;

        MediaPartition partition = m_backend.GetCurrentPartition();
        SyncErrorFrom(m_backend);

        return WentOK ? partition : MediaPartition.Current;
    }

    #endregion

    #region *** Private Helpers ***

    private bool RefreshDriveCaps()
    {
        m_backend.FillDriveCapabilities(out DriveCapabilities driveParams);
        SyncErrorFrom(m_backend);

        if (WentOK)
            m_driveParams = driveParams;

        return WentOK;
    }

    internal bool RefreshMediaParams()
    {
        m_backend.FillMediaParameters(out MediaParameters mediaParams);
        SyncErrorFrom(m_backend);

        if (WentOK)
            m_mediaParams = mediaParams;

        return WentOK;
    }

    private void SetOptimalDriveParams()
    {
        if (!IsDriveOpen || m_driveParams == null)
            return;

        bool compression = m_driveParams.Value.SupportsCompression;
        bool ecc = m_driveParams.Value.SupportsEcc;
        bool padding = m_driveParams.Value.SupportsPadding;
        bool reportSetmarks = m_driveParams.Value.SupportsSetmarks;
        uint eotZone = padding ? m_driveParams.Value.DefaultBlockSize * 4 : 0;

        if (!m_backend.SetDriveParameters(compression, ecc, padding, reportSetmarks, eotZone))
        {
            SyncErrorFrom(m_backend);
            ResetError(); // Ignore failure
        }
    }

    private void SetOptimalMediaParams()
    {
        SetBlockSize(c_defaultBlockSize);
    }

    #endregion
}