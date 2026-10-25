using Microsoft.Extensions.Logging;
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Media;
using TapeLibNET.Scan;
using TapeLibNET.Toc;
using Windows.Win32.Foundation;

namespace TapeLibNET.Agents;

/// <summary>
/// The TOC location ladder (Design-TocLocation §4, Phase 4): S0 native, S1 sibling, S3 expected position, S2 anchor walk.
///  S4 — the full-media walk, asked first — follows in Phase 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Look before you trust (TL-1).</b> Every candidate is IDENTIFIED first (<see cref="TapeHeaderBlock.ReadAndIdentify"/>,
///  16 KiB, the one dispatcher), checked for identity and plausibility (TL-5), ranked, and only then read in full
///  (<see cref="RestoreTOCAt"/>, CRC-verified). Marks, anchors and recorded block numbers only say where to LOOK (TL-2).
/// </para>
/// <para>
/// <b>Partition-explicit (TL-11).</b> Every absolute move names its partition (<see cref="TapeDrive.MoveToBlock(MediaPartition, long)"/>).
///  On partitioned media the TOC lives at block 0 of the initiator partition: S3 (a content-partition position) does not
///  apply to the TOC there, and S2 walks the whole — small — initiator partition.
/// </para>
/// <para>
/// <b>Bounded, unasked (TL-4).</b> S3 is one locate; S2 starts at an anchor near the end of content and stops after a few
///  copies. Nothing here writes.
/// </para>
/// </remarks>
public partial class TapeAgentBase
{
    #region *** Constants and types ***

    /// <summary>Filemarks the anchor chain steps back from end-of-data at most — the TOC area and some debris after it.</summary>
    private const int c_anchorFilemarksBack = 5;

    /// <summary>The walk stops after this many TOC copies: two of the current save, plus older ones to rank against.</summary>
    private const int c_walkTocCopies = 4;

    /// <summary>A TOC copy identified at a position, by the stage that found it.</summary>
    private readonly record struct TocCandidate(MediaPartition Partition, long Block, TapeTocPeek Peek,
        TapeTocAnomalyStage Stage);

    #endregion

    #region *** The ladder ***

    /// <summary>
    /// <see cref="RestoreTOC"/>, knowing which volume the cartridge should be and — on a multi-volume resume — the series
    ///  TOC that describes it. Reads the TOC where the layout keeps it (S0, S1); failing that, searches (S3, S2).
    /// </summary>
    /// <param name="expectedVolume">The volume requested; <see langword="null"/> when nothing is expected.</param>
    /// <param name="tocNotify">Optional callback for TOC anomalies.</param>
    /// <param name="seriesToc">
    /// A TOC describing this volume (the series TOC of a resume): enables S3 and the series anchor, and its
    ///  <see cref="TapeTOC.MediaId"/> filters foreign copies. <see langword="null"/>: no expectation beyond the volume.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Anomalies</b>: a 1st copy that did not read while the 2nd did → <see cref="TapeTocAnomalyKind.CopyUnreadable"/>,
    ///  recovered by <see cref="TapeTocAnomalyStage.Sibling"/> (as before). The layout's own position yielding no readable
    ///  copy → <see cref="TapeTocAnomalyKind.NotFoundNatively"/> (position unreachable) or
    ///  <see cref="TapeTocAnomalyKind.NoReadableCopy"/> (reached, nothing read) — recovered by
    ///  <see cref="TapeTocAnomalyStage.ExpectedPosition"/> or <see cref="TapeTocAnomalyStage.AnchorWalk"/> when the search
    ///  finds one. Copies of another series / volume, or implausibly placed, → <see cref="TapeTocAnomalyKind.ForeignCopySkipped"/>.
    /// </para>
    /// <para>Resets the latched error on entry, like every TOC verb; never clears the anomaly records.</para>
    /// </remarks>
    internal TapeResult RestoreTOCFromMedia(int? expectedVolume, ITapeTocNotifiable? tocNotify, TapeTOC? seriesToc = null)
    {
        ResetLatchedFailure();
#if TEST_HOOKS
        _tocCopyCounter = 0;
#endif
        // ── S0 / S1: where the layout keeps it ────────────────────────────────────────────────────
        bool positioned = ReadNativeCopies(out bool restored, out TapeResult copy1Failure, out long nativeBlock);

        if (IsAbortRequested && WentOK)
        {
            m_logger.LogWarning("TOC restore aborted by user request");
            SetError(WIN32_ERROR.ERROR_CANCELLED, "TOC loading aborted by user");
        }

        if (restored)
        {
            // The TOC read — it names its volume now. Settle what this operation could not read there before, then
            //  report the copy that did not read.
            int volume = TOC.Volume;
            ResolveTocAnomalies(tocNotify, volume, TapeTocAnomalyStage.Retried,
                TapeTocAnomalyKind.NotFoundNatively, TapeTocAnomalyKind.NoReadableCopy);
            if (!copy1Failure.Success && !IsAbortRequested)
            {
                TapeTocAnomaly unreadable = BuildTocAnomaly(TapeTocAnomalyKind.CopyUnreadable, volume, 1, copy1Failure)
                    with { Block = nativeBlock };
                NotifyTocAnomaly(tocNotify, unreadable);
                NotifyTocAnomalyRecovered(tocNotify,
                    unreadable with { Stage = TapeTocAnomalyStage.Sibling, Diagnosis = TapeResult.OK });
            }
            return TapeResult.OK;
        }

        if (IsAbortRequested)
            return FailedOperationResult;

        TapeResult nativeFailure = FailedOperationResult;     // captured before the search resets anything
        TapeTocAnomalyKind kind = positioned ? TapeTocAnomalyKind.NoReadableCopy : TapeTocAnomalyKind.NotFoundNatively;
        m_logger.LogWarning("TOC not readable where this layout keeps it ({Error}) — searching", nativeFailure.ErrorMessage);

        // ── S3, S2: search ────────────────────────────────────────────────────────────────────────
        var skipped = new List<TocCandidate>();
        TocCandidate? winner = LocateTocByLadder(expectedVolume, seriesToc, skipped, out string? searchNote);

        // Whatever the search did with the head: no believed position survives it.
        Manager.EndReadWrite();
        Navigator.ResetContentSet();

        if (winner is { } found)
        {
            int volume = TOC.Volume;
            ResolveTocAnomalies(tocNotify, volume, TapeTocAnomalyStage.Retried,
                TapeTocAnomalyKind.NotFoundNatively, TapeTocAnomalyKind.NoReadableCopy);
            TapeTocAnomaly detected = BuildTocAnomaly(kind, volume, 0, nativeFailure)
                with { Block = nativeBlock, CanAttemptRecovery = true };
            NotifyTocAnomaly(tocNotify, detected);
            NotifyTocAnomalyRecovered(tocNotify, detected with
            {
                Stage = found.Stage,
                CopyIndex = found.Peek.CopyIndex ?? (byte)0,
                Partition = found.Partition,
                Block = found.Block,
                CanAttemptRecovery = false,
                Diagnosis = TapeResult.OK,
            });
            ReportSkippedCopies(tocNotify, skipped, volume);
            m_logger.LogWarning("TOC recovered via {Stage} from block {Block} of the {Partition} partition",
                found.Stage, found.Block, found.Partition);
            ResetError();
            ResetLatchedFailure();
            return TapeResult.OK;
        }

        // ── Nothing readable ──────────────────────────────────────────────────────────────────────
        int unknownVolume = expectedVolume ?? 0;
        TapeResult failure = searchNote is null
            ? nativeFailure
            : TapeResult.Fail(nativeFailure.ErrorCode, $"{nativeFailure.ErrorMessage} ({searchNote})");
        NotifyTocAnomaly(tocNotify, BuildTocAnomaly(kind, unknownVolume, 0, failure)
            with { Block = nativeBlock, CanAttemptRecovery = false });     // Phase 5: S4 remains, asked first
        ReportSkippedCopies(tocNotify, skipped, unknownVolume);

        // The search's verdict supersedes the native one — it carries the native message PLUS what the search found.
        ResetLatchedFailure();
        SetError(failure.ErrorCode, failure.ErrorMessage);
        LatchFailure();
        return FailedOperationResult;
    }

    /// <summary>
    /// S0 and S1: the layout's own TOC position, then the other copy — 1st copy, 2nd copy (sequential), 2nd copy (direct).
    /// </summary>
    /// <param name="restored">A copy read; <see cref="TapeAgentBase.TOC"/> holds it.</param>
    /// <param name="copy1Failure">Why the 1st copy did not read; OK when it did.</param>
    /// <param name="tocBlock">Where the layout put the head for the 1st copy; −1 when it could not.</param>
    /// <returns>Whether the TOC position was reached at all.</returns>
    private bool ReadNativeCopies(out bool restored, out TapeResult copy1Failure, out long tocBlock)
    {
        restored = false;
        copy1Failure = TapeResult.OK;
        tocBlock = -1;

        m_logger.LogTrace("Restoring TOC from 1st copy");
        if (!BeginReadTOC())
        {
            m_logger.LogWarning("Failed to begin TOC read in {Method}", nameof(ReadNativeCopies));
            return false;
        }
        tocBlock = Drive.CurrentBlock;

        bool result = RestoreTOCCore();
        if (result)
            m_logger.LogTrace("TOC restored from 1st copy");
        else
        {
            copy1Failure = TapeResult.Fail(this);   // captured before the retries reset it
            m_logger.LogWarning("TOC restore from 1st copy failed. Trying 2nd copy");

            // Notice we now must be at the beginning of the 2nd copy, as Manager calls Navigator.MoveToNextTOCFilemark()
            //  from Manager.EndReadFile() when disposing the 1st read TOC stream
            ResetError();
            ResetLatchedFailure(); // if we succeed on the 2nd copy, we treat it as the overall success
            result = RestoreTOCCore();
            if (result)
                m_logger.LogTrace("TOC restored from 2nd copy");
            else
                m_logger.LogWarning("TOC restore from 2nd copy failed");

            if (!result && !IsAbortRequested)
            {
                // Last try: BeginReadTOC() again, then immediately move to the filemark for the 2nd copy
                m_logger.LogTrace("Attempting to skip to 2nd TOC copy directly");
                result = BeginReadTOC() && !IsAbortRequested
                    && Navigator.MoveToNextTOCFilemark() && !IsAbortRequested
                    && RestoreTOCCore();
                if (result)
                    m_logger.LogTrace("TOC restored directly from 2nd copy");
                else
                    m_logger.LogWarning("TOC restore directly from 2nd copy failed");
            }
        }

        restored = result;
        return true;
    }

    /// <summary>S3, then S2. Returns the copy that read (now in <see cref="TapeAgentBase.TOC"/>), or null.</summary>
    /// <param name="skipped">Collects the copies rejected by identity or plausibility.</param>
    /// <param name="note">A finding worth adding to the failure diagnosis (e.g. a blank TOC partition).</param>
    private TocCandidate? LocateTocByLadder(int? expectedVolume, TapeTOC? seriesToc, List<TocCandidate> skipped,
        out string? note)
    {
        note = null;
        var tried = new HashSet<(MediaPartition, long)>();

        // ── S3: the expected position, from a TOC describing this volume (TOC-in-set only) ──
        if (Navigator is TapeNavigatorTOCInSet && seriesToc is not null && expectedVolume is int volume)
        {
            long end = seriesToc.FirstBlockAfterContentOnVolume(volume);
            if (end >= 0)
            {
                m_logger.LogTrace("TOC search S3: content of volume #{Volume} ends at block {Block}", volume, end);
                List<TocCandidate> atEnd = PeekCopiesFrom(end,
                    skipTocMark: Navigator is TapeNavigatorTOCInSetWithFmksAndTOCMark);
                if (TryCandidates(atEnd, expectedVolume, seriesToc, skipped, tried) is { } found)
                    return found;
            }
        }

        // ── S2: walk from an anchor ──
#warning Phase 5: Optimize S2 to avoid re-reading S3 blocks and SM probing on every fragment (s. comment below)
        /*
S2 on setmark layouts: two avoidable moves (optional)
- S2 re-reads S3's block. S3 has just identified block 258 as "not a TOC", and the S2 anchor is the same block, so the walk reads it again. Pass S3's checked block to the walk and start one file later when the anchor equals it.
- A setmark probe on every fragment. For each fragment, the scanner's nearest-mark logic also tries a forward setmark space, which runs to end-of-data and fails, followed by a locate back. Past the last setmark there can be no further setmark by definition, since the TOC area holds only filemarks. When WalkFrom starts at the last-setmark anchor, the TOC-only options can disable setmark probing. Here the end of data is close, so it's cheap, but it costs two extra moves per fragment.

Neither affects correctness, so fold them into Phase 5's walker work.
         */
        foreach ((MediaPartition partition, long block) in Anchors(expectedVolume, seriesToc))
        {
            if (IsAbortRequested)
                return null;
            m_logger.LogTrace("TOC search S2: walking from block {Block} of the {Partition} partition", block, partition);
            List<TocCandidate> walked = WalkForTocCopies(partition, block, out bool blank);
            if (blank && partition == MediaPartition.Initiator)
                note = "the TOC partition is blank";
            if (TryCandidates(walked, expectedVolume, seriesToc, skipped, tried) is { } found)
                return found;
        }
        return null;
    }

    #endregion

    #region *** Candidates ***

    /// <summary>
    /// S3: the copies standing at <paramref name="block"/> of the content partition — the copy there, and the one after
    ///  it. On the TOC-mark layout, content end is the gap file: the copies follow the mark (gap ‖ FM ×3).
    /// </summary>
    private List<TocCandidate> PeekCopiesFrom(long block, bool skipTocMark)
    {
        var found = new List<TocCandidate>();
        ResetError();
        if (!Drive.MoveToBlock(MediaPartition.Content, block)
            || (skipTocMark && !Drive.MoveToNextFilemark(3)))
        {
            ResetError();
            return found;
        }

        for (int copy = 0; copy < 2; copy++)
        {
            long at = Drive.CurrentBlock;
            IdentifiedBlock id = TapeHeaderBlock.ReadAndIdentify(Drive, out _, out _, out _, out bool eod);
            if (eod || !id.TryGetToc(out TapeTocPeek peek))
                break;
            found.Add(new TocCandidate(MediaPartition.Content, at, peek, TapeTocAnomalyStage.ExpectedPosition));
            if (!Drive.MoveToBlock(MediaPartition.Content, at) || !Drive.MoveToNextFilemark(1))
                break;
        }
        ResetError();
        return found;
    }

    /// <summary>
    /// S2: walks forward from <paramref name="block"/> of <paramref name="partition"/> with the scanner — the one walker —
    ///  identifying every block after every mark, and returns the TOC copies it met.
    /// </summary>
    /// <param name="blank">The walk found nothing at all.</param>
    private List<TocCandidate> WalkForTocCopies(MediaPartition partition, long block, out bool blank)
    {
        var scanner = new TapeScanner(Drive)
        {
            Options = new ScanMediaOptions { HarvestTocCopies = false, StopAfterTocCopies = c_walkTocCopies },
        };
        IReadOnlyList<TapeMediaFragment>? fragments = scanner.WalkFrom(partition, block);
        ResetError();       // the walk's diagnosis is not this operation's error
        blank = fragments is { Count: 0 };

        var found = new List<TocCandidate>();
        foreach (TapeMediaFragment f in fragments ?? [])
            if (f.Kind == FragmentKind.TOC && f.TocPeek is { } peek)
                found.Add(new TocCandidate(f.Partition, f.StartBlock, peek, TapeTocAnomalyStage.AnchorWalk));
        return found;
    }

    /// <summary>
    /// Filters (identity, plausibility — TL-5), ranks (newest save, then copy 1, then position), and reads candidates until
    ///  one parses. Returns the winner; the rejected ones join <paramref name="skipped"/>.
    /// </summary>
    private TocCandidate? TryCandidates(List<TocCandidate> found, int? expectedVolume, TapeTOC? seriesToc,
        List<TocCandidate> skipped, HashSet<(MediaPartition, long)> tried)
    {
        var plausible = new List<TocCandidate>();
        foreach (TocCandidate c in found)
        {
            if (!tried.Add((c.Partition, c.Block)))
                continue;               // already read (or rejected) by an earlier stage
            if (RejectionOf(c, expectedVolume, seriesToc) is { } why)
            {
                m_logger.LogWarning("TOC copy at block {Block} of the {Partition} partition skipped: {Why}",
                    c.Block, c.Partition, why);
                skipped.Add(c);
            }
            else
                plausible.Add(c);
        }

        foreach (TocCandidate c in plausible
                     .OrderByDescending(c => c.Peek.LastSaveTime ?? DateTime.MinValue)
                     .ThenBy(c => c.Peek.CopyIndex ?? byte.MaxValue)
                     .ThenBy(c => c.Block))
        {
            if (IsAbortRequested)
                return null;
            if (RestoreTOCAt(c.Block, c.Partition))
                return c;
            m_logger.LogWarning("TOC copy at block {Block} of the {Partition} partition did not read", c.Block, c.Partition);
        }
        return null;
    }

    /// <summary>Why a candidate cannot be this volume's TOC; null when it can (TL-5).</summary>
    /// <remarks>
    /// Identity is filtered only against what is EXPECTED: the series' MediaId (a resume), the requested volume. A plain
    ///  load expects nothing — the in-memory TOC may belong to the previous cartridge. Plausibility needs no expectation:
    ///  content cannot end after a copy that stands in the same partition.
    /// </remarks>
    private static string? RejectionOf(in TocCandidate c, int? expectedVolume, TapeTOC? seriesToc)
    {
        if (seriesToc is { MediaId: var series } && series != Guid.Empty
            && c.Peek.MediaId != Guid.Empty && c.Peek.MediaId != series)
            return $"another series ({c.Peek.MediaId:N})";
        if (expectedVolume is int expected && c.Peek.Volume is int volume && volume != expected)
            return $"volume #{volume}, not #{expected}";
        if (c.Partition == MediaPartition.Content && c.Peek.FirstBlockAfterContent is long end && end > c.Block)
            return $"says content ends at block {end}, after the copy itself";
        return null;
    }

    /// <summary>Records the skipped copies — in the trail; the callback was raised for the volume already.</summary>
    private void ReportSkippedCopies(ITapeTocNotifiable? notify, List<TocCandidate> skipped, int volume)
    {
        foreach (TocCandidate c in skipped)
            NotifyTocAnomaly(notify, BuildTocAnomaly(TapeTocAnomalyKind.ForeignCopySkipped, volume,
                    c.Peek.CopyIndex ?? (byte)0,
                    TapeResult.Fail((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                        $"TOC copy at block {c.Block} skipped: volume #{c.Peek.Volume?.ToString() ?? "?"}, " +
                        $"media {c.Peek.MediaId:N}"))
                with { Partition = c.Partition, Block = c.Block });
    }

    #endregion

    #region *** Anchors (S2, Design-TocLocation §4.2) ***

    /// <summary>
    /// Where the S2 walk may start, cheapest and most specific first. Computed lazily — each anchor costs moves — and only
    ///  hints (TL-2): the walk identifies everything it reads.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///  <item><b>Partitions:</b> block 0 of the initiator partition — the whole TOC partition, small.</item>
    ///  <item><b>Series:</b> the start of this volume's last set (the previous set's <c>FirstBlockAfterSet</c>).</item>
    ///  <item><b>Setmarks:</b> just after the last setmark — the TOC copies follow it.</item>
    ///  <item><b>Filemark layouts</b> (with or without TOC mark): up to five filemarks back from end-of-data, the deepest
    ///   reachable — covers the TOC area and debris after it. Not the TOC-mark search: S0 ran it and it failed.</item>
    /// </list>
    /// </remarks>
    private IEnumerable<(MediaPartition Partition, long Block)> Anchors(int? expectedVolume, TapeTOC? seriesToc)
    {
        if (Navigator is TapeNavigatorTOCInPartition)
        {
            yield return (MediaPartition.Initiator, 0L);
            yield break;
        }

        if (seriesToc is not null && expectedVolume is int volume)
        {
            long lastSetStart = SecondLastSetEnd(seriesToc, volume);
            if (lastSetStart >= 0)
                yield return (MediaPartition.Content, lastSetStart);
        }

        if (Navigator is TapeNavigatorTOCInSetWithSmks)
        {
            ResetError();
            if (Drive.FastforwardToEnd(MediaPartition.Content)
                && Drive.MoveToNextSetmark(-1) && Drive.MoveToNextSetmark(1))
                yield return (MediaPartition.Content, Drive.CurrentBlock);
            ResetError();
            yield break;
        }

        ResetError();
        if (!Drive.FastforwardToEnd(MediaPartition.Content))
        {
            ResetError();
            yield break;
        }
        long eod = Drive.CurrentBlock;
        for (int back = c_anchorFilemarksBack; back >= 1; back--)
        {
            ResetError();
            if (Drive.MoveToBlock(MediaPartition.Content, eod)
                && Drive.MoveToNextFilemark(-back) && Drive.MoveToNextFilemark(1))
            {
                long anchor = Drive.CurrentBlock;
                ResetError();
                yield return (MediaPartition.Content, anchor);
                yield break;
            }
        }
        ResetError();
    }

    /// <summary>The end of the second-last set on <paramref name="volume"/> — where its last set starts; −1 when unknown.</summary>
    private static long SecondLastSetEnd(TapeTOC toc, int volume)
    {
        long last = -1, secondLast = -1;
        foreach (TapeSetTOC set in toc)
        {
            if (set.Volume != volume || set.FirstBlockAfterSet < 0)
                continue;
            secondLast = last;
            last = set.FirstBlockAfterSet;
        }
        return secondLast;
    }

    #endregion
}
