using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
/// <para>
/// Sampling is TWO-PHASE (see <see cref="TapeCalibrationPlan"/>): a coarse BODY across most of the
/// medium, then a fine TAIL over the EW → EOM region (entered at physical EW or the last few percent
/// of capacity, whichever comes first). Real LTO runs proved a uniform cadence far too coarse in that
/// tail — LTO-4 keeps ~31 GB of phantom-free runway past EW, while LTO-3 collapses its reported
/// figure to 0 the instant EW fires — so the tail earns a dedicated, proportionally finer chunk.
/// </para>
/// <para>
/// Conceptually create-use-discard: <c>new TapeCalibrator(drive).Run()</c>. Backend-agnostic — it
/// drives only the public <see cref="TapeDrive"/> surface, so it works identically for the Win32,
/// remote, and virtual backends. The one EXPERIMENTAL exception is the optional native (LTO) remaining
/// probe, which reaches into a Win32 backend directly to cross-check the driver figure. Cancellation is
/// cooperative via <see cref="IsAbortRequested"/> (poll/flip from the caller's async wrapper),
/// mirroring <c>TapeFileAgent</c>.
/// </para>
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

    #region *** Run ***

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

    /// <summary>
    /// Executes the calibration. DESTRUCTIVE: overwrites the medium from BOT of the content partition.
    /// Leaves the tape at (or just past) EOM; the caller typically reformats/reloads afterward.
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

        // Neutralize any active reserve and loaded calibrations for the duration so WriteDirect surfaces
        //  the RAW physical early warning the run must measure (not a logical/calibrated remapping). Also
        //  enables the backend to report its physical EW (SetEarlyWarning always requests backend EW).
        //  All restored in the finally below.
        long savedReserve = Drive.EarlyWarning;
        var savedCalibrations = new List<ITapeCalibration>(Drive.Calibrations);
        Drive.RemoveAllCalibrations();
        Drive.SetEarlyWarning(0);            // clears reserve AND enables backend physical-EW reporting
        Drive.ResetEarlyWarningRuntime();

        // --- Resolve caller intent into a concrete, drive-specific plan ---
        TapeCalibrationPlan plan = Options.ResolveFor(Drive);

        // --- First of all, position at BOM of the content partition ---
        //  to ensure the new block size applies to the content partition!
        if (!Drive.MoveToPartition(MediaPartition.Content) || !Drive.Rewind())
        {
            SyncErrorFrom(Drive);
            LogErrorAsDebug("Calibration: failed to rewind content partition");
            return null;
        }

        // --- Configure the drive for a deterministic byte→position mapping ---
        if (!Drive.SetBlockSize(plan.BlockSize))
        {
            SyncErrorFrom(Drive);
            LogErrorAsDebug("Calibration: failed to set block size");
            return null;
        }

        uint blockSize = Drive.BlockSize; // effective value the drive accepted
        if (blockSize == 0)
        {
            if (Drive.LastErrorWin32 == WIN32_ERROR.NO_ERROR)
                SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            else
                SyncErrorFrom(Drive);

            LogErrorAsDebug("Calibration: drive reports zero block size");
            return null;
        }

        // The drive may round the requested max to its own granularity; re-derive the chunks so
        //  ChunkSize/TailChunkSize stay consistent with what the hardware actually accepted.
        if (blockSize != plan.BlockSize)
        {
            m_logger.LogWarning("{Prefix}: Calibration — drive adjusted block size {Requested} → {Effective}; re-deriving chunks",
                LogPrefix, plan.BlockSize, blockSize);

            plan = plan.WithBlockSize(blockSize);
        }

        // Now move to BOM and determine the capacity reported at BOM
        long capacityReportedAtBom;
        try
        {
            // Hardware compression OFF so incompressible bytes map 1:1 to tape position.
            Drive.SetHardwareCompression(false);

            // To get correct reported remaining at BOM, we must first write a small block to the tape
            //  -- otherwise, in case the media isn't empty, the drive will report partial remaining!
            if (!Drive.WriteGapFile())
            {
                SyncErrorFrom(Drive);
                LogErrorAsDebug("Calibration: failed to write gap file");
                return null;
            }

            // Now rewind again
            if (!Drive.Rewind())
            {
                SyncErrorFrom(Drive);
                LogErrorAsDebug("Calibration: failed to rewind");
                return null;
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
                return null;
            }
        }
        catch (Exception ex)
        {
            if (Drive.LastErrorWin32 == WIN32_ERROR.NO_ERROR)
                SetError(WIN32_ERROR.ERROR_IO_DEVICE);
            else
                SyncErrorFrom(Drive);

            m_logger.LogError(ex, "{Prefix}: Calibration: exception during setup", LogPrefix);
            throw; // we don't catch exceptions here -- the caller is reposible for handling them
        }

        // --- Prepare an incompressible payload chunk (whole blocks, BODY size — the largest we write) ---
        using TapeWriteBufferPool pool = new();
        var buffer = pool.Rent(plan.ChunkSize);
        Random.Shared.NextBytes(buffer.Data()); // random ⇒ incompressible; reused every write (compression is off)

        // --- Two-phase sample cadence: coarse body, fine tail; the tail starts at EW or the last few percent ---
        long bodySampleInterval = plan.BodySampleInterval(capacityReportedAtBom);
        long tailSampleInterval = plan.TailSampleInterval(capacityReportedAtBom);
        long tailStartBytes = plan.TailStartBytes(capacityReportedAtBom);

        // EXPERIMENTAL: probe the drive's own remaining figure over SCSI (LOG SENSE 0x31), alongside the
        //  driver-reported one, so we can decide offline whether it dodges the tail quirks (esp. the LTO-3
        //  collapse). Only meaningful on a Win32 LTO backend; a no-op (−1) otherwise.
        //  Proven redundant across LTO-3/4/6, so off by default in Options. Can re-enable for new models
        TapeDriveWin32Backend? ltoBackend = Drive.Backend as TapeDriveWin32Backend;
        bool probeLto = Options.CaptureLtoRemaining && ltoBackend?.IsLto == true;

        m_logger.LogInformation(
            "{Prefix}: Calibration start — profile '{Key}', reportedCapacityAtBom {Cap}, blockSize {Bs}, " +
            "bodyChunk {BChunk}, tailChunk {TChunk}, bodyInterval {BInt}, tailInterval {TInt}, tailStart {TStart}, " +
            "samples {Samples} (body {Body} + tail {Tail}), ltoProbe {Lto}",
            LogPrefix, Drive.DriveProfileKey, capacityReportedAtBom, blockSize,
            plan.ChunkSize, plan.TailChunkSize, bodySampleInterval, tailSampleInterval, tailStartBytes,
            plan.SampleCount, plan.BodySampleCount, plan.TailSampleCount, probeLto);

        // --- Write to hard EOM, sampling as we go ---
        var samples = new List<(long ActualWritten, long ReportedRemaining)>();
        var ltoSamples = new List<(long ActualWritten, long LtoRemaining)>();
        (long ActualWritten, long ReportedRemaining)? ewPoint = null;
        long bytesWritten = 0;
        long nextSample = 0;

        bool inTail = false;
        int currentChunk = plan.ChunkSize;         // body chunk; shrinks to plan.TailChunkSize in the tail
        long sampleInterval = bodySampleInterval;  // body cadence; tightens to tailSampleInterval in the tail

        // Local: read the drive's native (LOG SENSE) remaining, record it against the driver figure, and
        //  trace any divergence — spotlighting the COLLAPSE (driver 0 while the drive still claims space).
        long SampleLtoRemaining(long reportedRemaining, long actualWritten)
        {
            if (!probeLto || ltoBackend is null)
                return -1L;

            if (!ltoBackend.GetLtoRemainingCapacity(out long ltoRem, out _))
                return -1L;

            ltoSamples.Add((actualWritten, ltoRem));

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

        long ltoAtBom = SampleLtoRemaining(capacityReportedAtBom, 0L);
        samples.Add((ActualWritten: 0L, ReportedRemaining: capacityReportedAtBom));

        try
        {
            while (true)
            {
                if (CheckForAbort())
                    return null;

                int written = Drive.WriteDirect(buffer.Array, buffer.Offset, currentChunk,
                    out _ /* tapemark */, out _ /* ew (gated on reserve, unused here) */, out bool eom);
                bytesWritten += written;

                // Capture the EW landmark exactly once, at first occurrence. We read Drive.IsEarlyWarning
                //  (set on every write regardless of the requested reserve) rather than the WriteDirect ew
                //  out-param, which is suppressed while the run holds no reserve.
                if (Drive.IsEarlyWarning && ewPoint is null)
                {
                    long rrEw = Drive.GetReportedContentRemaining();
                    ewPoint = (bytesWritten, rrEw);
                    long ltoEw = SampleLtoRemaining(rrEw, bytesWritten);
                    samples.Add((bytesWritten, rrEw));

                    progress?.Report(new TapeCalibrationProgress(
                        bytesWritten, rrEw, Drive.GetCurrentBlock(), EarlyWarning: true, EndOfMedium: false, "early-warning")
                        { LtoReportedRemaining = ltoEw });

                    m_logger.LogInformation("{Prefix}: Calibration EW at {Bytes} bytes (reportedRemaining {RR})",
                        LogPrefix, bytesWritten, rrEw);
                }

                // Enter the fine-grained TAIL phase at whichever comes first: the drive's physical EW, or the
                //  last TailCapacityFraction of capacity. From here the write chunk shrinks and the cadence
                //  tightens, so the EW→EOM stretch — where LTO reporting misbehaves — is densely sampled.
                if (!inTail && (Drive.IsPhysicalEarlyWarningSeen || bytesWritten >= tailStartBytes))
                {
                    inTail = true;
                    currentChunk = plan.TailChunkSize;
                    sampleInterval = tailSampleInterval;
                    nextSample = bytesWritten; // sample immediately at tail entry

                    m_logger.LogInformation(
                        "{Prefix}: Calibration entering TAIL at {Bytes} bytes (chunk {Chunk}, interval {Int}) — {Reason}",
                        LogPrefix, bytesWritten, currentChunk, sampleInterval,
                        Drive.IsPhysicalEarlyWarningSeen ? "physical early warning" : "last capacity fraction");
                }

                if (eom)
                {
                    long rrEom = Drive.GetReportedContentRemaining();
                    long ltoEom = SampleLtoRemaining(rrEom, bytesWritten);
                    samples.Add((bytesWritten, rrEom));

                    progress?.Report(new TapeCalibrationProgress(
                        bytesWritten, rrEom, Drive.GetCurrentBlock(), EarlyWarning: ewPoint is not null, EndOfMedium: true, "eom")
                        { LtoReportedRemaining = ltoEom });

                    m_logger.LogInformation("{Prefix}: Calibration EOM at {Bytes} bytes (reportedRemaining {RR}) — actual capacity",
                        LogPrefix, bytesWritten, rrEom);
                    break;
                }

                // No progress and no EOM ⇒ a genuine write error; stop.
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

                if (bytesWritten >= nextSample)
                {
                    long rr = Drive.GetReportedContentRemaining();
                    long lto = SampleLtoRemaining(rr, bytesWritten);
                    samples.Add((bytesWritten, rr));

                    progress?.Report(new TapeCalibrationProgress(
                        bytesWritten, rr, Drive.GetCurrentBlock(), EarlyWarning: ewPoint is not null, EndOfMedium: false,
                        inTail ? "sampling-tail" : "sampling")
                        { LtoReportedRemaining = lto });

                    nextSample += sampleInterval;
                }
            }

            long capacityActual = bytesWritten;
            if (capacityActual <= 0)
            {
                SetError(WIN32_ERROR.ERROR_IO_DEVICE);
                LogErrorAsDebug("Calibration: reached EOM with zero bytes written");
                return null;
            }

            TapeCalibration calibration = TapeCalibration.FromMeasurements(
                Drive.DriveProfileKey, capacityReportedAtBom, capacityActual, samples, ewPoint,
                ltoSamples.Count > 0 ? ltoSamples : null);

            m_logger.LogInformation(
                "{Prefix}: Calibration done — actualCapacity {Act} ({Pct:F1}% of reported at BOM), " +
                "phantomFreeAtEom {Phantom}, EW {Ew}, points {N} (LOG SENSE points {Lto})",
                LogPrefix, capacityActual,
                calibration.ReportedCapacityAtBom > 0 ? 100.0 * capacityActual / calibration.ReportedCapacityAtBom : 0.0,
                calibration.PhantomFreeAtEom,
                ewPoint is { } e ? $"{e.ActualWritten} bytes / RR {e.ReportedRemaining}" : "(none)",
                samples.Count, ltoSamples.Count);

            ResetError();
            return calibration;
        }
        catch (Exception ex)
        {
            if (Drive.LastErrorWin32 == WIN32_ERROR.NO_ERROR)
                SetError(WIN32_ERROR.ERROR_IO_DEVICE);
            else
                SyncErrorFrom(Drive);

            m_logger.LogError(ex, "{Prefix}: Calibration: exception during setup", LogPrefix);
            throw; // we don't catch exceptions here -- the caller is reposible for handling them
        }
        finally
        {
            // Restore the caller's reserve and calibrations regardless of how the run ended.
            foreach (var c in savedCalibrations)
                Drive.AddCalibration(c);

            Drive.SetEarlyWarning(savedReserve);
            Drive.ResetEarlyWarningRuntime();

            pool.Return(buffer);
        }
    }

    #endregion
}
