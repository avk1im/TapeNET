using TapeLibNET.Drive;

namespace TapeLibNET.Agents;

/// <summary>What is wrong with a TOC copy, or with the search for one (Design-TocLocation §5.1).</summary>
public enum TapeTocAnomalyKind
{
    /// <summary>One copy could not be written; the other one was. The TOC is on tape, without its redundancy.</summary>
    CopyWriteFailed,
    /// <summary>No copy could be written: this volume carries no TOC.</summary>
    NoCopyWritten,
    /// <summary>The layout's own TOC position could not be reached — no copy was found there to read.</summary>
    NotFoundNatively,
    /// <summary>A copy was found but did not read (torn, CRC); another copy did.</summary>
    CopyUnreadable,
    /// <summary>A copy of another series or volume, or one implausibly placed, was found and skipped (Phase 4).</summary>
    ForeignCopySkipped,
    /// <summary>The TOC position was reached, but no copy there could be read.</summary>
    NoReadableCopy,
}

/// <summary>
/// Which stage produced — or settled — a TOC anomaly. Mirrors <see cref="TapeSetAnomalyStage"/>: the DETECTION carries
///  <see cref="Detected"/>, the RECOVERY the stage that succeeded.
/// </summary>
public enum TapeTocAnomalyStage
{
    /// <summary>Detected; no recovery yet.</summary>
    Detected,
    /// <summary>Settled by the other copy, one file away (S1).</summary>
    Sibling,
    /// <summary>Settled at the expected position from the series TOC (S3, Phase 4).</summary>
    ExpectedPosition,
    /// <summary>Settled by the walk from a layout anchor (S2, Phase 4).</summary>
    AnchorWalk,
    /// <summary>Settled by the full-media walk (S4, Phase 5).</summary>
    FullWalk,
    /// <summary>
    /// Settled by a later attempt on the same volume within the same operation: an enforced TOC rewrite that wrote the
    ///  copies, or a re-inserted cartridge whose TOC then read.
    /// </summary>
    Retried,
}

/// <summary>
/// A TOC that could not be written or read as the layout expects — the payload of both TOC callbacks. By value; the
///  agent keeps the operation's trail in <see cref="TapeAgentBase.TocAnomalies"/>.
/// </summary>
/// <param name="Kind">What went wrong.</param>
/// <param name="Stage"><see cref="TapeTocAnomalyStage.Detected"/> on the detection; the succeeding stage on the recovery.</param>
/// <param name="Volume">The volume concerned — the one written to, or the one identified on read; 0 = unknown.</param>
/// <param name="CopyIndex">1 or 2; 0 = not about one copy.</param>
/// <param name="Partition">Where the layout keeps its TOC.</param>
/// <param name="Block">Where the copy stands; -1 = unknown (Phase 4 knows it).</param>
/// <param name="CanAttemptRecovery">Whether a further stage remains.</param>
/// <param name="Diagnosis">The drive's or the reader's own error; OK on a recovery.</param>
public readonly record struct TapeTocAnomaly(
    TapeTocAnomalyKind Kind,
    TapeTocAnomalyStage Stage,
    int Volume,
    byte CopyIndex,
    MediaPartition Partition,
    long Block,
    bool CanAttemptRecovery,
    TapeResult Diagnosis)
{
    /// <summary>Whether this is a recovery event rather than a detection.</summary>
    public bool IsRecovery => Stage != TapeTocAnomalyStage.Detected;

    /// <summary>What happened, in one clause — culture-neutral, for log lines and the head of a report.</summary>
    public string Summary => Kind switch
    {
        TapeTocAnomalyKind.CopyWriteFailed => $"TOC copy {CopyIndex} could not be written",
        TapeTocAnomalyKind.NoCopyWritten => "no TOC copy could be written",
        TapeTocAnomalyKind.NotFoundNatively => "the TOC was not found where this layout keeps it",
        TapeTocAnomalyKind.CopyUnreadable => $"TOC copy {CopyIndex} is unreadable",
        TapeTocAnomalyKind.ForeignCopySkipped => "a TOC copy of another series or volume was skipped",
        TapeTocAnomalyKind.NoReadableCopy => "no readable TOC copy",
        _ => Kind.ToString(),
    };

    /// <summary>How it was settled — meaningful on a recovery event.</summary>
    public string RecoverySummary => Stage switch
    {
        TapeTocAnomalyStage.Sibling => "the TOC was read from its other copy",
        TapeTocAnomalyStage.ExpectedPosition => "the TOC was found at its expected position",
        TapeTocAnomalyStage.AnchorWalk or TapeTocAnomalyStage.FullWalk => "the TOC was found by searching",
        TapeTocAnomalyStage.Retried => Kind is TapeTocAnomalyKind.CopyWriteFailed or TapeTocAnomalyKind.NoCopyWritten
            ? "the TOC was written on a further attempt"
            : "the TOC was read on a further attempt",
        _ => "settled",
    };

    public override string ToString() =>
        $"Volume #{Volume} TOC anomaly ({Kind}, {Stage}): {(IsRecovery ? RecoverySummary : Summary)}";
}

