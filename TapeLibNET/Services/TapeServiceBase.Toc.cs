using TapeLibNET.Agents;
using TapeLibNET.Headers;
using TapeLibNET.Toc;

namespace TapeLibNET.Services;

/// <summary>
/// What the service recommends the user DO about the TOC anomalies an operation met (Design-TocLocation §5.4). Policy,
///  computed from the anomaly records by <see cref="TapeServiceBase.AdviseOnTocAnomalies"/>; the wording lives in
///  <see cref="TapeServiceBase.AdviseTocText"/>.
/// </summary>
/// <remarks>
/// By CAUSE and LAYOUT, never by symptom alone: "only one copy fit" calls for calibration on a TOC-in-set cartridge and
///  for fewer files per set on a TOC-in-partition one. Ordered by severity, most severe first.
/// </remarks>
public enum TocAnomalyAdvice
{
    /// <summary>No anomalies — nothing to say.</summary>
    None,
    /// <summary>A copy found only by searching: re-save it so it is found directly again.</summary>
    TocFoundBySearch,
    /// <summary>A copy could not be written for another reason, or did not read: the medium is suspect.</summary>
    SingleCopyDamaged,
    /// <summary>TOC in set: only one copy fit before end-of-media — the reserve armed too late.</summary>
    SingleCopyReserve,
    /// <summary>An append was refused: the TOC's recorded end of content and the tape disagree.</summary>
    EndOfContentDisputed,
    /// <summary>TOC in partition: only one copy fit — the TOC outgrew its partition.</summary>
    TocPartitionFull,
    /// <summary>The volume's TOC could not be read at all.</summary>
    TocUnreadable,
    /// <summary>No copy could be written: the volume carries no TOC.</summary>
    TocLost,
}

public partial class TapeServiceBase
{
    #region TOC persistence (Design-FormatV21 §9.2, F28)

    // ── TOC persistence ───────────────────────────────────────────────────────
    //  Every verb that writes the TOC to tape goes through SaveTocCore (backup, rename media, rename set)
    //   or reports through ReportTocUpgrade (delete, whose agent writes the TOC internally). Format and the
    //   initial TOC need neither: they write a freshly created TOC, which is never legacy.

    /// <summary>
    /// Writes the TOC to tape through <paramref name="agent"/> and reports a legacy → 2.1 upgrade once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A legacy tape's TOC upgrades to format 2.1 on its first write (Design-FormatV21 §9.2, F3). From then on, earlier
    ///  TapeNET builds can no longer read the tape's TOC — worth one warning, never a prompt: unattended runs must not
    ///  stall on it.
    /// </para>
    /// <para>
    /// "Once" follows from the flag itself: the agent clears <see cref="TapeTOC.LoadedFromLegacy"/> when a TOC copy
    ///  reaches the tape, so a later save in the same operation (the next volume of a multi-volume backup) finds it
    ///  cleared and stays silent.
    /// </para>
    /// <para>
    /// TOC anomalies are NOT reported here: one operation may save the TOC once per volume, and its report covers all of
    ///  them (<see cref="ReportTocAnomalyOutcome(TapeAgentBase)"/>).
    /// </para>
    /// </remarks>
    /// <param name="agent">The agent that owns the navigator for this operation.</param>
    /// <param name="enforce">Passed through to <see cref="TapeAgentBase.BackupTOC"/>.</param>
    /// <param name="tocNotify">The operation's notifiable, for live TOC anomaly callbacks; optional.</param>
    protected TapeResult SaveTocCore(TapeAgentBase agent, bool enforce = false, ITapeTocNotifiable? tocNotify = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        bool wasLegacy = agent.TOC.LoadedFromLegacy;
        TapeResult result = agent.BackupTOC(enforce: enforce, tocNotify: tocNotify);
        ReportTocUpgrade(wasLegacy, agent.TOC);
        return result;
    }

    /// <summary>
    /// Reports a legacy → 2.1 TOC upgrade if one just happened: the TOC was legacy before the operation and is no
    ///  longer. Silent otherwise — including when the write failed and the TOC stayed legacy.
    /// </summary>
    /// <param name="wasLegacy"><see cref="TapeTOC.LoadedFromLegacy"/> as captured before the write.</param>
    /// <param name="toc">The TOC after the write.</param>
    protected void ReportTocUpgrade(bool wasLegacy, TapeTOC toc)
    {
        if (!wasLegacy || toc.LoadedFromLegacy)
            return;
        LogWarn("Table of contents upgraded to format 2.1");
        LogWarnSub("Earlier TapeNET versions can no longer read this tape's table of contents");
    }

    /// <summary>Display text for a TOC's on-tape format.</summary>
    protected static string DescribeTocFormat(TapeTOC toc) => toc.LoadedFromLegacy
        ? "legacy (pre-2.1) — upgraded to 2.1 on the next write"
        : "2.1";

    /// <summary>Display text for a set's data format (file headers on tape).</summary>
    protected static string DescribeDataFormat(TapeSetTOC set) => set.DataFormat == TapeDataFormat.Legacy
        ? "legacy (pre-2.1)"
        : "2.1";

    #endregion

    #region TOC search (Design-TocLocation §4, S4)

    // ── TOC search ──────────────────────────────────────────────────────────
    //  The one question the TOC location asks — before reading the whole medium (S4) — and its progress. Every TOC read the
    //   service starts on its own (RestoreTOCAsync, IdentifyMediaAsync, the restore's initial read) goes through a
    //   ServiceTocProgressHandler made by CreateTocProgressHandler; apps override the factory to show the progress.
    //  A multi-volume resume never asks: it holds the series TOC, which addresses every file (the agent skips S4 there).

    /// <summary>
    /// Creates the notifiable for a TOC read the service starts on its own — the one that answers the full-media search
    ///  question and receives its progress. Override in app subclasses to display the progress.
    /// </summary>
    /// <param name="agent">The agent about to read the TOC — its abort flag stops the search.</param>
    protected virtual ServiceTocProgressHandler CreateTocProgressHandler(TapeAgentBase agent)
        => new(_host, agent);

    /// <summary>
    /// The handler for a TOC read of <paramref name="agent"/>, wired to the service's full-media search question.
    /// </summary>
    /// <param name="alreadyConsented">
    /// The user has just agreed to a lengthy TOC search on this cartridge (<see cref="MediaPromptContext.SearchForTOC"/>):
    ///  asking again would be asking twice.
    /// </param>
    protected ServiceTocProgressHandler CreateTocProgressHandlerFor(TapeAgentBase agent, bool alreadyConsented = false)
    {
        ServiceTocProgressHandler handler = CreateTocProgressHandler(agent);
        handler.FullWalkConfirm = alreadyConsented
            ? volume => { LogInfo("Searching the whole medium for a table of contents..."); return true; }
            : volume => ConfirmFullWalk(volume, suppress: false);
        return handler;
    }

    /// <summary>
    /// Asks — through the media prompt — whether to search the whole medium for the TOC of <paramref name="volume"/> (S4).
    /// </summary>
    /// <param name="volume">The volume concerned; 0 when unknown (a plain load).</param>
    /// <param name="suppress">Answer Proceed without asking (unattended), logged.</param>
    /// <returns><see langword="true"/> to search.</returns>
    /// <remarks>
    /// The verdict is <see cref="TapeMediaVerdict.Unidentified"/> — the TOC, not the cartridge, is what could not be
    ///  identified — and the context <see cref="MediaPromptContext.FullWalkForTOC"/> tells the host to word it so: the
    ///  cartridge may well carry a valid media header. Read-only, so Proceed is the unattended answer.
    /// </remarks>
    protected bool ConfirmFullWalk(int volume, bool suppress)
    {
        LogWarn($"The table of contents of {DescribeVolumes([volume])} was not found where it should be");
        MediaMismatchChoice choice = PresentVerdict(TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC,
            suppress, allowRetry: false, allowProceedAlways: false);
        if (choice == MediaMismatchChoice.Abort)
        {
            LogInfo("Search of the whole medium declined");
            return false;
        }
        LogInfo("Searching the whole medium for a table of contents...");
        OnStatusUpdate("Searching for the TOC...");
        return true;
    }

    #endregion

    #region TOC anomaly advisory (Design-TocLocation §5.4)

    // ── TOC anomaly advisory ────────────────────────────────────────────────
    //  The counterpart of the set-anomaly advisory in TapeServiceBase.Outcome.cs. Online: one headline, one action, and a
    //   pointer to the Help topic "TOC space" where the cause is the TOC's room on tape (§0.4).

    /// <summary>The Help topic the reserve-related advice points to.</summary>
    public const string TocSpaceHelpTopic = "TOC space";

    /// <summary>
    /// Derives the recommendation from an operation's TOC anomaly trail, and the volumes it concerns. Pure; mirrors
    ///  <see cref="AdviseOnSetAnomalies"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A detection is SETTLED when the trail holds a recovery for the same volume, kind and copy — settled detections advise
    ///  nothing. One exception: a copy that did not read stays damaged on tape when its sibling settled it
    ///  (<see cref="TapeTocAnomalyStage.Sibling"/>); only a TOC write on that volume (<see cref="TapeTocAnomalyStage.Retried"/>)
    ///  replaces it.
    /// </para>
    /// <para>
    /// Severity order: no TOC written → no TOC readable → an append refused at a disputed end → one copy for lack of room →
    ///  one copy for another reason or a damaged copy → found only by searching.
    /// </para>
    /// </remarks>
    public static (TocAnomalyAdvice Advice, IReadOnlyList<int> Volumes) AdviseOnTocAnomalies(
        IReadOnlyList<TapeTocAnomaly> anomalies, TapeTocPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(anomalies);

        // A detection about one copy is settled by a recovery of THAT copy; one about no particular copy (CopyIndex 0 —
        //  the TOC position, a search) by any recovery of its kind on its volume: the recovery names the copy it found.
        bool SettledBy(TapeTocAnomaly d, Func<TapeTocAnomalyStage, bool> stage) => anomalies.Any(r => r.IsRecovery
            && r.Volume == d.Volume && r.Kind == d.Kind && stage(r.Stage)
            && (d.CopyIndex == 0 || r.CopyIndex == d.CopyIndex));
        bool Settled(TapeTocAnomaly d) => SettledBy(d, _ => true);
        bool Rewritten(TapeTocAnomaly d) => SettledBy(d, s => s == TapeTocAnomalyStage.Retried);

        List<int> Volumes(Func<TapeTocAnomaly, bool> which)
            => [.. anomalies.Where(a => !a.IsRecovery && which(a)).Select(a => a.Volume).Distinct().Order()];

        List<int> v = Volumes(a => a.Kind == TapeTocAnomalyKind.NoCopyWritten && !Settled(a));
        if (v.Count > 0)
            return (TocAnomalyAdvice.TocLost, v);

        v = Volumes(a => a.Kind is TapeTocAnomalyKind.NotFoundNatively or TapeTocAnomalyKind.NoReadableCopy
                         && !Settled(a));
        if (v.Count > 0)
            return (TocAnomalyAdvice.TocUnreadable, v);

        v = Volumes(a => a.Kind == TapeTocAnomalyKind.RecordedEndDisputed);
        if (v.Count > 0)
            return (TocAnomalyAdvice.EndOfContentDisputed, v);

        v = Volumes(a => a.Kind == TapeTocAnomalyKind.CopyWriteFailed && !Settled(a)
                         && ErrorManageableBase.IsEOM(a.Diagnosis.ErrorCode));
        if (v.Count > 0)
            return (placement == TapeTocPlacement.InPartition
                ? TocAnomalyAdvice.TocPartitionFull : TocAnomalyAdvice.SingleCopyReserve, v);

        // A damaged copy: still on tape unless a TOC write replaced it; a failed write: unless a later write succeeded.
        v = Volumes(a => (a.Kind == TapeTocAnomalyKind.CopyUnreadable && !Rewritten(a))
                         || (a.Kind == TapeTocAnomalyKind.CopyWriteFailed && !Settled(a)));
        if (v.Count > 0)
            return (TocAnomalyAdvice.SingleCopyDamaged, v);

        // Found only by searching — unless a TOC write on that volume has since put it back where the layout keeps it.
        v = [.. anomalies.Where(r => r.Stage is TapeTocAnomalyStage.ExpectedPosition
                    or TapeTocAnomalyStage.AnchorWalk or TapeTocAnomalyStage.FullWalk)
                .Where(r => !anomalies.Any(w => w.Stage == TapeTocAnomalyStage.Retried && w.Volume == r.Volume))
                .Select(r => r.Volume).Distinct().Order()];
        if (v.Count > 0)
            return (TocAnomalyAdvice.TocFoundBySearch, v);

        return (TocAnomalyAdvice.None, []);
    }

    /// <summary>"volume #2", "volumes #1, #3", or "the loaded volume" when the volume could not be identified.</summary>
    public static string DescribeVolumes(IReadOnlyList<int> volumes)
    {
        var known = volumes.Where(v => v > 0).ToList();
        if (known.Count == 0)
            return "the loaded volume";
        return known.Count == 1
            ? $"volume #{known[0]}"
            : $"volumes {string.Join(", ", known.Select(v => $"#{v}"))}";
    }

    /// <summary>Renders an advice as a headline plus one actionable sub-line. Mirrors <see cref="AdviseText"/>.</summary>
    public static (ServiceReportLevel Level, string Headline, string Action) AdviseTocText(
        TocAnomalyAdvice advice, IReadOnlyList<int> volumes)
    {
        string on = DescribeVolumes(volumes);
        return advice switch
        {
            TocAnomalyAdvice.TocLost => (
                ServiceReportLevel.Failed,
                $"The table of contents could not be written to {on}",
                "Export the TOC to a file (Media | Export TOC to file), unless done already — " +
                $"the files stay restorable through it (Help: {TocSpaceHelpTopic})"),
            TocAnomalyAdvice.TocUnreadable => (
                ServiceReportLevel.Failed,
                $"The table of contents of {on} could not be read",
                "Recover it with Scan Media, or import a TOC file exported earlier"),
            TocAnomalyAdvice.EndOfContentDisputed => (
                ServiceReportLevel.Failed,
                $"The end of content on {on} does not match its table of contents — nothing was written",
                "Scan the media (Media | Scan Media) and adopt the TOC it recovers, or reload the TOC; " +
                "then retry the backup"),
            TocAnomalyAdvice.SingleCopyReserve => (
                ServiceReportLevel.Warning,
                $"Only one TOC copy fit on {on}",
                "Export the TOC to a file now; calibrate the drive for this media to prevent it " +
                $"(Help: {TocSpaceHelpTopic})"),
            TocAnomalyAdvice.TocPartitionFull => (
                ServiceReportLevel.Warning,
                $"The table of contents no longer fits its partition on {on}",
                "Export the TOC to a file now; back up fewer files per set or per volume " +
                $"(Help: {TocSpaceHelpTopic})"),
            TocAnomalyAdvice.SingleCopyDamaged => (
                ServiceReportLevel.Warning,
                $"A TOC copy on {on} is damaged",
                "Re-save the TOC (e.g. by renaming the media) to restore both copies; retire the cartridge if this recurs"),
            TocAnomalyAdvice.TocFoundBySearch => (
                ServiceReportLevel.Warning,
                $"The table of contents of {on} was found only by searching",
                "Re-save the TOC (e.g. by renaming the media) so it can be found directly again"),
            _ => (ServiceReportLevel.Info, string.Empty, string.Empty),
        };
    }

    /// <summary>
    /// Reports the TOC-level summary and its recommendation. Silent when nothing was observed — the happy path must stay
    ///  provably quiet. Mirrors <see cref="ReportSetAnomalyOutcome"/>.
    /// </summary>
    protected TocAnomalyAdvice ReportTocAnomalyOutcome(in TapeTocStatistics toc,
        IReadOnlyList<TapeTocAnomaly> anomalies, TapeTocPlacement placement)
    {
        var (advice, volumes) = AdviseOnTocAnomalies(anomalies, placement);
        if (advice == TocAnomalyAdvice.None)
            return advice;

        var parts = new List<string>(3);
        if (toc.AnomaliesDetected > 0)
            parts.Add($"{toc.AnomaliesDetected:N0} TOC anomaly(ies) detected");
        if (toc.AnomaliesRecovered > 0)
            parts.Add($"{toc.AnomaliesRecovered:N0} recovered");
        if (toc.CopyWriteFailures > 0)
            parts.Add($"{toc.CopiesWritten:N0} TOC copy(ies) written, {toc.CopyWriteFailures:N0} not");

        var (level, headline, action) = AdviseTocText(advice, volumes);

        ReportAdvice(level, headline, parts.Count > 0 ? string.Join(", ", parts) : null, action);

        return advice;
    }

    /// <summary>
    /// Reports what <paramref name="agent"/> recorded — the convenience every verb uses, the stand-alone TOC verbs
    ///  included: their agent records anomalies without any notifiable.
    /// </summary>
    protected TocAnomalyAdvice ReportTocAnomalyOutcome(TapeAgentBase agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return ReportTocAnomalyOutcome(agent.Statistics.TocStats, agent.TocAnomalies, agent.TOCPlacement);
    }

    #endregion
}
