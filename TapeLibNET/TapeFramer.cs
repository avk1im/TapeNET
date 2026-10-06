using TapeLibNET.Format;

namespace TapeLibNET;

/// <summary>
/// Block framing of header records — the dual-format façade over <see cref="TapeFrame"/> (format 2.1) and
///  <see cref="Legacy.LegacyFramer"/> (legacy, read-only).
/// </summary>
/// <remarks>
/// Headers are written as 2.1 block frames <c>Record ‖ CRC-64</c> with the magic at byte 0 (Design-Format-v2 §4.5) and
///  read in either format, dispatched on the first bytes. Since Phase 6 nothing in the product writes a legacy frame.
///  The move-only folder commit folds this class into <see cref="TapeFrame"/> and <see cref="FrameStatus"/> into
///  <c>Format/</c>.
/// </remarks>
public static class TapeFramer
{
    /// <summary>Why an unpack did or did not yield a record.</summary>
    public enum FrameStatus
    {
        /// <summary>Framed, CRC intact, payload parsed.</summary>
        Ok,
        /// <summary>No plausible frame: no magic / implausible length, or the block ends inside the frame.</summary>
        NotFramed,
        /// <summary>A plausible frame whose CRC disagrees — a torn or corrupt record.</summary>
        CrcMismatch,
        /// <summary>CRC intact, but the record would not parse — unknown kind, newer version, unknown critical field.</summary>
        Unparseable,
    }

    /// <summary>Builds the frame bytes of <paramref name="header"/>: a 2.1 block frame <c>Record ‖ CRC-64</c>.</summary>
    public static byte[] PackHeader(TapeHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return TapeFrame.Pack<TapeHeader>(header);
    }

    /// <summary>
    /// Parses a header frame at the start of <paramref name="block"/>, in either format: <c>TpN#</c> at byte 0 → 2.1;
    ///  anything else → the legacy probe. Total: every fault becomes a status, never an exception.
    /// </summary>
    /// <param name="length">Bytes actually read (may be less than <paramref name="block"/>'s length).</param>
    /// <param name="header">The concrete header when the result is <see cref="FrameStatus.Ok"/>; null otherwise.</param>
    /// <remarks>
    /// <b>A status is not an identity.</b> <see cref="FrameStatus.CrcMismatch"/> alone does not prove the block was ever
    ///  ours. <see cref="TapeHeaderBlock.IdentifyBlock"/> establishes identity (magic or legacy signature) first.
    /// </remarks>
    public static FrameStatus TryUnpackHeader(byte[] block, int length, out TapeHeader? header)
    {
        ArgumentNullException.ThrowIfNull(block);
        ReadOnlySpan<byte> data = block.AsSpan(0, Math.Clamp(length, 0, block.Length));
        return TapeFrame.TryUnpackWithLegacy(data, out header, out _, out _);
    }

    /// <summary>
    /// Narrow form of <see cref="TryUnpackHeader(byte[], int, out TapeHeader?)"/>: a header of another kind yields
    ///  <see cref="FrameStatus.Unparseable"/> and null — never a misparse.
    /// </summary>
    public static FrameStatus TryUnpackHeader<T>(byte[] block, int length, out T? header) where T : TapeHeader
    {
        FrameStatus status = TryUnpackHeader(block, length, out TapeHeader? any);
        header = any as T;
        return status == FrameStatus.Ok && header is null ? FrameStatus.Unparseable : status;
    }

    /// <summary>
    /// As <see cref="TryUnpackHeader(byte[], int, out TapeHeader?)"/>, collapsing every non-<see cref="FrameStatus.Ok"/>
    ///  outcome to null.
    /// </summary>
    public static TapeHeader? UnpackHeader(byte[] block, int length)
        => TryUnpackHeader(block, length, out TapeHeader? header) == FrameStatus.Ok ? header : null;

    /// <summary>Narrow form of <see cref="UnpackHeader(byte[], int)"/>: null for a header of another kind.</summary>
    public static T? UnpackHeader<T>(byte[] block, int length) where T : TapeHeader
        => UnpackHeader(block, length) as T;
}
