using Windows.Win32.Foundation;

namespace TapeLibNET;

/// <summary>
/// The single point of truth for reading and writing a header as ONE standard-sized tape block.
/// <para>
/// SCOPE: pure block I/O at the CURRENT tape position. Positioning is the caller's job and stays where
///  it belongs — the navigator/manager for the agent, <c>PrepareDrive</c>/<c>Rewind</c> for the
///  calibrator. This class never rewinds, never switches partitions, and never touches presence state.
/// </para>
/// </summary>
/// <remarks>
/// Every subsystem that puts a <see cref="TapeHeader"/> on tape should go through here: the backup agent
///  (via <see cref="TapeStreamManager"/>) and <see cref="TapeCalibrator"/> alike — so the block size,
///  the framing, the size guard, and the block-size discipline are defined once. Sharing the block size
///  is what lets each subsystem classify the other's media: a 16 KiB read always spans a whole header
///  block, whatever wrote it.
/// </remarks>
public static class TapeHeaderBlock
{
    /// <summary>The standard header block size — one block, terminated by one filemark.</summary>
    public const int Size = (int)TapeHeader.FixedHeaderBlockSize;   // 16 KiB

    /// <summary>
    /// A filemark terminates the header record on tape, so the first content write is always a
    ///  POST-MARK write. Tape drives classically accept a write only at BOP, at EOD, or immediately
    ///  after a mark; without this the begin-of-content write is mid-data and drive-dependent.
    /// </summary>
    public const bool WritesTrailingMark = true;

    /// <summary>
    /// Frames <paramref name="header"/> into exactly <see cref="Size"/> bytes (remainder left as zero
    ///  padding, ignored on read). Returns <see langword="null"/> when the framed record does not fit,
    ///  which is a programming error rather than a media condition.
    /// </summary>
    public static byte[]? Frame(TapeHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        byte[] frame = TapeFramer.Pack(header);
        if (frame.Length > Size)
            return null;

        var block = new byte[Size];
        Array.Copy(frame, block, frame.Length);
        return block;
    }

    /// <summary>
    /// Classifies a block that was already read, returning the concrete header kind (media, calibration)
    ///  or <see langword="null"/> for blank / foreign / torn. The single call site of the polymorphic probe.
    /// </summary>
    public static TapeHeader? Classify(byte[] buffer, int length)
        => length > 0 ? TapeFramer.Unpack<TapeHeader>(buffer, length) : null;

    /// <summary>
    /// Writes <paramref name="header"/> as one standard block at the CURRENT position, temporarily
    ///  setting the drive's block size and restoring it afterwards. No filemark is written.
    /// </summary>
    /// <returns>True on success; on failure the drive carries the error (use <c>SyncErrorFrom(drive)</c>).</returns>
    public static bool Write(TapeDrive drive, TapeHeader header)
    {
        ArgumentNullException.ThrowIfNull(drive);

        byte[]? block = Frame(header);
        if (block is null)
        {
            drive.SetError(WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER, "Header frame exceeds the standard header block");
            return false;
        }

        return WriteFramed(drive, block);
    }

    /// <summary>
    /// Writes a pre-framed, exactly <see cref="Size"/>-byte block at the CURRENT position. Used by
    ///  <see cref="TapeStreamManager"/>, which frames via <see cref="Frame"/> at the agent layer.
    /// </summary>
    public static bool WriteFramed(TapeDrive drive, byte[] framedBlock, bool withFilemark = true)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(framedBlock);

        if (framedBlock.Length != Size)
        {
            drive.SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER, "Header block must be exactly the standard size");
            return false;
        }

        uint previous = drive.BlockSize;
        try
        {
            if (!drive.SetBlockSize(Size))
                return false;

            int written = drive.WriteDirect(framedBlock, 0, Size, out _, out _, out _);
            if (written != Size)
                return false;

            // Terminate the header with a filemark — the single reason this constant exists (§15.1).
            //  Leaves the head PAST the mark, i.e. exactly at begin-of-content.
            if (WritesTrailingMark && !drive.WriteFilemark(1))
                return false;

            return true;
        }
        finally
        {
            RestoreBlockSize(drive, previous);
        }
    }

    /// <summary>
    /// Reads one standard block at the CURRENT position into <paramref name="buffer"/> (which must be at
    ///  least <see cref="Size"/> bytes) and classifies it. The drive's block size is set for the read and
    ///  restored afterwards.
    /// </summary>
    /// <param name="header">The concrete header kind, or null for blank / foreign / torn.</param>
    /// <returns>Bytes read, or ≤ 0 on failure (the drive carries the error).</returns>
    public static int Read(TapeDrive drive, byte[] buffer, out TapeHeader? header)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(buffer);

        header = null;

        if (buffer.Length < Size)
        {
            drive.SetError(WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER, "Header buffer smaller than the standard block");
            return -1;
        }

        uint previous = drive.BlockSize;
        try
        {
            if (!drive.SetBlockSize(Size))
                return -1;

            int read = drive.ReadDirect(buffer, 0, Size, out _, out _);
            if (read <= 0)
                return read;

            header = Classify(buffer, read);
            return read;
        }
        finally
        {
            RestoreBlockSize(drive, previous);
        }
    }

    /// <summary>
    /// True when the drive can carry a standard header block. Drives whose maximum block is smaller
    ///  cannot hold one; callers that must still function (e.g. calibration on tiny virtual media)
    ///  fall back to their own block and log the deviation.
    /// </summary>
    public static bool IsSupportedBy(TapeDrive drive)
        => drive.MaximumBlockSize == 0 || drive.MaximumBlockSize >= Size;

    private static void RestoreBlockSize(TapeDrive drive, uint previous)
    {
        // Leaving the drive on the header block would silently reshape whatever the caller writes next
        //  (content set, calibration payload). Restoring is the single-point discipline that prevents it.
        if (previous != 0 && previous != Size)
            drive.SetBlockSize(previous);
    }
}
