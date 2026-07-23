using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;

namespace TapeLibNET;

/// <summary>
/// Options controlling a calibration run. Defaults target a correct, deterministic measurement:
/// the maximum block size, hardware compression off, and ~<see cref="SampleCount"/> curve points
/// spread across the medium.
/// </summary>
public readonly record struct TapeCalibrationOptions
{
    /// <summary>Approximate number of <c>ReportedRemaining → ActualRemaining</c> curve points to record. Default 100.</summary>
    public int SampleCount { get; init; }

    /// <summary>Block size to use during the run, in bytes. 0 = the drive's maximum block size.</summary>
    public uint BlockSize { get; init; }

    /// <summary>Target payload size per <c>WriteDirect</c> call, in bytes (rounded down to whole blocks). Default 4 MB.</summary>
    public long ChunkBytesTarget { get; init; }

    /// <summary>Smallest spacing between curve samples, in bytes. Default 256 MB.</summary>
    public long MinSampleInterval { get; init; }

    public TapeCalibrationOptions()
    {
        SampleCount = 100;
        BlockSize = 0;
        ChunkBytesTarget = 4L * 1024 * 1024;
        MinSampleInterval = 256L * 1024 * 1024;
    }
}

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

        // --- Configure the drive for a deterministic byte→position mapping ---
        uint blockSize = Options.BlockSize != 0 ? Options.BlockSize : Drive.MaximumBlockSize;
        if (!Drive.SetBlockSize(blockSize))
        {
            SyncErrorFrom(Drive);
            LogErrorAsDebug("Calibration: failed to set block size");
            return null;
        }
        blockSize = Drive.BlockSize; // effective value the drive accepted
        if (blockSize == 0)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            LogErrorAsDebug("Calibration: drive reports zero block size");
            return null;
        }

        // Hardware compression OFF so incompressible bytes map 1:1 to tape position.
        Drive.SetHardwareCompression(false);

        long capacityReported = Drive.Capacity;

        // --- Position at BOT of the content partition ---
        if (!Drive.MoveToPartition(MediaPartition.Content) || !Drive.Rewind())
        {
            SyncErrorFrom(Drive);
            LogErrorAsDebug("Calibration: failed to rewind content partition");
            return null;
        }

        // --- Prepare an incompressible payload chunk (whole blocks) ---
        long targetChunk = Math.Max(blockSize, Options.ChunkBytesTarget);
        int blocksPerChunk = (int)Math.Max(1, targetChunk / blockSize);
        int chunkBytes = checked(blocksPerChunk * (int)blockSize);

        byte[] buffer = new byte[chunkBytes];
        Random.Shared.NextBytes(buffer); // random ⇒ incompressible; reused every write (compression is off)

        // --- Sample cadence ---
        long sampleInterval = capacityReported > 0
            ? Math.Max(capacityReported / Math.Max(1, Options.SampleCount), Options.MinSampleInterval)
            : Options.MinSampleInterval;

        m_logger.LogInformation(
            "{Prefix}: Calibration start — profile '{Key}', reportedCapacity {Cap}, blockSize {Bs}, chunk {Chunk}, sampleInterval {Int}",
            LogPrefix, Drive.DriveProfileKey, capacityReported, blockSize, chunkBytes, sampleInterval);

        // --- Write to hard EOM, sampling as we go ---
        var samples = new List<(long ActualWritten, long ReportedRemaining)>();
        (long ActualWritten, long ReportedRemaining)? ewPoint = null;

        long bytesWritten = 0;
        long nextSample = 0;

        while (true)
        {
            if (IsAbortRequested)
            {
                SetError(WIN32_ERROR.ERROR_CANCELLED);
                m_logger.LogWarning("{Prefix}: Calibration aborted by caller at {Bytes} bytes", LogPrefix, bytesWritten);
                return null;
            }

            int written = Drive.WriteDirect(buffer, 0, chunkBytes,
                out _ /* tapemark */, out bool ew, out bool eom);
            bytesWritten += written;

            // Capture the EW landmark exactly once, at first occurrence.
            if (ew && ewPoint is null)
            {
                long rrEw = Drive.GetRemainingCapacity();
                ewPoint = (bytesWritten, rrEw);
                progress?.Report(new TapeCalibrationProgress(
                    bytesWritten, rrEw, Drive.GetCurrentBlock(), EarlyWarning: true, EndOfMedium: false, "early-warning"));
                m_logger.LogInformation("{Prefix}: Calibration EW at {Bytes} bytes (reportedRemaining {RR})",
                    LogPrefix, bytesWritten, rrEw);
            }

            if (eom)
            {
                long rrEom = Drive.GetRemainingCapacity();
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
                long rr = Drive.GetRemainingCapacity();
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

        ITapeCalibration calibration = TapeCalibration.FromMeasurements(
            Drive.DriveProfileKey, capacityReported, capacityActual, samples, ewPoint);

        m_logger.LogInformation(
            "{Prefix}: Calibration done — actualCapacity {Act} ({Pct:F1}% of reported), EW {Ew}, points {N}",
            LogPrefix, capacityActual,
            capacityReported > 0 ? 100.0 * capacityActual / capacityReported : 0.0,
            ewPoint is { } e ? $"{e.ActualWritten} bytes / RR {e.ReportedRemaining}" : "(none)",
            samples.Count);

        ResetError();
        return calibration;
    }

    #endregion
}
