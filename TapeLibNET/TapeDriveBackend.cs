using System;
using System.IO;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using Windows.Win32.Foundation;

namespace TapeLibNET;

/// <summary>
/// Drive capabilities and parameters (abstracted from TAPE_GET_DRIVE_PARAMETERS).
/// Determines which <see cref="TapeNavigator"/> subclass is instantiated.
/// </summary>
public readonly record struct DriveCapabilities(
    uint MinimumBlockSize,
    uint MaximumBlockSize,
    uint DefaultBlockSize,
    bool SupportsCompression,
    bool SupportsEcc,
    bool SupportsPadding,
    bool SupportsSetmarks,
    bool SupportsSeqFilemarks,
    bool SupportsInitiatorPartition
);

/// <summary>
/// Current media parameters (abstracted from TAPE_GET_MEDIA_PARAMETERS).
/// Refreshed after media load, format, partition switch, or block-size change.
/// </summary>
public readonly record struct MediaParameters(
    long Capacity,
    long Remaining,
    uint BlockSize,
    bool HasInitiatorPartition,
    bool WriteProtected
)
{
    /// <summary>
    /// Effective early-warning reserve currently in force, in bytes before physical EOM
    /// (0 = none/unknown). Declared as a non-positional, init-only property so existing
    /// positional <c>new MediaParameters(...)</c> calls in every backend keep compiling
    /// unchanged; backends that support early warning populate it in
    /// <see cref="TapeDriveBackend.FillMediaParameters"/>.
    /// </summary>
    public long EarlyWarning { get; init; }
}

/// <summary>
/// Logical tape partition identifier. Maps to the Win32 partition numbers.
/// <see cref="Initiator"/> holds the TOC in the <c>WithPartitions</c> organization;
/// <see cref="Content"/> holds backup-set data.
/// </summary>
public enum MediaPartition
{
    /// <summary>"For this op, remain in current partition" (0 specified in Win32)</summary>
    Current = 0,
    /// <summary>Optional small partition for TOC/metadata (partition 2 in Win32).</summary>
    Initiator = 2,
    /// <summary>Main data partition (partition 1 in Win32).</summary>
    Content = 1
}

/// <summary>
/// Abstract base class for tape drive backends (physical or virtual).
/// Inherits error handling and logging from ErrorManageableBase.
/// </summary>
public abstract class TapeDriveBackend : ErrorManageableBase, IDisposable
{
    #region *** Constructor ***

#pragma warning disable IDE0290 // "Use primary constructor" - primary cannot be protected
    protected TapeDriveBackend(ILoggerFactory loggerFactory)
#pragma warning restore IDE0290 // Use primary constructor
        : base(loggerFactory.CreateLogger<TapeDriveBackend>())
    {
        LoggerFactory = loggerFactory;
    }

    #endregion

    #region *** Logging ***

    protected override string LogPrefix => $"Backend[{DeviceName}]";
    public ILoggerFactory LoggerFactory { get; }

    #endregion

    #region *** Timeout ***

    /// <summary>
    /// Maximum time to wait for a tape operation to complete when using polled (bImmediate) mode.
    /// Default is 5 minutes. Set to <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.
    /// </summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(5);

    #endregion

    #region *** Abstract State Properties ***

    public abstract bool IsOpen { get; }
    public abstract bool HasMedia { get; }
    public abstract string DeviceName { get; }
    public abstract uint DriveNumber { get; }
    public abstract string Vendor { get; }
    public abstract string Product { get; }

    /// <summary>
    /// Drive firmware / microcode revision (SCSI INQUIRY "Product Revision Level", bytes 32-35).
    /// Empty when unknown. Used, together with <see cref="Vendor"/> and <see cref="Product"/>,
    /// to key early-warning calibration data.
    /// </summary>
    public virtual string Revision => string.Empty;

    /// <summary>
    /// Stable identity for keying calibration data to this drive+media profile.
    /// <para>
    /// Anchored on vendor, product, firmware revision AND a coarse native-capacity bucket.
    /// The capacity term matters because the early-warning POSITION is a property of the medium,
    /// not the drive: e.g. an LTO-3 cartridge in an LTO-4 drive reaches EW/EOM at the LTO-3
    /// position (~400 GB), not the LTO-4 one (~800 GB). Bucketing absorbs cartridge-to-cartridge
    /// and remaining-life jitter (e.g. 781.47 GB → 780 GB) while keeping distinct media
    /// generations — and distinct partition layouts, which change reported capacity — apart.
    /// </para>
    /// <para>
    /// Block size is deliberately NOT part of the key: it does not move the physical EW position,
    /// only the residual-to-bytes math at the crossing, which calibration already handles.
    /// </para>
    /// Backends may override to add density or other discriminators.
    /// </summary>
    public virtual string ProfileKey => $"{Vendor}|{Product}|{Revision}|cap={CapacityBucketGB(Capacity)}GB";

    /// <summary>
    /// Rounds a native capacity (bytes) to a coarse GB bucket (2 significant figures) so that
    /// cartridge-to-cartridge jitter never splits a calibration profile, while genuinely different
    /// media generations stay distinct. Examples: 781.47 GB → 780, 402 GB → 400, 1495 GB → 1500,
    /// 2498 GB → 2500. Returns 0 when capacity is unknown (e.g. no media loaded).
    /// </summary>
    protected static long CapacityBucketGB(long capacityBytes)
    {
        if (capacityBytes <= 0)
            return 0;

        double gb = capacityBytes / (1024.0 * 1024 * 1024);
        // Keep 2 significant figures: round to the nearest 10^(floor(log10)-1).
        double mag = Math.Pow(10, Math.Floor(Math.Log10(gb)) - 1);
        if (mag < 1) mag = 1; // never sub-GB granularity

        return (long)(Math.Round(gb / mag) * mag);
    }

    #endregion

    #region *** Abstract Drive & Media Properties ***

    public abstract uint BlockSize { get; }
    public abstract uint MinBlockSize { get; }
    public abstract uint MaxBlockSize { get; }
    public abstract uint DefaultBlockSize { get; }
    public abstract long Capacity { get; }
    public abstract long Remaining { get; }
    public abstract long Position { get; }
    public abstract bool SupportsInitiatorPartition { get; }
    public abstract bool HasInitiatorPartition { get; }
    public abstract bool SupportsSetmarks { get; }
    public abstract bool SupportsSeqFilemarks { get; }

    #endregion

    #region *** Early Warning ***

    /// <summary>
    /// If early warnings are being reported. This is what the drive actually does — which may differ
    /// from what was requested via <see cref="ReportEarlyWarning"/>, exactly like block size.
    /// Default: <c>false</c> since not supported.
    /// </summary>
    public virtual bool ReportsEarlyWarning => false;

    /// <summary>How <see cref="ReportsEarlyWarning"/> is currently realized (best available mechanism).</summary>
    public virtual EarlyWarningMechanism EarlyWarningMechanism => EarlyWarningMechanism.None;

    /// <summary>
    /// Best-effort, non-binding request that the backend SURFACE the drive's physical early warning
    /// in <see cref="Write"/> (via the <c>ew</c>/<c>pew</c> flags). Backends that can't simply accept
    /// and ignore it — TapeDrive maps the LOGICAL early warning itself. Returns whether honored.
    /// Default: not supported.
    /// </summary>
    public virtual bool ReportEarlyWarning(bool report) => false;

    #endregion

    #region *** Abstract Drive Operations ***

    /// <summary>
    /// Opens the tape drive by number (backend generates device name).
    /// </summary>
    public abstract bool Open(uint driveNumber);

    /// <summary>Closes the drive handle.</summary>
    public abstract void Close();

    /// <summary>Configures drive parameters (compression, ECC, padding, setmark reporting, EOT warning).</summary>
    public abstract bool SetDriveParameters(bool compression, bool ecc, bool dataPadding, bool reportSetmarks, uint eotWarningZoneSize);

    #endregion

    #region *** Abstract Media Operations ***

    /// <summary>Loads (tensions) the tape into the drive.</summary>
    public abstract bool LoadMedia();

    /// <summary>Ejects the tape from the drive.</summary>
    public abstract bool UnloadMedia();

    /// <summary>Sets the tape block size in bytes.</summary>
    public abstract bool SetBlockSize(uint size);

    /// <summary>Formats media, optionally creating an initiator partition of the given size.</summary>
    public abstract bool FormatMedia(long initiatorPartitionSize = -1);

    #endregion

    #region *** Abstract Read/Write Operations ***

    /// <summary>
    /// Reads data from the tape.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Offset in buffer.</param>
    /// <param name="count">Number of bytes to read (must be multiple of BlockSize).</param>
    /// <param name="tapemark">Set to true if a filemark/setmark was encountered.</param>
    /// <param name="eof">Set to true if end-of-file/end-of-media was encountered.</param>
    /// <returns>Number of bytes actually read.</returns>
    public abstract int Read(byte[] buffer, int offset, int count, out bool tapemark, out bool eof);

    /// <summary>
    /// Writes data to the tape.
    /// </summary>
    /// <param name="buffer">Source buffer.</param>
    /// <param name="offset">Offset in buffer.</param>
    /// <param name="count">Number of bytes to write (must be multiple of BlockSize).</param>
    /// <param name="tapemark">Set to true if a filemark/setmark was encountered.</param>
    /// <param name="pew">Set to true if a programmable early-warning boundary was encountered (if supported -- e.g. on LTO-5+).</param>
    /// <param name="ew">Set to true if an early-warning boundary was encountered.</param>
    /// <param name="eom">Set to true if end-of-media was encountered. Callers can also distinguish
    /// via <see cref="IErrorManageable.LastError"/>: <see cref="WIN32_ERROR.ERROR_END_OF_MEDIA"/>
    /// for hard EOM (data NOT written) versus <see cref="TapeEarlyWarning.EarlyWarningError"/> for early
    /// warning (data WAS written — wrap up and write the TOC).
    /// </param>
    /// <returns>Number of bytes actually written.</returns>
    public abstract int Write(byte[] buffer, int offset, int count,
        out bool tapemark, out bool pew, out bool ew, out bool eom);

    #endregion

    #region *** Abstract Positioning Operations ***

    /// <summary>Moves tape to a logical block address.</summary>
    public abstract bool SetPosition(long block);

    /// <summary>Moves tape to a block in a specific partition.</summary>
    public abstract bool SetPositionToPartition(MediaPartition partition, long block);

    /// <summary>Returns the current logical block address.</summary>
    public abstract long GetPosition();

    /// <summary>Returns the partition the tape head is currently in.</summary>
    public abstract MediaPartition GetCurrentPartition();

    /// <summary>Rewinds the tape to block 0 in the current partition.</summary>
    public abstract bool Rewind();

    /// <summary>Fast-forwards to the end-of-data mark in the specified partition.</summary>
    public abstract bool SeekToEnd(MediaPartition partition);

    /// <summary>Spaces forward (positive) or backward (negative) by filemark count.</summary>
    public abstract bool SpaceFilemarks(int count);

    /// <summary>Spaces forward or backward by setmark count.</summary>
    public abstract bool SpaceSetmarks(int count);

    /// <summary>Spaces forward or backward by sequential filemark count.</summary>
    public abstract bool SpaceSequentialFilemarks(int count);

    #endregion

    #region *** Abstract Tapemark Operations ***

    /// <summary>Writes <paramref name="count"/> filemarks at the current position.</summary>
    public abstract bool WriteFilemarks(uint count);

    /// <summary>Writes <paramref name="count"/> setmarks at the current position.</summary>
    public abstract bool WriteSetmarks(uint count);

    #endregion

    #region *** Abstract Parameter Queries ***

    /// <summary>Queries the drive and populates <paramref name="parameters"/>.</summary>
    public abstract void FillDriveCapabilities(out DriveCapabilities parameters);

    /// <summary>Queries the loaded media and populates <paramref name="parameters"/>.</summary>
    public abstract void FillMediaParameters(out MediaParameters parameters);

    #endregion

    #region *** Validation Helpers ***

    /// <summary>Validates that drive is ready for read/write operations.</summary>
    public void CheckForRW([CallerMemberName] string methodName = "")
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!IsOpen)
            throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_HANDLE, $"Drive not open in {methodName}");
        if (!HasMedia)
            throw new TapeIOException((uint)WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE, $"Media not loaded in {methodName}");
    }

    /// <summary>Validates buffer arguments and drive state for read/write.</summary>
    public void CheckForRW(byte[] buffer, int offset, int count, [CallerMemberName] string methodName = "")
    {
        ArgumentNullException.ThrowIfNull(buffer, nameof(buffer) + $" in {methodName}");
        ArgumentOutOfRangeException.ThrowIfNegative(offset, nameof(offset) + $" in {methodName}");
        ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(count) + $" in {methodName}");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length,
            $"{nameof(offset)} + {nameof(count)} in {methodName}");
        CheckForRW(methodName);
    }

    #endregion

    #region *** IDisposable ***

    protected bool IsDisposed { get; private set; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!IsDisposed)
        {
            // Set the flag before calling Close() so that any exception from Close()
            //  (e.g. a failed RPC) does not leave the object in a re-entrant state.
            IsDisposed = true;
            if (disposing)
            {
                Close();
            }
        }
    }

    ~TapeDriveBackend()
    {
        Dispose(disposing: false);
    }

    #endregion
}
