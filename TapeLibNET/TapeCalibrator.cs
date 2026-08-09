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
    string Phase);

/// <summary>
/// One-shot, destructive early-warning / capacity calibrator. Rewinds the loaded scratch medium,
/// writes incompressible blocks (hardware compression off) to hard EOM while sampling the driver's
/// <c>ReportedRemaining</c> against the true bytes-written, and captures the EW landmark. Produces an
/// <see cref="ITapeCalibration"/> the application can persist and later hand to
/// <see cref="TapeDrive.SetCalibration"/>.
/// <para>
/// Conceptually create-use-discard: <c>new TapeCalibrator(drive).Run()</c>. Backend-agnostic — it
/// drives only the public <see cref="TapeDrive"/> surface, so it works identically for the Win32,
/// remote, and virtual backends. Cancellation is cooperative via <see cref="IsAbortRequested"/>
/// (poll/flip from the caller's async wrapper), mirroring <c>TapeFileAgent</c>.
/// </para>
/// </summary>
public sealed class TapeCalibrator(TapeDrive drive) : TapeDriveHolder<TapeCalibrator>(drive)
{
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

        // The drive may round the requested max to its own granularity; re-derive the plan so
        //  ChunkSize stays consistent with what the hardware actually accepted.
        if (blockSize != plan.BlockSize)
        {
            m_logger.LogWarning("{Prefix}: Calibration — drive adjusted block size {Requested} → {Effective}; re-deriving chunk",
                LogPrefix, plan.BlockSize, blockSize);
            plan = TapeCalibrationPlan.Create(plan.SampleCount, blockSize, plan.BlocksPerChunk);
        }

        int chunkSize = plan.ChunkSize;

        // Hardware compression OFF so incompressible bytes map 1:1 to tape position.
        Drive.SetHardwareCompression(false);

        // --- Position at BOT of the content partition ---
        if (!Drive.MoveToPartition(MediaPartition.Content) || !Drive.Rewind())
        {
            SyncErrorFrom(Drive);
            LogErrorAsDebug("Calibration: failed to rewind content partition");
            return null;
        }

        // The reported-capacity side of the run intentionally tracks the DRIVER-facing Remaining
        //  figure, not the true physical capacity, so emulations that still claim phantom free
        //  space at hard EOM remain visible in the resulting calibration.
        long capacityReportedAtBom = Drive.GetReportedContentRemaining();
        if (capacityReportedAtBom <= 0)
        {
            if (Drive.LastErrorWin32 == WIN32_ERROR.NO_ERROR)
                SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            else
                SyncErrorFrom(Drive);
            LogErrorAsDebug("Calibration: drive reports zero capacity at BOM");
            return null;
        }

        // --- Prepare an incompressible payload chunk (whole blocks) ---
        using TapeWriteBufferPool pool = new();
        var buffer = pool.Rent(chunkSize);
        Random.Shared.NextBytes(buffer.Data()); // random ⇒ incompressible; reused every write (compression is off)

        // --- Sample cadence: never finer than one chunk, ~SampleCount points across the medium ---
        long sampleInterval = Math.Max(plan.ChunkSize, capacityReportedAtBom / plan.SampleCount);

        m_logger.LogInformation(
            "{Prefix}: Calibration start — profile '{Key}', reportedCapacityAtBom {Cap}, blockSize {Bs}, chunk {Chunk}, sampleInterval {Int}",
            LogPrefix, Drive.DriveProfileKey, capacityReportedAtBom, blockSize, chunkSize, sampleInterval);

        // --- Write to hard EOM, sampling as we go ---
        var samples = new List<(long ActualWritten, long ReportedRemaining)>();
        (long ActualWritten, long ReportedRemaining)? ewPoint = null;

        long bytesWritten = 0;
        long nextSample = 0;

        samples.Add((ActualWritten: 0L, ReportedRemaining: capacityReportedAtBom));

        try
        {
            while (true)
            {
                if (IsAbortRequested)
                {
                    SetError(WIN32_ERROR.ERROR_CANCELLED);
                    m_logger.LogWarning("{Prefix}: Calibration aborted by caller at {Bytes} bytes", LogPrefix, bytesWritten);
                    return null;
                }

                int written = Drive.WriteDirect(buffer.Array, buffer.Offset, chunkSize,
                    out _ /* tapemark */, out _ /* ew (gated on reserve, unused here) */, out bool eom);
                bytesWritten += written;

                // Capture the EW landmark exactly once, at first occurrence. We read Drive.IsEarlyWarning
                //  (set on every write regardless of the requested reserve) rather than the WriteDirect ew
                //  out-param, which is suppressed while the run holds no reserve.
                if (Drive.IsEarlyWarning && ewPoint is null)
                {
                    long rrEw = Drive.GetReportedContentRemaining();
                    ewPoint = (bytesWritten, rrEw);
                    progress?.Report(new TapeCalibrationProgress(
                        bytesWritten, rrEw, Drive.GetCurrentBlock(), EarlyWarning: true, EndOfMedium: false, "early-warning"));
                    m_logger.LogInformation("{Prefix}: Calibration EW at {Bytes} bytes (reportedRemaining {RR})",
                        LogPrefix, bytesWritten, rrEw);
                }

                if (eom)
                {
                    long rrEom = Drive.GetReportedContentRemaining();
                    samples.Add((bytesWritten, rrEom));
                    progress?.Report(new TapeCalibrationProgress(
                        bytesWritten, rrEom, Drive.GetCurrentBlock(), EarlyWarning: ewPoint is not null, EndOfMedium: true, "eom"));
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
                    samples.Add((bytesWritten, rr));
                    progress?.Report(new TapeCalibrationProgress(
                        bytesWritten, rr, Drive.GetCurrentBlock(), EarlyWarning: ewPoint is not null, EndOfMedium: false, "sampling"));
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
                Drive.DriveProfileKey, capacityReportedAtBom, capacityActual, samples, ewPoint);

            m_logger.LogInformation(
                "{Prefix}: Calibration done — actualCapacity {Act} ({Pct:F1}% of reported at BOM), " +
                "phantomFreeAtEom {Phantom}, EW {Ew}, points {N}",
                LogPrefix, capacityActual,
                calibration.ReportedCapacityAtBom > 0 ? 100.0 * capacityActual / calibration.ReportedCapacityAtBom : 0.0,
                calibration.PhantomFreeAtEom,
                ewPoint is { } e ? $"{e.ActualWritten} bytes / RR {e.ReportedRemaining}" : "(none)",
                samples.Count);

            ResetError();
            return calibration;
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