using TapeLibNET; // TapeFileInfo

namespace TapeLibNET.Services;

// ── Abstract base ────────────────────────────────────────────────────────────

/// <summary>
/// Abstract base for all service-level operation result records.
/// Carries cross-cutting fields shared by every operation type,
///  mirroring <see cref="ServiceOperationRequest"/> on the input side.
/// </summary>
public abstract record ServiceOperationResult
{
    /// <summary>
    /// <c>true</c> when the operation completed without a catastrophic failure.
    /// Partial failures (skipped / failed files) are still reported via
    ///  <see cref="ServiceReportLevel"/> and the file-count properties on derived types.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Summary severity of the outcome, using the same scale as log entries.
    /// Typical mapping: <see cref="ServiceReportLevel.Completed"/> = full success,
    ///  <see cref="ServiceReportLevel.Warning"/> = partial, <see cref="ServiceReportLevel.Failed"/>
    ///  = user abort, <see cref="ServiceReportLevel.Error"/> = catastrophic failure.
    /// </summary>
    public ServiceReportLevel Outcome { get; init; }

    /// <summary>Optional human-readable summary message set by the service.</summary>
    public string? Message { get; init; }

    /// <summary>Non-null when a catastrophic exception terminated the operation.</summary>
    public Exception? Error { get; init; }

    /// <summary>Wall-clock duration of the operation, excluding user-interaction time.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Total bytes read from or written to tape during the operation.</summary>
    public long BytesProcessed { get; init; }

    /// <summary>Number of files that were actually touched (read / written) by the agent.</summary>
    public int FilesProcessed { get; init; }
}

// ── Intermediate: file-level statistics ──────────────────────────────────────

/// <summary>
/// Intermediate abstract record for operations that work on individual files
///  and produce per-file statistics (backup and restore/validate/verify).
/// </summary>
public abstract record FileOperationResult : ServiceOperationResult
{
    /// <summary>Total number of files selected for the operation.</summary>
    public int FilesTotal { get; init; }
    
    /// <summary>Total logical bytes expected for the entire operation (sum of all file lengths).</summary>
    public long BytesTotal { get; init; }

    /// <summary>Files that completed without error.</summary>
    public int FilesSucceeded { get; init; }

    /// <summary>Files that encountered an error (whether skipped or not).</summary>
    public int FilesFailed { get; init; }

    /// <summary>Files that were explicitly skipped (e.g. via "Skip" in an error dialog).</summary>
    public int FilesSkipped { get; init; }

    /// <summary>Whether the user aborted the operation before completion.</summary>
    public bool WasAborted { get; init; }

    /// <summary>Whether a catastrophic error terminated the operation.</summary>
    public bool HasFailed { get; init; }

    /// <summary>
    /// <c>true</c> when all selected files were processed successfully with
    ///  no aborts, failures, or skips.
    /// </summary>
    public virtual bool IsFullSuccess =>
        !WasAborted && !HasFailed && FilesFailed == 0 && FilesSkipped == 0 && FilesProcessed > 0;
}

// ── Backup ───────────────────────────────────────────────────────────────────

/// <summary>
/// Summary statistics returned by a backup operation.
/// Allows the caller to distinguish full success, partial failure, abort,
///  and "no files backed up" without cross-boundary exceptions.
/// </summary>
public sealed record BackupResult : FileOperationResult;

// ── Restore / Validate / Verify ───────────────────────────────────────────────

/// <summary>
/// Summary statistics returned by a restore, validate, or verify operation.
/// </summary>
public sealed record RestoreResult : FileOperationResult
{
    /// <summary>Files that were selected but never encountered on tape.</summary>
    public int FilesMissing => FilesTotal - FilesProcessed;

    /// <inheritdoc/>
    /// <remarks>Also requires zero missing files.</remarks>
    public override bool IsFullSuccess =>
        base.IsFullSuccess && FilesMissing == 0;

    /// <summary>
    /// Per-set dictionary of successfully processed files, populated by the
    ///  progress handler. Kept for post-operation bookkeeping by callers.
    /// </summary>
    public Dictionary<int, List<TapeFileInfo>> ProcessedFiles { get; init; } = [];
}

// ── Calibrate ────────────────────────────────────────────────────────────────

/// <summary>
/// Service-level judgment of a <see cref="CalibrationMode.Recalibrate"/> run: whether the reassessed
/// calibration is close enough to the previous one to keep using, or the drive has shifted enough that a
/// full re-run is advised. This is policy (threshold-based), computed by <c>TapeServiceBase</c> from the
/// raw <see cref="TapeRecalibrationDelta"/> that <see cref="TapeCalibrator.Recalibrate"/> reports.
/// </summary>
public enum RecalibrationVerdict
{
    /// <summary>The reassessed calibration is within tolerance of the existing one — keep using it.</summary>
    Holds,

    /// <summary>The drive's behavior shifted beyond tolerance — a full recalibration is advised.</summary>
    FullRecalibrationAdvised,
}

/// <summary>
/// Summary statistics returned by a calibration operation.
/// </summary>
/// <remarks>
/// To preserve the established operation-triad shape, the inherited "file" counters map
/// calibration chunks → files. <see cref="BytesTotal"/> / <see cref="BytesProcessed"/> remain
/// the more meaningful quantities for callers and UI progress.
/// </remarks>
public sealed record CalibrateResult : FileOperationResult
{
    /// <summary>Which calibration mode produced this result.</summary>
    public CalibrationMode Mode { get; init; } = CalibrationMode.New;

    /// <summary>The calibration produced by the run, or <see langword="null"/> on failure/abort.</summary>
    public ITapeCalibration? Calibration { get; init; }

    /// <summary>Matched drive+media profile key for this run.</summary>
    public string ProfileKey { get; init; } = string.Empty;

    /// <summary>
    /// Quantity (4) — the driver's remaining claim on a virgin cartridge, sampled at BOM at the start of
    /// the run. Compare against <see cref="CapacityActual"/> to see whether the driver inflates capacity
    /// from the first byte.
    /// </summary>
    public long ReportedCapacityAtBom { get; init; }

    /// <summary>
    /// Quantity (5) — the headline number of a calibration run: the phantom free space the driver still
    /// claimed at the instant hard EOM fired. This space does not exist. LTO-4: ~28 GB.
    /// </summary>
    public long PhantomFreeAtEom { get; init; }

    /// <summary>
    /// The total capacity implied by the driver's own figures: <see cref="CapacityActual"/> plus the
    /// <see cref="PhantomFreeAtEom"/> it still claims at hard EOM.
    /// </summary>
    public long ReportedCapacityTotal => CapacityActual + PhantomFreeAtEom;

    /// <summary>True raw capacity measured at hard EOM (bytes) — quantity (1).</summary>
    public long CapacityActual { get; init; }

    /// <summary>Captured EW landmark, or <see langword="null"/> when none was observed.</summary>
    public CalibrationPoint? EarlyWarning { get; init; }

    /// <summary>Bytes still writable when EW fired, or 0 when no EW landmark was observed.</summary>
    public long EwToEomDistance { get; init; }

    /// <summary>Number of points in the calibrated curve.</summary>
    public int CurvePointCount => Calibration?.Curve.Count ?? 0;

    /// <summary>For <see cref="CalibrationMode.Recalibrate"/>: how the key figures moved versus the
    ///  existing calibration, or <see langword="null"/> for New/Resume.</summary>
    public TapeRecalibrationDelta? RecalibrationDelta { get; init; }

    /// <summary>For <see cref="CalibrationMode.Recalibrate"/>: the service's threshold-based verdict, or
    ///  <see langword="null"/> for New/Resume.</summary>
    public RecalibrationVerdict? RecalibrationVerdict { get; init; }

    /// <inheritdoc/>
    public override bool IsFullSuccess => base.IsFullSuccess && Calibration is not null;
}

/// <summary>
/// Result of a non-destructive <see cref="TapeCalibrator.InspectMedia"/> probe, enriched with the
/// service-layer policy (store lookup) that the calibrator itself stays free of. Lets a UI decide
/// which mode to recommend WITHOUT gating anything — inspection is always an optional convenience.
/// </summary>
public sealed record InspectCalibrationMediaResult : ServiceOperationResult
{
    /// <summary>True when a valid calibration run header was found on the loaded cartridge.</summary>
    public bool HasRunHeader { get; init; }

    /// <summary>The drive+media profile key recorded in the header, or empty when no header was found.</summary>
    public string ProfileKey { get; init; } = string.Empty;

    /// <summary>When the inspected run started (UTC), or <see langword="default"/> when no header was found.</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>Driver-reported capacity at BOM, captured at the start of the inspected run.</summary>
    public long CapacityReportedAtBom { get; init; }

    /// <summary>True when a CRC-valid checkpoint of the run exists — i.e. Resume can proceed.</summary>
    public bool HasCheckpoint { get; init; }

    /// <summary>Bytes written as of the last good checkpoint.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Progress hint in 0..1 — see <see cref="TapeCalibrationMediaInfo.ProgressFraction"/>.</summary>
    public double ProgressFraction { get; init; }

    /// <summary>True when the header's profile key matches the currently loaded drive+media.</summary>
    public bool MatchesCurrentDrive { get; init; }

    /// <summary>True when a calibration profile for this run's key is already in the shared store —
    ///  the strongest signal that the run reached the tail and completed.</summary>
    public bool HasStoredCalibration { get; init; }

    /// <summary>The mode the service recommends offering, or <see langword="null"/> when no header was found.</summary>
    public CalibrationMode? RecommendedMode { get; init; }

    /// <summary>Ready-to-display summary of the inspection, for the UI's inspect-result pane.</summary>
    public string Summary { get; init; } = string.Empty;
}

// ── List ──────

/// <summary>
/// Summary result of a list / contents-display operation.
/// Inherits <see cref="ServiceOperationResult.Success"/> and
///  <see cref="ServiceOperationResult.Outcome"/> for uniform error handling.
/// </summary>
public sealed record ListResult : ServiceOperationResult
{
    /// <summary>Number of backup sets listed.</summary>
    public int SetsListed { get; init; }

    /// <summary>Total number of files across all listed sets.</summary>
    public int TotalFiles { get; init; }

    /// <summary>Total bytes across all listed sets.</summary>
    public long TotalBytes { get; init; }

    // ── Convenience factory methods ──────────────────────────────────────────

    /// <summary>Creates a failed <see cref="ListResult"/> with no counts.</summary>
    public static ListResult Failed(string? message = null, Exception? error = null) => new()
    {
        Success = false,
        Outcome = ServiceReportLevel.Error,
        Message = message,
        Error   = error,
    };

    /// <summary>Creates a successful <see cref="ListResult"/> with the given counts.</summary>
    public static ListResult Ok(int setsListed, int totalFiles, long totalBytes,
        TimeSpan duration = default) => new()
    {
        Success       = true,
        Outcome       = ServiceReportLevel.Completed,
        SetsListed    = setsListed,
        TotalFiles    = totalFiles,
        TotalBytes    = totalBytes,
        FilesProcessed = totalFiles,
        BytesProcessed = totalBytes,
        Duration      = duration,
    };
}
