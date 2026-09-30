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
    /// <summary>Why <see cref="TryUnpack{T}"/> did or did not yield a record.</summary>
    public enum FrameStatus
    {
        /// <summary>Framed, CRC intact, payload parsed.</summary>
        Ok,

        /// <summary>No plausible frame: length prefix out of range, or the block ends inside the frame.</summary>
        NotFramed,

        /// <summary>A plausible frame whose CRC disagrees — a torn or corrupt record.</summary>
        CrcMismatch,

        /// <summary>CRC intact, but the payload would not deserialize — unknown kind, or a newer version.</summary>
        Unparseable,
    }

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
    /// <remarks>
    /// Collapses every non-<see cref="FrameStatus.Ok"/> outcome to null — its contract since before Scan
    ///  Media, kept unchanged for the calibration resume walk. A caller that must know WHICH outcome
    ///  happened uses <see cref="TryUnpack{T}"/>.
    /// </remarks>
    public static T? Unpack<T>(byte[] block, int length) where T : class, ITapeSerializable
        => TryUnpack(block, length, out T? record) == FrameStatus.Ok ? record : null;

    /// <summary>
    /// As <see cref="Unpack{T}"/>, but reports why no record came back.
    /// </summary>
    /// <param name="record">The record when the result is <see cref="FrameStatus.Ok"/>; null otherwise.</param>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> <see cref="Unpack{T}"/> answers "not a frame at all" and "a frame whose CRC
    ///  failed" with the same null. Scan Media must tell them apart: a damaged header is OUR record,
    ///  damaged; random bytes are nobody's. Treating them alike once let a CRC-damaged set header surface
    ///  as a phantom TOC copy.
    /// </para>
    /// <para>
    /// Total, like <see cref="Unpack{T}"/>: every framing and format fault becomes a status, never an
    ///  exception.
    /// </para>
    /// <para>
    /// <b>A status is not an identity.</b> <see cref="FrameStatus.CrcMismatch"/> alone does not prove the
    ///  block was ever ours — a random block whose first four bytes form a plausible length fails the CRC
    ///  too. Identity needs the signature as well; <see cref="TapeHeaderBlock.IdentifyBlock"/> checks it
    ///  BEFORE calling here.
    /// </para>
    /// </remarks>
    public static FrameStatus TryUnpack<T>(byte[] block, int length, out T? record)
        where T : class, ITapeSerializable
    {
        ArgumentNullException.ThrowIfNull(block);
        record = null;

        byte[]? payload;
        byte[]? crcStored;

        try
        {
            using var ms = new MemoryStream(block, 0, Math.Min(length, block.Length), writable: false);
            var d = new TapeDeserializer(ms);

            int payloadLen = d.DeserializeInt32();

            if (payloadLen < 0 || payloadLen > block.Length - 8)
                return FrameStatus.NotFramed;           // implausible length ⇒ not a valid frame

            payload = d.DeserializeBytes(payloadLen);
            crcStored = d.DeserializeBytes(4);
        }
        catch (Exception)
        {
            return FrameStatus.NotFramed;               // the block ends inside the frame
        }

        if (payload is null || crcStored is null)
            return FrameStatus.NotFramed;

        var crc = new Crc32();
        crc.Append(payload);

        if (!crc.GetCurrentHash().AsSpan().SequenceEqual(crcStored))
            return FrameStatus.CrcMismatch;             // CRC mismatch ⇒ torn / corrupt

        try
        {
            using var pms = new MemoryStream(payload, writable: false);
            record = T.ConstructFrom(new TapeDeserializer(pms)) as T;   // ConstructFrom re-checks signature/version
        }
        catch (Exception)
        {
            // A format error past a GOOD CRC is a record we cannot read — not a torn one.
            record = null;
        }

        return record is null ? FrameStatus.Unparseable : FrameStatus.Ok;
    }
}
