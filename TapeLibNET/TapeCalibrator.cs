using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using Windows.Win32.Foundation;

namespace TapeLibNET;

/// <summary>
/// A progress sample emitted during a calibration run, suitable for <see cref="IProgress{T}"/>.
/// </summary>
public readonly record struct TapeCalibrationProgress(
    long BytesWritten,
    long ReportedRemaining,
    long PositionBlock,
    bool EarlyWarning,
    bool EndOfMedium,
    string Phase)
{
    /// <summary>
    /// EXPERIMENTAL cross-check: the drive's OWN remaining-capacity figure read directly over SCSI
    /// (LOG SENSE, Tape Capacity page 0x31), bypassing the Windows tape class driver. -1 when not
    /// available (non-LTO drive, or the probe failed). Declared as a non-positional init property so
    /// existing positional <c>new TapeCalibrationProgress(...)</c> calls keep compiling unchanged.
    /// </summary>
    public long LtoReportedRemaining { get; init; } = -1L;
}

/// <summary>
/// One-shot, destructive early-warning / capacity calibrator. Rewinds the loaded scratch medium,
/// writes incompressible blocks (hardware compression off) to hard EOM while sampling the driver's
/// <c>ReportedRemaining</c> against the true bytes-written, and captures the EW landmark. Produces an
/// <see cref="ITapeCalibration"/> the application can persist and later hand to
/// <see cref="TapeDrive.SetCalibration"/>.
/// <reamrks>
/// <para>
/// Sampling is TWO-PHASE (see <see cref="TapeCalibrationPlan"/>): a coarse BODY across most of the
/// medium, then a fine TAIL over the EW → EOM region (entered at physical EW or the last few percent
/// of capacity, whichever comes first). Real LTO runs proved a uniform cadence far too coarse in that
/// tail — LTO-4/6 keep tens/hundreds of GB of phantom-free runway past EW, while LTO-3 collapses its
/// reported figure to 0 the instant EW fires — so the tail earns a dedicated, proportionally finer chunk.
/// </para>
/// <para>
/// RESUMABLE: the run lays down a self-describing on-tape trail (a header block at BOM plus body
/// checkpoints, filemark-delimited — see <see cref="TapeCalibrationRecord"/>). A run interrupted by a
/// transport fault can be continued with <see cref="Resume"/> from the last good checkpoint, and a
/// COMPLETE calibration cartridge can be re-measured cheaply after a firmware update / drive swap with
/// <see cref="Recalibrate"/>. The cartridge is the single source of truth — no host sidecar.
/// </para>
/// <para>
/// Conceptually create-use-discard: <c>new TapeCalibrator(drive).Run()</c>. Backend-agnostic — it drives
/// only the public <see cref="TapeDrive"/> surface (including filemark write/space), so it works
/// identically for the Win32, remote, and virtual backends. The one EXPERIMENTAL exception is the optional
/// native (LTO) remaining probe, which reaches into a Win32 backend directly. Cancellation is cooperative
/// via <see cref="IsAbortRequested"/>. This class does NOT judge drive-profile matching — that is the
/// caller's / service layer's responsibility.
/// </para>
/// </reamrks>
/// </summary>
public sealed class TapeCalibrator(TapeDrive drive) : TapeDriveHolder<TapeCalibrator>(drive)
{
    #region *** Constants ***

    // Above this |LTO − driver| gap we log the divergence at Information (else Trace). The reported
    //  COLLAPSE (driver 0 while the drive's own LOG SENSE still claims capacity) is ALWAYS logged at
    //  Information regardless of this threshold, since it is the exact quirk the tail phase exists to tame.
    private const long c_ltoDivergenceTraceThreshold = 1L * 1024 * 1024 * 1024; // 1 GB

    #endregion

    #region *** Options & Cancellation ***

    /// <summary>Run options; defaults are sensible for LTO and most linear-tape drives.</summary>
    public TapeCalibrationOptions Options { get; init; } = new();

    /// <summary>
    /// Set by the caller to request an early, graceful abort. The run checks it between writes and
    /// returns <see langword="null"/> with <see cref="WIN32_ERROR.ERROR_CANCELLED"/> when observed.
    /// </summary>
    public bool IsAbortRequested { get; set; }

    #endregion

    #region *** Run state ***

    /// <summary>
    /// Mutable state carried through the shared write loop, so a fresh <see cref="Run"/> and a
    /// <see cref="Resume"/> / <see cref="Recalibrate"/> continuation drive the identical machinery —
    /// the only difference is how the state is seeded (empty at BOM vs. restored from a checkpoint).
    /// </summary>
    private sealed class RunState
    {
        public Guid RunId;
        public long BytesWritten;
        public int CheckpointIndex;
        // public bool InTail; -- not needed, set dynamically in RunLoop(): inTail = state.BytesWritten >= tailStartBytes;
        public (long ActualWritten, long ReportedRemaining)? EwPoint;
        public readonly List<(long ActualWritten, long ReportedRemaining)> Samples = [];
        public readonly List<(long ActualWritten, long LtoRemaining)> LtoSamples = [];
    }

    #endregion

    #region *** Public API ***

    /// <summary>
    /// Executes a FRESH calibration from BOM. DESTRUCTIVE: overwrites the medium from BOT of the content
    /// partition. Writes a header block at BOM and body checkpoints as it goes, so an interruption can be
    /// continued later via <see cref="Resume"/>. Leaves the tape at (or just past) EOM.
    /// </summary>
    /// <param name="progress">Optional progress sink (fired on each sample and on EW/EOM).</param>
    /// <returns>The calibration, or <see langword="null"/> on failure/abort (see <see cref="IErrorManageable.LastError"/>).</returns>
    public ITapeCalibration? Run(IProgress<TapeCalibrationProgress>? progress = null)
    {
        ResetError();
        IsAbortRequested = false;

        if (!Drive.IsMediaLoaded)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            LogErrorAsDebug("Calibration: no media loaded");
            return null;
        }

        RunGuard guard = new(this);
        try
        {
            if (!PrepareDrive(out TapeCalibrationPlan plan, out uint blockSize))
                return null;

            // Establish the driver-reported remaining at BOM (leaves the tape rewound at BOM).
            if (!EstablishBomCapacity(out long capacityReportedAtBom))
                return null;

            // Seed fresh state and lay down the header block as File 0 at BOM.
            var state = new RunState { RunId = Guid.NewGuid(), BytesWritten = 0, CheckpointIndex = 0 };
            state.Samples.Add((0L, capacityReportedAtBom));

            var header = new TapeCalibrationRunHeader(
                state.RunId, Drive.DriveProfileKey, capacityReportedAtBom, blockSize, DateTime.UtcNow, plan);

            using var records = new RecordBlockWriter(this, blockSize);
            if (!records.Emit(header, ref state.BytesWritten, writeLeadingFilemark: false))
            {
                LogErrorAsDebug("Calibration: failed to write run header");
                return null;
            }

            m_logger.LogInformation(
                "{Prefix}: Calibration start — run {RunId}, profile '{Key}', reportedCapacityAtBom {Cap}, " +
                "blockSize {Bs}, samples {Samples} (body {Body} + tail {Tail}), checkpoints {Chk}",
                LogPrefix, state.RunId, Drive.DriveProfileKey, capacityReportedAtBom, blockSize,
                plan.SampleCount, plan.BodySampleCount, plan.TailSampleCount, plan.NumCheckpoints);

            return RunLoop(plan, capacityReportedAtBom, state, records, progress);
        }
        finally
        {
            guard.Restore();
        }
    }

    /// <summary>
    /// Resumes a calibration interrupted by a transport fault, continuing from the last good on-tape
    /// checkpoint on the CURRENTLY LOADED cartridge. Reads the header, walks back to the last valid
    /// checkpoint (CRC-verified, same <c>RunId</c>), restores run state, and writes on to EOM.
    /// <para>
    /// Returns <see langword="null"/> (error state set) when no resumable run is found on the cartridge —
    /// e.g. a blank/foreign tape, or a run that failed before the first checkpoint. The caller may then
    /// fall back to a fresh <see cref="Run"/>. Does NOT verify that the cartridge belongs to this drive:
    /// loading the correct cartridge, and deciding to trust the result, is the caller's responsibility.
    /// </para>
    /// </summary>
    public ITapeCalibration? Resume(IProgress<TapeCalibrationProgress>? progress = null)
    {
        ResetError();
        IsAbortRequested = false;

        RunGuard guard = new(this);
        try
        {
            return ResumeCore(progress);
        }
        finally
        {
            guard.Restore();
        }
    }

    /// <summary>
    /// FAST post-firmware / post-swap re-measurement. Given an <paramref name="existing"/> calibration and
    /// its (retained) calibration cartridge, re-runs only from the last body checkpoint to the new EOM —
    /// a few percent of the medium instead of a full pass — and rebuilds a reassessed calibration:
    /// the body curve is REUSED from the trail (its actual-remaining values auto-translate to the freshly
    /// measured EOM), while <c>CapacityActual</c>, the tail curve, the EW landmark
    /// (<c>EwToEomDistance</c>, <c>EarlyWarning.ReportedRemaining</c>) and <c>PhantomFreeAtEom</c> are
    /// re-measured. <c>ReportedCapacityAtBom</c> is a BOM quantity and is carried over from the header,
    /// NOT re-measured.
    /// <para>
    /// Returns the reassessed <see cref="ITapeCalibration"/> (or <see langword="null"/> if re-measurement
    /// was not possible) together with a verdict-free <see cref="TapeRecalibrationDelta"/> of how the key
    /// figures moved. This is low-level DATA: the caller decides whether to keep the reassessed calibration
    /// or schedule a full <see cref="Run"/>.
    /// </para>
    /// </summary>
    public (ITapeCalibration? Reassessed, TapeRecalibrationDelta Delta) Recalibrate(
        ITapeCalibration existing, IProgress<TapeCalibrationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ResetError();
        IsAbortRequested = false;

        RunGuard guard = new(this);
        try
        {
            ITapeCalibration? reassessed = ResumeCore(progress);
            if (reassessed is null)
                return (null, default);

            var delta = new TapeRecalibrationDelta(
                existing.EwToEomDistance, reassessed.EwToEomDistance,
                existing.CapacityActual, reassessed.CapacityActual,
                existing.PhantomFreeAtEom, reassessed.PhantomFreeAtEom);

            m_logger.LogInformation(
                "{Prefix}: Recalibrate done — EW {OldEw}→{NewEw} ({EwPct:+0.0%;-0.0%}), capacity {OldCap}→{NewCap} " +
                "({CapPct:+0.0%;-0.0%}), phantom {OldPh}→{NewPh}",
                LogPrefix, delta.OldEwToEomDistance, delta.NewEwToEomDistance, delta.EwShiftFraction,
                delta.OldCapacityActual, delta.NewCapacityActual, delta.CapacityShiftFraction,
                delta.OldPhantomFreeAtEom, delta.NewPhantomFreeAtEom);

            return (reassessed, delta);
        }
        finally
        {
            guard.Restore();
        }
    }

    #endregion

    #region *** Resume core (shared by Resume & Recalibrate) ***

    /// <summary>
    /// Reads the on-tape header, restores state from the last good checkpoint, repositions, and writes on
    /// to EOM. Shared by <see cref="Resume"/> and <see cref="Recalibrate"/> (they differ only in what they
    /// report to the caller). Assumes the neutralizing <see cref="RunGuard"/> is already in effect.
    /// </summary>
    private TapeCalibration? ResumeCore(IProgress<TapeCalibrationProgress>? progress)
    {
        // Position at BOM and read the run header (read-only; shared with InspectMedia).
        TapeCalibrationRunHeader? header = ReadRunHeader(out uint blockSize, out byte[] recordBuffer);
        if (header is null)
            return null;   // no media / no valid header — error state already set

        // Prefer the ORIGINAL plan (identical cadence/chunking); re-derive chunks if the drive now rounds
        //  the block size differently than when the run started.
        TapeCalibrationPlan plan = header.Plan;
        if (plan.BlockSize != blockSize)
            plan = plan.WithBlockSize(blockSize);

        long capacityReportedAtBom = header.CapacityReportedAtBom;

        // --- Walk back from EOD to the last CRC-valid checkpoint of this run. ---
        TapeCalibrationCheckpoint? checkpoint = FindLastCheckpoint(header.RunId, recordBuffer, out int nBack);
        if (checkpoint is null)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_DATA);
            LogErrorAsDebug("Resume: no valid checkpoint found — run failed before the first checkpoint");
            return null;
        }

        // --- Restore run state from the checkpoint. ---
        var state = new RunState
        {
            RunId = header.RunId,
            BytesWritten = checkpoint.BytesWritten,
            CheckpointIndex = checkpoint.Index,
            EwPoint = checkpoint.EarlyWarning,
        };
        state.Samples.AddRange(checkpoint.Samples);

        m_logger.LogInformation(
            "{Prefix}: Resume — run {RunId}, from checkpoint {Idx} ({Back} filemarks from EOD) at {Bytes} bytes ({Samples} samples restored, EW {Ew})",
            LogPrefix, state.RunId, checkpoint.Index, nBack, state.BytesWritten, state.Samples.Count,
            state.EwPoint is { } e ? $"{e.ActualWritten}/{e.ReportedRemaining}" : "(none)");

        // --- Reposition BOP-side of the FM preceding the good checkpoint (FindLastCheckpoint() brought us right after it) and re-establish the boundary. ---
        if (!Drive.MoveToNextFilemark(-1))
        {
            SyncErrorFrom(Drive);
            LogErrorAsDebug("Resume: failed to reposition for continuation");
            return null;
        }

        using var records = new RecordBlockWriter(this, blockSize);

        // Rewrite the FM + checkpoint block from the restored state, yielding a pristine boundary and
        //  reproducing the exact byte accounting the original run had at this point.
        var reCheckpoint = new TapeCalibrationCheckpoint(
            state.RunId, state.CheckpointIndex, state.BytesWritten, state.EwPoint, state.Samples);
        if (!records.Emit(reCheckpoint, ref state.BytesWritten, writeLeadingFilemark: true))
        {
            LogErrorAsDebug("Resume: failed to rewrite boundary checkpoint");
            return null;
        }
        state.CheckpointIndex++;

        return RunLoop(plan, capacityReportedAtBom, state, records, progress);
    }

    #endregion

    #region *** Media inspection (read-only) ***

    /// <summary>
    /// Reads the on-tape run header (File 0 at BOM) and, when present, the last CRC-valid checkpoint,
    /// WITHOUT writing anything — safe to call speculatively (e.g. from a UI on media load, to decide
    /// whether to offer Resume / Recalibrate). Returns a <see cref="TapeCalibrationMediaInfo"/> describing
    /// the run and its resumability, or <see langword="null"/> (error state set, per convention) when no
    /// valid calibration header is present on the loaded cartridge.
    /// <para>
    /// Non-destructive: like the run verbs it positions on the content partition and sets the drive's block
    /// size / compression via <see cref="PrepareDrive"/>, but it never writes to tape, so the calibration
    /// trail is preserved. Unlike the run verbs it does NOT neutralize the caller's loaded calibrations or
    /// EW reserve (no <c>RunGuard</c>) — a pure read leaves that state untouched.
    /// </para>
    /// </summary>
    public TapeCalibrationMediaInfo? InspectMedia()
    {
        ResetError();

        TapeCalibrationRunHeader? header = ReadRunHeader(out _, out byte[] recordBuffer);
        if (header is null)
            return null;   // no media / no valid header — error state already set

        // Locate the last CRC-valid checkpoint of this run (read-only; null ⇒ header-only / all torn).
        TapeCalibrationCheckpoint? last = FindLastCheckpoint(header.RunId, recordBuffer, out _);

        ResetError();
        return new TapeCalibrationMediaInfo(header, last);
    }

    /// <summary>
    /// Positions at BOM and reads + CRC-parses the run header (File 0). Shared by <see cref="ResumeCore"/>
    /// and <see cref="InspectMedia"/>. On success the tape sits just past the header block and
    /// <paramref name="recordBuffer"/> is sized to one calibration block for reuse by
    /// <see cref="FindLastCheckpoint"/>. Returns <see langword="null"/> (error state set) when the drive
    /// cannot be prepared or no valid header is present. WRITES NOTHING.
    /// </summary>
    private TapeCalibrationRunHeader? ReadRunHeader(out uint blockSize, out byte[] recordBuffer)
    {
        blockSize = 0;
        recordBuffer = [];

        if (!Drive.IsMediaLoaded)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            LogErrorAsDebug("Calibration inspect: no media loaded");
            return null;
        }

        if (!PrepareDrive(out _, out blockSize))
            return null;

        // Rewind to and read the header block (File 0 at BOM). PrepareDrive already positions at BOM;
        //  the explicit rewind is belt-and-suspenders and matches the original resume path.
        if (!Drive.Rewind())
        {
            SyncErrorFrom(Drive);
            LogErrorAsDebug("Calibration inspect: failed to rewind to header");
            return null;
        }

        recordBuffer = new byte[blockSize];
        TapeCalibrationRunHeader? header = ReadRecord<TapeCalibrationRunHeader>(recordBuffer);
        if (header is null)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_DATA);
            LogErrorAsDebug("Calibration inspect: no valid calibration header on this cartridge");
            return null;
        }

        return header;
    }

    #endregion

    #region *** Shared write loop ***

    /// <summary>
    /// The common write-to-EOM loop: writes incompressible payload chunks, samples the driver's
    /// ReportedRemaining against true bytes-written, captures the EW landmark, enters the fine TAIL phase
    /// at EW / the last capacity fraction, emits BODY checkpoints at the planned interval, and builds the
    /// final calibration at hard EOM. Drives only the public <see cref="TapeDrive"/> surface, so it works
    /// on every backend (including virtual).
    /// </summary>
    private TapeCalibration? RunLoop(
        TapeCalibrationPlan plan, long capacityReportedAtBom, RunState state,
        RecordBlockWriter records, IProgress<TapeCalibrationProgress>? progress)
    {
        // --- Prepare an incompressible payload chunk (whole blocks, BODY size — the largest we write) ---
        using TapeWriteBufferPool pool = new();
        var buffer = pool.Rent(plan.ChunkSize);
        Random.Shared.NextBytes(buffer.Data()); // random ⇒ incompressible; reused every write (compression is off)

        // --- Cadence: coarse body, fine tail; the tail starts at EW or the last few percent ---
        long bodySampleInterval = plan.BodySampleInterval(capacityReportedAtBom);
        long tailSampleInterval = plan.TailSampleInterval(capacityReportedAtBom);
        long tailStartBytes = plan.TailStartBytes(capacityReportedAtBom);
        long checkpointInterval = plan.CheckpointInterval(capacityReportedAtBom);

        // EXPERIMENTAL LOG SENSE cross-check — gated (proven redundant across LTO-3/4/6), off by default.
        TapeDriveWin32Backend? ltoBackend = Drive.Backend as TapeDriveWin32Backend;
        bool probeLto = Options.CaptureLtoRemaining && ltoBackend?.IsLto == true;

        // Recompute tail state from position. This is SUFFICIENT — no need to persist "inTail" — ONLY
        //  because checkpoints are BODY-ONLY: a restored checkpoint is always strictly before tailStartBytes
        //  (it was written while !inTail), so this always yields false on resume, and the tail is re-entered
        //  naturally as writing continues (physical-EW runtime state was reset by RunGuard).
        //  ⚠ If checkpointing were ever allowed in the tail, the physical-EW-fired-early path would make this
        //    recompute wrong — we would then have to persist inTail (or IsPhysicalEarlyWarningSeen) in the checkpoint.
        bool inTail = state.BytesWritten >= tailStartBytes; int currentChunk = inTail ? plan.TailChunkSize : plan.ChunkSize;
        long sampleInterval = inTail ? tailSampleInterval : bodySampleInterval;

        long nextSample = state.BytesWritten;                                   // sample promptly on entry/resume
        long nextCheckpoint = state.BytesWritten + checkpointInterval;

        // Local: read the drive's native (LOG SENSE) remaining, record it against the driver figure, and
        //  trace any divergence — spotlighting the COLLAPSE (driver 0 while the drive still claims space).
        long SampleLtoRemaining(long reportedRemaining, long actualWritten)
        {
            if (!probeLto || ltoBackend is null)
                return -1L;

            if (!ltoBackend.GetLtoRemainingCapacity(out long ltoRem, out _))
                return -1L;

            state.LtoSamples.Add((actualWritten, ltoRem));

            long divergence = ltoRem - reportedRemaining;
            if (reportedRemaining <= 0 && ltoRem > 0)
                m_logger.LogInformation(
                    "{Prefix}: Reported COLLAPSE — driver 0, LOG SENSE {Lto} still remaining (actualWritten {Aw})",
                    LogPrefix, ltoRem, actualWritten);
            else if (Math.Abs(divergence) > c_ltoDivergenceTraceThreshold)
                m_logger.LogInformation(
                    "{Prefix}: Reported/LOG SENSE divergence {Div} (driver {Rep}, LOG SENSE {Lto}, actualWritten {Aw})",
                    LogPrefix, divergence, reportedRemaining, ltoRem, actualWritten);
            else
                m_logger.LogTrace(
                    "{Prefix}: LOG SENSE remaining {Lto} vs driver {Rep} (actualWritten {Aw})",
                    LogPrefix, ltoRem, reportedRemaining, actualWritten);

            return ltoRem;
        }

        try
        {
            while (true)
            {
                if (CheckForAbort())
                    return null;

                int written = Drive.WriteDirect(buffer.Array, buffer.Offset, currentChunk,
                    out _ /* tapemark */, out _ /* ew (gated on reserve, unused here) */, out bool eom);
                state.BytesWritten += written;

                // Capture the EW landmark exactly once, at first occurrence. We read Drive.IsEarlyWarning
                //  (set on every write regardless of the requested reserve) rather than the WriteDirect ew
                //  out-param, which is suppressed while the run holds no reserve.
                if (Drive.IsEarlyWarning && state.EwPoint is null)
                {
                    long rrEw = Drive.GetReportedContentRemaining();
                    state.EwPoint = (state.BytesWritten, rrEw);
                    long ltoEw = SampleLtoRemaining(rrEw, state.BytesWritten);
                    state.Samples.Add((state.BytesWritten, rrEw));

                    progress?.Report(new TapeCalibrationProgress(
                        state.BytesWritten, rrEw, Drive.GetCurrentBlock(), EarlyWarning: true, EndOfMedium: false, "early-warning")
                        { LtoReportedRemaining = ltoEw });

                    m_logger.LogInformation("{Prefix}: Calibration EW at {Bytes} bytes (reportedRemaining {RR})",
                        LogPrefix, state.BytesWritten, rrEw);
                }

                // Enter the fine-grained TAIL phase at whichever comes first: the drive's physical EW, or
                //  the last TailCapacityFraction of capacity. From here the write chunk shrinks, the cadence
                //  tightens, and CHECKPOINTS STOP (a failure here has already written ~95%).
                if (!inTail && (Drive.IsPhysicalEarlyWarningSeen || state.BytesWritten >= tailStartBytes))
                {
                    inTail = true;
                    currentChunk = plan.TailChunkSize;
                    sampleInterval = tailSampleInterval;
                    nextSample = state.BytesWritten; // sample immediately at tail entry

                    m_logger.LogInformation(
                        "{Prefix}: Calibration entering TAIL at {Bytes} bytes (chunk {Chunk}, interval {Int}) — {Reason}",
                        LogPrefix, state.BytesWritten, currentChunk, sampleInterval,
                        Drive.IsPhysicalEarlyWarningSeen ? "physical early warning" : "last capacity fraction");
                }

                if (eom)
                {
                    long rrEom = Drive.GetReportedContentRemaining();
                    long ltoEom = SampleLtoRemaining(rrEom, state.BytesWritten);
                    state.Samples.Add((state.BytesWritten, rrEom));

                    progress?.Report(new TapeCalibrationProgress(
                        state.BytesWritten, rrEom, Drive.GetCurrentBlock(), EarlyWarning: state.EwPoint is not null, EndOfMedium: true, "eom")
                        { LtoReportedRemaining = ltoEom });

                    m_logger.LogInformation("{Prefix}: Calibration EOM at {Bytes} bytes (reportedRemaining {RR}) — actual capacity",
                        LogPrefix, state.BytesWritten, rrEom);
                    break;
                }

                // No progress and no EOM ⇒ a genuine write error; stop. Prior checkpoints stay on tape,
                //  so this run remains resumable from the last one.
                if (written == 0)
                {
                    SyncErrorFrom(Drive);
                    if (WentBad)
                    {
                        LogErrorAsDebug("Calibration: write failed before EOM");
                        return null;
                    }

                    // Defensive: avoid a busy spin if the drive returns 0 without error.
                    SetError(WIN32_ERROR.ERROR_IO_DEVICE);
                    LogErrorAsWarning("Calibration: write returned 0 bytes without EOM — stopping");
                    return null;
                }

                if (state.BytesWritten >= nextSample)
                {
                    long rr = Drive.GetReportedContentRemaining();
                    long lto = SampleLtoRemaining(rr, state.BytesWritten);
                    state.Samples.Add((state.BytesWritten, rr));

                    progress?.Report(new TapeCalibrationProgress(
                        state.BytesWritten, rr, Drive.GetCurrentBlock(), EarlyWarning: state.EwPoint is not null, EndOfMedium: false,
                        inTail ? "sampling-tail" : "sampling")
                        { LtoReportedRemaining = lto });

                    nextSample += sampleInterval;
                }

                // Emit a resumable BODY checkpoint at the planned interval (never in the tail). The record
                //  is written as a FM + one full block; BytesWritten is advanced to count the block.
                if (!inTail && state.BytesWritten >= nextCheckpoint)
                {
                    var checkpoint = new TapeCalibrationCheckpoint(
                        state.RunId, state.CheckpointIndex, state.BytesWritten, state.EwPoint, state.Samples);

                    if (!records.Emit(checkpoint, ref state.BytesWritten, writeLeadingFilemark: true))
                    {
                        // A checkpoint write failed — the last checkpoint still stands, so resume remains
                        //  possible. Surface the error and stop.
                        SyncErrorFrom(Drive);
                        LogErrorAsWarning("Calibration: failed to write checkpoint — stopping (run stays resumable)");
                        return null;
                    }

                    m_logger.LogTrace("{Prefix}: Calibration checkpoint {Idx} at {Bytes} bytes ({N} samples)",
                        LogPrefix, state.CheckpointIndex, checkpoint.BytesWritten, state.Samples.Count);

                    state.CheckpointIndex++;
                    nextCheckpoint = state.BytesWritten + checkpointInterval;
                }
            }

            long capacityActual = state.BytesWritten;
            if (capacityActual <= 0)
            {
                SetError(WIN32_ERROR.ERROR_IO_DEVICE);
                LogErrorAsDebug("Calibration: reached EOM with zero bytes written");
                return null;
            }

            TapeCalibration calibration = TapeCalibration.FromMeasurements(
                Drive.DriveProfileKey, capacityReportedAtBom, capacityActual, state.Samples, state.EwPoint,
                state.LtoSamples.Count > 0 ? state.LtoSamples : null);

            m_logger.LogInformation(
                "{Prefix}: Calibration done — actualCapacity {Act} ({Pct:F1}% of reported at BOM), " +
                "phantomFreeAtEom {Phantom}, EW {Ew}, points {N} (LOG SENSE points {Lto})",
                LogPrefix, capacityActual,
                calibration.ReportedCapacityAtBom > 0 ? 100.0 * capacityActual / calibration.ReportedCapacityAtBom : 0.0,
                calibration.PhantomFreeAtEom,
                state.EwPoint is { } e ? $"{e.ActualWritten} bytes / RR {e.ReportedRemaining}" : "(none)",
                state.Samples.Count, state.LtoSamples.Count);

            ResetError();
            return calibration;
        }
        catch (Exception ex)
        {
            if (Drive.LastErrorWin32 == WIN32_ERROR.NO_ERROR)
                SetError(WIN32_ERROR.ERROR_IO_DEVICE);
            else
                SyncErrorFrom(Drive);

            m_logger.LogError(ex, "{Prefix}: Calibration: exception during run", LogPrefix);
            throw; // we don't catch exceptions here -- the caller is reposible for handling them
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    #endregion

    #region *** Setup helpers ***

    /// <summary>
    /// Positions on the content partition, sets the run block size, and re-derives the plan if the drive
    /// rounded the requested (maximum) block size to its own granularity. Common to fresh runs and resumes.
    /// </summary>
    private bool PrepareDrive(out TapeCalibrationPlan plan, out uint blockSize)
    {
        plan = Options.ResolveFor(Drive);
        blockSize = 0;

        // Position at BOM of the content partition FIRST, so the new block size applies to it.
        if (!Drive.MoveToPartition(MediaPartition.Content) || !Drive.Rewind())
        {
            SyncErrorFrom(Drive);
            LogErrorAsDebug("Calibration: failed to rewind content partition");
            return false;
        }

        if (!Drive.SetBlockSize(plan.BlockSize))
        {
            SyncErrorFrom(Drive);
            LogErrorAsDebug("Calibration: failed to set block size");
            return false;
        }

        blockSize = Drive.BlockSize; // effective value the drive accepted
        if (blockSize == 0)
        {
            if (Drive.LastErrorWin32 == WIN32_ERROR.NO_ERROR)
                SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            else
                SyncErrorFrom(Drive);

            LogErrorAsDebug("Calibration: drive reports zero block size");
            return false;
        }

        // The drive may round the requested max to its own granularity; re-derive the chunks so
        //  ChunkSize/TailChunkSize stay consistent with what the hardware actually accepted.
        if (blockSize != plan.BlockSize)
        {
            m_logger.LogWarning("{Prefix}: Calibration — drive adjusted block size {Requested} → {Effective}; re-deriving chunks",
                LogPrefix, plan.BlockSize, blockSize);

            plan = plan.WithBlockSize(blockSize);
        }

        // Hardware compression OFF so incompressible bytes map 1:1 to tape position.
        Drive.SetHardwareCompression(false);
        return true;
    }

    /// <summary>
    /// Determines the driver-reported remaining at BOM (writes a gap file first so a non-empty medium does
    /// not report partial remaining), then rewinds to BOM ready for the header write. Only for FRESH runs.
    /// </summary>
    private bool EstablishBomCapacity(out long capacityReportedAtBom)
    {
        capacityReportedAtBom = 0;
        try
        {
            // To get correct reported remaining at BOM, we must first write a small block to the tape
            //  -- otherwise, in case the media isn't empty, the drive will report partial remaining!
            if (!Drive.WriteGapFile())
            {
                SyncErrorFrom(Drive);
                LogErrorAsDebug("Calibration: failed to write gap file");
                return false;
            }

            if (!Drive.Rewind())
            {
                SyncErrorFrom(Drive);
                LogErrorAsDebug("Calibration: failed to rewind");
                return false;
            }

            // The reported-capacity side of the run intentionally tracks the DRIVER-facing Remaining
            //  figure, not the true physical capacity, so emulations that still claim phantom free
            //  space at hard EOM remain visible in the resulting calibration.
            capacityReportedAtBom = Drive.GetReportedContentRemaining();
            if (capacityReportedAtBom <= 0)
            {
                if (Drive.LastErrorWin32 == WIN32_ERROR.NO_ERROR)
                    SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
                else
                    SyncErrorFrom(Drive);

                LogErrorAsDebug("Calibration: drive reports zero capacity at BOM");
                return false;
            }
        }
        catch (Exception ex)
        {
            if (Drive.LastErrorWin32 == WIN32_ERROR.NO_ERROR)
                SetError(WIN32_ERROR.ERROR_IO_DEVICE);
            else
                SyncErrorFrom(Drive);

            m_logger.LogError(ex, "{Prefix}: Calibration: exception during BOM capacity setup", LogPrefix);
            throw; // we don't catch exceptions here -- the caller is reposible for handling them
        }

        return true;
    }

    #endregion

    #region *** Resume read helpers ***

    /// <summary>Reads one record block at the current position and unpacks+CRC-checks it as <typeparamref name="T"/>.</summary>
    private T? ReadRecord<T>(byte[] recordBuffer) where T : class, ITapeSerializable
    {
        int read = Drive.ReadDirect(recordBuffer, 0, recordBuffer.Length, out _, out _);
        if (read <= 0)
            return null;

        return TapeCalibrationRecord.Unpack<T>(recordBuffer, read);
    }

    /// <summary>
    /// Walks back from EOD one filemark at a time, reading the checkpoint block that each filemark
    /// precedes, until it finds a CRC-valid checkpoint belonging to <paramref name="runId"/>. Returns the
    /// checkpoint and (via <paramref name="filemarksBack"/>) how many filemarks back it sits, so the caller
    /// can reposition for the continuation. Returns <see langword="null"/> at BOP (no resumable run).
    /// </summary>
    private TapeCalibrationCheckpoint? FindLastCheckpoint(Guid runId, byte[] recordBuffer, out int filemarksBack)
    {
        filemarksBack = 0;

        // Seek to EOD ONCE, then walk backward checkpoint-by-checkpoint — a SINGLE reverse pass, so a
        //  wrong cartridge (ordinary backup sets, no calibration trail) is rejected in one traversal
        if (!Drive.FastforwardToEnd(MediaPartition.Content))
        {
            m_logger.LogInformation(
                "{Prefix}: Resume — seek-to-EOD failed ({Err}); cartridge is likely full to EOM w/o EOD mark, " +
                    "proceeding from the current (end-of-data) position",
                LogPrefix, Drive.LastErrorMessage);
            ResetError();
        }

        // Back up before the last filemark; none present ⇒ no resumable run (header-only / blank).
        if (!Drive.MoveToNextFilemark(-1))
        {
            ResetError();   // BEGINNING_OF_PARTITION ⇒ nothing to resume
            return null;
        }

        for (int n = 1; ; n++)
        {
            // Forward over that filemark lands at the start of the checkpoint block it precedes.
            if (!Drive.MoveToNextFilemark(1))
            {
                SyncErrorFrom(Drive);
                return null;
            }

            TapeCalibrationCheckpoint? cp = ReadRecord<TapeCalibrationCheckpoint>(recordBuffer);
            if (cp is not null && cp.RunId == runId)
            {
                filemarksBack = n;
                return cp;               // valid, same run → done
            }

            // Torn, foreign, or non-record block ⇒ step back one more filemark and retry.
            m_logger.LogTrace("{Prefix}: Resume — checkpoint at -{N} FM invalid; stepping back", LogPrefix, n);

            // Reading advanced the head into this checkpoint's payload, so stepping back to the PREVIOUS
            //  checkpoint crosses TWO filemarks (this checkpoint's own FM + the previous one).
            if (!Drive.MoveToNextFilemark(-2))
            {
                ResetError();   // BEGINNING_OF_PARTITION ⇒ no more checkpoints to try
                return null;
            }
        }
    }

    #endregion

    #region *** Record block writer ***

    /// <summary>
    /// Writes calibration RECORDS (header, checkpoints) into single full-size blocks: the framed record
    /// (see <see cref="TapeCalibrationRecord.Pack"/>) at the front, random padding for the rest. A fixed
    /// random block is reused across records (padding content is immaterial with compression off; only the
    /// front is overwritten per record), so no per-record allocation churn. The full block is counted into
    /// the run's <c>bytesWritten</c>.
    /// </summary>
    private sealed class RecordBlockWriter : IDisposable
    {
        private readonly TapeCalibrator m_cal;
        private readonly int m_blockSize;
        private readonly byte[] m_block;
        private bool m_tooLargeWarned;

        public RecordBlockWriter(TapeCalibrator cal, uint blockSize)
        {
            m_cal = cal;
            m_blockSize = (int)blockSize;
            m_block = new byte[m_blockSize];
            Random.Shared.NextBytes(m_block); // random padding, filled once
        }

        /// <summary>
        /// Optionally writes a leading filemark, then writes <paramref name="record"/> as one full block,
        /// advancing <paramref name="bytesWritten"/> by the block size. Returns <see langword="false"/> on
        /// any write failure (error state set on the drive), or when the framed record does not fit one
        /// block (checkpointing is then skipped — the run still completes, just not resumably).
        /// </summary>
        public bool Emit(ITapeSerializable record, ref long bytesWritten, bool writeLeadingFilemark)
        {
            byte[] frame = TapeCalibrationRecord.Pack(record);
            if (frame.Length > m_blockSize)
            {
                if (!m_tooLargeWarned)
                {
                    m_cal.m_logger.LogWarning(
                        "{Prefix}: Calibration record ({Len} B) exceeds block size ({Bs} B) — skipping checkpoints; run will not be resumable",
                        m_cal.LogPrefix, frame.Length, m_blockSize);
                    m_tooLargeWarned = true;
                }
                return true; // non-fatal: keep calibrating, just don't checkpoint
            }

            if (writeLeadingFilemark && !m_cal.Drive.WriteFilemark(1))
            {
                m_cal.SyncErrorFrom(m_cal.Drive);
                return false;
            }

            // Overwrite only the front with the frame; the remainder stays random padding.
            Array.Copy(frame, m_block, frame.Length);

            int written = m_cal.Drive.WriteDirect(m_block, 0, m_blockSize, out _, out _, out _);
            if (written != m_blockSize)
            {
                m_cal.SyncErrorFrom(m_cal.Drive);
                return false;
            }

            bytesWritten += written;
            return true;
        }

        public void Dispose() { /* nothing owned beyond the managed array */ }
    }

    #endregion

    #region *** Run guard (neutralize + restore drive state) ***

    /// <summary>
    /// Neutralizes any active reserve and loaded calibrations for the duration of a run so
    /// <see cref="TapeDrive.WriteDirect"/> surfaces the RAW physical early warning the run must measure
    /// (not a logical/calibrated remapping), and restores them afterward regardless of how the run ended.
    /// </summary>
    private readonly struct RunGuard
    {
        private readonly TapeCalibrator m_cal;
        private readonly long m_savedReserve;
        private readonly List<ITapeCalibration> m_savedCalibrations;

        public RunGuard(TapeCalibrator cal)
        {
            m_cal = cal;
            m_savedReserve = cal.Drive.EarlyWarning;
            m_savedCalibrations = [.. cal.Drive.Calibrations];

            cal.Drive.RemoveAllCalibrations();
            cal.Drive.SetEarlyWarning(0);            // clears reserve AND enables backend physical-EW reporting
            cal.Drive.ResetEarlyWarningRuntime();
        }

        public void Restore()
        {
            foreach (var c in m_savedCalibrations)
                m_cal.Drive.AddCalibration(c);

            m_cal.Drive.SetEarlyWarning(m_savedReserve);
            m_cal.Drive.ResetEarlyWarningRuntime();
        }
    }

    #endregion

    #region *** Abort ***

    private bool CheckForAbort()
    {
        if (IsAbortRequested)
        {
            SetError(WIN32_ERROR.ERROR_CANCELLED);
            m_logger.LogWarning("{Prefix}: Calibration aborted by caller", LogPrefix);
            return true;
        }
        return false;
    }

    #endregion
}
