using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TapeLibNET;

/// <summary>
/// Exception thrown when user requests to abort a tape operation.
/// </summary>
public class TapeAbortRequestedException(string? message = null) :
    OperationCanceledException(message ?? "Operation aborted by user request.")
{
}

/// <summary>
/// Cumulative SET-level statistics, mirroring <see cref="TapeFileStatistics"/> one level up.
/// </summary>
/// <remarks>
/// <para>
/// Nested inside <see cref="TapeFileStatistics"/> rather than exposed separately, so every existing
///  callback already receives it: legacy implementations ignore the field, and new ones can use it to
///  inform a decision — a third anomaly on one cartridge argues for aborting where the first argued for
///  repairing.
/// </para>
/// <para>
/// Counters ONLY. The anomaly records live on the agent (<seealso cref="TapeFileAgent.SetAnomalies"/>), because a
///  struct copied into every callback cannot own a list without every copy aliasing it.
/// </para>
/// </remarks>
public struct TapeSetStatistics
{
    /// <summary>Sets entered (started), whatever their outcome.</summary>
    public int SetsProcessed;
    /// <summary>Sets completed without a set-level anomaly.</summary>
    public int SetsSucceeded;
    /// <summary>Set anomalies detected — corrected or not. May exceed <see cref="SetsProcessed"/>.</summary>
    public int AnomaliesDetected;
    /// <summary>Set anomalies corrected and re-verified, by either recovery stage.</summary>
    public int AnomaliesRecovered;
    /// <summary>Set anomalies that needed the second stage — the re-navigation from BOM (SH-14, Step 5).</summary>
    /// <remarks>
    /// A subset of <see cref="AnomaliesRecovered"/>, tracked apart because it indicts the TAIL of the
    ///  cartridge rather than a single mark — a sharper signal for whoever is holding it.
    /// </remarks>
    public int AnomaliesRecoveredFromBom;
    /// <summary>
    /// Whether a destructive write was refused. A <see langword="bool"/>, not a counter: the refusal
    ///  aborts the operation, so it can happen at most once within one operation.
    /// </summary>
    public bool SetWriteBlocked;
    /// <summary>True when anything at all went wrong at set level during this operation.</summary>
    public readonly bool HasAnomalies => AnomaliesDetected > 0;

    /// <summary>Reset all counters to zero.</summary>
    public void Reset() => this = default;

    /// <summary>Difference against an earlier snapshot, mirroring <see cref="TapeFileStatistics.Delta"/>.</summary>
    public readonly TapeSetStatistics Delta(in TapeSetStatistics baseline) => new()
    {
        SetsProcessed = SetsProcessed - baseline.SetsProcessed,
        SetsSucceeded = SetsSucceeded - baseline.SetsSucceeded,
        AnomaliesDetected = AnomaliesDetected - baseline.AnomaliesDetected,
        AnomaliesRecovered = AnomaliesRecovered - baseline.AnomaliesRecovered,
        AnomaliesRecoveredFromBom = AnomaliesRecoveredFromBom - baseline.AnomaliesRecoveredFromBom,
        SetWriteBlocked = SetWriteBlocked,   // latched, not differenced
    };
}

/// <summary>Action chosen by <see cref="ITapeFileNotifiable.OnFileFailed"/> when a file operation fails.</summary>
public enum FileFailedAction
{
    /// <summary>Skip this file and continue with the next.</summary>
    Skip,
    /// <summary>Retry the same file from the beginning.</summary>
    Retry,
    /// <summary>Abort the entire operation.</summary>
    Abort,
    /// <summary>Skip this file and all future failures without prompting.</summary>
    SkipAll
}

/// <summary>Action chosen by <see cref="ITapeFileNotifiable.OnSetAnomaly"/> when a set does not verify.</summary>
public enum SetAnomalyAction
{
    /// <summary>Stop the operation. The safe default for a notifiable that has not been taught otherwise.</summary>
    Abort,
    /// <summary>Authorize the remaining recovery stages — or, where none remain, the terminal action.</summary>
    Proceed,
}

/// <summary>Which recovery stage produced — or failed at — a <see cref="TapeSetAnomaly"/>.</summary>
/// <remarks>
/// A drift settled by the relative delta indicts a MARK; one that needed the re-navigation indicts the
///  TAIL. Different facts about the cartridge, so the summary must be able to tell them apart (SH-16).
/// </remarks>
public enum TapeSetAnomalyStage
{
    /// <summary>Detected on the first verification; no recovery attempted yet.</summary>
    Detected,
    /// <summary>Produced or settled by the relative-delta correction (SH-10).</summary>
    Delta,
    /// <summary>Produced or settled by the re-navigation from begin-of-content (SH-14, Step 5).</summary>
    Renavigated,
}

/// <summary>
/// A set that cannot be used as the TOC describes it — the payload of both set-level callbacks.
/// </summary>
/// <remarks>
/// Carries DESCRIPTIONS, not merely indices: a prompt must name WHICH set, which is the same reason
///  <see cref="TapeSetHeader.Description"/> was admitted to the record at all.
///  <see cref="ActualDescription"/> comes from <see cref="TapeSetHeader.DisplayName"/> and is therefore
///  never empty — it synthesizes a name from the indices when the header carries none.
/// </remarks>
public readonly record struct TapeSetAnomaly(
    TapeSetHeaderVerdict Verdict,
    TapeSetAnomalyStage Stage,
    int SetIndex,
    int ExpectedVolumeSetIndex,
    int ActualVolumeSetIndex,
    string ExpectedDescription,
    string ActualDescription,
    int ExpectedVolume,
    int ActualVolume,
    bool IsDestructive,
    bool CanAttemptRecovery,
    TapeResult Diagnosis)
{
    /// <summary>
    /// A one-line summary suitable for a log entry or the head of a prompt.
    /// </summary>
    /// <remarks>
    /// Descriptions in <c>&gt;…&lt;</c>, as everywhere else in TapeNET. The ALT index is deliberately
    ///  absent: deriving it needs the TOC, which this record does not carry (and must not, being a
    ///  by-value payload). The service-side handler adds it — see
    ///  <see cref="Services.ServiceOperationProgressHandler.DescribeSet"/>.
    /// </remarks>
    public override string ToString() =>
        $"Set #{SetIndex} anomaly ({Verdict}, {Stage}): expected on-volume set {ExpectedVolumeSetIndex} " +
        $">{ExpectedDescription}<, found {ActualVolumeSetIndex} >{ActualDescription}<";
}

/// <summary>
/// Cumulative file-operation statistics maintained by the tape agent.
/// A snapshot is passed to every <see cref="ITapeFileNotifiable"/> callback so
/// the caller never needs to track its own counters.
/// <para>Invariant: <c>FilesProcessed == FilesSucceeded + FilesFailed + FilesSkipped</c></para>
/// </summary>
public struct TapeFileStatistics
{
    /// <summary>Total files expected for the entire operation (across all batches/volumes).</summary>
    public int FilesTotal;
    /// <summary>
    /// Total logical (actual file-length) bytes estimated for the entire operation
    ///  (across all batches/volumes). May keep growing as the operation progresses
    ///  — see the background size estimation started by <c>TapeFileBackupAgent</c>/
    ///  <c>TapeFileRestoreBaseAgent</c>.
    /// </summary>
    public long BytesTotal;

    /// <summary>Files finished (succeeded + failed + skipped). Retried files are counted once.</summary>
    public int FilesProcessed;
    /// <summary>Files completed without error.</summary>
    public int FilesSucceeded;
    /// <summary>Files that hit an error and were not retried.</summary>
    public int FilesFailed;
    /// <summary>Files skipped (by pre-processor, incremental, or user choice).</summary>
    public int FilesSkipped;
    /// <summary>
    /// Total logical (actual file-length) bytes of succeeded files. Comparable to
    ///  <see cref="BytesTotal"/> for computing a logical-size-based completion share.
    /// </summary>
    public long FileBytesProcessed;
    /// <summary>
    /// Total on-tape footprint (header + body, after any software compression) of succeeded
    ///  files, as recorded via <see cref="TapeFileInfo.SizeOnTape"/>. This can diverge from
    ///  <see cref="FileBytesProcessed"/> when software compression is in effect — it never
    ///  reflects hardware tape-drive compression, which isn't observable above the drive I/O.
    /// </summary>
    public long BytesOnTapeProcessed;

    /// <summary>
    /// Set-level statistics for the same operation. Non-nullable: an empty struct reads as "clean", so
    ///  legacy callers that never look at it are never wrong.
    /// </summary>
    public TapeSetStatistics Sets;

    /// <summary>Reset all counters to zero, including <see cref="Sets"/>.</summary>
    public void Reset() => this = default;

    /// <summary>
    /// Returns a new <see cref="TapeFileStatistics"/> whose counters are the difference
    ///  between this snapshot and an earlier <paramref name="baseline"/> snapshot.
    ///  Useful for computing per-batch statistics from the running totals.
    /// </summary>
    public readonly TapeFileStatistics Delta(in TapeFileStatistics baseline) => new()
    {
        FilesTotal = FilesTotal,
        BytesTotal = BytesTotal,
        FilesProcessed = FilesProcessed - baseline.FilesProcessed,
        FilesSucceeded = FilesSucceeded - baseline.FilesSucceeded,
        FilesFailed = FilesFailed - baseline.FilesFailed,
        FilesSkipped = FilesSkipped - baseline.FilesSkipped,
        FileBytesProcessed = FileBytesProcessed - baseline.FileBytesProcessed,
        BytesOnTapeProcessed = BytesOnTapeProcessed - baseline.BytesOnTapeProcessed,

        Sets = Sets.Delta(baseline.Sets),
    };
}

/// <summary>
/// Callback interface for file-level progress notifications during backup, restore, and verify operations.
/// <para>Implementations control the UI (progress bars, logs) and can influence the operation:
///  <see cref="PreProcessFile"/> can skip files, <see cref="OnFileFailed"/> can retry or abort.
///  Any callback may throw <see cref="TapeAbortRequestedException"/> to abort immediately.</para>
/// <para>Every callback receives a <see cref="TapeFileStatistics"/> snapshot reflecting the
///  state <em>after</em> the event (e.g. counters are incremented before the call).</para>
/// </summary>
public interface ITapeFileNotifiable
{
    /// <summary>Called when a new set begins processing. <paramref name="setIndex"/> is 1-based.</summary>
    void SetStart(int setIndex, in TapeFileStatistics stats);
    /// <summary>Called when a set finishes processing.</summary>
    void SetEnd(int setIndex, in TapeFileStatistics stats);

    // The following methods may throw TapeAbortRequestedException to abort the entire operation (not just the file)

    /// <summary>Called before processing a file. Return false to skip the file.</summary>
    bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats);
    /// <summary>Called after successfully processing a file. Return false to skip applying file attributes.</summary>
    bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats);
    /// <summary>Called when a file error occurs. Returns how to proceed.</summary>
    FileFailedAction OnFileFailed(TapeFileInfo fileInfo, TapeResult result, in TapeFileStatistics stats);
    /// <summary>Called when a file is skipped.</summary>
    void OnFileSkipped(TapeFileInfo fileInfo, in TapeFileStatistics stats);

    // ── Set-level anomalies ──────────────────────────────────────────────
    //  Default implementations, so every existing implementer compiles untouched. The defaults are
    //   asymmetric on purpose: recovery is merely reported, but a notifiable that has not been taught
    //   about destructive recovery must never silently authorize it (SH-18).

    /// <summary>
    /// Called when a set does not verify against the TOC. Return <see cref="SetAnomalyAction.Proceed"/>
    ///  to authorize the remaining recovery stages, or <see cref="SetAnomalyAction.Abort"/> to stop.
    /// <para>
    /// Raised at most ONCE per set — being prompted twice about one cartridge reasonably reads as a loop.
    ///  <paramref name="stats"/><c>.Sets</c> carries the running counts, so an implementation can weigh
    ///  the third anomaly differently from the first.
    /// </para>
    /// </summary>
    SetAnomalyAction OnSetAnomaly(in TapeSetAnomaly anomaly, in TapeFileStatistics stats)
        => SetAnomalyAction.Abort;
    /// <summary>Called after an anomaly has been corrected and re-verified. Informational.</summary>
    void OnSetAnomalyRecovered(in TapeSetAnomaly anomaly, in TapeFileStatistics stats) { }
}

