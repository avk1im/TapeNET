using TapeLibNET.Format;
using TapeLibNET.Legacy;

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
//  FORMAT (Design-Format-v2 §5.5): records are written as 2.1 block frames
//  (Record ‖ CRC-64, magic first). Legacy frames ([len][payload][crc32]) of
//  earlier runs keep reading, block by block — so a trail may mix both, and a
//  legacy run resumes and recalibrates unchanged.
//
//  NOTE: checkpoints are laid down in the BODY only (never the tail), so the
//  last checkpoint is always PRE-tail — exactly the restart point Resume needs
//  and the re-measure point Recalibrate needs.
// =============================================================================

/// <summary>Wire form of the early-warning landmark — a nested group; its absence means "no EW seen".</summary>
/// <remarks>
/// A group rather than two optional scalars: the EW point legitimately carries a ReportedRemaining of 0 (LTO-3 collapses
///  to 0 exactly at EW), which default elision would drop. A group is present or absent as a whole.
/// </remarks>
internal sealed class TapeCalibrationEwWire
{
    public long ActualWritten;
    public long ReportedRemaining;

    /// <summary>Group schema (no record kind).</summary>
    public static readonly TapeSchema<TapeCalibrationEwWire> Schema = new()
    {
        { 1, w => w.ActualWritten,     (w, v) => w.ActualWritten = v,     FieldFlags.Required },
        { 2, w => w.ReportedRemaining, (w, v) => w.ReportedRemaining = v, FieldFlags.Required },
    };
}

/// <summary>Wire form of the 2.1 calibration checkpoint (record kind <c>CalibrationCheckpoint</c>, §5.5).</summary>
internal sealed class TapeCalibrationCheckpointWire
{
    public Guid RunId;
    public int Index;
    public long BytesWritten;
    public byte[] Samples = [];
    public TapeCalibrationEwWire? EarlyWarning;

    public static readonly TapeSchema<TapeCalibrationCheckpointWire> Schema = new(TapeRecordKind.CalibrationCheckpoint)
    {
        // ── scalars 1–31 ──
        { 1,  w => w.RunId,        (w, v) => w.RunId = v,        FieldFlags.Required },
        { 2,  w => w.Index,        (w, v) => w.Index = v,        FieldFlags.Required },
        { 3,  w => w.BytesWritten, (w, v) => w.BytesWritten = v, FieldFlags.Required },
        // ── bulk / groups 48–63 ──
        { 48, w => w.Samples,      (w, v) => w.Samples = v },                                  // delta-coded (TapeSampleCoder)
        { 49, w => w.EarlyWarning, (w, v) => w.EarlyWarning = v, TapeCalibrationEwWire.Schema },  // absent ⇔ no EW
    };
}

/// <summary>
/// Written at each body checkpoint. CUMULATIVE and self-contained: a single valid read fully restores run state (bytes
///  written so far, all samples, the EW landmark if seen). Samples are delta-coded on tape (a few bytes each), so a
///  checkpoint stays comfortably inside one calibration block.
/// <para>
/// <see cref="BytesWritten"/> is the byte count as of the FM that PRECEDES this checkpoint block (i.e. before the
///  "FM + checkpoint block" pair is written). On resume the tape is repositioned BOP-side of that FM and the pair is
///  rewritten from the restored state, reproducing identical byte accounting.
/// </para>
/// </summary>
public sealed record TapeCalibrationCheckpoint(
    Guid RunId,
    int Index,
    long BytesWritten,
    (long ActualWritten, long ReportedRemaining)? EarlyWarning,
    IReadOnlyList<(long ActualWritten, long ReportedRemaining)> Samples) : ITapeFramedRecord<TapeCalibrationCheckpoint>
{
    #region *** Format 2.1 ***

    /// <inheritdoc/>
    public TapeRecordKind RecordKind => TapeRecordKind.CalibrationCheckpoint;

    /// <inheritdoc/>
    public void WriteBody(TapeFieldWriter fields) => TapeCalibrationCheckpointWire.Schema.Write(fields, new TapeCalibrationCheckpointWire
    {
        RunId = RunId,
        Index = Index,
        BytesWritten = BytesWritten,
        Samples = TapeSampleCoder.Encode(Samples),
        EarlyWarning = EarlyWarning is { } ew
            ? new TapeCalibrationEwWire { ActualWritten = ew.ActualWritten, ReportedRemaining = ew.ReportedRemaining }
            : null,
    });

    /// <inheritdoc/>
    public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.CalibrationCheckpoint;

    /// <inheritdoc/>
    public static TapeCalibrationCheckpoint ReadBody(TapeFieldReader fields)
    {
        TapeCalibrationCheckpointWire w = TapeCalibrationCheckpointWire.Schema.Read(fields, new TapeCalibrationCheckpointWire());
        return new TapeCalibrationCheckpoint(
            w.RunId, w.Index, w.BytesWritten,
            w.EarlyWarning is { } ew ? (ew.ActualWritten, ew.ReportedRemaining) : null,
            TapeSampleCoder.Decode(w.Samples));
    }

    #endregion

    #region *** Legacy read ***

    /// <summary>Parses a LEGACY checkpoint frame (<c>[int32 len][payload][crc32]</c>). Never throws.</summary>
    public static TapeFrameStatus TryReadLegacy(ReadOnlySpan<byte> block, out TapeCalibrationCheckpoint? record)
        => LegacyFramer.TryUnpack(block.ToArray(), block.Length, LegacyCheckpointReader.ReadCheckpoint, out record);

    #endregion
}

/// <summary>
/// Frames calibration records for on-tape storage with a CRC guard, so a torn tail record is DETECTED (and the resume
///  walk steps back) rather than silently deserialized into garbage.
/// </summary>
/// <remarks>
/// Writes format 2.1 (<see cref="TapeFrame"/>, <see cref="TapeFramer.PackHeader"/>); reads both formats, block by block,
///  so a trail mixing legacy and 2.1 checkpoints reads correctly. Kept distinct from <see cref="TapeFramer"/> to allow
///  future calibration-specific logic.
/// </remarks>
public static class TapeCalibrationFramer
{
    /// <summary>The 2.1 block frame (<c>Record ‖ CRC-64</c>) of a checkpoint.</summary>
    public static byte[] Pack(TapeCalibrationCheckpoint checkpoint) => TapeFrame.Pack(checkpoint);

    /// <summary>The 2.1 block frame of a run header — for the run-block shape on drives without a standard header block.</summary>
    public static byte[] Pack(TapeCalibrationHeader header) => TapeFramer.PackHeader(header);

    /// <summary>
    /// Parses a framed record out of a full block read back from tape and verifies its CRC, in either format. Returns
    ///  the record, or <see langword="null"/> when the block is not one of our records, is torn, or fails the CRC — the
    ///  exact signals the resume walk treats as "step back to the previous checkpoint".
    /// </summary>
    public static T? Unpack<T>(byte[] block, int length) where T : class, ITapeFramedRecord<T>
    {
        ArgumentNullException.ThrowIfNull(block);
        ReadOnlySpan<byte> data = block.AsSpan(0, Math.Clamp(length, 0, block.Length));
        return TapeFrame.TryUnpackWithLegacy(data, out T? record, out _, out _) == TapeFrameStatus.Ok ? record : null;
    }

    /// <summary>A run header in either format, or <see langword="null"/> for anything else — including another header kind.</summary>
    public static TapeCalibrationHeader? UnpackHeader(byte[] block, int length)
        => TapeFramer.UnpackHeader<TapeCalibrationHeader>(block, length);
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

/// <summary>
/// Read-only snapshot of what a cartridge holds, produced by <see cref="TapeCalibrator.InspectMedia"/>
/// WITHOUT writing anything. Lets a UI (or service) decide whether to offer <see cref="TapeCalibrator.Resume"/>
/// / <see cref="TapeCalibrator.Recalibrate"/> and show run identity + progress, before committing to a
/// destructive operation. Present ⇒ a valid calibration header was found; <see cref="IsResumable"/> ⇒ a
/// CRC-valid checkpoint of that run also exists.
/// </summary>
public sealed record TapeCalibrationMediaInfo(
    TapeCalibrationHeader Header,
    TapeCalibrationCheckpoint? LastCheckpoint)
{
    /// <summary>The run's unique id (from the header).</summary>
    public Guid RunId => Header.RunId;

    /// <summary>The drive+media profile key the run was recorded against.</summary>
    public string ProfileKey => Header.ProfileKey;

    /// <summary>Driver-reported capacity at BOM captured at the start of the run.</summary>
    public long CapacityReportedAtBom => Header.CapacityReportedAtBom;

    /// <summary>When the run started (UTC).</summary>
    public DateTime StartedUtc => Header.StartedUtc;

    /// <summary>True when a CRC-valid checkpoint of this run exists — i.e. <see cref="TapeCalibrator.Resume"/>
    ///  / <see cref="TapeCalibrator.Recalibrate"/> can proceed. False when the run died before its first
    ///  checkpoint (or every checkpoint is torn): the cartridge is inspectable but not resumable.</summary>
    public bool IsResumable => LastCheckpoint is not null;

    /// <summary>Bytes written as of the last good checkpoint (0 when none) — the resume restart point.</summary>
    public long CheckpointedBytes => LastCheckpoint?.BytesWritten ?? 0L;

    /// <summary>Index of the last good checkpoint, or -1 when none.</summary>
    public int CheckpointIndex => LastCheckpoint?.Index ?? -1;

    /// <summary>Whether the EW landmark was already captured by the last checkpoint.</summary>
    public bool EarlyWarningCaptured => LastCheckpoint?.EarlyWarning is not null;

    /// <summary>
    /// Progress hint in 0..1 = last-checkpoint bytes / BOM-reported capacity. Because checkpoints are
    /// BODY-ONLY (they stop just before the tail), a COMPLETED run reads ≈ (1 − TailCapacityFraction)
    /// (~0.95), and an interrupted one reads proportionally less — a good "how far did it get" figure.
    /// </summary>
    public double ProgressFraction =>
        Header.CapacityReportedAtBom > 0
            ? Math.Clamp((double)CheckpointedBytes / Header.CapacityReportedAtBom, 0.0, 1.0)
            : 0.0;

    /// <summary>
    /// Heuristic (no extra tape I/O): the last checkpoint is within one checkpoint-interval of the tail
    /// start, so the run most likely REACHED the tail and COMPLETED — favor Recalibrate. Otherwise it was
    /// interrupted mid-body — favor Resume. Fuzzy near the very end, by nature.
    /// </summary>
    public bool AppearsComplete
    {
        get
        {
            long cap = Math.Max(1L, Header.CapacityReportedAtBom);
            return LastCheckpoint is not null
                && CheckpointedBytes >= Header.Plan.TailStartBytes(cap) - Header.Plan.CheckpointInterval(cap);
        }
    }
}
