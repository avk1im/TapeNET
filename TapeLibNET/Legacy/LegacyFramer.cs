using System.IO.Hashing;

using TapeLibNET.Format; // TapeFrameStatus

namespace TapeLibNET.Legacy;


/// <summary>
/// Frozen, read-only reader of the pre-2.1 frame: <c>[int32 payloadLen][payload][crc32]</c>, where the CRC-32 covers the
///  payload only. Total: every framing and format fault becomes a <see cref="TapeFrameStatus"/>.
/// </summary>
internal static class LegacyFramer
{
    /// <summary>Bytes a frame adds around its payload: the int32 length prefix and the CRC-32 trailer.</summary>
    public const int Overhead = sizeof(int) + sizeof(uint);

    /// <summary>
    /// Parses a framed record out of <paramref name="block"/> with <paramref name="parse"/> and verifies its CRC.
    /// </summary>
    /// <param name="block">The block as read from tape; padding behind the frame is ignored.</param>
    /// <param name="length">Bytes actually read (may be less than <paramref name="block"/>'s length).</param>
    /// <param name="parse">
    /// Payload reader — <see cref="LegacyHeaderReader.Read"/> for headers, <see cref="LegacyCheckpointReader.ReadCheckpoint"/>
    ///  for checkpoints; returns null for a payload that is not the expected record.
    /// </param>
    /// <param name="record">The record when the result is <see cref="TapeFrameStatus.Ok"/>; null otherwise.</param>
    public static TapeFrameStatus TryUnpack<T>(byte[] block, int length, Func<LegacyDeserializer, T?> parse,
        out T? record) where T : class
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(parse);
        record = null;

        byte[]? payload;
        byte[]? crcStored;
        try
        {
            using var ms = new MemoryStream(block, 0, Math.Clamp(length, 0, block.Length), writable: false);
            var d = new LegacyDeserializer(ms);
            int payloadLen = d.DeserializeInt32();
            if (payloadLen < 0 || payloadLen > block.Length - Overhead)
                return TapeFrameStatus.NotFramed;    // implausible length => not a valid frame
            payload = d.DeserializeBytes(payloadLen);
            crcStored = d.DeserializeBytes(sizeof(uint));
        }
        catch (Exception)
        {
            return TapeFrameStatus.NotFramed;        // the block ends inside the frame
        }
        if (payload is null || crcStored is null)
            return TapeFrameStatus.NotFramed;

        var crc = new Crc32();
        crc.Append(payload);
        if (!crc.GetCurrentHash().AsSpan().SequenceEqual(crcStored))
            return TapeFrameStatus.CrcMismatch;      // torn / corrupt

        try
        {
            using var pms = new MemoryStream(payload, writable: false);
            record = parse(new LegacyDeserializer(pms));    // the parser re-checks signature / version
        }
        catch (Exception)
        {
            // A format error past a GOOD CRC is a record we cannot read -- not a torn one.
            record = null;
        }
        return record is null ? TapeFrameStatus.Unparseable : TapeFrameStatus.Ok;
    }
}
