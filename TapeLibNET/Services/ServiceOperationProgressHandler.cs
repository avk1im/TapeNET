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
    /// <summary>Files completed without errorEx.</summary>
    public int FilesSucceeded { get; private set; }
    /// <summary>Files that hit an errorEx and were not retried.</summary>
    public int FilesFailed { get; private set; }
    /// <summary>Files skipped (by pre-processor, incremental, or user choice).</summary>
    public int FilesSkipped { get; private set; }
    /// <summary>Total logical bytes of the succeeded files.</summary>
    public long BytesProcessed { get; private set; }

    // ── Shared private state ──────────────────────────────────────────────────

    private TapeFileStatistics _setStartSnapshot;
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
        SetStats       = stats.Sets; // ← ensure the set-level counters ride along
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
    public virtual void SetStart(int setIndex, in TapeFileStatistics stats)
    {
        _setStartSnapshot = stats;
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
    public virtual void SetEnd(int setIndex, in TapeFileStatistics stats)
    {
        Sync(stats);
        var toc = Agent.TOC;
        var batch = stats.Delta(in _setStartSnapshot);

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

        // Route to the host's structured file-errorEx prompt.
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

    // ── Set-level statistics (written by Sync, read by the service afterwards) ──

    /// <summary>
    /// Set-level statistics for the operation, mirroring the file counters above.
    /// </summary>
    /// <remarks>
    /// A struct copy refreshed by <see cref="Sync"/>, so it is current whenever the service reads it —
    ///  including after an abort, where the agent's own <c>_stats</c> may already have moved on.
    /// </remarks>
    public TapeSetStatistics SetStats { get; private set; }

    /// <summary>
    /// Set anomalies observed during the operation, in order — the forensic trail behind
    ///  <see cref="SetStats"/>'s counters.
    /// </summary>
    public IReadOnlyList<TapeSetAnomaly> SetAnomalies => _setAnomalies;
    private readonly List<TapeSetAnomaly> _setAnomalies = [];

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>This override is not optional.</b> Without it the handler inherits the interface's default
    ///  implementation, which returns <see cref="SetAnomalyAction.Abort"/> by design (SH-18) — correct
    ///  for a notifiable that knows nothing about set recovery, catastrophic for the one the service
    ///  uses, since it would abort every operation that meets a repairable drift.
    /// </para>
    /// <para>
    /// Reports FIRST, asks SECOND. The host's dialog is modal in most apps, so the log line must already
    ///  be on screen when it opens — the user is being asked to decide about something they should be
    ///  able to read (SH-16).
    /// </para>
    /// </remarks>
    public virtual SetAnomalyAction OnSetAnomaly(in TapeSetAnomaly anomaly, in TapeFileStatistics stats)
    {
        Sync(stats);
        _setAnomalies.Add(anomaly);

        // A set-level fault is louder than a file-level one: it can invalidate an entire set, and on a
        //  destructive path it is the difference between repairing a cartridge and ruining it.
        _host.Report(anomaly.IsDestructive ? ServiceReportLevel.Failed : ServiceReportLevel.Warning,
            $"Set #{anomaly.SetIndex} anomaly ({DescribeVerdict(anomaly.Verdict)})");
        _host.Report(ServiceReportLevel.Warning,
            $"Expected: {anomaly.ExpectedDescription}", isSubEntry: true);
        _host.Report(ServiceReportLevel.Warning,
            $"Found: {anomaly.ActualDescription}", isSubEntry: true);
        if (!anomaly.Diagnosis.Success && !string.IsNullOrWhiteSpace(anomaly.Diagnosis.ErrorMessage))
            _host.Report(ServiceReportLevel.Warning,
                anomaly.Diagnosis.ErrorMessage, isSubEntry: true);

        ReportProgress(stats);

        // Nothing left to try: the ladder is telling us, not asking us. Answering "proceed" would
        //  authorize a stage that does not exist, and would read in the log as a decision the user made.
        if (!anomaly.CanAttemptRecovery)
        {
            _host.Report(ServiceReportLevel.Warning,
                "No recovery remains for this set", isSubEntry: true);
            return SetAnomalyAction.Abort;
        }

        // Same suppression latch the file path uses: an unattended run must not stall on a prompt.
        //  Deliberately shared with SkipAllErrors rather than given its own flag — a caller that asked
        //  not to be prompted about files did not mean "except about sets".
        if (_skipAllErrors)
        {
            _host.Report(ServiceReportLevel.Warning,
                "Attempting recovery (error prompts suppressed)", isSubEntry: true);
            return SetAnomalyAction.Proceed;
        }

        bool authorized = _host.OnSetAnomalySelect(
            anomaly.ExpectedDescription, anomaly.ActualDescription,
            anomaly.Diagnosis.ErrorMessage, anomaly.IsDestructive, _operationName);

        if (!authorized)
        {
            // A declined recovery is a user decision, not a fault — say so plainly, and let the agent
            //  supply the diagnosis. Do NOT throw here: OnSetAnomaly's contract is the enum, and the
            //  agent converts it into IsAbortRequested on its own.
            _host.Report(ServiceReportLevel.Warning,
                $"{_operationName}: set recovery declined", isSubEntry: true);
            return SetAnomalyAction.Abort;
        }

        _host.Report(ServiceReportLevel.Info, "Attempting recovery...", isSubEntry: true);
        return SetAnomalyAction.Proceed;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reported at Warning, never Info (SH-16). A corrected drift means the drive or the medium
    ///  miscounted marks; a recovery that needed the BOM renavigation means the cartridge's TAIL is
    ///  unreliable. Both are facts the person holding the cartridge should see, and this is the only
    ///  surface where they will.
    /// </remarks>
    public virtual void OnSetAnomalyRecovered(in TapeSetAnomaly anomaly, in TapeFileStatistics stats)
    {
        Sync(stats);

        string how = anomaly.Stage == TapeSetAnomalyStage.Renavigated
            ? "by re-navigating from the start of the volume"
            : "by correcting the set position";

        _host.Report(ServiceReportLevel.Warning,
            $"Set #{anomaly.SetIndex} recovered {how}");

        if (anomaly.Stage == TapeSetAnomalyStage.Renavigated)
            _host.Report(ServiceReportLevel.Warning,
                "The end of this volume appears damaged — see the summary for advice", isSubEntry: true);

        ReportProgress(stats);
    }

    /// <summary>Culture-neutral label for a verdict, used in LOG lines only — mirrors <c>VerdictToString</c>.</summary>
    private static string DescribeVerdict(TapeSetHeaderVerdict verdict) => verdict switch
    {
        TapeSetHeaderVerdict.SetIndexDrift => "wrong set reached",
        TapeSetHeaderVerdict.Unreadable => "set marker unreadable",
        TapeSetHeaderVerdict.WrongMedia => "different cartridge",
        TapeSetHeaderVerdict.WrongVolume => "different volume",
        _ => verdict.ToString(),
    };
}

// ── Set-level operations (delete) ────────────────────────────────────────────

/// <summary>
/// <see cref="ServiceOperationProgressHandler"/> specialisation for operations that act on SETS rather
///  than files. Inherits the whole set-anomaly channel and contributes no file behaviour of its own.
/// </summary>
/// <remarks>
/// A delete never enters a file loop, so <c>SetStart</c> / <c>PreProcessFile</c> and their siblings are
///  never called — which is precisely why the verb needs its own result type (§3) rather than being
///  reported through the file counters.
/// </remarks>
public class ServiceSetProgressHandler(
    ITapeServiceHost host,
    TapeFileAgent agent,
    bool skipAllErrors,
    string operationName)
    : ServiceOperationProgressHandler(host, agent, skipAllErrors, operationName);

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

    private readonly int _estimatedChunkSize = calibrator.Options.ResolveFor(calibrator.Drive).ChunkSize;
    private bool _abortLogged;
    private bool _ewLogged;

    /// <summary>Estimated number of chunks needed to traverse the medium.</summary>
    public int FilesTotal =>
        capacityReported > 0
            ? (int)Math.Min(int.MaxValue, (capacityReported + _estimatedChunkSize - 1L) / _estimatedChunkSize)
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
        FilesProcessed = _estimatedChunkSize > 0
            ? (int)Math.Min(int.MaxValue, (BytesProcessed + _estimatedChunkSize - 1L) / _estimatedChunkSize)
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
        Exception? errorEx = null) => new()
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
        ErrorException  = errorEx,
        Calibration     = calibration,
        ProfileKey      = calibration?.ProfileKey ?? string.Empty,
        ReportedCapacityAtBom = calibration?.ReportedCapacityAtBom ?? BytesTotal,
        PhantomFreeAtEom = calibration?.PhantomFreeAtEom ?? 0,
        CapacityActual  = calibration?.CapacityActual ?? BytesProcessed,
        EarlyWarning    = calibration?.EarlyWarning,
        EwToEomDistance = calibration?.EwToEomDistance ?? 0L,
    };

    private static string FormatPhase(string phase) => phase switch
    {
        "sampling"      => "Writing to the main media section",
        "sampling-tail" => "Writing to the final media section",
        "early-warning" => "Capturing early-warning landmark",
        "eom-inferred"  => "Inferred end-of-media; finalizing calibration",
        "eom"           => "Finalizing calibration",
        _               => string.IsNullOrWhiteSpace(phase) ? "Calibrating" : phase,
    };
}
