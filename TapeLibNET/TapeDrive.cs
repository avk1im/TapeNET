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
    //  to query its capacity. Updated whenever EnsureMediaParams() is called while on Content.
    private long m_cachedContentCapacity = -1L;
    private long m_cachedContentRemaining = -1L;
    private bool m_onContentPartition; // tracked via MoveToPartition / EnsureOnContentPartition

    // Cached block size to accelerate BlockSize access; updated by EnsureMediaParams() and SetBlockSize()
    private uint m_cachedBlockSize = 0U;

    // Desired LOGICAL early-warning reserve, in bytes before EOM (0 = none). Preserved across
    //  media reload (a session preference like block size); cleared only in CloseDrive.
    private long m_desiredEarlyWarning = 0L;

    // Logical early-warning runtime state, mapped from the backend's physical EW/PEW + calibration.
    private bool m_physicalEwSeen = false;          // backend reported built-in EW this pass
    private long m_ewAnchorBlock = -1L;             // drive logical block where physical EW first fired
    private long m_ewZoneEntryBlock = -1L;          // immovable LBA where physical EW first fired (membership test)
    private long m_bytesAfterPhysicalEwCarry = 0L;  // bytes-after-EW frozen across block-size changes
    private long m_bytesSinceRemainingPoll = 0L;    // paces the ReportedRemaining poll (approx ok)
    // Writable headroom (estimate minus reserve) observed at the last poll. Paces the NEXT poll so the
    //  sampling rate tracks how little is actually left. long.MaxValue = not yet polled (first write
    //  after a reserve is set polls immediately to establish a real value).
    private long m_writableHeadroomAtLastPoll = long.MaxValue;

    // Calibrations loaded by the app (typically one per capacity bucket / media type). TapeDrive
    //  auto-selects the matching one into m_calibration. Not owned/persisted here.
    private readonly List<ITapeCalibration> m_calibrations = [];
    private ITapeCalibration? m_calibration = null;
    private bool m_isCalibrationMatched = false;

    // Effective EW mechanism selected by SetEarlyWarning (best available; see SelectEarlyWarningMechanism).
    private EarlyWarningMechanism m_ewMechanism = EarlyWarningMechanism.None;
    // A-priori (blind-guess) calibration synthesized from Capacity when no measured calibration matches,
    //  so a requested reserve is ALWAYS honored. Null when a real calibration is used or no reserve is set.
    private ITapeCalibration? m_aprioriCalibration = null;

    // Disposing
    private bool m_disposed = false;

    #endregion

    #region *** Private Constants ***

    private const int c_gapFileLength = 64;

    // Upper bound for the (device-querying) ReportedRemaining poll used by the pre-physical-EW logical
    //  EW check. The effective interval is scaled down from this ceiling to a fraction of the WRITABLE
    //  headroom still ahead of the reserve (see RemainingPollInterval), so the check also works on
    //  drives WITHOUT a physical EW and on media whose headroom is smaller than the ceiling -- there
    //  the curve poll is the ONLY way logical EW can fire.
    private const long c_ewRemainingPollIntervalMax = 64L * 1024 * 1024; // 64 MB

    // The poll must be fine-grained relative to the headroom it is watching shrink -- NOT relative to
    //  the reserve, which may be arbitrarily large compared to what is actually left (e.g. a 32 MiB TOC
    //  reserve on a 36 MiB cartridge leaves ~2 MiB of headroom). Sampling every quarter of the headroom
    //  bounds the overshoot to ~25% of it, at a cost of at most 4 queries per headroom-width of tape.
    //  Floored at the block size so it can never degenerate to polling on every write.
    //  Uses the headroom CACHED at the last poll, so this stays a pure arithmetic property: it is read
    //  on every write and must never query the device itself.
    private long RemainingPollInterval
    {
        get
        {
            // Guard the (pathological) case of a block size at/above the ceiling, which would
            //  make min > max and throw in Math.Clamp.
            long floor = Math.Clamp(Math.Max(1L, BlockSize), 1L, c_ewRemainingPollIntervalMax);
            return Math.Clamp(Math.Max(0L, m_writableHeadroomAtLastPoll) / 4L, floor, c_ewRemainingPollIntervalMax);
        }
    }

    #endregion // *** Private Constants ***

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

    #endregion // *** Constructors ***

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
    public bool IsMediaLoaded => IsDriveOpen && EnsureMediaParams() is not null;

    /// <summary>Drive hardware supports a separate initiator (TOC) partition.</summary>
    public bool SupportsInitiatorPartition => m_driveParams?.SupportsInitiatorPartition ?? false;

    /// <summary>Current media has an initiator partition created.</summary>
    public bool HasInitiatorPartition => EnsureMediaParams()?.HasInitiatorPartition ?? false;

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
    public uint BlockSize => m_cachedBlockSize > 0U ? m_cachedBlockSize
        : EnsureMediaParams()?.BlockSize ?? 0U;

    /// <summary>Current logical block address from the device.</summary>
    internal long BlockCounter => GetCurrentBlock();

    /// <summary>Total capacity of the current partition, in bytes.</summary>
    public long Capacity => EnsureMediaParams()?.Capacity ?? 0L;

    /// <summary>
    /// Capacity of the Content partition, cached from the last time media params were
    /// refreshed while on Content. Falls back to <see cref="Capacity"/> if not yet cached.
    /// </summary>
    public long ContentCapacity => m_cachedContentCapacity >= 0 ? m_cachedContentCapacity : Capacity;

    /// <summary>
    /// Quantity (3) — the RAW driver-reported remaining for the CURRENT partition (refreshes media
    /// params). Optimistic on real hardware: it overshoots the truth and floors above zero at hard EOM.
    /// Use <see cref="EstimateActualRemaining"/> for capacity decisions. Returns 0 on failure.
    /// </summary>
    public long GetReportedCurrentPartitionRemaining() => EnsureMediaParams()?.Remaining ?? 0L;

    /// <summary>
    /// Quantity (3) for the Content partition, cached from the last time media params were refreshed
    /// while on Content. If currently on Content, refreshes first. Still the RAW driver figure.
    /// </summary>
    public long GetReportedContentRemaining()
    {
        if (m_onContentPartition)
        {
            // On content — refresh to get the latest value and cache it
            return GetReportedCurrentPartitionRemaining();
        }

        // On another partition — return the cached content remaining
        return m_cachedContentRemaining >= 0 ? m_cachedContentRemaining : 0;
    }

    /// <summary>
    /// Quantity (6) — the authoritative ESTIMATE of bytes still actually writable on the Content
    /// partition: the figure the rest of the library and the apps should consume. Calibrated when a
    /// calibration (measured or a-priori) is available, otherwise the raw driver value.
    /// <para>Delegates to <see cref="EstimateActualRemaining"/> (throttled/cached per its contract).</para>
    /// </summary>
    public long EstimatedContentRemaining => EstimateActualRemaining();

    /// <summary>
    /// Quantity (3) — property form of <see cref="GetReportedContentRemaining"/>, kept for diagnostics,
    /// calibration, and "driver says vs. we estimate" UI display. Prefer
    /// <see cref="EstimatedContentRemaining"/> for capacity decisions.
    /// </summary>
    public long ReportedContentRemaining => GetReportedContentRemaining();

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
    /// Positive value if the drive is a true LTO drive; 0 if pre-LTO SCSI-adressable drive; -1 otherwise
    /// </summary>
    public int LtoGeneration => m_backend is TapeDriveWin32Backend wbe? wbe.LtoGeneration
                : m_backend is RemoteTapeDriveBackend rbe? rbe.LtoGeneration
                : -1;

    /// <summary>
    /// Desired LOGICAL early-warning reserve, in bytes before physical EOM (0 = none). TapeDrive maps
    /// the backend's physical EW/PEW and driver ReportedRemaining — through the active
    /// <see cref="Calibration"/> — onto this logical threshold, so <see cref="WriteDirect"/> raises
    /// <c>ew</c> (and the sticky <see cref="IsEarlyWarning"/>) ~this many bytes before EOM.
    /// Precision reflects <see cref="EarlyWarningMechanism"/>. To set, use <see cref="SetEarlyWarning(long)"/>.
    /// Cleared in <see cref="CloseDrive"/>.
    /// </summary>
    public long EarlyWarning => m_desiredEarlyWarning;

    /// <summary>How the logical <see cref="EarlyWarning"/> is currently realized (best available mechanism).</summary>
    public EarlyWarningMechanism EarlyWarningMechanism => m_ewMechanism;

    /// <summary>The calibration actually driving EW mapping / remaining estimation: the matching user
    ///  calibration if any, else the synthesized a-priori baseline, else null.</summary>
    private ITapeCalibration? EffectiveCalibration => m_calibration ?? m_aprioriCalibration;

    /// <summary>
    /// A sticky flag set after <see cref="WriteDirect"/> sensed an early-warning crossing (data was written;
    /// wrap up and write the TOC) — as opposed to a hard end-of-media (reported as a hard error).
    /// <para>Reset in <see cref="UnloadMedia"/>, <see cref="CloseDrive"/>, or when <see cref="WriteDirect"/> returned no early warning.</para>
    /// </summary>
    public bool IsEarlyWarning { get; private set; } = false;

    /// <summary>Sticky flag set after <see cref="WriteDirect"/> sensed a Programmable-Early-Warning crossing
    /// (if supported, e.g. on LTO-5+). Reset on unload/close, or when <see cref="WriteDirect"/> returned no programmable early warning.
    /// <para>Marked <c>protected</c> since used internally to support logical implementation of <see cref="IsEarlyWarning"/></para>
    /// </summary>
    internal bool IsProgrammableEarlyWarning { get; private set; } = false;

    /// <summary>Sticky flag: the BACKEND's physical early warning has fired this pass (the landmark
    /// anchoring the precise tail estimate). Distinct from the logical <see cref="IsEarlyWarning"/>,
    /// which maps this plus the calibration onto the caller's requested reserve.</summary>
    internal bool IsPhysicalEarlyWarningSeen => m_physicalEwSeen;

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
        if (EnsureMediaParams() is MediaParameters mediaParams)
            sb.Append('\n').Append(mediaParams.ToString());
        else
            sb.Append("<not filled>");
        return sb.ToString();
    }

    #endregion // *** Properties ***

    #region *** Disposing ***

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

    #endregion // *** Disposing ***

    #region *** Direct Read/Write ***

    /// <summary>Writes raw blocks to tape. Returns bytes written;
    /// sets <paramref name="tapemark"/>/<paramref name="ew"/>/<paramref name="eom"/> on boundary conditions.</summary>
    public int WriteDirect(byte[] buffer, int offset, int count,
        out bool tapemark, out bool ew, out bool eom)
    {
        m_backend.CheckForRW(buffer, offset, count);
        int blocksToWrite = count / (int)BlockSize;
        int toWrite = blocksToWrite * (int)BlockSize;
        tapemark = false;
        ew = false;
        eom = false;
        if (toWrite == 0)
            return 0;

        // Logical EW is evaluated AFTER the write, so on its own it can only ever report
        //  "you have already crossed the line". That is harmless while a single write is small
        //  relative to the reserve, but a write comparable to (or larger than) the reserve can
        //  consume the whole reserve -- and the room meant for the TOC -- in one go. Guard that
        //  case by clamping the write to what still fits ahead of the reserve.
        bool ewClamped = ClampWriteToEarlyWarning(ref toWrite);
        if (toWrite == 0)
        {
            // Nothing fits ahead of the reserve: report EW without writing so the caller
            //  wraps up the set (the packer rolls back its uncommitted tail).
            ew = m_desiredEarlyWarning > 0L;
            IsEarlyWarning = true;
            return 0;
        }

        m_IoTimer.Restart();
        int written = m_backend.Write(buffer, offset, toWrite,
            out tapemark, out bool pew, out bool physicalEw, out eom);
        m_IoTimer.Stop();
        IoTimeCounterUs += m_IoTimer.ElapsedMicroseconds;
        SyncErrorFrom(m_backend);

        InvalidateMediaParams(keepBlockSize: true); // force refresh on next GetRemainingCapacity() to see the new position

        // Track the physical early-warning landmark at the drive's AUTHORITATIVE block position.
        //  PEW stays an internal detail (phase-2 anchor); EW anchor drives the precise tail estimate.
        //  We ALWAYS track the physical EW/PEW landmark: cheap (anchor set once), and required by
        //  EstimateActualRemaining()'s precise tail path even when NO logical reserve was requested

        IsProgrammableEarlyWarning = pew;
        if (physicalEw && !m_physicalEwSeen)
        {
            m_physicalEwSeen = true;
            m_ewAnchorBlock = GetCurrentBlock();
            m_ewZoneEntryBlock = m_ewAnchorBlock; // ← the fixed zone start (m_ewAnchorBlock may later re-anchor; this does not)
            m_bytesAfterPhysicalEwCarry = 0L;
            m_logger.LogTrace("{Prefix}: Physical early warning at block {Block}", LogPrefix, m_ewAnchorBlock);
        }

        // Map physical EW/PEW + calibrated ReportedRemaining onto the caller's LOGICAL early warning.
        //  With NO reserve requested this surfaces the drive's physical EW 1:1 (v1.0 behavior, and exactly
        //  what a calibration run needs to capture the EW landmark). The costly ReportedRemaining poll lives
        //  inside EvaluateLogicalEarlyWarning and is gated + throttled, so calling it per write is cheap.
        bool logicalEw = EvaluateLogicalEarlyWarning(written, physicalEw) || ewClamped;
        if (logicalEw && !IsEarlyWarning)
            m_logger.LogInformation("{Prefix}: WriteDirect crossed logical early-warning boundary", LogPrefix);
        IsEarlyWarning = logicalEw;
        ew = m_desiredEarlyWarning > 0L && logicalEw; // only report to caller if desired


        if (WentBad)
        {
            if (tapemark)
                LogErrorAsInfo("WriteDirect encountered tapemark");
            if (eom)
                LogErrorAsInfo("WriteDirect encountered EOM");
            if (!tapemark && !ew && !eom)
                LogErrorAsDebug("WriteDirect encountered error");
        }

        m_byteCounter += written;
        return written;
    }

    /// <summary>Writes raw blocks to tape (parameter-less version). Returns bytes written; ignores tapemark/ew/eom.</summary>
    public int WriteDirect(byte[] buffer, int offset, int count)
        => WriteDirect(buffer, offset, count, out _, out _, out _);

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
                LogErrorAsDebug("Read encountered error");
        }

        m_byteCounter += read;
        return read;
    }

    /// <summary>
    /// Parameter-less version of <see cref="ReadDirect(byte[], int, int, out bool, out bool)"/>. Returns bytes read; ignores tapemark/eof. 
    /// </summary>
    public int ReadDirect(byte[] buffer, int offset, int count)
        => ReadDirect(buffer, offset, count, out _, out _);

    internal void CheckForRW([CallerMemberName] string methodName = "") => m_backend.CheckForRW(methodName);
    internal void CheckForRW(byte[] buffer, int offset, int count, [CallerMemberName] string methodName = "") =>
        m_backend.CheckForRW(buffer, offset, count, methodName);

    #endregion // *** Direct Read/Write ***

    #region *** Early Warning ***

    /// <summary>Resets per-session early-warning tracking. Called on (un)load and by the calibrator.</summary>
    internal void ResetEarlyWarningRuntime()
    {
        IsEarlyWarning = false;
        IsProgrammableEarlyWarning = false;
        m_physicalEwSeen = false;
        m_ewAnchorBlock = -1L;
        m_ewZoneEntryBlock = -1L;   
        m_bytesAfterPhysicalEwCarry = 0L;
        m_bytesSinceRemainingPoll = 0L;
        m_writableHeadroomAtLastPoll = long.MaxValue;
    }

    /// <summary>
    /// Re-evaluates EW state after a tape REPOSITION, since early warning is a property of physical
    /// position, not a permanent latch. The logical sticky is derived per-write, so it is always dropped
    /// here — it re-fires on the next write if we are still in the tail (e.g. a file-write retry a few
    /// blocks back), and correctly stays clear once the caller has repositioned out of the zone (e.g. a
    /// rewind to overwrite earlier sets). The PHYSICAL anchor is kept unless we have moved before the
    /// zone-entry landmark, because losing it would let a later re-detection re-anchor DEEPER and
    /// over-estimate remaining. Only meaningful on the content partition.
    /// </summary>
    /// <param name="newBlock">The content-partition logical block the tape was repositioned to.</param>
    private void ReevaluateEarlyWarningAfterReposition(long newBlock)
    {
        // Drop the derived logical sticky on ANY reposition; it is recomputed on the next write.
        IsEarlyWarning = false;
        IsProgrammableEarlyWarning = false;

        // Physical zone membership: only when we have moved BEFORE where physical EW first fired do we
        //  leave the zone and shed the anchor + accounting. A within-zone move (retry) keeps them, so
        //  BytesAfterPhysicalEw() recomputes correctly from the new, earlier-but-still-in-zone position.
        //  The zone-entry LBA is immovable across block-size changes, so this comparison is robust.
        if (m_physicalEwSeen && m_ewZoneEntryBlock >= 0L && newBlock < m_ewZoneEntryBlock)
        {
            m_physicalEwSeen = false;
            m_ewAnchorBlock = -1L;
            m_ewZoneEntryBlock = -1L;
            m_bytesAfterPhysicalEwCarry = 0L;

            // Left the zone → reset the pre-EW curve-poll pacing to a clean state. (A within-zone move
            //  deliberately KEEPS the cached headroom so ClampWriteToEarlyWarning stays armed for the
            //  very next write, and keeps the poll counter high so that write re-polls immediately.)
            m_bytesSinceRemainingPoll = 0L;
            m_writableHeadroomAtLastPoll = long.MaxValue;
        }
    }

    /// <summary>
    /// Requests an early-warning reserve of <paramref name="bytesBeforeEom"/> bytes before EOM.
    /// ALWAYS honored while media is loaded: TapeDrive selects the best available mechanism —
    /// a matching calibration, the drive's physical EW/PEW, or an a-priori estimate — so the
    /// caller can always rely on EW to reserve room for the TOC. Read <see cref="EarlyWarning"/>
    /// back for the requested value and <see cref="EarlyWarningMechanism"/> for the achieved precision.
    /// Pass 0 to disable.
    /// </summary>
    /// <param name="bytesBeforeEom">Desired reserve in bytes before EOM (0 = none).</param>
    /// <returns>
    /// True if the requested reserve is honored (<c>true</c> unless capacity is unknown and the backend has no physical EW).
    /// </returns>
    public bool SetEarlyWarning(long bytesBeforeEom)
    {
        if (!IsMediaLoaded)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }
        if (bytesBeforeEom < 0L)
            bytesBeforeEom = 0L;

        m_desiredEarlyWarning = bytesBeforeEom;

        // Ask the backend to surface its physical EW too (best-effort, non-binding): when present it
        //  feeds the precise calibrated-tail path and serves as a safety backstop. A backend without
        //  any EW mechanism simply ignores this — we never fail the call on it.
        m_backend.ReportEarlyWarning(true); // we use backend EW not only for logical EW
                                            // but also for more accurate remaining capacity tracking!
        ResetError(); // ok if backend doesn't support EW

        // Pick the best available logical-EW mechanism. Always succeeds: with neither backend EW nor a
        //  calibration we fall back to an a-priori estimate, so the reserve is honored regardless.
        SelectEarlyWarningMechanism();

        m_logger.LogTrace("{Prefix}: Early warning set to {Req} bytes (mechanism {Mech})",
            LogPrefix, bytesBeforeEom, m_ewMechanism);

        return m_desiredEarlyWarning == 0L
            || EarlyWarningMechanism != EarlyWarningMechanism.None; // Failure is unlikely -- only happens when
                                                                    //  capacity is unknown and the backend has no physical EW
    }

    /// <summary>
    /// Selects the best available logical-EW mechanism given the loaded calibration, backend
    /// capabilities, and media capacity. Synthesizes an a-priori baseline when no measured calibration
    /// matches, so a reserve is always enforceable as soon as one is requested via
    /// <see cref="SetEarlyWarning"/>. Runs independently of whether a reserve has actually been
    /// requested yet, so <c>EarlyWarningMechanism</c> (and the UI's "Estimation by") reflects a
    /// matched/auto-loaded calibration immediately, not only once a reserve is later set.
    /// </summary>
    private void SelectEarlyWarningMechanism()
    {
        m_aprioriCalibration = null;

        if (!IsMediaLoaded)
        {
            m_ewMechanism = EarlyWarningMechanism.None;
            return;
        }

        // Best: a matching, measured calibration.
        if (m_calibration is not null)
        {
            m_ewMechanism = EarlyWarningMechanism.Calibrated;
            return;
        }

        EarlyWarningMechanism backendMech = m_backend.EarlyWarningMechanism;
        bool backendHasEw = backendMech is EarlyWarningMechanism.ProgrammableEarlyWarning
                                         or EarlyWarningMechanism.HardwareEarlyWarning;

        // A pre-LTO / LTO-1..3 physical EW fires far too late to be the real mechanism — the a-priori curve is.
        bool ewIsUseful = backendHasEw && LtoGeneration >= 4;
        long capacity = Capacity;
        if (capacity > 0L)
        {
            if (m_backend.ReportsExactRemaining)
            {
                // Honest backend (un-emulated virtual): identity calibration — no margin, so the reserve
                // fires exactly at TOC size and "space remaining" is the truth. This is what lets the
                // multivolume tests tune to exact capacities, immune to a-priori constant changes.
                m_aprioriCalibration = TapeCalibration.Ideal(DriveProfileKey, capacity);
                m_ewMechanism = EarlyWarningMechanism.Uncalibrated; // FIXME: Consider introducing EarlyWarningMechanism.Exact
            }
            else
            {
                // Synthesize an a-priori baseline so the reserve is honored even with no measured data.
                //  A backend physical EW, when present, opportunistically sharpens the tail estimate —
                //  hence the higher (Hardware/Programmable) precision label in that case.
                m_aprioriCalibration = TapeCalibration.Apriori(DriveProfileKey, capacity, LtoGeneration);
                m_ewMechanism = ewIsUseful ? backendMech : EarlyWarningMechanism.Uncalibrated;
            }
        }
        else
        {
            // Capacity unknown: rely solely on the backend's physical EW if it has one.
            m_ewMechanism = ewIsUseful ? backendMech : EarlyWarningMechanism.None;
        }
    }

    /// <summary>
    /// The pure estimate logic shared by <see cref="EstimateActualRemaining"/> and
    /// <see cref="EvaluateLogicalEarlyWarning"/>, given an already-fetched <paramref name="reported"/>
    /// value — so callers can control (and throttle) the device poll themselves.
    /// <para>
    /// Before physical EW → the calibrated <c>ReportedRemaining → ActualRemaining</c> curve.
    /// After physical EW → the precise, self-anchored per-cartridge byte-count from the EW landmark
    /// (<c>EwToEomDistance − bytesSinceEw</c>). We TRUST the byte-count in the tail rather than combine it
    /// with the curve: the curve is unreliable there — on collapse drives (LTO-0..3) it has already
    /// dropped to ~0 while real capacity remains, so a min() would wrongly abandon the writable tail; the
    /// byte-count can never OVER-estimate (measured landmarks are exact; a-priori landmarks are set ≤ the
    /// real runway), so it never risks an overrun. <paramref name="reported"/> is IGNORED post-EW.
    /// </para>
    /// </summary>
    private long EstimateActualRemainingCore(ITapeCalibration cal, long reported)
    {
        if (m_physicalEwSeen)
            return Math.Max(0L, cal.EwToEomDistance - BytesAfterPhysicalEw());

        return cal.TranslateReportedToActual(reported);
    }

    /// <summary>
    /// Maps the backend's physical early warning + calibrated ReportedRemaining onto the caller's
    /// logical <see cref="EarlyWarning"/> reserve. With no reserve requested or no matching
    /// calibration it surfaces the physical EW 1:1 — which is exactly what a calibration RUN sees,
    /// since the calibrator loads no calibration.
    /// </summary>
    private bool EvaluateLogicalEarlyWarning(int written, bool physicalEw)
    {
        // Sticky: once the logical EW fires, it stays fired until media reload / reset.
        if (IsEarlyWarning)
            return true;

        // No reserve requested → surface the drive's physical EW 1:1 as a safety backstop (legacy).
        if (m_desiredEarlyWarning <= 0L)
            return physicalEw;

        ITapeCalibration? cal = EffectiveCalibration;
        if (cal is null)
            return physicalEw;   // capacity-unknown fallback: physical EW only

        // Precise tail regime: after physical EW, byte-count down from the (measured or a-priori)
        //  EW→EOM distance. No Remaining query needed. Refresh the pacing hint so ClampWriteToEarlyWarning
        //  stays correct in the post-physical-EW / pre-logical-EW window (where a stale, large headroom
        //  would otherwise suppress clamping of a big final write near the reserve).
        if (m_physicalEwSeen)
        {
            long est = EstimateActualRemainingCore(cal, 0L /* reported ignored post-EW */);
            m_writableHeadroomAtLastPoll = est - m_desiredEarlyWarning;
            return est <= m_desiredEarlyWarning;
        }

        // Before physical EW: consult the curve on ReportedRemaining, throttling the costly query,
        //  while still honoring a physical-EW backstop between polls. On a drive with no physical EW
        //  this poll is the ONLY path that can raise logical EW, hence the reserve-relative interval.
        m_bytesSinceRemainingPoll += written;
        if (m_bytesSinceRemainingPoll < RemainingPollInterval)
            return physicalEw;
        m_bytesSinceRemainingPoll = 0L;

        long est2 = EstimateActualRemainingCore(cal, GetReportedContentRemaining());
        m_writableHeadroomAtLastPoll = est2 - m_desiredEarlyWarning; // paces the next poll
        return est2 <= m_desiredEarlyWarning || physicalEw;
    }

    /// <summary>
    /// Clamps a pending write so it cannot overrun the requested early-warning reserve in a single
    /// call. Logical EW is necessarily evaluated AFTER a write, so a write that is large relative to
    /// the reserve could otherwise consume the reserve -- the room set aside for the TOC -- before
    /// anyone gets a chance to react. This is the pre-write counterpart to
    /// <see cref="EvaluateLogicalEarlyWarning"/>.
    /// <para>
    /// Deliberately cheap and self-limiting: it only engages once the write is big enough to matter
    /// relative to the headroom still ahead of the reserve, so on real drives -- where a 256 KiB write
    /// sits against gigabytes of headroom -- it never fires and costs a single comparison. It becomes
    /// active exactly in the coarse-granularity regime (tiny emulated media, a nearly-full cartridge,
    /// or a genuinely huge write) where the post-hoc signal is too late to be useful.
    /// </para>
    /// </summary>
    /// <param name="toWrite">Block-aligned byte count to write; reduced in place if it would overrun.</param>
    /// <returns>True if the write was clamped, i.e. the reserve boundary is being reached now.</returns>
    private bool ClampWriteToEarlyWarning(ref int toWrite)
    {
        if (m_desiredEarlyWarning <= 0L || IsEarlyWarning)
            return false;

        // Only worth checking once a single write could eat a meaningful part of the headroom still
        //  ahead of the reserve. Gauged against the HEADROOM (not the reserve, which may dwarf what is
        //  actually left), using the value cached by the EW poll so this stays off the hot path: on a
        //  real drive a 256 KiB write against GB of headroom exits on one comparison.
        if (m_writableHeadroomAtLastPoll != long.MaxValue
            && toWrite < m_writableHeadroomAtLastPoll / 4L)
            return false;

        long writable = EstimateActualRemaining() - m_desiredEarlyWarning;
        m_writableHeadroomAtLastPoll = writable; // refresh the pacing hint; we just paid for the query
        if (writable >= toWrite)
            return false;

        uint blockSize = BlockSize;
        long aligned = writable > 0L && blockSize > 0
            ? writable - (writable % blockSize)
            : 0L;

        m_logger.LogInformation(
            "{Prefix}: Clamping write of {ToWrite} B to {Aligned} B to preserve the {Reserve} B early-warning reserve",
            LogPrefix, toWrite, aligned, m_desiredEarlyWarning);

        toWrite = (int)Math.Min(aligned, toWrite);
        return true;
    }

    /// <summary>
    /// Physical-tape bytes written since the built-in early warning fired, measured from the DRIVE's
    /// authoritative logical block position (blocks × block size) and carried correctly across any
    /// mid-stream block-size change. Returns 0 before physical EW.
    /// </summary>
    private long BytesAfterPhysicalEw()
    {
        if (m_ewAnchorBlock < 0L)
            return 0L;
        long cur = GetCurrentBlock();
        long spaced = cur > m_ewAnchorBlock ? (cur - m_ewAnchorBlock) * (long)BlockSize : 0L;
        return m_bytesAfterPhysicalEwCarry + spaced;
    }

    #endregion // *** Early Warning ***

    #region *** Calibration ***

    /// <summary>The active (auto-selected, matching) calibration, or null.</summary>
    public ITapeCalibration? Calibration => m_calibration;

    /// <summary>All loaded calibration profiles (read-only). TapeDrive auto-selects the matching one.</summary>
    public IReadOnlyList<ITapeCalibration> Calibrations => m_calibrations;

    /// <summary>True when a loaded calibration matches the current drive+media profile.</summary>
    public bool IsCalibrationMatched => m_isCalibrationMatched;

    /// <summary>
    /// Adds a calibration profile. Several may be loaded (e.g. one per cartridge capacity bucket);
    /// TapeDrive auto-selects the one matching the loaded media. A new entry supersedes an existing
    /// one with the same <see cref="ITapeCalibration.ProfileKey"/>. Returns whether it matches now.
    /// </summary>
    public bool AddCalibration(ITapeCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        m_calibrations.RemoveAll(c => string.Equals(c.ProfileKey, calibration.ProfileKey, StringComparison.Ordinal));
        m_calibrations.Add(calibration);
        SelectCalibration();
        m_logger.LogTrace("{Prefix}: Calibration added (profile '{Key}', {Count} loaded, matched {Matched})",
            LogPrefix, calibration.ProfileKey, m_calibrations.Count, m_isCalibrationMatched);
        return m_isCalibrationMatched;
    }

    /// <summary>Removes a previously added calibration and re-selects the best match.</summary>
    public void RemoveCalibration(ITapeCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        m_calibrations.Remove(calibration);
        SelectCalibration();
    }

    /// <summary>Removes all loaded calibrations.</summary>
    public void RemoveAllCalibrations()
    {
        m_calibrations.Clear();
        SelectCalibration();
    }

    /// <summary>Convenience: replaces all loaded calibrations with the one supplied (null clears). Returns match.</summary>
    public bool SetCalibration(ITapeCalibration? calibration)
    {
        m_calibrations.Clear();
        if (calibration is not null)
            m_calibrations.Add(calibration);
        SelectCalibration();
        return m_isCalibrationMatched;
    }

    /// <summary>Selects the loaded calibration whose profile key matches the current media, else none.</summary>
    private void SelectCalibration()
    {
        string key = DriveProfileKey;
        m_calibration = null;
        foreach (ITapeCalibration c in m_calibrations)
            if (string.Equals(c.ProfileKey, key, StringComparison.Ordinal))
            {
                m_calibration = c;
                break;
            }

        m_isCalibrationMatched = m_calibration is not null;
        SelectEarlyWarningMechanism(); // up/downgrade the mechanism depending on new m_calibration
    }

    /// <summary>
    /// Best available estimate of the bytes still actually writable, in bytes.
    /// <para>
    /// No calibration → raw driver <c>Remaining</c>. Calibration present but EW not yet crossed →
    /// the calibrated <c>ReportedRemaining → ActualRemaining</c> curve. After EW has fired this
    /// session → precise per-cartridge byte-counting from the EW landmark (the curve is unreliable
    /// in the tail). See the calibration design notes.
    /// </para>
    /// </summary>
    public long EstimateActualRemaining()
    {
        long reported = GetReportedContentRemaining();
        if (reported < 0L)
            return 0L;

        ITapeCalibration? cal = EffectiveCalibration;
        if (cal is null)
            return reported;

        return EstimateActualRemainingCore(cal, reported);
    }

    #endregion // *** Calibration ***

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

        if (!ReloadDriveCaps())
        {
            LogErrorAsDebug("Failed to pre-fill drive parameters");
            return false;
        }
        SetOptimalDriveParams();
        if (!ReloadDriveCaps())
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

        ResetEarlyWarningRuntime();
        m_desiredEarlyWarning = 0L;
        SelectEarlyWarningMechanism();   // → None (reserve cleared / media gone)

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

        InvalidateMediaParams(keepBlockSize: false);
        EnsureOnContentPartition(); // CHECKME: do we indeed need to force to Content partition right away? or is it ok to fill the content param cache later?

        ReloadDriveCaps(); // refresh drive caps after media load, as some drives (e.g. virtual ones) change their capabilities depending on the media loaded

        ResetEarlyWarningRuntime();

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

        InvalidateMediaParams(keepBlockSize: false);
        ResetEarlyWarningRuntime();

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

        SelectCalibration();

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

        // ReloadMediaParams() first. No wrong caching here — m_onContentPartition is false.
        if (WentOK)
            ReloadMediaParams();
        if (WentOK)
            EnsureOnContentPartition();
        if (WentOK)
            PrepareMedia();

        ResetEarlyWarningRuntime();

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

        // Map to the correct values
        if (size == 0)
            size = DefaultBlockSize;
        else if (size > MaximumBlockSize)
            size = MaximumBlockSize;
        else if (size < MinimumBlockSize)
            size = MinimumBlockSize;
        size = Math.Min(size, int.MaxValue);

        if (BlockSize == size)
            return true;

        // If we're past physical EW, freeze bytes-after-EW in the OLD block-size frame, then
        //  re-anchor at the current block so subsequent counting uses the NEW block size.
        if (m_ewAnchorBlock >= 0L)
        {
            long cur = GetCurrentBlock();
            if (cur > m_ewAnchorBlock)
                m_bytesAfterPhysicalEwCarry += (cur - m_ewAnchorBlock) * BlockSize;
            m_ewAnchorBlock = cur;
        }

        if (!m_backend.SetBlockSize(size))
        {
            SyncErrorFrom(m_backend);
            LogErrorAsDebug("Failed to set block size");
            return false;
        }

        InvalidateMediaParams(keepBlockSize: true);
        m_cachedBlockSize = size;

        m_logger.LogTrace("{Prefix}: Block size set to {Size}", LogPrefix, BlockSize);
        return WentOK;
    }

    #endregion // *** Drive & Media Operations ***

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
        InvalidateMediaParams(keepBlockSize: false);

        if (m_onContentPartition)
        {
            CacheContentMediaParams(EnsureMediaParams()); // refresh content capacity cache asap
            ReevaluateEarlyWarningAfterReposition(block);
        }

        m_logger.LogTrace("{Prefix}: Moved to partition {Partition}", LogPrefix, partition);
        return true;
    }

    #endregion // *** Partition Operations ***

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

        OnPositionChanged(backwards: count < 0);

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

        OnPositionChanged(backwards: count < 0);

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

        OnPositionChanged(backwards: count < 0);

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
        int result = WriteDirect(buffer, 0, length);
        SetBlockSize(blockSize);

        if (WentOK && result == length)
            m_logger.LogTrace("{Prefix}: Wrote gap file: {Bytes} bytes", LogPrefix, length);
        else
            LogErrorAsDebug("Failed to write gap file");
        return WentOK && result == length;
    }

    #endregion // *** Tapemark Operations ***

    #region *** Tape Moving & Positioning ***

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

        OnPositionChanged(backwards: true);

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

        OnPositionChanged(backwards: false);

        m_logger.LogTrace("{Prefix}: Fast forwarded to end", LogPrefix);
        return true;
    }

    /// <summary>Positions the tape to the specified logical block address. No-op if already there.</summary>
    public bool MoveToBlock(long block)
    {
        if (!IsMediaLoaded)
            return false;

        long currBlock = BlockCounter;

        if (block == currBlock)
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

        OnPositionChanged(backwards: block < currBlock, toBlock: block);

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

    #endregion // *** Tape Moving & Positioning ***

    #region *** Private Helpers ***

    private bool ReloadDriveCaps()
    {
        m_backend.FillDriveCapabilities(out DriveCapabilities driveParams);
        SyncErrorFrom(m_backend);
        if (WentOK)
            m_driveParams = driveParams;
        return WentOK;
    }

    private MediaParameters? EnsureMediaParams()
    {
        if (m_mediaParams is not null)
            return m_mediaParams;

        m_backend.FillMediaParameters(out MediaParameters mediaParams);
        // Do NOT SyncErrorFrom(m_backend); -- not to mask potential existing error from a prior operation

        m_mediaParams = mediaParams;
        m_cachedBlockSize = mediaParams.BlockSize;
        if (m_onContentPartition)
            CacheContentMediaParams(mediaParams);
        return m_mediaParams;
    }

    private void InvalidateMediaParams(bool keepBlockSize)
    {
        m_mediaParams = null;
        if (!keepBlockSize)
            m_cachedBlockSize = 0U;
    }

    private void ReloadMediaParams()
    {
        InvalidateMediaParams(keepBlockSize: false);
        EnsureMediaParams();
    }

    private void CacheContentMediaParams(in MediaParameters? mediaParams)
    {
        if (mediaParams is not null)
        {
            m_cachedContentCapacity = mediaParams.Value.Capacity;
            m_cachedContentRemaining = mediaParams.Value.Remaining;
        }
    }

    private void InvalidateContentCache()
    {
        m_cachedContentCapacity = -1;
        m_cachedContentRemaining = -1;
        m_onContentPartition = false;
    }

    private void OnPositionChanged(bool backwards, long toBlock = -1)
    {
        InvalidateMediaParams(keepBlockSize: true); // position changed ⇒ Remaining must be re-read
        if (m_onContentPartition && backwards) // we moved backwards ⇒ might've left the EW zone!
        {
            if (toBlock < 0)
                toBlock = BlockCounter;
            ReevaluateEarlyWarningAfterReposition(toBlock);
        }
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
                    m_onContentPartition = true;
                else
                    MoveToPartition(MediaPartition.Content); // move + refresh + cache
            }
        }
        else
        {
            // Single-partition media is always Content — populate cache from current params
            m_onContentPartition = true;
            if (m_cachedContentCapacity < 0 && EnsureMediaParams() is MediaParameters mediaParams)
                CacheContentMediaParams(mediaParams);
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
        SetBlockSize(DefaultBlockSize);

        // Ask the backend to surface its physical EW too (best-effort, non-binding): when present it
        //  feeds the precise calibrated-tail path and serves as a safety backstop. A backend without
        //  any EW mechanism simply ignores this — we never fail the call on it.
        m_backend.ReportEarlyWarning(true); // we use backend EW not only for logical EW
                                            // but also for more accurate remaining capacity tracking!
        ResetError(); // ok if backend doesn't support EW
    }

    #endregion // *** Private Helpers ***

} // class TapeDrive
