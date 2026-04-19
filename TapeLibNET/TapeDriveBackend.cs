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
);

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
    /// <param name="eof">Set to true if end-of-file/end-of-media was encountered.</param>
    /// <returns>Number of bytes actually written.</returns>
    public abstract int Write(byte[] buffer, int offset, int count, out bool tapemark, out bool eof);

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
            if (disposing)
            {
                Close();
            }
            IsDisposed = true;
        }
    }

    ~TapeDriveBackend()
    {
        Dispose(disposing: false);
    }

    #endregion
}








