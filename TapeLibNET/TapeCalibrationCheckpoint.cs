using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;

namespace TapeLibNET;

// =============================================================================
//  RESUMABLE CALIBRATION — on-tape checkpoint records
//
//  A calibration run writes a self-describing trail so a run interrupted by a
//  transport fault (bus reset, power loss, app crash) can be RESUMED from the
//  last good checkpoint instead of restarting from BOM. The cartridge is the
//  single source of truth — no host-side sidecar — so a retained calibration
//  cartridge carries practically the whole run state.
//
//  SINGLE-FILEMARK layout ('FM' = filemark). Each FM immediately PRECEDES a
//  checkpoint block, so the resume walk always lands at a checkpoint-block
//  start — never inside payload gibberish, even if a checkpoint write was torn:
//
//   BOM
//    │ ┌─ header ─┐┌ payload ┐    ┌ checkpt 0 ┐┌ payload ┐    ┌ checkpt 1 ┐
//    ├▶│  block   ││ blocks  │─FM─▶│   block   ││ blocks  │─FM─▶│   block   │─FM─▶ …
//    │ └──────────┘└─────────┘    └───────────┘└─────────┘    └───────────┘
//    │  RunId,plan,               cumulative                   cumulative
//    │  capacity                  samples+EW+bytes             samples+EW+bytes
//    │
//    │ … ┌ checkpt k ┐┌ partial payload ┐
//    … ─▶│   block    ││ (write failed)   │  ◀── EOD (no trailing FM)
//        └───────────┘└──────────────────┘
//               ▲
//   Resume READ:  FastforwardToEnd ─▶ MoveToNextFilemark(-n) ─▶ MoveToNextFilemark(+1)
//                 ─▶ ReadDirect one block ─▶ Unpack+CRC
//                    valid & RunId match?  yes → use it
//                                          no  → n++ and retry (torn/foreign)
//                                          BOP → no resumable run (header-only / blank)
//   Resume WRITE: FastforwardToEnd ─▶ MoveToNextFilemark(-n)  (lands BOP-side of the FM
//                 before the good checkpoint) ─▶ rewrite FM + checkpoint + payload.
//
//  Each record occupies ONE calibration block (the run's normal block size,
//  e.g. 1 MB on LTO). The framed record sits at the FRONT; the remaining block
//  bytes are random padding (compression is off, so content is immaterial to
//  position — random simply keeps the block consistent with the payload and
//  avoids a compressible run should a profile ever run with compression on).
//  The FULL block is counted in bytesWritten, so the reported→actual mapping
//  stays honest and even reflects real set-delimited overhead.
//
//  NOTE: checkpoints are laid down in the BODY only (never the tail), so the
//  last checkpoint is always PRE-tail — exactly the restart point Resume needs
//  and the re-measure point Recalibrate needs.
// =============================================================================

/// <summary>
/// Written once as the header block at BOM. Self-identifies the run and cartridge so <c>Resume</c> can
/// verify "same run" (internal <see cref="RunId"/> consistency) before trusting any checkpoint, and so a
/// returned cartridge is inspectable ("what run / drive / when does this hold?"). Profile MATCHING against
/// the current drive is deliberately NOT done here — that is the caller's / service layer's responsibility.
/// </summary>
public sealed record TapeCalibrationRunHeader(
    Guid RunId,
    string ProfileKey,
    long CapacityReportedAtBom,
    uint BlockSize,
    DateTime StartedUtc,
    TapeCalibrationPlan Plan) : ITapeSerializable
{
    public void SerializeTo(TapeSerializer s)
    {
        s.SerializeSignature();

        s.Serialize(RunId.ToByteArray());            // 16 raw bytes (fixed length)
        s.Serialize(ProfileKey);                     // length-prefixed UTF-8
        s.Serialize(CapacityReportedAtBom);
        s.Serialize(BlockSize);
        s.Serialize(StartedUtc);                     // ticks

        // Plan — enough to resume with an IDENTICAL cadence/chunking, without re-resolving.
        s.Serialize(Plan.SampleCount);
        s.Serialize(Plan.BodySampleCount);
        s.Serialize(Plan.TailSampleCount);
        s.Serialize(Plan.BlockSize);
        s.Serialize(Plan.BlocksPerChunk);
        s.Serialize(Plan.ChunkSize);
        s.Serialize(Plan.TailBlocksPerChunk);
        s.Serialize(Plan.TailChunkSize);
        s.Serialize(Plan.TailCapacityFraction);
        s.Serialize(Plan.NumCheckpoints);
    }

    public static ITapeSerializable? ConstructFrom(TapeDeserializer d)
    {
        if (!d.ValidateSignature())
            return null;                             // wrong signature/version → not our record

        var runId = new Guid(d.DeserializeBytes(16) ?? throw new FormatException("RunId"));
        string profileKey = d.DeserializeString();
        long capacity = d.DeserializeInt64();
        uint blockSize = d.DeserializeUInt32();
        DateTime started = d.DeserializeDateTime();

        var plan = new TapeCalibrationPlan(
            d.DeserializeInt32(),                    // SampleCount
            d.DeserializeInt32(),                    // BodySampleCount
            d.DeserializeInt32(),                    // TailSampleCount
            d.DeserializeUInt32(),                   // BlockSize
            d.DeserializeInt32(),                    // BlocksPerChunk
            d.DeserializeInt32(),                    // ChunkSize
            d.DeserializeInt32(),                    // TailBlocksPerChunk
            d.DeserializeInt32(),                    // TailChunkSize
            d.DeserializeDouble(),                   // TailCapacityFraction
            d.DeserializeInt32());                   // NumCheckpoints

        return new TapeCalibrationRunHeader(runId, profileKey, capacity, blockSize, started, plan);
    }
}

/// <summary>
/// Written at each body checkpoint. CUMULATIVE and self-contained: a single valid read fully restores
/// run state (bytes written so far, all samples, the EW landmark if seen). Small — ~16 bytes per sample,
/// so ≤ ~16 KB even near the end — comfortably inside one calibration block.
/// <para>
/// <see cref="BytesWritten"/> is the byte count as of the FM that PRECEDES this checkpoint block (i.e.
/// before the "FM + checkpoint block" pair is written). On resume the tape is repositioned BOP-side of
/// that FM and the pair is rewritten from the restored state, reproducing identical byte accounting.
/// </para>
/// </summary>
public sealed record TapeCalibrationCheckpoint(
    Guid RunId,
    int Index,
    long BytesWritten,
    (long ActualWritten, long ReportedRemaining)? EarlyWarning,
    IReadOnlyList<(long ActualWritten, long ReportedRemaining)> Samples) : ITapeSerializable
{
    public void SerializeTo(TapeSerializer s)
    {
        s.SerializeSignature();

        s.Serialize(RunId.ToByteArray());
        s.Serialize(Index);
        s.Serialize(BytesWritten);

        s.Serialize(EarlyWarning.HasValue);
        if (EarlyWarning is { } ew)
        {
            s.Serialize(ew.ActualWritten);
            s.Serialize(ew.ReportedRemaining);
        }

        s.Serialize(Samples.Count);
        foreach (var (aw, rr) in Samples)
        {
            s.Serialize(aw);
            s.Serialize(rr);
        }
    }

    public static ITapeSerializable? ConstructFrom(TapeDeserializer d)
    {
        if (!d.ValidateSignature())
            return null;

        var runId = new Guid(d.DeserializeBytes(16) ?? throw new FormatException("RunId"));
        int index = d.DeserializeInt32();
        long bytesWritten = d.DeserializeInt64();

        (long, long)? ew = null;
        if (d.DeserializeBoolean())
            ew = (d.DeserializeInt64(), d.DeserializeInt64());

        int count = d.DeserializeInt32();
        var samples = new List<(long ActualWritten, long ReportedRemaining)>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
            samples.Add((d.DeserializeInt64(), d.DeserializeInt64()));

        return new TapeCalibrationCheckpoint(runId, index, bytesWritten, ew, samples);
    }
}

/// <summary>
/// Frames an <see cref="ITapeSerializable"/> calibration record for on-tape storage with a CRC-32 guard,
/// so a torn tail record is DETECTED (and the resume walk steps back) rather than silently deserialized
/// into garbage. Reuses the library's <see cref="HashingStream"/> / <see cref="Crc32"/> plumbing.
/// <para>
/// Wire framing: <c>[int32 payloadLen][payload][4-byte crc]</c>, where <c>payload</c> is the record's own
/// <see cref="ITapeSerializable.SerializeTo"/> output (signature + fields) and <c>crc</c> is CRC-32 over
/// that payload — kept OUTSIDE the hashed span. The whole frame is copied into the front of a full block;
/// the block's remaining bytes are caller-supplied random padding (ignored on read-back).
/// </para>
/// </summary>
public static class TapeCalibrationRecord
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

/// <summary>
/// Raw, verdict-free deltas produced by <see cref="TapeCalibrator.Recalibrate"/>: how the freshly
/// re-measured tail moved the key figures versus the existing calibration. This is DATA, not advice —
/// the caller (service / UI) decides whether the shift is small enough to keep the reassessed calibration
/// or large enough to warrant a full re-run. The convenience fractions are signed (new − old).
/// </summary>
public readonly record struct TapeRecalibrationDelta(
    long OldEwToEomDistance, long NewEwToEomDistance,
    long OldCapacityActual, long NewCapacityActual,
    long OldPhantomFreeAtEom, long NewPhantomFreeAtEom)
{
    /// <summary>Signed relative shift of the EW→EOM distance (the most critical figure), or 0 if old was 0.</summary>
    public double EwShiftFraction
        => OldEwToEomDistance > 0 ? (double)(NewEwToEomDistance - OldEwToEomDistance) / OldEwToEomDistance : 0.0;

    /// <summary>Signed relative shift of the measured actual capacity, or 0 if old was 0.</summary>
    public double CapacityShiftFraction
        => OldCapacityActual > 0 ? (double)(NewCapacityActual - OldCapacityActual) / OldCapacityActual : 0.0;

    /// <summary>Signed relative shift of the phantom-free-at-EOM figure, or 0 if old was 0.</summary>
    public double PhantomShiftFraction
        => OldPhantomFreeAtEom > 0 ? (double)(NewPhantomFreeAtEom - OldPhantomFreeAtEom) / OldPhantomFreeAtEom : 0.0;
}
