using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Windows.Win32.Foundation;

using TapeLibNET.Drive;
using TapeLibNET.Calibration;
using TapeLibNET.Headers;
using TapeLibNET.Media;
using TapeLibNET.Agents;

namespace TapeLibNET.Scan;


/// <summary>
/// Reads a cartridge — EVERY partition it has — from the beginning forward and describes everything identifiable on it,
///  with no table of contents in hand and none assumed (SM-2). Writes nothing (SM-1).
/// </summary>
/// <remarks>
/// <para>
/// <b>A sibling of <see cref="TapeCalibrator"/>, deliberately NOT a <c>TapeAgentBase</c>.</b> The calibrator WRITES a
///  self-describing trail and reads it back to measure; the scanner READS whatever trail it finds and describes it. Both
///  drive only the public <see cref="TapeDrive"/> surface, both are backend-agnostic, and neither has files, sets, or a TOC.
/// </para>
/// <para>
/// Living outside the agent hierarchy is what makes the walk honest. <see cref="TapeNavigator"/> is TOC-bound by
///  construction, and the scanner needs RAW mark hops and absolute block numbers. An agent is borrowed only where it owns a
///  ceremony: block 0 of the content partition (<see cref="IdentifyBomFragment"/>) and TOC recovery
///  (<see cref="HarvestLastTocCopy"/>).
/// </para>
/// <para>
/// <b>Every partition, symmetrically (SM-14).</b> The content partition is walked first, then — on partitioned media —
///  the initiator partition, with the SAME identification. Nothing is presumed about what a partition holds: debris in
///  the TOC partition, a TOC copy in the content partition, a media header where a TOC belongs — each is reported as what
///  it is, where it is. Only the CLOSING MARK follows a convention (§3.2) — and where none applies, the nearest mark is
///  measured.
/// </para>
/// <para>
/// Conceptually create-use-discard: <c>new TapeScanner(drive).Scan()</c>. Cancellation is cooperative via
///  <see cref="IsAbortRequested"/>. This class does NOT judge what it finds — no verdicts (SM-3).
/// </para>
/// </remarks>
public sealed partial class TapeScanner : TapeDriveHolder<TapeScanner>
{
    #region *** Error latching and result building ***

    /// <summary>Builder for the current scan's result, latching on the FIRST failure.</summary>
    /// <remarks>
    /// The scan deliberately TOLERATES failures and resets the error — a failed TOC harvest, a failed small-block probe, an
    ///  unidentifiable fragment — so the live error state is a poor witness by the time the walk ends. Mirrors
    ///  <c>TapeCalibrator._resultBuilder</c> and <c>TapeAgentBase._resultBuilder</c>.
    /// </remarks>
    private readonly TapeResultBuilder _resultBuilder;

    private void LatchFailure() => _resultBuilder.LatchFailure();
    private void ResetLatchedFailure() => _resultBuilder.Reset();

    /// <summary>
    /// Diagnosis of the current (or most recent) scan: its first failure, or <see cref="TapeResult.OK"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Scan"/> returns a nullable map, so null is the only signal the caller gets — this property turns it into
    ///  an explanation. A TRUNCATED map is still returned, so null means "no map at all", not "an imperfect scan".
    /// </remarks>
    public TapeResult LastResult => _resultBuilder.Result;

    private TapeResult FailedScanResult => _resultBuilder.BuildFailure(
        IsAbortRequested ? (uint)WIN32_ERROR.ERROR_CANCELLED : (uint)WIN32_ERROR.ERROR_INVALID_STATE,
        IsAbortRequested ? "Scan aborted by user request" : "Scan did not complete");

    #endregion

    #region *** Options & cancellation ***

    /// <summary>Scan options; the defaults are safe on an unknown, possibly damaged cartridge.</summary>
    public ScanMediaOptions Options { get; init; } = ScanMediaOptions.Default;

    /// <summary>
    /// Set by the caller — or by a progress sink that threw (SM-10) — to request a graceful abort. Polled between reads,
    ///  so the walk always stops at a clean boundary.
    /// </summary>
    public bool IsAbortRequested { get; set; }

    /// <summary>
    /// Index of the first fragment of the partition being walked. Fragments before it belong to a partition already
    ///  walked: they are final, already reported, and never touched again — no mark run folds into them, and no
    ///  closing state is written onto them.
    /// </summary>
    private int m_partitionStart;

    #endregion

    #region *** Construction ***

    public TapeScanner(TapeDrive drive) : base(drive)
    {
        _resultBuilder = new(this);
    }

    #endregion

    #region *** Public API ***

    /// <summary>
    /// Walks every partition of the loaded cartridge to its end of data and returns what it found. READ-ONLY throughout.
    /// </summary>
    /// <param name="progress">
    /// Optional sink, fired once per fragment — when that fragment is FINAL, i.e. one read behind the head.
    /// </param>
    /// <returns>
    /// The map, or <see langword="null"/> when no scan was possible at all (no media) — see <see cref="LastResult"/>. A map
    ///  whose walk ended early is still RETURNED, flagged <see cref="MediaScanMap.Truncated"/> (SM-5).
    /// </returns>
    public MediaScanMap? Scan(IProgress<TapeScanProgress>? progress = null)
    {
        ResetError();
        ResetLatchedFailure();      // fresh verb ⇒ fresh diagnosis
        IsAbortRequested = false;

        // Derived from capabilities alone, BEFORE the tape moves — reportable even on a cartridge that then yields
        //  nothing at all, where knowing what SHOULD have been there is the diagnosis.
        TapeMediaLayout layout = TapeMediaLayout.Predict(Drive);

        if (!Drive.IsMediaLoaded)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            LatchFailure();
            LogErrorAsDebug("Scan: no media loaded");
            return null;
        }

        // The scan never writes, so the block size is the only drive state it disturbs (the TOC harvest sets it
        //  deliberately). Restore it whatever happens — SM-9.
        BlockSizeGuard guard = new(this);
        try
        {
            return ScanCore(layout, progress);
        }
        catch (Exception ex)
        {
            // A survey must not throw at its caller: latch, log, and report a null map with a real diagnosis.
            SetError(ex);
            LatchFailure();
            m_logger.LogError(ex, "{Prefix}: Scan: unexpected exception", LogPrefix);
            return null;
        }
        finally
        {
            guard.Restore();
        }
    }

    /// <summary>
    /// Walks ONE partition forward from <paramref name="block"/> to its end of data — the TOC search's S2 (Design-TocLocation
    ///  §4.2). Same identification, same mark handling as <see cref="Scan"/>; no BOM ceremony, no second partition.
    /// </summary>
    /// <returns>The fragments met; <see langword="null"/> when the start could not be reached (see <see cref="LastResult"/>).</returns>
    /// <remarks>Set <see cref="ScanMediaOptions.HarvestTocCopies"/> false to only peek. Read-only; restores the block size.</remarks>
    internal IReadOnlyList<TapeMediaFragment>? WalkFrom(MediaPartition partition, long block)
    {
        ResetError();
        ResetLatchedFailure();
        IsAbortRequested = false;
        if (!Drive.IsMediaLoaded)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            LatchFailure();
            return null;
        }

        TapeMediaLayout layout = TapeMediaLayout.Predict(Drive);
        BlockSizeGuard guard = new(this);
        try
        {
            if (!MoveTo(partition, block))
            {
                SyncErrorFrom(Drive);
                LatchFailure();
                return null;
            }
            List<TapeMediaFragment> fragments = [];
            bool ours = false;
            WalkPartition(partition, layout, fragments, progress: null, crossFirstClosingMark: false, ref ours);
            return fragments;
        }
        catch (Exception ex)
        {
            SetError(ex);
            LatchFailure();
            m_logger.LogError(ex, "{Prefix}: Walk: unexpected exception", LogPrefix);
            return null;
        }
        finally
        {
            guard.Restore();
        }
    }

    #endregion

    #region *** The walk ***

    /// <summary>
    /// The SM §3 scan: the content partition from block 0, then — on partitioned media — the initiator partition from
    ///  block 0, each walked to its end of data by <see cref="WalkPartition"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Content first, always.</b> Block 0 of the content partition decides whether the cartridge is a calibration
    ///  cartridge (SM-7) and reaches the scanner through the probe agent's BOM ceremony; keeping it first also keeps
    ///  <c>Fragments[0]</c> meaning "block 0 of the content partition" whenever that block holds anything.
    /// </para>
    /// <para>
    /// <b>A blank content partition does not end the scan.</b> A partitioned cartridge whose content is empty may still
    ///  carry a TOC in its initiator partition — a freshly formatted one does. The cartridge is
    ///  <see cref="ScannedMediaKind.Blank"/> only when EVERY partition is.
    /// </para>
    /// <para>
    /// <b>A content fault does not skip the initiator partition</b> — the TOC there is the most valuable thing to save
    ///  from a failing cartridge. An abort, or the fragment guard, does: both are "stop now".
    /// </para>
    /// </remarks>
    private MediaScanMap ScanCore(TapeMediaLayout layout, IProgress<TapeScanProgress>? progress)
    {
        List<TapeMediaFragment> fragments = [];
        List<ScannedPartition> partitions = [];
        bool ours = false;

        // ── Content partition, block 0 — via a throwaway agent ─────────────────────────────────────────
        m_partitionStart = 0;
        TapeMediaFragment first = IdentifyBomFragment(out int bomBytesRead);
        if (bomBytesRead <= 0)
        {
            // Nothing readable at block 0 at all. A clean finding — never a failure (SM-6).
            m_logger.LogInformation("{Prefix}: Scan: content partition is blank (no data at block 0)", LogPrefix);
            ResetError();
            partitions.Add(new ScannedPartition(MediaPartition.Content, FragmentCount: 0,
                Truncated: false, TerminatorWin32: (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED));
        }
        else
        {
            first = first with { Partition = MediaPartition.Content };
            fragments.Add(first);

            // A calibration cartridge is a COMPLETE, correct conclusion — identified, not walked (SM-7).
            if (first.Kind == FragmentKind.CalibrationHeader)
            {
                m_logger.LogInformation("{Prefix}: Scan: calibration cartridge — {Fragment}", LogPrefix, first);
                Report(progress, first);
                TapeCalibrationMediaInfo? calInfo = Options.InspectCalibrationTrail
                    ? InspectCalibrationTrail(progress)
                    : null;
                ResetError();   // the inspection is best-effort; a failed one is not the scan's error
                return BuildMap(layout, ScannedMediaKind.CalibrationCartridge, fragments,
                    [new ScannedPartition(MediaPartition.Content, 1, Truncated: false, (uint)WIN32_ERROR.NO_ERROR)],
                    calibrationInfo: calInfo);
            }

            ours |= IsOurs(first);

            // Block 0 is identified like every other block — a TOC copy there is harvested like every other copy.
            if (first.Kind == FragmentKind.TOC && Options.HarvestTocCopies && !HarvestLastTocCopy(fragments, progress))
            {
                LatchFailure();
                partitions.Add(new ScannedPartition(MediaPartition.Content, FragmentCount: 1, Truncated: true,
                    TerminatorWin32: (uint)LastErrorWin32));
            }
            else
                partitions.Add(WalkPartition(MediaPartition.Content, layout, fragments, progress,
                    crossFirstClosingMark: true, ref ours));
        }

        // ── Initiator partition ────────────────────────────────────────────────────────────────────────
        if (Drive.PartitionCount > 1)
        {
            if (IsAbortRequested || fragments.Count >= Options.MaxFragments)
            {
                // "Stop now" — do not move the tape again. The partition is reported, unscanned.
                m_logger.LogWarning("{Prefix}: Scan: initiator partition not scanned (walk stopped)", LogPrefix);
                partitions.Add(new ScannedPartition(MediaPartition.Initiator, FragmentCount: 0, Truncated: true,
                    TerminatorWin32: IsAbortRequested ? (uint)WIN32_ERROR.ERROR_CANCELLED : (uint)WIN32_ERROR.ERROR_INVALID_DATA));
            }
            else
            {
                partitions.Add(ScanInitiatorPartition(layout, fragments, progress, ref ours));
            }

            // Courtesy, not bookkeeping: leave the drive where every other verb expects it to start.
            ReturnToContentPartition();
        }

        ScannedMediaKind kind = fragments.Count == 0 ? ScannedMediaKind.Blank
            : ours ? ScannedMediaKind.Backup
            : ScannedMediaKind.Foreign;

        return BuildMap(layout, kind, fragments, partitions);
    }

    /// <summary>Positions at block 0 of the initiator partition and walks it.</summary>
    /// <remarks>
    /// No probe agent here: block 0 of the initiator partition carries no BOM ceremony — no media header is expected,
    ///  and none is skipped. It is read raw, like every block after the first, so whatever stands there (a TOC copy,
    ///  a header, debris) is identified by the same <see cref="ReadFragmentAt"/>.
    /// </remarks>
    private ScannedPartition ScanInitiatorPartition(TapeMediaLayout layout, List<TapeMediaFragment> fragments,
        IProgress<TapeScanProgress>? progress, ref bool ours)
    {
        ReportPhase(progress, TapeScanProgress.PhaseScanningInitiator, fragments.Count);

        if (!Drive.MoveToPartition(MediaPartition.Initiator, 0))
        {
            // The partition exists (PartitionCount says so) but cannot be reached: a transport fault, not a finding.
            SyncErrorFrom(Drive);
            LatchFailure();
            uint error = (uint)LastErrorWin32;
            m_logger.LogError("{Prefix}: Scan: cannot reach the initiator partition ({Err}) — map truncated",
                LogPrefix, LastErrorWin32);
            ResetError();
            return new ScannedPartition(MediaPartition.Initiator, FragmentCount: 0, Truncated: true, TerminatorWin32: error);
        }

        return WalkPartition(MediaPartition.Initiator, layout, fragments, progress, crossFirstClosingMark: false, ref ours);
    }

    /// <summary>
    /// Walks one partition from the head's position to its end of data: alternately cross whatever closes the current
    ///  fragment, and read what follows.
    /// </summary>
    /// <param name="crossFirstClosingMark">
    /// <see langword="true"/> when the partition's first fragment was already read (content block 0, via the probe agent)
    ///  and its closing mark lies ahead; <see langword="false"/> when the walk starts with a read.
    /// </param>
    /// <param name="ours">Set when any fragment of this partition is positively one of ours.</param>
    /// <remarks>
    /// <para>
    /// <b>No end-of-content bound, deliberately.</b> Walking to EOD needs no bound: trailing TOC copies are IDENTIFIED,
    ///  not avoided.
    /// </para>
    /// <para>
    /// <b>No retry, no resync-by-seeking.</b> Skipping a bad region by seeking blind is guessing at block numbers on
    ///  damaged tape. Stop, record where, and say the map is truncated.
    /// </para>
    /// <para>
    /// <b>No mark fishing</b> — except where it is the only honest answer (<see cref="CrossNearestMark"/>). Adjacent marks
    ///  are detected by the identification read itself: a read that returns nothing but a tapemark has already crossed it.
    ///  Such marks are gathered into a pending run and committed once the next data block or the end shows where the run
    ///  stops (<see cref="FoldMarkRun"/>).
    /// </para>
    /// <para>
    /// <b>A fragment is reported once it is FINAL</b> — when the next one of the same partition is committed, and the last
    ///  one when the partition's walk ends.
    /// </para>
    /// </remarks>
    private ScannedPartition WalkPartition(MediaPartition partition, TapeMediaLayout layout,
        List<TapeMediaFragment> fragments, IProgress<TapeScanProgress>? progress,
        bool crossFirstClosingMark, ref bool ours)
    {
        ScanMediaOptions options = Options;
        m_partitionStart = crossFirstClosingMark ? fragments.Count - 1 : fragments.Count;
        bool truncated = false;
        uint terminator = (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED;

        bool crossClosingMark = crossFirstClosingMark;
        int runMarks = 0;               // adjacent marks read since the last data fragment
        int tocCopies = 0;
        long runStart = -1L;            // block of the first of them

        do // while (true)
        {
            if (IsAbortRequested)
            {
                SetError(WIN32_ERROR.ERROR_CANCELLED);
                LatchFailure();
                m_logger.LogWarning("{Prefix}: Scan aborted by caller after {N} fragment(s)", LogPrefix, fragments.Count);
                truncated = true;
                terminator = (uint)WIN32_ERROR.ERROR_CANCELLED;
                break;
            }

            // The run counter is guarded too: a pathological medium can yield marks indefinitely without a fragment.
            if (fragments.Count >= options.MaxFragments || runMarks >= options.MaxFragments)
            {
                m_logger.LogWarning("{Prefix}: Scan stopped at the {Max}-fragment guard — the map is truncated",
                    LogPrefix, options.MaxFragments);
                truncated = true;
                terminator = (uint)WIN32_ERROR.ERROR_INVALID_DATA;
                break;
            }

            // ── Cross whatever closes the last data fragment ──
            if (crossClosingMark)
            {
                if (!CrossClosingMark(fragments[^1], layout))
                {
                    terminator = (uint)LastErrorWin32;
                    // A positional error means "the partition ends here" — a FINDING. Anything else means the drive went
                    //  away, and a map built on a dead drive's silence is fiction (SM-5).
                    if (!IsPositionalEnd(LastErrorWin32))
                    {
                        LatchFailure();
                        m_logger.LogError("{Prefix}: Scan: transport fault in the {Partition} partition after {N} " +
                            "fragment(s) ({Err}) — map truncated", LogPrefix, partition, fragments.Count, LastErrorWin32);
                        truncated = true;
                    }
                    else
                    {
                        m_logger.LogTrace("{Prefix}: Scan: {Fragment} is not closed — the partition ends inside it",
                            LogPrefix, fragments[^1]);
                        ResetError();   // reaching the end of the partition is not this operation's error
                    }
                    CloseLastFragment(fragments, closedBySeparator: false, nextStart: -1L);
                    break;
                }
                CloseLastFragment(fragments, closedBySeparator: true, nextStart: Drive.CurrentBlock);
                crossClosingMark = false;
            }

            // ── Read what follows ──
            long at = Drive.CurrentBlock;
            ReadOutcome outcome = ReadFragmentAt(fragments.Count, at, out TapeMediaFragment? next);

            if (outcome == ReadOutcome.Tapemark)
            {
                // The read has already crossed the mark — nothing to hop. Note it and read on.
                if (runMarks++ == 0)
                    runStart = at;
                continue;
            }

            if (outcome == ReadOutcome.EndOfData)
            {
                // End-of-data after a mark (or at the partition's start): the NORMAL end of a sound partition.
                m_logger.LogTrace("{Prefix}: Scan: end of data in the {Partition} partition after {N} fragment(s)",
                    LogPrefix, partition, fragments.Count);
                ResetError();
                break;
            }

            Debug.Assert(outcome == ReadOutcome.Fragment && next is not null,
                $"ReadFragmentAt returned {outcome} with {(next is null ? "no" : "a")} fragment");
            TapeMediaFragment fragment = next! with { Partition = partition };

            // A data fragment: the run before it (if any) now has a known end.
            FoldMarkRun(fragments, layout, partition, ref runMarks, runStart, endBlock: at, progress);
            Commit(fragments, fragment, progress);

            // TOC recovery is a step of the WALK, not of identification: it moves the head over many blocks and
            //  returns it. A head that cannot be returned ends the walk truncated (SM-5).
            if (fragment.Kind == FragmentKind.TOC && options.HarvestTocCopies
                && !HarvestLastTocCopy(fragments, progress))
            {
                LatchFailure();
                truncated = true;
                terminator = (uint)LastErrorWin32;
                break;
            }

            ours |= IsOurs(fragment);

            if (fragment.Kind == FragmentKind.TOC && options.StopAfterTocCopies > 0
                && ++tocCopies >= options.StopAfterTocCopies)
                break;                              // enough copies for the TOC search (§4.4)

            crossClosingMark = true;

        } while (true);

        // Whatever ended the walk, a run still pending ends here.
        FoldMarkRun(fragments, layout, partition, ref runMarks, runStart, endBlock: Drive.CurrentBlock, progress);

        // The partition's last fragment is final only now.
        if (fragments.Count > m_partitionStart)
            Report(progress, fragments[^1]);

        int count = fragments.Count - m_partitionStart;
        m_logger.LogTrace("{Prefix}: Scan: {Partition} partition — {N} fragment(s){T}",
            LogPrefix, partition, count, truncated ? ", TRUNCATED" : "");
        return new ScannedPartition(partition, count, truncated, terminator);
    }

    #endregion

    #region *** Closing marks ***

    /// <summary>Which mark closes a fragment.</summary>
    private enum ClosingMark
    {
        /// <summary>A filemark, by convention.</summary>
        Filemark,
        /// <summary>The layout's set separator, by convention.</summary>
        Setmark,
        /// <summary>Whichever mark comes first — for fragments no convention can speak for.</summary>
        Nearest,
    }

    /// <summary>Which mark closes a fragment — by convention where one applies, the nearest mark where none does.</summary>
    /// <remarks>
    /// <para>
    /// <b>Filemark-only layouts</b> have one mark type: nothing to decide.
    /// </para>
    /// <para>
    /// <b>The kind decides first.</b> A media header (<see cref="TapeHeaderBlock.WritesTrailingMark"/>) and a TOC copy are
    ///  closed by a filemark on every layout and in every partition. Hopping a setmark from either overshoots — past the
    ///  whole first set, or past the second TOC copy.
    /// </para>
    /// <para>
    /// <b>No convention for what the scan could not identify</b> — nor for anything standing in the initiator partition
    ///  that is not a TOC copy (debris, or content from a mixed-up cartridge). Such a fragment may be a damaged TOC copy
    ///  closed by a filemark, or a damaged set header closed by a setmark; guessing either way loses the fragments behind
    ///  it. It is closed by the NEAREST mark, measured (<see cref="CrossNearestMark"/>).
    /// </para>
    /// <para>
    /// Set headers, header-less sets and calibration headers in the content partition close with the layout's separator.
    ///  Mark runs and TOC marks never reach here: they consist of marks already crossed.
    /// </para>
    /// </remarks>
    private static ClosingMark ClosingMarkOf(TapeMediaFragment fragment, TapeMediaLayout layout)
    {
        if (!layout.UseSmks)
            return ClosingMark.Filemark;
        return fragment.Kind switch
        {
            FragmentKind.MediaHeader or FragmentKind.TOC => ClosingMark.Filemark,
            FragmentKind.Unknown => ClosingMark.Nearest,
            _ when fragment.Partition == MediaPartition.Initiator => ClosingMark.Nearest,
            _ => ClosingMark.Setmark,
        };
    }

    /// <summary>
    /// Crosses the mark that closes <paramref name="current"/>.
    /// </summary>
    /// <returns>
    /// False when no closing mark was found. The scanner's error then carries the DRIVE's reason, so the caller can tell
    ///  a positional end from a transport fault.
    /// </returns>
    private bool CrossClosingMark(TapeMediaFragment current, TapeMediaLayout layout)
    {
        bool hopped = ClosingMarkOf(current, layout) switch
        {
            ClosingMark.Filemark => Drive.MoveToNextFilemark(1),
            ClosingMark.Setmark => Drive.MoveToNextSetmark(1),
            _ => CrossNearestMark(current.Partition),
        };

        if (!hopped)
        {
            // The DRIVE carries the reason; the scanner's own error channel sees nothing unless synced.
            SyncErrorFrom(Drive);
            return false;
        }
        ResetError();
        return true;
    }

    /// <summary>
    /// Crosses whichever mark — filemark or setmark — comes first from the head's position, by measuring both.
    /// </summary>
    /// <returns>
    /// True with the head just past the nearer mark. False when neither exists ahead (positional) or on a transport
    ///  fault; the DRIVE carries which.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Measured, not inferred.</b> Win32 stops a filemark space at a setmark with <c>ERROR_SETMARK_DETECTED</c> when
    ///  setmark reporting is on — but where it leaves the head then is not something this code relies on, and a drive
    ///  without the report spaces past the setmark silently. So both hops start from the same block, and the block numbers
    ///  decide: the head returns to the start between them, and to the filemark when that one is nearer.
    /// </para>
    /// <para>
    /// <b>Cost:</b> up to two locates and a second space — paid only for fragments no convention covers, which a healthy
    ///  cartridge does not have.
    /// </para>
    /// </remarks>
    private bool CrossNearestMark(MediaPartition partition)
    {
        long start = Drive.CurrentBlock;

        bool viaFilemark = Drive.MoveToNextFilemark(1);
        long filemarkAt = viaFilemark ? Drive.CurrentBlock : long.MaxValue;
        if (!viaFilemark && !IsPositionalEnd(Drive.LastErrorWin32))
            return false;                                   // transport fault — the drive carries it

        if (!MoveTo(partition, start))
            return false;
        bool viaSetmark = Drive.MoveToNextSetmark(1);
        long setmarkAt = viaSetmark ? Drive.CurrentBlock : long.MaxValue;
        if (!viaSetmark && !IsPositionalEnd(Drive.LastErrorWin32))
            return false;

        if (!viaFilemark && !viaSetmark)
            return false;                                   // no mark of either type ahead: the partition ends here

        m_logger.LogTrace("{Prefix}: Scan: nearest mark from block {Start}: filemark {Fm}, setmark {Sm}",
            LogPrefix, start, viaFilemark ? filemarkAt : -1L, viaSetmark ? setmarkAt : -1L);

        if (setmarkAt < filemarkAt)
            return true;                                    // already standing just past it
        return MoveTo(partition, filemarkAt);
    }

    /// <summary>
    /// Whether <paramref name="error"/> says "there is no more tape that way" — the partition ENDED, rather than the drive
    ///  or the cartridge having failed.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>TapeAgentBase.IsPositionalNavigationError</c>: treating a dead drive as a tape end would present a map as
    ///  complete when the walk was cut short.
    /// </remarks>
    private static bool IsPositionalEnd(WIN32_ERROR error) => error is
        WIN32_ERROR.ERROR_NO_DATA_DETECTED          // ran past EOD hunting a mark that is not there
        or WIN32_ERROR.ERROR_END_OF_MEDIA           // the same, physically
        or WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA
        or WIN32_ERROR.ERROR_FILEMARK_DETECTED
        or WIN32_ERROR.ERROR_SETMARK_DETECTED
        or WIN32_ERROR.ERROR_NOT_SUPPORTED;         // e.g. setmark spacing on a drive that lost the cap

    /// <summary>Whether a fragment is positively one of ours — what makes a cartridge a backup cartridge.</summary>
    private static bool IsOurs(TapeMediaFragment fragment) => fragment.Kind is
        FragmentKind.MediaHeader or FragmentKind.SetHeader or FragmentKind.SetContent or FragmentKind.TOC;

    #endregion

    #region *** Positioning ***

    /// <summary>
    /// Moves to <paramref name="block"/> of <paramref name="partition"/> — a combined locate on partitioned media, a plain
    ///  block locate otherwise. The scanner's only way back to a position it left (SM-12).
    /// </summary>
    private bool MoveTo(MediaPartition partition, long block) => Drive.MoveToBlock(partition, block);

    /// <summary>
    /// Best-effort: returns the drive to block 0 of the content partition after a scan of a partitioned cartridge, where
    ///  every other verb expects to begin. A failure is logged, never the scan's failure — the map is already complete.
    /// </summary>
    private void ReturnToContentPartition()
    {
        if (!Drive.MoveToPartition(MediaPartition.Content, 0))
        {
            SyncErrorFrom(Drive);
            m_logger.LogWarning("{Prefix}: Scan: could not return to the content partition ({Err})",
                LogPrefix, LastErrorWin32);
        }
        ResetError();
    }

    #endregion

    #region *** Fragment assembly ***

    /// <summary>
    /// Commits a pending run of adjacent marks, now that its end is known — as a <see cref="FragmentKind.TocMark"/> folded
    ///  into the gap before it, or as a <see cref="FragmentKind.MarkRun"/> of its own. No-op when no run is pending.
    /// </summary>
    /// <param name="partition">The partition being walked — stamped on a new run, and the fold's boundary.</param>
    /// <param name="endBlock">Where the run stops: the next data block, or where the walk ended.</param>
    /// <remarks>
    /// <para>
    /// <b>The TOC-mark fold.</b> On a <see cref="TapeMediaLayout.HasTocMark"/> layout the navigator writes
    ///  <c>[gap][FM][FM][FM]</c> before the first TOC copy. The walk sees the gap as an <see cref="FragmentKind.Unknown"/>
    ///  block closed by the first filemark, then reads the other two as a run.
    /// </para>
    /// <para>
    /// <b>Guarded four ways.</b> The layout must write TOC marks; the run must hold at least two marks (a legacy
    ///  double-filemark end of data holds one); the block before it must have been READ cleanly (a damaged header is also
    ///  Unknown, but no gap); and that block must belong to THIS partition — a run at the start of the initiator partition
    ///  never folds into the content partition's last fragment.
    /// </para>
    /// </remarks>
    private void FoldMarkRun(List<TapeMediaFragment> fragments, TapeMediaLayout layout, MediaPartition partition,
                             ref int runMarks, long runStart, long endBlock,
                             IProgress<TapeScanProgress>? progress)
    {
        if (runMarks == 0)
            return;

        TapeMediaFragment? last = fragments.Count > m_partitionStart ? fragments[^1] : null;
        if (layout.HasTocMark
            && runMarks >= 2
            && last is { Kind: FragmentKind.Unknown, ClosedBySeparator: true, Diagnosis.Success: true })
        {
            m_logger.LogTrace("{Prefix}: Scan: TOC mark at block {Block} (gap + {N} adjacent mark(s))",
                LogPrefix, last.StartBlock, runMarks);
            // Still unreported (fragments are reported when final), so the retype is invisible to the sink.
            fragments[^1] = last with
            {
                Kind = FragmentKind.TocMark,
                MarkCount = runMarks,
                BlockSpan = endBlock - last.StartBlock,
                Fingerprint = null,     // positively identified — the gap's bytes are no longer evidence
            };
        }
        else
        {
            Commit(fragments, new TapeMediaFragment
            {
                Ordinal = fragments.Count,
                StartBlock = runStart,
                Partition = partition,
                Kind = FragmentKind.MarkRun,
                MarkCount = runMarks,
                ClosedBySeparator = true,   // a run consists of marks; it cannot be left open
                BlockSpan = endBlock - runStart,
            }, progress);
        }
        runMarks = 0;
    }

    /// <summary>
    /// Appends <paramref name="fragment"/>, reporting its predecessor in the same partition — final from this moment on.
    /// </summary>
    /// <remarks>
    /// Assigns the ordinal itself: ordinals run contiguously across partitions, and the list position is the only
    ///  authority. A predecessor from a partition already walked was reported when that walk ended, and is not reported
    ///  twice.
    /// </remarks>
    private void Commit(List<TapeMediaFragment> fragments, TapeMediaFragment fragment,
                        IProgress<TapeScanProgress>? progress)
    {
        if (fragments.Count > m_partitionStart)
            Report(progress, fragments[^1]);
        fragments.Add(fragment with { Ordinal = fragments.Count });
    }

    /// <summary>
    /// Fills in what only the NEXT position can tell us about the last fragment: whether a separator closed it, and how
    ///  many blocks it spanned.
    /// </summary>
    /// <remarks>
    /// <see cref="TapeMediaFragment.BlockSpan"/> is an upper bound INCLUDING marks (SM-11). Called only while the last
    ///  fragment belongs to the partition being walked.
    /// </remarks>
    private static void CloseLastFragment(List<TapeMediaFragment> fragments, bool closedBySeparator, long nextStart)
    {
        if (fragments.Count == 0)
            return;
        TapeMediaFragment last = fragments[^1];
        fragments[^1] = last with
        {
            ClosedBySeparator = closedBySeparator,
            BlockSpan = nextStart > last.StartBlock ? nextStart - last.StartBlock : -1L,
        };
    }

    /// <summary>
    /// Builds the map. Truncated when any partition's walk was; the terminator is the first truncating partition's, else
    ///  the normal end of data.
    /// </summary>
    private MediaScanMap BuildMap(
        TapeMediaLayout layout, ScannedMediaKind kind, List<TapeMediaFragment> fragments,
        IReadOnlyList<ScannedPartition> partitions, TapeCalibrationMediaInfo? calibrationInfo = null)
    {
        ScannedPartition? cut = partitions.FirstOrDefault(p => p.Truncated);
        var map = new MediaScanMap
        {
            Layout = layout,
            Kind = kind,
            Fragments = fragments,
            Partitions = partitions,
            ScannedUtc = DateTime.UtcNow,
            Truncated = cut is not null,
            TerminatorWin32 = cut?.TerminatorWin32
                ?? (kind == ScannedMediaKind.CalibrationCartridge
                    ? (uint)WIN32_ERROR.NO_ERROR
                    : (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED),
            CalibrationInfo = calibrationInfo,
        };
        m_logger.LogInformation("{Prefix}: Scan done — {Map}", LogPrefix, map);
        return map;
    }

    #endregion

    #region *** Progress ***

    /// <summary>
    /// Reports one fragment, converting an abort request into the cooperative flag and swallowing everything else (SM-10).
    /// </summary>
    /// <remarks>
    /// The scanner's <see cref="TapeAbortRequestedException"/> handling is REAL, unlike the calibrator's: a WPF sink
    ///  marshals to the UI thread and may well throw, and a service-side handler may bridge a <c>CancellationToken</c>
    ///  that way. A progress sink can abort a scan; it can never fail one.
    /// </remarks>
    private void Report(IProgress<TapeScanProgress>? progress, TapeMediaFragment fragment)
    {
        if (progress is null)
            return;
        try
        {
            progress.Report(TapeScanProgress.ForFragment(fragment, Drive.CurrentBlock));
        }
        catch (TapeAbortRequestedException ex)
        {
            m_logger.LogInformation("{Prefix}: Abort requested while reporting a fragment: {Exception}", LogPrefix, ex);
            IsAbortRequested = true;    // honoured at the next poll, at a clean boundary
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "{Prefix}: Exception while reporting a fragment", LogPrefix);
        }
    }

    private void ReportPhase(IProgress<TapeScanProgress>? progress, string phase, int ordinal)
    {
        if (progress is null)
            return;
        try
        {
            progress.Report(TapeScanProgress.ForPhase(phase, ordinal, Drive.CurrentBlock));
        }
        catch (TapeAbortRequestedException)
        {
            IsAbortRequested = true;
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "{Prefix}: Exception while reporting a phase", LogPrefix);
        }
    }

    #endregion

    #region *** Block size guard ***

    /// <summary>Restores the drive's block size on the way out (SM-9), whatever happened in between.</summary>
    /// <remarks>
    /// <see cref="TapeHeaderBlock.Read"/> restores its own, but the small-block fallback and
    ///  the TOC harvest both set the size deliberately. Mirrors <see cref="TapeCalibrator.RunGuard"/> in shape.
    /// </remarks>
    private readonly struct BlockSizeGuard(TapeScanner scanner)
    {
        private readonly TapeScanner m_scanner = scanner;
        private readonly uint m_savedBlockSize = scanner.Drive.BlockSize;

        public void Restore()
        {
            if (m_savedBlockSize > 0 && m_scanner.Drive.BlockSize != m_savedBlockSize)
                m_scanner.Drive.SetBlockSize(m_savedBlockSize);
        }
    }

    #endregion
}
