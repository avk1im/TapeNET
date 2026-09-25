using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TapeLibNET;
using TapeLibNET.Services;
using Windows.Win32.System.SystemServices;


namespace TapeLibNET.Services;

/// <summary>
/// Severity classification for service-level log entries and operation outcomes.
/// Replaces the per-app <c>WarningLevel</c> enums in TapeWinNET and TapeConNET.
/// The name uses "Report" rather than "Warning" because <see cref="Info"/> and
/// <see cref="Completed"/> are not warnings.
/// </summary>
public enum ServiceReportLevel
{
    /// <summary>Plain informational text without any severity emphasis.</summary>
    None,
    /// <summary>General informational message.</summary>
    Info,
    /// <summary>Successful completion of a step or operation.</summary>
    Completed,
    /// <summary>Recoverable issue worth surfacing.</summary>
    Warning,
    /// <summary>Operation failed but the program can continue.</summary>
    Failed,
    /// <summary>Unrecoverable error.</summary>
    Error,
}

/// <summary>
/// Classification of a completed file operation (backup / restore / validate / verify), derived from its
/// counters plus the agent's diagnosis. Policy lives in <see cref="TapeServiceBase.JudgeFileOperation"/>;
/// wording lives in <see cref="TapeServiceBase.VerbalizeFileOperation"/>.
/// </summary>
/// <remarks>
/// Separated from <see cref="ServiceReportLevel"/> on purpose: the verdict says WHAT happened, the level
///  says how loudly to say it. One verdict maps to exactly one level, but the reverse does not hold —
///  three different verdicts all report at Warning.
/// </remarks>
public enum FileOperationVerdict
{
    /// <summary>Every selected file was processed successfully.</summary>
    FullSuccess,

    /// <summary>All attempted files succeeded, but some were skipped.</summary>
    CompletedWithSkips,

    /// <summary>Some files failed; the rest completed.</summary>
    CompletedWithFailures,

    /// <summary>A set-level verification refused the operation; no files were touched.</summary>
    SetVerificationBlocked,

    /// <summary>The operation ran to completion but touched no files at all.</summary>
    NothingProcessed,

    /// <summary>Stopped at the user's request.</summary>
    Aborted,

    /// <summary>A catastrophic error terminated the operation.</summary>
    Failed,
}

/// <summary>
/// What the service recommends the user DO about the set anomalies an operation met. Policy, computed
///  from <see cref="TapeSetStatistics"/>; the wording lives in <see cref="TapeServiceBase.AdviseText"/>.
/// </summary>
/// <remarks>
/// Separate from <see cref="FileOperationVerdict"/> on purpose: that says what happened to the FILES,
///  this says what the MEDIUM appears to need. A backup can complete perfectly and still leave a
///  cartridge that wants attention.
/// </remarks>
public enum SetAnomalyAdvice
{
    /// <summary>No anomalies — nothing to say.</summary>
    None,

    /// <summary>
    /// A set was reached only after re-navigating from the start of the volume: the volume's TAIL no
    ///  longer matches the TOC. Deleting the trailing sets rewrites the damaged mark structure.
    /// </summary>
    RepairTrailingSets,

    /// <summary>
    /// Drift was corrected in place, without needing the tail recovery — the mark structure is intact
    ///  but was miscounted, which points at the drive or the medium rather than the layout.
    /// </summary>
    CheckDriveOrMedia,

    /// <summary>A destructive write was refused. The tape is untouched and the cause needs resolving.</summary>
    WriteRefused,
}

public partial class TapeServiceBase
{
    #region Outcome judgement (policy)

    // ── Outcome judgment (policy) ─────────────────────────────────────────────

    /// <summary>
    /// Classifies a finished file operation from its counters and its embedded diagnosis. Pure and
    ///  side-effect free — the counterpart to <see cref="JudgeRecalibration"/>.
    /// </summary>
    /// <param name="pendingContinuation">
    /// True when the agent stopped only because it needs another volume. Such a stop is NOT a failure,
    ///  and conflating the two is the single easiest mistake here.
    /// </param>
    protected static FileOperationVerdict JudgeFileOperation(
        in FileOperationResult result, bool pendingContinuation = false)
    {
        if (result.WasAborted) return FileOperationVerdict.Aborted;
        if (result.HasFailed) return FileOperationVerdict.Failed;

        // Checked BEFORE the file counters: a refused destructive write processes no files, so every
        //  counter-based verdict below would describe it as "nothing happened" — which is true and
        //  useless. This is the verdict that names the cause.
        if (result.Sets.SetWriteBlocked && result.FilesProcessed == 0)
            return FileOperationVerdict.SetVerificationBlocked;

        if (result.FilesFailed > 0) return FileOperationVerdict.CompletedWithFailures;

        // Nothing processed AND a diagnosis to explain it ⇒ the operation did not merely find nothing
        //  to do; something stopped it.
        if (result.FilesProcessed == 0)
            return FileOperationVerdict.NothingProcessed;

        if (result.FilesSkipped > 0) return FileOperationVerdict.CompletedWithSkips;

        // A pending continuation with everything so far successful is still a success for THIS volume.
        _ = pendingContinuation;
        return FileOperationVerdict.FullSuccess;
    }

    /// <summary>
    /// Renders a verdict as a headline: the severity to report at, and the user-facing text.
    /// </summary>
    /// <param name="operationName">"Backup", "Restore", "Validate", "Verify" — the caller's verb.</param>
    protected static (ServiceReportLevel Level, string Message) VerbalizeFileOperation(
        FileOperationVerdict verdict,
        in FileOperationResult result,
        string operationName)
    {
        // The diagnosis now travels WITH the result — no separate parameter, and no way for a caller to
        //  pass one that disagrees with what the result carries.
        var diagnosis = result.Diagnosis;
        string reason = !diagnosis.Success && !string.IsNullOrWhiteSpace(diagnosis.ErrorMessage)
            ? $" — {diagnosis.ErrorMessage}"
            : string.Empty;

        return verdict switch
        {
            FileOperationVerdict.FullSuccess => (
                ServiceReportLevel.Completed,
                $"{operationName} of {result.FilesTotal:N0} file(s) completed successfully"),

            FileOperationVerdict.CompletedWithSkips => (
                ServiceReportLevel.Warning,
                $"{operationName} of {result.FilesTotal:N0} file(s) completed with " +
                $"{result.FilesSkipped:N0} skipped"),

            FileOperationVerdict.CompletedWithFailures => (
                ServiceReportLevel.Failed,
                $"{operationName} of {result.FilesTotal:N0} file(s) completed with " +
                $"{result.FilesFailed:N0} failed{reason}"),

            // The set-level counterpart of NothingProcessed, and the reason that verdict exists: a
            //  refused destructive write is not "nothing happened", it is "we stopped you".
            FileOperationVerdict.SetVerificationBlocked => (
                ServiceReportLevel.Failed,
                $"{operationName} refused: the backup set on tape is not the one expected{reason}"),

            // The verdict that most needs the diagnosis: without it the user is told only that nothing
            //  happened, with no indication of why — the exact gap a rejected set used to fall into.
            FileOperationVerdict.NothingProcessed => (
                ServiceReportLevel.Warning,
                $"{operationName} of {result.FilesTotal:N0} file(s) completed — no files processed{reason}"),

            FileOperationVerdict.Aborted => (
                ServiceReportLevel.Failed,
                $"{operationName} of {result.FilesTotal:N0} file(s): aborted per user request"),

            FileOperationVerdict.Failed => (
                ServiceReportLevel.Error,
                $"{operationName} failed{reason}"),

            _ => (ServiceReportLevel.Info, $"{operationName} finished"),
        };
    }

    #endregion

    #region Reporting

    // ── Reporting ──────────────────────────────────────────

    /// <summary>
    /// Judges, verbalizes, and reports a finished file operation in one call — the common ending for
    ///  backup and restore alike. Returns the verdict so the caller can branch on it.
    /// </summary>
    protected FileOperationVerdict ReportFileOperationOutcome(
        in FileOperationResult result,
        string operationName,
        bool pendingContinuation = false)
    {
        var verdict = JudgeFileOperation(result, pendingContinuation);
        var (level, message) = VerbalizeFileOperation(verdict, result, operationName);
        _host.Report(level, message);
        return verdict;
    }

    /// <summary>
    /// Reports the uniform per-operation statistics sub-line ("N succeeded, M failed, … X processed").
    ///  Shared so backup and restore cannot drift apart in wording.
    /// </summary>
    protected void ReportFileOperationStats(in FileOperationResult result,
        double secsTotal, double secsIo, string processedVerb = "processed")
    {
        if (result.FilesProcessed == 0)
            return;

        var parts = new List<string>(4) { $"{result.FilesSucceeded:N0} succeeded" };
        if (result.FilesFailed > 0) parts.Add($"{result.FilesFailed:N0} failed");
        if (result.FilesSkipped > 0) parts.Add($"{result.FilesSkipped:N0} skipped");
        parts.Add($"{Helpers.BytesToString(result.BytesProcessed)} {processedVerb}");

        var timingParts = new List<string>(2) { FormatElapsed(secsTotal) + " elapsed" };
        string rate = FormatDataIoRate(result.BytesProcessed, secsIo);
        if (rate.Length > 0) timingParts.Add(rate);

        LogInfoSub(string.Join(", ", parts));
        LogInfoSub(string.Join(", ", timingParts));
    }

    protected void ReportRestoreStats(in RestoreResult result,
        double dataSecs, double ioSecs)
    {
        ReportFileOperationStats(result, dataSecs, ioSecs, "restored");

        if (result.FilesMissing > 0)
            LogWarnSub($"{result.FilesMissing:N0} file(s) not found on media");
    }

    protected void ReportBackupStats(in BackupResult result,
        double dataSecs, double ioSecs, double tocSecs)
    {
        ReportFileOperationStats(result, dataSecs, ioSecs, "written to media");

        if (tocSecs >= 0.5)
            LogInfoSub($"TOC saved in {FormatElapsed(tocSecs)}");

        LogInfoSub($"Remaining writable media capacity  (est.): {Helpers.BytesToStringLong(WritableRemaining)}");
        // A (much) more detailed version:
        //static string triple(long b1, long b2, long b3)
        //    => $"{Helpers.BytesToStringLong(b1)} / {Helpers.BytesToStringLong(b2)} / {Helpers.BytesToStringLong(b3)}";
        //LogInfoSub($"Remaining media capacity (reported/estimated/writable): {triple(ReportedContentRemaining, EstimatedContentRemaining, WritableRemaining)}");
    }

    #endregion

    #region Anomaly Advisory

    // ── Anomaly Advisory ──────────────────────────────────────────

    /// <summary>
    /// Derives the recommendation from an operation's set statistics. Pure; mirrors
    ///  <see cref="JudgeFileOperation"/>.
    /// </summary>
    /// <remarks>
    /// Ordered by severity of what the cartridge is telling us, not by count. A refusal outranks
    ///  everything (the user is blocked right now); a BOM recovery outranks a delta one (the tail is
    ///  damaged, not merely miscounted) even when the delta recoveries are more numerous.
    /// </remarks>
    protected static SetAnomalyAdvice AdviseOnSetAnomalies(in TapeSetStatistics sets)
    {
        if (sets.SetWriteBlocked) return SetAnomalyAdvice.WriteRefused;
        if (sets.AnomaliesRecoveredFromBom > 0) return SetAnomalyAdvice.RepairTrailingSets;
        if (sets.AnomaliesRecovered > 0) return SetAnomalyAdvice.CheckDriveOrMedia;
        if (sets.AnomaliesDetected > 0) return SetAnomalyAdvice.CheckDriveOrMedia;
        return SetAnomalyAdvice.None;
    }

    /// <summary>Renders an advice as a headline plus one actionable sub-line.</summary>
    protected static (ServiceReportLevel Level, string Headline, string Action) AdviseText(
        SetAnomalyAdvice advice) => advice switch
        {
            SetAnomalyAdvice.RepairTrailingSets => (
                ServiceReportLevel.Warning,
                "The end of this volume does not match its table of contents",
                "Deleting the last backup set(s) would rewrite the damaged area " +
                "(Backup | Delete Backup Sets)"),

            SetAnomalyAdvice.CheckDriveOrMedia => (
                ServiceReportLevel.Warning,
                "Backup set positions on this volume had to be corrected",
                "The drive or the cartridge miscounted tape marks — consider cleaning the drive, " +
                "and retiring the cartridge if this recurs"),

            SetAnomalyAdvice.WriteRefused => (
                ServiceReportLevel.Failed,
                "The operation was refused to protect existing data — the tape is unchanged",
                "Verify the correct cartridge is loaded, then retry; if the volume is known to be " +
                "damaged, delete its trailing backup sets first"),

            _ => (ServiceReportLevel.Info, string.Empty, string.Empty),
        };

    /// <summary>
    /// Reports the set-level summary and its recommendation. Silent when nothing was observed — the
    ///  happy path must stay provably quiet.
    /// </summary>
    protected SetAnomalyAdvice ReportSetAnomalyOutcome(in TapeSetStatistics sets)
    {
        var advice = AdviseOnSetAnomalies(sets);
        if (advice == SetAnomalyAdvice.None)
            return advice;

        var parts = new List<string>(3);
        if (sets.AnomaliesDetected > 0)
            parts.Add($"{sets.AnomaliesDetected:N0} set anomaly(ies) detected");
        if (sets.AnomaliesRecovered > 0)
            parts.Add($"{sets.AnomaliesRecovered:N0} recovered");
        if (sets.AnomaliesRecoveredFromBom > 0)
            parts.Add($"{sets.AnomaliesRecoveredFromBom:N0} needed a full re-navigation");

        var (level, headline, action) = AdviseText(advice);

        _host.Report(level, headline);
        if (parts.Count > 0)
            _host.Report(level, string.Join(", ", parts), isSubEntry: true);
        if (action.Length > 0)
            _host.Report(ServiceReportLevel.Info, action, isSubEntry: true);

        return advice;
    }

    #endregion

    #region Formatting helpers

    // ── Formatting helpers ──────────────────────────────────────────

    /// <summary>Formats an elapsed duration as a human-readable string.</summary>
    public static string FormatElapsed(double totalSeconds)
    {
        if (totalSeconds < 1.0) return "< 1s";
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalMinutes < 1) return $"{ts.Seconds}s";
        if (ts.TotalHours < 1) return $"{ts.Minutes}m {ts.Seconds:D2}s";
        return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s";
    }

    /// <summary>
    /// Formats a data rate as <c>"X.XX MB/s"</c>; returns an empty string
    ///  when the duration is too short or no bytes were processed.
    /// </summary>
    public static string FormatDataIoRate(long bytes, double totalSeconds)
    {
        if (totalSeconds < 0.001 || bytes <= 0) return string.Empty;
        long bytesPerSecond = (long)(bytes / totalSeconds);
        return $"{Helpers.BytesToString(bytesPerSecond)}/s";
    }

    #endregion
}