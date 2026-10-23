using TapeLibNET.Calibration;
using TapeLibNET.Drive;
using TapeLibNET.Media;
using Windows.Win32.Foundation;

namespace TapeLibNET.Headers;

/// <summary>
/// The single point of truth for reading and writing a header as ONE standard-sized tape block — and for reading ANY
///  block for identification (<see cref="ReadAndIdentify"/>).
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
/// <para>
/// <b>The <c>.Identify</c> partial</b> holds THE dispatcher, <see cref="IdentifyBlock"/>: the TOC-less identification
///  of a block already read (SM-2). Every classification in the library — scanner, navigator, agent, calibrator — ends
///  there (Design-TocLocation §3).
/// </para>
/// <para>
/// <b>Three layers, and what each one buys.</b>
/// </para>
/// <code>
///  Layer                  Job                                          Why it is critical - must-retain
///  ─────────────────────  ───────────────────────────────────────────  ──────────────────────────────────
///  TapeHeaderBlock        fixed 16 KiB block; sets and restores the    tape reads are block-granular, so
///                          drive block size; zero padding              a header must be readable BEFORE
///                                                                      its length is known
///
///  TapeFrame              Record ‖ CRC-64, magic "TpN#" at byte 0      "is this ours?" is one 4-byte
///                                                                      compare; the CRC makes a torn
///                                                                      write DETECTABLE, not plausible
///
///  TapeRecord prologue    kind, format version, body length;           one read classifies a block as
///   + header schemas      shared tags 1–3 (id, created, block size)    media, set, or calibration
/// </code>
/// <para>
/// <b>The legacy wart, resolved for new media.</b> Legacy frames put the signature behind an int32 length, so "is this
///  ours?" first had to trust an untrusted length. Format 2.1 puts the magic first (Design-FormatV21 §4.5). Legacy
///  headers stay readable through <see cref="Legacy.LegacyFramer"/> — every cartridge ever written keeps its layout.
/// </para>
/// </remarks>
public static partial class TapeHeaderBlock
{
    /// <summary>The standard header block size — one block, terminated by one filemark.</summary>
    public const int Size = (int)TapeHeader.FixedHeaderBlockSize;   // 16 KiB

    /// <summary>
    /// A filemark terminates the header record on tape, so the first content write is always a
    ///  POST-MARK write. Media drives classically accept a write only at BOP, at EOD, or immediately
    ///  after a mark; without this the begin-of-content write is mid-data and drive-dependent.
    /// </summary>
    public const bool WritesTrailingMark = true;

    #region *** Framing and writing ***

    /// <summary>
    /// Frames <paramref name="header"/> into exactly <see cref="Size"/> bytes — a 2.1 block frame (<c>Record ‖ CRC-64</c>,
    ///  magic first), remainder zero padding, ignored on read.
    ///  Returns <see langword="null"/> when the frame does not fit, which is a programming error rather than a media
    ///  condition.
    /// </summary>
    public static byte[]? Frame(TapeHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        byte[] frame = TapeFramer.PackHeader(header);
        if (frame.Length > Size)
            return null;

        var block = new byte[Size];
        Array.Copy(frame, block, frame.Length);
        return block;
    }

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
    /// <param name="withFilemark">If specified, overrules <see cref="WritesTrailingMark"/>:
    ///  <see langword="true"/> writes a filemark, <see langword="false"/> does not.</param>
    public static bool WriteFramed(TapeDrive drive, byte[] framedBlock, bool? withFilemark = null)
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
            if ((withFilemark ?? WritesTrailingMark) && !drive.WriteFilemark(1))
                return false;

            return true;
        }
        finally
        {
            RestoreBlockSize(drive, previous);
        }
    }

    #endregion

    #region *** Reading ***

    /// <summary>
    /// Reads one standard block at the CURRENT position into <paramref name="buffer"/> (which must be at
    ///  least <see cref="Size"/> bytes) and returns the header it holds. The drive's block size is set for the read and
    ///  restored afterwards.
    /// </summary>
    /// <param name="header">The concrete header kind, or null for blank / foreign / torn — a projection of
    ///  <see cref="IdentifyBlock"/>.</param>
    /// <returns>Bytes read, or ≤ 0 on failure (the drive carries the error).</returns>
    public static int Read(TapeDrive drive, byte[] buffer, out TapeHeader? header)
        => Read(drive, buffer, out header, out _, out _);

    /// <summary>
    /// As <see cref="Read(TapeDrive, byte[], out TapeHeader?)"/>, additionally reporting WHY a read
    ///  delivered nothing: a tapemark stood there, or the data ended.
    /// </summary>
    /// <param name="tapemark">A filemark or setmark stood at the position; the read crossed it.</param>
    /// <param name="eod">End-of-data: nothing was ever written at this position.</param>
    /// <remarks>
    /// <para>
    /// <b>Flags, not the error.</b> <see cref="TapeDrive.ReadDirect(byte[], int, int, out bool, out bool)"/>
    ///  RESETS its error on both boundaries and reports them only through its <c>out</c> flags. A caller
    ///  holding just the byte count and the error sees <c>read == 0, NO_ERROR</c> for a mark, for EOD, and
    ///  for nothing at all — and deducing EOD from that silence would couple every caller to one drive
    ///  behaviour.
    /// </para>
    /// <para>
    /// <b>Narrower than the drive's <c>eof</c>.</b> <see cref="TapeDrive.ReadDirect(byte[], int, int, out bool, out bool)"/>
    ///  sets <c>eof</c> on ANY boundary, marks included. <paramref name="eod"/> is the end-of-data-only
    ///  reading — the one a caller needs to tell "the tape ends here" from "a mark stands here".
    /// </para>
    /// </remarks>
    public static int Read(TapeDrive drive, byte[] buffer, out TapeHeader? header,
                           out bool tapemark, out bool eod)
    {
        int read = ReadBlock(drive, buffer, out tapemark, out eod);
        header = read > 0 ? Classify(buffer, read) : null;
        return read;
    }

    /// <summary>
    /// Reads ONE block at the current position and identifies it — the shared read primitive of every block-by-block
    ///  identification: the scanner's walk, the navigator's checks, the TOC search (Design-TocLocation §3.4).
    /// </summary>
    /// <param name="buffer">The block's bytes, for evidence (fingerprints) — valid up to <paramref name="read"/>.</param>
    /// <param name="read">Bytes read; ≤ 0 when nothing was delivered (a mark, EOD, or a fault — the drive carries the error).</param>
    /// <param name="tapemark">A mark stood at the position; the read crossed it.</param>
    /// <param name="eod">End-of-data: nothing was ever written here.</param>
    /// <returns>What the block is; <see cref="IdentifiedBlock.Foreign"/> when nothing was read.</returns>
    /// <remarks>
    /// <para>
    /// <b>Small-block drives</b> (Design-ScanMedia §4.6, and not optional). <see cref="IsSupportedBy"/> is false when the
    ///  drive's maximum block is under 16 KiB — tiny virtual media, some pre-LTO drives — and the read then takes one
    ///  native block instead; without the fallback every block on such a cartridge would identify as foreign.
    /// </para>
    /// <para>
    /// The 16 KiB read also covers a TOC copy: the TOC is written at the same fixed block size as the headers, so its
    ///  first block arrives whole.
    /// </para>
    /// <para>
    /// <b>Moves the head</b> past the block (or the mark). Callers that must stay put return to the block themselves.
    /// </para>
    /// </remarks>
    internal static IdentifiedBlock ReadAndIdentify(TapeDrive drive, out byte[] buffer, out int read,
                                                    out bool tapemark, out bool eod)
    {
        ArgumentNullException.ThrowIfNull(drive);

        if (IsSupportedBy(drive))
        {
            buffer = new byte[Size];
            read = ReadBlock(drive, buffer, out tapemark, out eod);     // sets AND restores the block size
        }
        else
        {
            uint blockSize = drive.BlockSize > 0 ? drive.BlockSize : drive.DefaultBlockSize;
            buffer = new byte[blockSize];
            read = drive.ReadDirect(buffer, 0, buffer.Length, out tapemark, out bool boundary);
            eod = boundary && !tapemark;    // the drive's `eof` covers marks too
        }

        return read > 0 ? IdentifyBlock(buffer, read) : IdentifiedBlock.Foreign;
    }

    /// <summary>One standard block, raw: sets and restores the block size, reports the boundaries.</summary>
    private static int ReadBlock(TapeDrive drive, byte[] buffer, out bool tapemark, out bool eod)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(buffer);

        tapemark = false;
        eod = false;

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

            int read = drive.ReadDirect(buffer, 0, Size, out tapemark, out bool boundary);
            // The drive's `eof` covers marks too; only a boundary WITHOUT a mark is end-of-data.
            eod = boundary && !tapemark;
            return read;
        }
        finally
        {
            RestoreBlockSize(drive, previous);
        }
    }

    #endregion

    #region *** Drive support ***

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

    #endregion
}
