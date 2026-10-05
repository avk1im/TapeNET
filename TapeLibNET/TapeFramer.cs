using System.IO.Hashing;
using TapeLibNET.Format;
using TapeLibNET.Legacy;

namespace TapeLibNET;

/// <summary>
/// Block framing of block-boundary records — the dual-format façade over <see cref="TapeFrame"/> (format 2.1) and
///  <see cref="LegacyFramer"/> (legacy).
/// </summary>
/// <remarks>
/// <para>
/// <b>Headers</b> (<see cref="PackHeader"/>, <see cref="TryUnpackHeader(byte[], int, out TapeHeader?)"/>,
///  <see cref="UnpackHeader{T}"/>): written as 2.1 block frames <c>Record ‖ CRC-64</c> with the magic at byte 0
///  (Design-Format-v2 §4.5); read in either format, dispatched on the first bytes.
/// </para>
/// <para>
/// <b>Legacy records</b> (<see cref="Pack(ITapeSerializable)"/>, <see cref="Unpack{T}"/>, <see cref="TryUnpack{T}"/>):
///  <c>[int32 payloadLen][payload][crc32]</c> — still used by the calibration header and checkpoint until Phase 6.
///  Phase 6 removes this half; <see cref="FrameStatus"/> then moves into <c>Format/</c> and this class folds into
///  <see cref="TapeFrame"/>.
/// </para>
/// </remarks>
public static class TapeFramer
{
    // MERGE: keep your existing FrameStatus declaration (value order and docs) if it differs from this one.

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

    #region *** Headers — format 2.1, read in either format ***

    /// <summary>
    /// Builds the frame bytes of <paramref name="header"/>: a 2.1 block frame <c>Record ‖ CRC-64</c>. The calibration
    ///  header still gets its legacy frame until Phase 6.
    /// </summary>
    public static byte[] PackHeader(TapeHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return header is TapeCalibrationHeader calibration
            ? Pack(calibration)                        // transitional: legacy until Phase 6
            : TapeFrame.Pack<TapeHeader>(header);
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

    #endregion

    #region *** Legacy records — calibration header and checkpoint, removed in Phase 6 ***

    // MERGE: keep your existing Pack(ITapeSerializable) body if it differs — the calibration golden file pins its bytes.

    /// <summary>Serializes <paramref name="record"/> and returns the LEGACY framed <c>[len][payload][crc32]</c> bytes.</summary>
    public static byte[] Pack(ITapeSerializable record)
    {
        ArgumentNullException.ThrowIfNull(record);
        // Serialize the payload while hashing it — reuse HashingStream over a growable MemoryStream.
        using var payloadMs = new MemoryStream();
        var crc = new Crc32();
        using (var hashing = new HashingStream(payloadMs, crc, ownInner: false))
        {
            var ser = new TapeSerializer(hashing);
            record.SerializeTo(ser);
        }
        byte[] payload = payloadMs.ToArray();
        byte[] crcBytes = crc.GetCurrentHash();      // 4 bytes
        using var frameMs = new MemoryStream(payload.Length + 8);
        var frameSer = new TapeSerializer(frameMs);
        frameSer.Serialize(payload.Length);          // int32 length prefix
        frameSer.Serialize(payload);                 // raw payload (already hashed)
        frameSer.Serialize(crcBytes);                // raw 4-byte CRC trailer (outside the hash)
        return frameMs.ToArray();
    }

    /// <summary>
    /// Parses a LEGACY framed record and verifies its CRC; null for anything but <see cref="FrameStatus.Ok"/> — the
    ///  signal the calibration resume walk treats as "step back to the previous checkpoint".
    /// </summary>
    public static T? Unpack<T>(byte[] block, int length) where T : class, ITapeSerializable
        => TryUnpack(block, length, out T? record) == FrameStatus.Ok ? record : null;

    /// <summary>As <see cref="Unpack{T}"/>, but reports why no record came back. Total, never throws.</summary>
    public static FrameStatus TryUnpack<T>(byte[] block, int length, out T? record) where T : class, ITapeSerializable
        => LegacyFramer.TryUnpack(block, length, out record);

    #endregion
}
