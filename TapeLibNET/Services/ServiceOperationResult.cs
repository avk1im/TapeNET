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

// ── List ─────────────────────────────────────────────────────────────────────

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
