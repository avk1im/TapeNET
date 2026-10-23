using TapeLibNET.Toc;

namespace TapeLibNET.Agents;

/// <summary>
/// Cumulative TOC statistics of one operation, nested in <see cref="TapeFileStatistics.TocStats"/>. Counters only — the
///  records live on the agent, for the same aliasing reason as <see cref="TapeSetStatistics"/>.
/// </summary>
public struct TapeTocStatistics
{
    /// <summary>TOC copies written, over all volumes.</summary>
    public int CopiesWritten;
    /// <summary>TOC copies attempted (or skipped at end-of-media) and not written.</summary>
    public int CopyWriteFailures;
    /// <summary>Volumes left without any TOC copy — not counting those a later attempt fixed.</summary>
    public int VolumesWithoutToc;
    /// <summary>TOC anomalies detected — counted once per volume, the user-meaningful number.</summary>
    public int AnomaliesDetected;
    /// <summary>TOC anomalies settled, by any stage.</summary>
    public int AnomaliesRecovered;
    /// <summary>Subset of <see cref="AnomaliesRecovered"/> that needed a walk (S2 / S4).</summary>
    public int AnomaliesRecoveredByWalk;

    /// <summary>True when anything at all went wrong with a TOC during this operation.</summary>
    public readonly bool HasAnomalies => AnomaliesDetected > 0;

    /// <summary>Reset all counters to zero.</summary>
    public void Reset() => this = default;

    /// <summary>Difference against an earlier snapshot, mirroring <see cref="TapeSetStatistics.Delta"/>.</summary>
    public readonly TapeTocStatistics Delta(in TapeTocStatistics baseline) => new()
    {
        CopiesWritten = CopiesWritten - baseline.CopiesWritten,
        CopyWriteFailures = CopyWriteFailures - baseline.CopyWriteFailures,
        VolumesWithoutToc = VolumesWithoutToc,     // a state, not differenced
        AnomaliesDetected = AnomaliesDetected - baseline.AnomaliesDetected,
        AnomaliesRecovered = AnomaliesRecovered - baseline.AnomaliesRecovered,
        AnomaliesRecoveredByWalk = AnomaliesRecoveredByWalk - baseline.AnomaliesRecoveredByWalk,
    };
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
/// Counters ONLY. The anomaly records live on the agent (<seealso cref="TapeAgentBase.SetAnomalies"/>), because a
///  struct copied into every callback cannot own a list without every copy aliasing it.
/// </para>
/// </remarks>
public struct TapeSetStatistics
{
    /// <summary>SetStats entered (started), whatever their outcome.</summary>
    public int SetsProcessed;

    /// <summary>SetStats completed without a set-level anomaly.</summary>
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
    public TapeSetStatistics SetStats;

    /// <summary>
    /// TOC-level statistics for the same operation (Design-TocLocation §5.1) — across ALL its volumes. Non-nullable,
    ///  clean when empty, for the same reason as <see cref="SetStats"/>.
    /// </summary>
    public TapeTocStatistics TocStats;

    /// <summary>Reset all counters to zero, including <see cref="SetStats"/> and <see cref="TocStats"/>.</summary>
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
        SetStats = SetStats.Delta(baseline.SetStats),
        TocStats = TocStats.Delta(baseline.TocStats),
    };
}