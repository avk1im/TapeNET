using System.IO;
using System.Linq;

using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // Helpers.BytesToString

using TapeLibNET;

namespace TapeLibNET.Services;

// ── Base ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Base <see cref="ITapeFileNotifiable"/> implementation that bridges tape agent
///  callbacks to an <see cref="ITapeServiceHost"/>. Subclasses add the few
///  operation-specific fields needed for backup vs. restore/validate/verify.
/// <para>
/// Mirrors the statistics properties held by the old per-app
///  <c>GuiBackupProgressHandler</c> / <c>GuiRestoreProgressHandler</c> classes.
/// </para>
/// </summary>
/// <remarks>
/// Initialises a new handler with the shared fields required by all operation types.
/// </remarks>
public abstract class ServiceOperationProgressHandler(
    ITapeServiceHost host,
    TapeFileAgent agent,
    bool skipAllErrors,
    string operationName) : ITapeFileNotifiable
{
    private readonly ITapeServiceHost _host = host;

    /// <summary>The tape agent driving the current operation.</summary>
    protected readonly TapeFileAgent Agent = agent;

    private bool _skipAllErrors = skipAllErrors;
    private readonly string _operationName = operationName;

    /// <summary>Human-readable name of the operation ("Backup", "Restore", etc.). Available to subclasses.</summary>
    protected string OperationName => _operationName;

    // ── Statistics (written by Sync, read by the service after the operation) ──

    /// <summary>Total files expected for the entire operation.</summary>
    public int FilesTotal { get; private set; }
    /// <summary>Total logical bytes expected for the entire operation (sum of all file lengths). Can be updated progressively.</summary>
    public long BytesTotal { get; private set; }
    /// <summary>Files finished (succeeded + failed + skipped).</summary>
    public int FilesProcessed { get; private set; }
    /// <summary>Files completed without error.</summary>
    public int FilesSucceeded { get; private set; }
    /// <summary>Files that hit an error and were not retried.</summary>
    public int FilesFailed { get; private set; }
    /// <summary>Files skipped (by pre-processor, incremental, or user choice).</summary>
    public int FilesSkipped { get; private set; }
    /// <summary>Total logical bytes of the succeeded files.</summary>
    public long BytesProcessed { get; private set; }

    // ── Shared private state ──────────────────────────────────────────────────

    private TapeFileStatistics _batchStartSnapshot;
    private bool _abortLogged;

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>Synchronises the public statistics properties from an agent snapshot.</summary>
    protected void Sync(in TapeFileStatistics stats)
    {
        FilesTotal     = stats.FilesTotal;
        BytesTotal     = stats.BytesTotal;
        FilesProcessed = stats.FilesProcessed;
        FilesSucceeded = stats.FilesSucceeded;
        FilesFailed    = stats.FilesFailed;
        FilesSkipped   = stats.FilesSkipped;
        BytesProcessed = stats.FileBytesProcessed;
    }

    /// <summary>Reports current progress to the host. Override to add custom progress display.</summary>
    protected virtual void ReportProgress(in TapeFileStatistics stats, string? currentFile = null) { }

    /// <summary>
    /// Throws <see cref="TapeAbortRequestedException"/> if the agent's abort flag is set,
    ///  logging the abort event exactly once.
    /// </summary>
    protected void ThrowIfAbortRequested()
    {
        if (!Agent.IsAbortRequested) return;
        if (!_abortLogged)
        {
            _abortLogged = true;
            _host.Report(ServiceReportLevel.Warning, $"{_operationName} abort requested");
        }
        throw new TapeAbortRequestedException("User requested abort");
    }

    // ── ITapeFileNotifiable ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public virtual void BatchStart(int setIndex, in TapeFileStatistics stats)
    {
        _batchStartSnapshot = stats;
        Sync(stats);
        var toc = Agent.TOC;
        _host.Report(ServiceReportLevel.Info,
            $"Set #{setIndex} | {toc.SetIndexToAlt(setIndex)}: starting {_operationName.ToLowerInvariant()}...");
        ReportProgress(stats);
#if DEBUG
        // Agent.SimulateFileFailures.Enabled = true; // Enable simulation of file failures for testing purposes here
#endif
    }

    /// <inheritdoc/>
    public virtual void BatchEnd(int setIndex, in TapeFileStatistics stats)
    {
        Sync(stats);
        var toc = Agent.TOC;
        var batch = stats.Delta(in _batchStartSnapshot);

        var level = batch.FilesFailed > 0 ? ServiceReportLevel.Failed
                  : batch.FilesSkipped > 0 ? ServiceReportLevel.Warning
                  : ServiceReportLevel.Completed;
        var parts = new List<string>(3) { $"{batch.FilesSucceeded:N0} succeeded" };
        if (batch.FilesFailed > 0) parts.Add($"{batch.FilesFailed:N0} failed");
        if (batch.FilesSkipped > 0) parts.Add($"{batch.FilesSkipped:N0} skipped");

        _host.Report(level,
            $"Set #{setIndex} | {toc.SetIndexToAlt(setIndex)} complete: {string.Join(", ", parts)}");
        ReportProgress(stats);
    }

    /// <inheritdoc/>
    public virtual bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        ThrowIfAbortRequested();
        ReportProgress(stats, fileInfo.FileDescr.FullName);
        return true;
    }

    /// <inheritdoc/>
    public virtual bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        ThrowIfAbortRequested();
        Sync(stats);
        _host.Report(ServiceReportLevel.Completed,
            $"'{Path.GetFileName(fileInfo.FileDescr.FullName)}' {Helpers.BytesToString(fileInfo.FileDescr.Length)}",
            isSubEntry: true);
        ReportProgress(stats);
        return true;
    }

    /// <inheritdoc/>
    public virtual FileFailedAction OnFileFailed(TapeFileInfo fileInfo, TapeResult result, in TapeFileStatistics stats)
    {
        Sync(stats);
        ThrowIfAbortRequested();

        // End-of-media errors are handled by the multi-volume loop; always skip silently.
        if (result.ErrorCode == (uint)WIN32_ERROR.ERROR_END_OF_MEDIA ||
            result.ErrorCode == (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED)
        {
            return FileFailedAction.Skip;
        }

        _host.Report(ServiceReportLevel.Failed, $"Failed: '{fileInfo.FileDescr.FullName}'");
        _host.Report(ServiceReportLevel.Failed, $"Error: {result.ErrorMessage}", isSubEntry: true);
        ReportProgress(stats);

        if (_skipAllErrors)
            return FileFailedAction.Skip;

        // Route to the host's structured file-error prompt.
        //  The host shows the appropriate dialog (WPF FileErrorDialog, CLI menu, etc.)
        //  and returns the chosen action, including the SkipAll sentinel.
        var action = _host.OnFileErrorSelect(
            fileInfo.FileDescr.FullName, result.ErrorMessage, _operationName);

        if (action == FileFailedAction.SkipAll)
        {
            _skipAllErrors = true;
            return FileFailedAction.Skip;
        }
        if (action == FileFailedAction.Abort)
        {
            if (!_abortLogged)
            {
                _abortLogged = true;
                _host.Report(ServiceReportLevel.Warning, $"{_operationName} abort requested");
            }
            throw new TapeAbortRequestedException("User requested abort");
        }
        return action; // Skip or Retry
    }

    /// <inheritdoc/>
    public virtual void OnFileSkipped(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        Sync(stats);
        ThrowIfAbortRequested();
        _host.Report(ServiceReportLevel.None,
            $"Skipped: {Path.GetFileName(fileInfo.FileDescr.FullName)}", isSubEntry: true);
    }
}

// ── Backup ───────────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="ServiceOperationProgressHandler"/> specialisation for backup operations.
/// Adds an optional <see cref="ITapeFileFilter"/> applied in <see cref="PreProcessFile"/>
///  (pre-tape-I/O skip) that was previously implemented in the per-app handler.
/// </summary>
public class ServiceBackupProgressHandler(
    ITapeServiceHost host,
    TapeFileAgent agent,
    bool skipAllErrors,
    ITapeFileFilter? filter = null)
    : ServiceOperationProgressHandler(host, agent, skipAllErrors, "Backup")
{
    /// <inheritdoc/>
    /// <remarks>
    /// Applies the optional <see cref="ITapeFileFilter"/> before any tape I/O —
    ///  returning <see langword="false"/> causes the agent to skip the file and
    ///  report it via <see cref="ITapeFileNotifiable.OnFileSkipped"/>.
    /// </remarks>
    public override bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        ThrowIfAbortRequested();
        if (filter is not null && !filter.Matches(fileInfo.FileDescr))
            return false;
        ReportProgress(stats, fileInfo.FileDescr.FullName);
        return true;
    }

    /// <summary>
    /// Called by the base state machine when the backup operation finishes
    ///  (successfully, aborted, or failed). Override to finalise progress display
    ///  (e.g. call <c>IProgressScope.Complete()</c> in the CLI handler).
    /// No-op in this base implementation.
    /// </summary>
    public virtual void CompleteProgress() { }

    /// <summary>
    /// Called by the base state machine in the <c>finally</c> block to release
    ///  any resources held by the progress display (e.g. <c>IProgressScope.Dispose()</c>
    ///  in the CLI handler). No-op in this base implementation.
    /// </summary>
    public virtual void DisposeProgress() { }
}

// ── Restore / Validate / Verify ───────────────────────────────────────────────

/// <summary>
/// <see cref="ServiceOperationProgressHandler"/> specialisation for restore, validate,
///  and verify operations. Adds the per-set <see cref="ProcessedFiles"/> dictionary
///  that the service uses for post-operation bookkeeping and incremental logic.
/// </summary>
public class ServiceRestoreProgressHandler(
    ITapeServiceHost host,
    TapeFileAgent agent,
    int totalFilesToProcess,
    bool skipAllErrors,
    RestoreMode mode)
    : ServiceOperationProgressHandler(host, agent, skipAllErrors, mode.ToVerb())
{
    /// <summary>
    /// Total number of files to process across all batches/volumes, as supplied by the caller.
    ///  Subclasses should read this instead of re-capturing the constructor parameter.
    /// </summary>
    protected int TotalFilesToProcess { get; } = totalFilesToProcess;
    /// <summary>
    /// Per-set dictionary of successfully processed files, accumulated during the operation.
    /// Key = standard set index; Value = list of <see cref="TapeFileInfo"/> records.
    /// </summary>
    public Dictionary<int, List<TapeFileInfo>> ProcessedFiles { get; } = [];

    /// <summary>
    /// Builds a <see cref="RestoreResult"/> from the accumulated statistics.
    /// Called by the service after the operation completes.
    /// </summary>
    public RestoreResult GenerateResult() => new()
    {
        FilesTotal     = FilesTotal,
        BytesTotal     = BytesTotal,
        FilesProcessed = FilesProcessed,
        FilesSucceeded = FilesSucceeded,
        FilesFailed    = FilesFailed,
        FilesSkipped   = FilesSkipped,
        BytesProcessed = BytesProcessed,
        Success        = FilesFailed == 0,
        Outcome        = FilesFailed > 0  ? ServiceReportLevel.Warning
                       : FilesSkipped > 0 ? ServiceReportLevel.Warning
                       :                    ServiceReportLevel.Completed,
        ProcessedFiles = ProcessedFiles,
    };

    /// <inheritdoc/>
    /// <remarks>
    /// Intentionally a no-op in this base implementation so that TapeLibNET.Services carries
    ///  no progress-bar dependency. Apps that wire up a progress bar should override this further,
    ///  using <see cref="TotalFilesToProcess"/> for accurate cross-batch percentage.
    /// </remarks>
    protected override void ReportProgress(in TapeFileStatistics stats, string? currentFile = null) { }

    /// <inheritdoc/>
    public override bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        bool result = base.PostProcessFile(fileInfo, stats);
        AddToProcessed(fileInfo, Agent.TOC.CurrentSetIndex);
        return result;
    }

    private void AddToProcessed(in TapeFileInfo fileInfo, int setIndex)
    {
        if (!ProcessedFiles.TryGetValue(setIndex, out var list))
        {
            list = [];
            ProcessedFiles[setIndex] = list;
        }
        list.Add(fileInfo);
    }
}

// ── Calibrate ────────────────────────────────────────────────────────────────

/// <summary>
/// Progress adapter for destructive calibration runs. Mirrors the service-operation pattern used
/// by backup and restore, but maps calibration chunks → pseudo-files so existing overlays can
/// reuse their file-progress shape.
/// </summary>
public class ServiceCalibrateProgressHandler(
    ITapeServiceHost host,
    TapeCalibrator calibrator,
    long capacityReported)
    : IProgress<TapeCalibrationProgress>
{
    private readonly ITapeServiceHost _host = host;

    /// <summary>The live calibrator driving the operation.</summary>
    protected readonly TapeCalibrator Calibrator = calibrator;

    private readonly int _estimatedChunkBytes = checked((int)Math.Max(1L, calibrator.Options.ChunkBytesTarget));
    private bool _abortLogged;
    private bool _ewLogged;

    /// <summary>Estimated number of chunks needed to traverse the medium.</summary>
    public int FilesTotal { get; private set; } =
        capacityReported > 0
            ? (int)Math.Min(int.MaxValue, (capacityReported + Math.Max(1L, calibrator.Options.ChunkBytesTarget) - 1L)
                                            / Math.Max(1L, calibrator.Options.ChunkBytesTarget))
            : 0;

    /// <summary>Estimated media capacity reported by the drive at BOT.</summary>
    public long BytesTotal { get; private set; } = Math.Max(0L, capacityReported);

    /// <summary>Chunks written so far (pseudo-file count).</summary>
    public int FilesProcessed { get; private set; }

    /// <summary>Chunks successfully written so far (pseudo-file count).</summary>
    public int FilesSucceeded { get; private set; }

    /// <summary>No per-chunk failures are surfaced separately for calibration.</summary>
    public int FilesFailed { get; private set; }

    /// <summary>No per-chunk skips are surfaced separately for calibration.</summary>
    public int FilesSkipped { get; private set; }

    /// <summary>Bytes written so far.</summary>
    public long BytesProcessed { get; private set; }

    /// <summary>Current calibration phase, humanised for UI display.</summary>
    public string CurrentPhase { get; private set; } = "Preparing calibration";

    /// <summary>Finalises any host-specific progress display. No-op in the base implementation.</summary>
    public virtual void CompleteProgress() { }

    /// <summary>Releases any host-specific progress resources. No-op in the base implementation.</summary>
    public virtual void DisposeProgress() { }

    /// <summary>Hook for app-specific progress UI updates.</summary>
    protected virtual void ReportProgress(TapeCalibrationProgress progress) { }

    /// <summary>
    /// Throws <see cref="TapeAbortRequestedException"/> when the calibrator has been asked to abort,
    /// logging that state transition exactly once.
    /// </summary>
    protected void ThrowIfAbortRequested()
    {
        if (!Calibrator.IsAbortRequested) return;
        if (!_abortLogged)
        {
            _abortLogged = true;
            _host.Report(ServiceReportLevel.Warning, "Calibration abort requested");
        }
        throw new TapeAbortRequestedException("User requested abort");
    }

    /// <inheritdoc/>
    public void Report(TapeCalibrationProgress progress)
    {
        ThrowIfAbortRequested();

        BytesProcessed = Math.Max(0L, progress.BytesWritten);
        FilesProcessed = _estimatedChunkBytes > 0
            ? (int)Math.Min(int.MaxValue, (BytesProcessed + _estimatedChunkBytes - 1L) / _estimatedChunkBytes)
            : 0;
        FilesSucceeded = FilesProcessed;
        CurrentPhase = FormatPhase(progress.Phase);

        if (progress.EarlyWarning && !_ewLogged)
        {
            _ewLogged = true;
            _host.Report(ServiceReportLevel.Info, "Calibration captured the physical early-warning landmark");
        }

        ReportProgress(progress);
    }

    /// <summary>
    /// Builds a <see cref="CalibrateResult"/> from the accumulated progress state and an optional
    /// completed calibration artifact.
    /// </summary>
    public CalibrateResult GenerateResult(
        ITapeCalibration? calibration,
        bool aborted = false,
        bool failed = false,
        TimeSpan duration = default,
        string? message = null,
        Exception? error = null) => new()
    {
        FilesTotal      = FilesTotal,
        BytesTotal      = BytesTotal,
        FilesProcessed  = FilesProcessed,
        FilesSucceeded  = FilesSucceeded,
        FilesFailed     = FilesFailed,
        FilesSkipped    = FilesSkipped,
        BytesProcessed  = calibration?.CapacityActual ?? BytesProcessed,
        WasAborted      = aborted,
        HasFailed       = failed,
        Success         = !aborted && !failed && calibration is not null,
        Outcome         = aborted ? ServiceReportLevel.Failed
                        : failed  ? ServiceReportLevel.Error
                        :           ServiceReportLevel.Completed,
        Duration        = duration,
        Message         = message,
        Error           = error,
        Calibration     = calibration,
        ProfileKey      = calibration?.ProfileKey ?? string.Empty,
        CapacityReported = calibration?.CapacityReported ?? BytesTotal,
        CapacityActual  = calibration?.CapacityActual ?? BytesProcessed,
        EarlyWarning    = calibration?.EarlyWarning,
        EwToEomDistance = calibration?.EwToEomDistance ?? 0L,
    };

    private static string FormatPhase(string phase) => phase switch
    {
        "sampling"      => "Writing to EOM",
        "early-warning" => "Capturing EW landmark",
        "eom"           => "Finalizing calibration",
        _               => string.IsNullOrWhiteSpace(phase) ? "Calibrating" : phase,
    };
}
