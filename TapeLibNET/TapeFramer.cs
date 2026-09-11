using System;
using System.Collections.Generic;
using System.IO.Hashing;


namespace TapeLibNET;

/// <summary>
/// Frames an <see cref="ITapeSerializable"/> record for on-tape storage with a CRC-32 guard.
/// Reuses the library's <see cref="HashingStream"/> / <see cref="Crc32"/> plumbing.
/// </summary>
/// <remarks>
/// Wire framing: <c>[int32 payloadLen][payload][4-byte crc]</c>, where <c>payload</c> is the record's own
/// <see cref="ITapeSerializable.SerializeTo"/> output (signature + fields) and <c>crc</c> is CRC-32 over
/// that payload — kept OUTSIDE the hashed span. The whole frame is copied into the front of a full block;
/// the block's remaining bytes are caller-supplied random padding (ignored on read-back).
/// </remarks>
public static class TapeFramer
{
    /// <summary>Serializes <paramref name="record"/> and returns the framed <c>[len][payload][crc]</c> bytes.</summary>
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
    /// Parses a framed record out of a full block read back from tape and verifies its CRC. Returns the
    /// reconstructed record, or <see langword="null"/> when the block is not one of our records, is torn,
    /// or fails the CRC — the exact signals the resume walk treats as "step back to the previous checkpoint".
    /// </summary>
    public static T? Unpack<T>(byte[] block, int length) where T : class, ITapeSerializable
    {
        ArgumentNullException.ThrowIfNull(block);

        try
        {
            using var ms = new MemoryStream(block, 0, Math.Min(length, block.Length), writable: false);
            var d = new TapeDeserializer(ms);

            int payloadLen = d.DeserializeInt32();
            if (payloadLen < 0 || payloadLen > block.Length - 8)
                return null;                         // implausible length ⇒ not a valid frame

            byte[]? payload = d.DeserializeBytes(payloadLen);
            byte[]? crcStored = d.DeserializeBytes(4);
            if (payload is null || crcStored is null)
                return null;

            var crc = new Crc32();
            crc.Append(payload);
            if (!crc.GetCurrentHash().AsSpan().SequenceEqual(crcStored))
                return null;                         // CRC mismatch ⇒ torn / corrupt

            using var pms = new MemoryStream(payload, writable: false);
            var pd = new TapeDeserializer(pms);
            return T.ConstructFrom(pd) as T;         // ConstructFrom re-checks signature/version
        }
        catch (Exception)
        {
            // Any framing/format error ⇒ treat as an invalid record; the caller walks back.
            return null;
        }
    }
}


