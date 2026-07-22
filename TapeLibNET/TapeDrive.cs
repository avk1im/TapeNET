using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using TapeLibNET.Remote;
using Windows.Win32.Foundation;
using Stopwatch = Windows.Win32.System.SystemServices.Stopwatch;

namespace TapeLibNET;

/// <summary>
/// Platform-agnostic tape drive controller.
/// Delegates all hardware I/O to a <see cref="TapeDriveBackend"/> (Win32 or virtual)
///  while providing error handling, media lifecycle, and direct read/write access.
/// <para>Lifecycle: <see cref="ReopenDrive"/> → <see cref="ReloadMedia"/> → <see cref="PrepareMedia"/> → I/O → <see cref="Dispose"/>.</para>
/// </summary>
public class TapeDrive(ILoggerFactory loggerFactory, TapeDriveBackend backend)
    : ErrorManageableBase(loggerFactory.CreateLogger<TapeDrive>()), IDisposable
{
    #region *** Private Fields ***

    private readonly TapeDriveBackend m_backend = backend;
    private DriveCapabilities? m_driveParams = null;
    private MediaParameters? m_mediaParams = null;
    private long m_byteCounter = 0L; // running count of bytes transferred via WriteDirect/ReadDirect
    private readonly Stopwatch m_IoTimer = new();

    // Content partition capacity cache — avoids needing to be on the Content partition
    // to query its capacity. Updated whenever RefreshMediaParams() is called while on Content.
    private long m_cachedContentCapacity = -1;
    private long m_cachedContentRemaining = -1;
    private bool m_onContentPartition; // tracked via MoveToPartition / EnsureOnContentPartition

    // Last requested early-warning reserve (bytes before EOM). Re-applied after (re)load,
    //  since early warning is a media-level setting like block size. 0 = not requested.
    private long m_desiredEarlyWarning = 0L;

    #endregion

    #region *** Private Constants ***

    private const uint c_defaultBlockSize = 16 * 1024; // 16 KB
    private const int c_gapFileLength = 64;

    #endregion

    #region *** Constructors ***

    public TapeDrive(TapeDriveBackend backend)
        : this(backend.LoggerFactory, backend)
    {
    }

    /// <summary>Creates TapeDrive2 with Win32 backend.</summary>
    public static TapeDrive CreateWin32(ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        return new TapeDrive(new TapeDriveWin32Backend(loggerFactory));
    }

    /// <summary>
    /// Creates a TapeDrive with a remote backend connecting to a tape service on the network,
    /// using the supplied <see cref="Remote.RemoteHostSettings"/> (supports TLS).
    /// The remote service manages the actual hardware (or virtual) backend.
    /// </summary>
    /// <param name="settings">Host, port, TLS and certificate settings.</param>
    /// <param name="loggerFactory">Logger factory for logging.</param>
    public static TapeDrive CreateRemote(Remote.RemoteHostSettings settings, ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        return new TapeDrive(new Remote.RemoteTapeDriveBackend(settings, loggerFactory));
    }

    /// <summary>
    /// Creates a TapeDrive with a remote backend connecting to a tape service on the network.
    /// The remote service manages the actual hardware (or virtual) backend.
    /// </summary>
    /// <param name="host">Hostname or IP address of the tape service.</param>
    /// <param name="port">gRPC port (default 50551).</param>
    /// <param name="loggerFactory">Logger factory for logging.</param>
    public static TapeDrive CreateRemote(string host, int port = 50551, ILoggerFactory? loggerFactory = null)
        => CreateRemote(new Remote.RemoteHostSettings(host, port), loggerFactory);

    /// <summary>Probe if a tape drive with specified number is present.</summary>
    public static bool ProbeWin32(uint driveNumber = 0)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        try
        {
            using var drive = new TapeDrive(new TapeDriveWin32Backend(loggerFactory));
            return drive.ProbeDrive(driveNumber);
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region *** Properties ***

    protected override string LogPrefix => $"Drive #{DriveNumber}";

    /// <summary>The underlying backend implementation (Win32 or Virtual).</summary>
    public TapeDriveBackend Backend => m_backend;

    /// <summary>
    /// Maximum time to wait for a tape operation (forwarded to the backend).
    /// </summary>
    public TimeSpan OperationTimeout
    {
        get => m_backend.OperationTimeout;
        set => m_backend.OperationTimeout = value;
    }

    /// <summary>Zero-based drive number (\\.\ TAPE<i>N</i>).</summary>
    public uint DriveNumber => m_backend.DriveNumber;

    /// <summary>NT device path (e.g. <c>\\.\TAPE0</c>).</summary>
    public string DriveDeviceName => m_backend.DeviceName;

    /// <summary>Drive vendor name, can be empty</summary>
    public string DriveVendor => m_backend.Vendor;

    /// <summary>Drive product name, can be empty</summary>
    public string DriveProduct => m_backend.Product;

    /// <summary>Drive firmware / microcode revision, can be empty.</summary>
    public string DriveRevision => m_backend.Revision;

    /// <summary>Stable identity for keying calibration data to this drive+media profile.</summary>
    public string DriveProfileKey => m_backend.ProfileKey;

    /// <summary>Drive handle is open and capabilities have been read.</summary>
    public bool IsDriveOpen => m_backend.IsOpen && m_driveParams != null;

    /// <summary>Drive is open and media (tape cartridge) is loaded.</summary>
    public bool IsMediaLoaded => IsDriveOpen && m_mediaParams != null;

    /// <summary>Drive hardware supports a separate initiator (TOC) partition.</summary>
    public bool SupportsInitiatorPartition => m_driveParams?.SupportsInitiatorPartition ?? false;

    /// <summary>Current media has an initiator partition created.</summary>
    public bool HasInitiatorPartition => m_mediaParams?.HasInitiatorPartition ?? false;

    /// <summary>Number of partitions on the current media (1 or 2).</summary>
    public uint PartitionCount => HasInitiatorPartition ? 2U : 1U;

    /// <summary>Drive supports setmarks (enables <c>WithSetmarks</c> tape organization).</summary>
    public bool SupportsSetmarks => m_driveParams?.SupportsSetmarks ?? false;

    /// <summary>Drive can distinguish sequential filemark counts (enables <c>WithSeqFilemarks</c> organization).</summary>
    public bool SupportsSeqFilemarks => m_driveParams?.SupportsSeqFilemarks ?? false;

    /// <summary>Minimum block size the drive accepts, in bytes.</summary>
    public uint MinimumBlockSize => m_driveParams?.MinimumBlockSize ?? 0U;

    /// <summary>Maximum block size the drive accepts, in bytes.</summary>
    public uint MaximumBlockSize => m_driveParams?.MaximumBlockSize ?? 0U;

    /// <summary>Drive's default block size, in bytes.</summary>
    public uint DefaultBlockSize => m_driveParams?.DefaultBlockSize ?? 0U;

    /// <summary>Drive supports hardware compression</summary>
    public bool SupportsCompression => m_driveParams?.SupportsCompression ?? false;

    /// <summary>Current block size for read/write operations, in bytes.</summary>
    public uint BlockSize => m_mediaParams?.BlockSize ?? 0U;

    /// <summary>Current logical block address from the device.</summary>
    internal long BlockCounter => GetCurrentBlock();

    /// <summary>Total capacity of the current partition, in bytes.</summary>
    public long Capacity => m_mediaParams?.Capacity ?? 0L;

    /// <summary>
    /// Capacity of the Content partition, cached from the last time media params were
    /// refreshed while on Content. Falls back to <see cref="Capacity"/> if not yet cached.
    /// </summary>
    public long ContentCapacity => m_cachedContentCapacity >= 0 ? m_cachedContentCapacity : Capacity;

    /// <summary>Queries remaining capacity of the current partition (refreshes media params). Returns −1 on failure.</summary>
    public long GetRemainingCapacity()
    {
        if (!RefreshMediaParams())
            return -1;

        Debug.Assert(m_mediaParams != null);
        return m_mediaParams.Value.Remaining;
    }

    /// <summary>
    /// Remaining capacity of the Content partition, cached from the last time media params
    /// were refreshed while on Content. If currently on Content, refreshes first.
    /// </summary>
    public long GetContentRemainingCapacity()
    {
        if (m_onContentPartition)
        {
            // On content — refresh to get the latest value and cache it
            return GetRemainingCapacity();
        }

        // On another partition — return the cached content remaining
        return m_cachedContentRemaining >= 0 ? m_cachedContentRemaining : 0;
    }

    /// <summary>
    /// True if the underlying backend is a Win32 tape drive and the drive is an LTO model.
    /// </summary>
    public bool IsLtoDrive => m_backend is TapeDriveWin32Backend wbe && wbe.IsLto
                || m_backend is RemoteTapeDriveBackend rbe && rbe.IsLto;

    /// <summary>
    /// True if the underlying backend is a Win32 tape drive and the drive is an LTO-5+ model.
    /// </summary>
    public bool IsLto5PlusDrive => m_backend is TapeDriveWin32Backend wbe && wbe.IsLto5Plus
                || m_backend is RemoteTapeDriveBackend rbe && rbe.IsLto5Plus;

    /// <summary>
    /// Early-warning reserve, in bytes before physical EOM. Universal across all drives:
    /// under the hood the backend realizes it with the best mechanism available — programmable
    /// early warning (LTO-5+), the drive's built-in early-warning zone, a calibrated estimate,
    /// or an uncalibrated estimate.
    /// <para>
    /// Assigning requests a value; the drive may not honor it exactly, so read the property back
    /// afterward to see what was actually achieved (the same pattern as <see cref="BlockSize"/>).
    /// A crossing during writing is reported by <see cref="WriteDirect"/> setting <c>eof=true</c>
    /// together with <see cref="LastError"/> = <see cref="TapeEarlyWarning.EarlyWarningError"/>
    /// (see <see cref="IsEarlyWarning"/>), distinct from hard end-of-media.
    /// </para>
    /// </summary>
    public long EarlyWarning
    {
        get => m_mediaParams?.EarlyWarning ?? 0L;
        set => TrySetEarlyWarning(value);
    }

    /// <summary>How <see cref="EarlyWarning"/> is currently realized (best available mechanism).</summary>
    public EarlyWarningMechanism EarlyWarningMechanism => m_backend.EarlyWarningMechanism;

    /// <summary>
    /// True when the current error state indicates an early-warning crossing (data was written;
    /// wrap up and write the TOC) — as opposed to a hard end-of-media (<see cref="IsEOM"/>).
    /// </summary>
    public bool IsEarlyWarning => LastError == TapeEarlyWarning.EarlyWarningError;

    /// <summary>Running count of bytes transferred via <see cref="WriteDirect"/>/<see cref="ReadDirect"/>. Reset by the stream manager.</summary>
    public long ByteCounter
    {
        get => m_byteCounter;
        internal set
        {
            if (value == 0)
                IoTimeCounterUs = 0L; // reset I/O time counter when byte counter is reset
            m_byteCounter = value;
        }
    }

    /// <summary>Running count of microseconds spent in I/O operations via <see cref="WriteDirect"/>/<see cref="ReadDirect"/>.</summary>
    public long IoTimeCounterUs { get; internal set; } = 0L;

    /// <summary>Logger factory shared with the backend.</summary>
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
            // Set the flag before calling backend.Dispose() so that any exception
            //  from the backend does not leave this object re-entrant.
            m_disposed = true;
            if (disposing)
            {
                m_backend.Dispose();
            }
        }
    }

    ~TapeDrive()
    {
        Dispose(disposing: false);
    }

    #endregion

    #region *** Direct Read/Write ***

    /// <summary>Writes raw blocks to tape. Returns bytes written; sets <paramref name="tapemark"/>/<paramref name="eof"/> on boundary conditions.</summary>
    public int WriteDirect(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        m_backend.CheckForRW(buffer, offset, count);
        int blocksToWrite = count / (int)BlockSize;
        int toWrite = blocksToWrite * (int)BlockSize;
        tapemark = false;
        eof = false;
        if (toWrite == 0)
            return 0;

        m_IoTimer.Restart();
        int written = m_backend.Write(buffer, offset, toWrite, out tapemark, out eof);
        m_IoTimer.Stop();
        IoTimeCounterUs += m_IoTimer.ElapsedMicroseconds;
        SyncErrorFrom(m_backend);

        if (WentBad)
        {
            if (tapemark)
                LogErrorAsTrace("Write encountered tapemark");
            if (eof)
            {
                // Distinguish an early-warning crossing (data written; wrap up) from hard EOM.
                if (IsEarlyWarning)
                    LogErrorAsTrace("Write crossed early-warning boundary");
                else
                    LogErrorAsTrace("Write encountered EOF");
            }
            if (!tapemark && !eof)
                LogErrorAsDebug("Write failed");
        }

        m_byteCounter += written;
        return written;
    }

    /// <summary>Reads raw blocks from tape. Returns bytes read; sets <paramref name="tapemark"/>/<paramref name="eof"/> on boundary conditions.</summary>
    public int ReadDirect(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        m_backend.CheckForRW(buffer, offset, count);
        int blocksToRead = count / (int)BlockSize;
        int toRead = blocksToRead * (int)BlockSize;
        tapemark = false;
        eof = false;
        if (toRead == 0)
            return 0;

        m_IoTimer.Restart();
        int read = m_backend.Read(buffer, offset, toRead, out tapemark, out eof);
        m_IoTimer.Stop();
        IoTimeCounterUs += m_IoTimer.ElapsedMicroseconds;
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

        m_byteCounter += read;
        return read;
    }

    internal void CheckForRW([CallerMemberName] string methodName = "") => m_backend.CheckForRW(methodName);
    internal void CheckForRW(byte[] buffer, int offset, int count, [CallerMemberName] string methodName = "") =>
        m_backend.CheckForRW(buffer, offset, count, methodName);

    #endregion

    #region *** Early Warning & Calibration ***

    /// <summary>
    /// Requests an early-warning reserve of <paramref name="bytesBeforeEom"/> bytes before EOM.
    /// Returns <see langword="true"/> if the backend accepted the request. Read <see cref="EarlyWarning"/>
    /// back to see the value actually achieved, and <see cref="EarlyWarningMechanism"/> to see how.
    /// </summary>
    public bool TrySetEarlyWarning(long bytesBeforeEom)
    {
        if (!IsMediaLoaded)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }
        if (bytesBeforeEom < 0)
            bytesBeforeEom = 0;

        m_desiredEarlyWarning = bytesBeforeEom;

        bool ok = m_backend.SetEarlyWarning(bytesBeforeEom);
        SyncErrorFrom(m_backend);

        // Refresh either way so the EarlyWarning getter reflects the drive's actual state.
        RefreshMediaParams();

        if (ok)
            m_logger.LogTrace("{Prefix}: Early warning requested {Req} bytes → effective {Eff} bytes (mechanism {Mech})",
                LogPrefix, bytesBeforeEom, EarlyWarning, EarlyWarningMechanism);
        else
            LogErrorAsDebug("Failed to set early warning");

        return ok;
    }

    /// <summary>True if the backend can run and apply early-warning calibration.</summary>
    public bool SupportsEarlyWarningCalibration => m_backend.SupportsEarlyWarningCalibration;

    /// <summary>The calibration currently installed/active on the backend, if any.</summary>
    public ITapeCalibration? CurrentEarlyWarningCalibration => m_backend.CurrentEarlyWarningCalibration;

    /// <summary>
    /// Runs a (destructive) early-warning calibration on the loaded scratch media and returns an
    /// opaque, persistable result that is also installed as the active calibration. The application
    /// should save the result via <see cref="ITapeCalibration.SaveTo"/> keyed by
    /// <see cref="ITapeCalibration.ProfileKey"/>. Returns <see langword="null"/> if unsupported or on failure.
    /// </summary>
    public ITapeCalibration? CalibrateEarlyWarning(
        EarlyWarningCalibrationOptions? options = null,
        IProgress<EarlyWarningCalibrationProgress>? progress = null)
    {
        if (!IsMediaLoaded)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return null;
        }

        ITapeCalibration? result = m_backend.CalibrateEarlyWarning(
            options ?? new EarlyWarningCalibrationOptions(), progress);
        SyncErrorFrom(m_backend);
        RefreshMediaParams();

        if (result == null)
            LogErrorAsDebug("Early-warning calibration failed");
        else
            m_logger.LogTrace("{Prefix}: Early-warning calibration completed (profile {Key})",
                LogPrefix, result.ProfileKey);

        return result;
    }

    /// <summary>
    /// Installs a previously-saved calibration so subsequent <see cref="EarlyWarning"/> assignments can
    /// use the calibrated estimate. Returns <see langword="false"/> if unsupported or the calibration
    /// does not match this drive+media profile.
    /// </summary>
    public bool ApplyEarlyWarningCalibration(ITapeCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);

        bool ok = m_backend.ApplyEarlyWarningCalibration(calibration);
        SyncErrorFrom(m_backend);

        if (ok && m_desiredEarlyWarning > 0)
            TrySetEarlyWarning(m_desiredEarlyWarning); // re-derive effective value under the new calibration
        else if (!ok)
            LogErrorAsDebug("Failed to apply early-warning calibration");

        return ok;
    }

    /// <summary>
    /// Reconstructs an opaque calibration object from a stream the application previously saved.
    /// The backend is the factory (only it understands its own format). Returns <see langword="null"/>
    /// if unsupported or unrecognized.
    /// </summary>
    public ITapeCalibration? LoadEarlyWarningCalibration(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ITapeCalibration? cal = m_backend.LoadEarlyWarningCalibration(stream);
        SyncErrorFrom(m_backend);

        if (cal == null)
            LogErrorAsDebug("Failed to load early-warning calibration");

        return cal;
    }

    #endregion

    #region *** Drive & Media Operations ***

    /// <summary>Opens (or reopens) the drive, reads capabilities, and sets optimal parameters.</summary>
    /// <remarks>
    /// If the backend is already open (e.g. opened externally via <c>OpenVirtual</c>),
    /// skips the close/open cycle and only refreshes capabilities and parameters.
    /// </remarks>
    public bool ReopenDrive(uint driveNumber = 0, bool unconditionally = true)
    {
        if (!unconditionally && IsDriveOpen)
            return true;

        m_logger.LogTrace("{Prefix}: Reopening", LogPrefix);

        // If the backend was already opened externally (e.g. remote OpenVirtual),
        // skip the close/open cycle — just refresh caps from the existing backend.
        if (!m_backend.IsOpen)
        {
            CloseDrive();
            if (!m_backend.Open(driveNumber))
            {
                SyncErrorFrom(m_backend);
                LogErrorAsDebug("Failed to open drive");
                return false;
            }
        }

        if (!RefreshDriveCaps())
        {
            LogErrorAsDebug("Failed to pre-fill drive parameters");
            return false;
        }
        SetOptimalDriveParams();
        if (!RefreshDriveCaps())
        {
            LogErrorAsDebug("Failed to fill drive parameters after setting optimal ones");
            return false;
        }
        m_logger.LogTrace("{Prefix}: Drive reopened", LogPrefix);
        return IsDriveOpen;
    }

    internal bool ProbeDrive(uint driveNumber = 0)
    {
        if (IsDriveOpen) // do not probe with an already open drive not to spoil its state!
            return false;

        m_logger.LogTrace("Probing drive #{Number}", driveNumber);
        if (!m_backend.Open(driveNumber))
        {
            m_logger.LogTrace("Failed probing drive #{Number}", driveNumber);
            return false;
        }
        m_backend.Close();
        m_logger.LogTrace("Suceeded probing drive #{Number}", driveNumber);
        return true;
    }

    /// <summary>Closes the drive handle and clears cached parameters.</summary>
    public void CloseDrive()
    {
        m_logger.LogTrace("{Prefix}: Closing", LogPrefix);
        m_backend.Close();
        m_driveParams = null;
        m_mediaParams = null;
        m_desiredEarlyWarning = 0L;
        InvalidateContentCache();
        m_logger.LogTrace("{Prefix}: Closed", LogPrefix);
    }

    /// <summary>Loads (tensions) the tape cartridge and reads media parameters. Positions to the content partition.</summary>
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

        // RefreshMediaParams first: EnsureOnContentPartition needs HasInitiatorPartition
        // from m_mediaParams. No wrong caching here — m_onContentPartition is false.
        RefreshMediaParams();
        EnsureOnContentPartition();
        m_logger.LogTrace("{Prefix}: Media loaded", LogPrefix);
        return IsMediaLoaded;
    }

    /// <summary>Unloads (ejects) the tape cartridge and clears media parameters.</summary>
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
        InvalidateContentCache();
        m_logger.LogTrace("{Prefix}: Media unloaded", LogPrefix);
        return true;
    }

    /// <summary>Sets optimal media parameters (compression, ECC). Call after <see cref="ReloadMedia"/>.</summary>
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

    /// <summary>
    /// Formats (erases) the tape. Pass <paramref name="initiatorPartitionSize"/> &gt; 0 to create
    ///  an initiator partition for TOC storage. Reloads and prepares media afterward.
    /// </summary>
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

        // Backend format succeeded — clear any stale errors (e.g. from prior I/O failures)
        //  so the post-format step chain can proceed.
        ResetError();

        // Reload after format
        m_backend.LoadMedia();

        // RefreshMediaParams first: EnsureOnContentPartition needs HasInitiatorPartition
        //  from m_mediaParams. No wrong caching here — m_onContentPartition is false.
        if (WentOK)
            RefreshMediaParams();
        if (WentOK)
            EnsureOnContentPartition();
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

    /// <summary>Moves to the specified partition (and optional block). Refreshes media parameters for the new partition.</summary>
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

        // Track which partition we're on for content capacity caching
        if (partition != MediaPartition.Current)
            m_onContentPartition = (partition == MediaPartition.Content);

        // parameters may differ for another partition, e.g. Capacity -> refresh them
        RefreshMediaParams();
        m_logger.LogTrace("{Prefix}: Moved to partition {Partition}", LogPrefix, partition);
        return true;
    }

    #endregion

    #region *** Tapemark Operations ***

    /// <summary>Skips forward (positive) or backward (negative) by <paramref name="count"/> filemarks.</summary>
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

    /// <summary>Writes <paramref name="count"/> filemark(s) at the current position.</summary>
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

    /// <summary>Skips past <paramref name="count"/> sequential filemarks (used by <c>WithSeqFilemarks</c> organization).</summary>
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

    /// <summary>Skips forward (positive) or backward (negative) by <paramref name="count"/> setmarks.</summary>
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

    /// <summary>Writes <paramref name="count"/> setmark(s) at the current position.</summary>
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

    /// <summary>Writes a short dummy file (≥ <see cref="MinimumBlockSize"/> bytes) used as a gap before TOC marks.</summary>
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

    /// <summary>Rewinds the tape to the beginning of the current partition.</summary>
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

    /// <summary>Seeks to the end-of-data marker on the specified partition.</summary>
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

    /// <summary>Positions the tape to the specified logical block address. No-op if already there.</summary>
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

    /// <summary>Queries the current logical block address from the device. Returns −1 on failure.</summary>
    public long GetCurrentBlock()
    {
        if (!IsMediaLoaded)
            return -1;

        long position = m_backend.GetPosition();
        SyncErrorFrom(m_backend);
        return WentOK ? position : -1;
    }

    /// <summary>Queries the current partition from the device.</summary>
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
        {
            m_mediaParams = mediaParams;
            if (m_onContentPartition)
                CacheContentMediaParams(mediaParams);
        }
        return WentOK;
    }

    private void CacheContentMediaParams(in MediaParameters mediaParams)
    {
        m_cachedContentCapacity = mediaParams.Capacity;
        m_cachedContentRemaining = mediaParams.Remaining;
    }

    private void InvalidateContentCache()
    {
        m_cachedContentCapacity = -1;
        m_cachedContentRemaining = -1;
        m_onContentPartition = false;
    }

    /// <summary>
    /// Ensures the drive is on the Content partition and the capacity cache is populated.
    /// For multi-partition media, checks actual position first to avoid an unnecessary move.
    /// </summary>
    private void EnsureOnContentPartition()
    {
        if (HasInitiatorPartition)
        {
            if (!m_onContentPartition)
            {
                // Check actual position — after media load we're likely already on Content
                if (GetCurrentPartition() == MediaPartition.Content)
                {
                    m_onContentPartition = true;
                    RefreshMediaParams(); // re-read to populate the cache
                }
                else
                {
                    MoveToPartition(MediaPartition.Content); // move + refresh + cache
                }
            }
        }
        else
        {
            // Single-partition media is always Content — populate cache from current params
            m_onContentPartition = true;
            if (m_cachedContentCapacity < 0 && m_mediaParams != null)
                CacheContentMediaParams(m_mediaParams.Value);
        }
    }

    private void SetOptimalDriveParams()
    {
        if (!IsDriveOpen || m_driveParams == null)
            return;

        bool compression = m_driveParams.Value.SupportsCompression;
        bool ecc = m_driveParams.Value.SupportsEcc;
        bool padding = false; // m_driveParams.Value.SupportsPadding;
        bool reportSetmarks = m_driveParams.Value.SupportsSetmarks;
        uint eotZone = (uint)Math.Min(TapeNavigator.DefaultTOCCapacity(this), uint.MaxValue);
        if (!m_backend.SetDriveParameters(compression, ecc, padding, reportSetmarks, eotZone))
        {
            SyncErrorFrom(m_backend);
            ResetError(); // Ignore failure
        }
    }

    /// <summary>
    /// Overrides the drive's hardware compression flag for the duration of a backup/restore set.
    /// <para>Call with <see langword="false"/> before writing/reading a set whose compression mode is
    ///  <see cref="TapeCompression.Software"/> or <see cref="TapeCompression.None"/>; call with
    ///  <see langword="true"/> (or don't call at all) for <see cref="TapeCompression.Hardware"/>.</para>
    /// <para>This method is idempotent and failure-tolerant — a drive that does not support the toggle
    ///  is silently ignored, matching the pattern of <see cref="SetOptimalDriveParams"/>.</para>
    /// </summary>
    /// <param name="enabled"><see langword="true"/> to enable, <see langword="false"/> to disable HW compression.</param>
    internal void SetHardwareCompression(bool enabled)
    {
        if (!IsDriveOpen || m_driveParams == null)
            return;

        bool ecc = m_driveParams.Value.SupportsEcc;
        bool padding = false; // m_driveParams.Value.SupportsPadding;
        bool reportSetmarks = m_driveParams.Value.SupportsSetmarks;
        uint eotZone = (uint)Math.Min(TapeNavigator.DefaultTOCCapacity(this), uint.MaxValue);
        if (!m_backend.SetDriveParameters(enabled, ecc, padding, reportSetmarks, eotZone))
        {
            SyncErrorFrom(m_backend);
            ResetError(); // Ignore failure — drive may not support dynamic toggle
        }
    }

    private void SetOptimalMediaParams()
    {
        SetBlockSize(uint.Max(c_defaultBlockSize, DefaultBlockSize));

        // Re-apply a previously requested early-warning reserve (media-level setting, like block
        //  size). Failure-tolerant: a drive without any EW mechanism simply keeps EarlyWarning = 0.
        if (m_desiredEarlyWarning > 0)
        {
            if (!m_backend.SetEarlyWarning(m_desiredEarlyWarning))
            {
                SyncErrorFrom(m_backend);
                ResetError(); // ignore — best-effort re-application
            }
            RefreshMediaParams();
        }
    }

    #endregion
}
