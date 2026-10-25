using TapeLibNET.Scan;
using TapeLibNET.Toc;

namespace TapeLibNET.Agents;

/// <summary>
/// Exception thrown when user requests to abort a tape operation.
/// </summary>
public class TapeAbortRequestedException(string? message = null) :
    OperationCanceledException(message ?? "Operation aborted by user request.")
{
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


// ── The notifiable hierarchy (Design-TocLocation §5.2) ───────────────────────
//  ITapeTocNotifiable ← ITapeSetNotifiable ← ITapeFileNotifiable: every set operation reads or writes the TOC, and
//   every file operation is a set operation. Every member has a default implementation, and the members that moved up
//   kept their signatures — every existing implementer compiles unchanged.

/// <summary>
/// Callbacks for TOC-level anomalies — the narrowest notifiable, taken by the TOC verbs (<see cref="TapeAgentBase.BackupTOC"/>,
///  <see cref="TapeAgentBase.RestoreTOC"/>).
/// </summary>
/// <remarks>
/// A TOC anomaly is REPORTED, never asked about — the agent always tries whatever recovery is cheap (Design-TocLocation
///  §0, TL-4). The one question, before the full-media walk, is <see cref="OnTocFullWalkConfirm"/> (Phase 5).
/// </remarks>
public interface ITapeTocNotifiable
{
    /// <summary>
    /// A TOC copy could not be written or read as the layout expects. Raised at most ONCE per volume per operation;
    ///  further findings on that volume are recorded in <see cref="TapeAgentBase.TocAnomalies"/> without a callback.
    /// </summary>
    void OnTocAnomaly(in TapeTocAnomaly anomaly, in TapeFileStatistics stats) { }

    /// <summary>A TOC anomaly was settled; <see cref="TapeTocAnomaly.Stage"/> says how. Informational.</summary>
    void OnTocAnomalyRecovered(in TapeTocAnomaly anomaly, in TapeFileStatistics stats) { }

    /// <summary>
    /// Asked before the full-media walk for the TOC of <paramref name="volume"/> (S4, Phase 5). Default: proceed — the
    ///  walk only reads, and an unattended run must not stall.
    /// </summary>
    bool OnTocFullWalkConfirm(int volume) => true;

    /// <summary>
    /// Progress of the full-media walk (S4), once it was confirmed: a sample at its start, then one per fragment met.
    ///  May throw <see cref="TapeAbortRequestedException"/> to stop the walk at the next block.
    /// </summary>
    void OnTocSearchProgress(in TapeScanProgress progress) { }
}

/// <summary>
/// Callbacks for set-level anomalies — taken by the set verbs (delete). Extends <see cref="ITapeTocNotifiable"/>: every
///  set operation also writes the TOC.
/// </summary>
public interface ITapeSetNotifiable : ITapeTocNotifiable
{
    // ── Set-level anomalies ──────────────────────────────────────────────
    //  Default implementations, so every existing implementer compiles untouched. The defaults are
    //   asymmetric on purpose: recovery is merely reported, but a notifiable that has not been taught
    //   about destructive recovery must never silently authorize it (SH-18).

    /// <summary>
    /// Called when a set does not verify against the TOC. Return <see cref="SetAnomalyAction.Proceed"/>
    ///  to authorize the remaining recovery stages, or <see cref="SetAnomalyAction.Abort"/> to stop.
    /// <para>
    /// Raised at most ONCE per set — being prompted twice about one cartridge reasonably reads as a loop.
    ///  <paramref name="stats"/><c>.SetStats</c> carries the running counts, so an implementation can weigh
    ///  the third anomaly differently from the first.
    /// </para>
    /// </summary>
    SetAnomalyAction OnSetAnomaly(in TapeSetAnomaly anomaly, in TapeFileStatistics stats)
        => SetAnomalyAction.Abort;

    /// <summary>Called after an anomaly has been corrected and re-verified. Informational.</summary>
    void OnSetAnomalyRecovered(in TapeSetAnomaly anomaly, in TapeFileStatistics stats) { }
}

/// <summary>
/// Callback interface for file-level progress notifications during backup, restore, and verify operations.
/// <para>Implementations control the UI (progress bars, logs) and can influence the operation:
///  <see cref="PreProcessFile"/> can skip files, <see cref="OnFileFailed"/> can retry or abort.
///  Any callback may throw <see cref="TapeAbortRequestedException"/> to abort immediately.</para>
/// <para>Every callback receives a <see cref="TapeFileStatistics"/> snapshot reflecting the
///  state <em>after</em> the event (e.g. counters are incremented before the call).</para>
/// </summary>
public interface ITapeFileNotifiable : ITapeSetNotifiable
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

    /// <summary>
    /// A file the tape lost after it was handed over: a media write fault took it with the buffer it was in. REPORTED,
    ///  not asked about — there is nothing to retry or skip, and the set ends with the fault.
    /// </summary>
    /// <param name="fileInfo">The lost file (no tape address — it never got one).</param>
    /// <param name="reason">The drive's error.</param>
    /// <param name="stats">Statistics with this file already counted as failed.</param>
    void OnFileLost(TapeFileInfo fileInfo, TapeResult reason, in TapeFileStatistics stats) { }
}
