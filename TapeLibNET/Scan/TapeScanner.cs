using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TapeLibNET.Agents;
using TapeLibNET.Calibration;
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Media;
using Windows.Win32.Foundation;

namespace TapeLibNET.Scan;


/// <summary>
/// Reads a cartridge from BOM forward and describes everything identifiable on it — with no table of
///  contents in hand and none assumed (SM-2). Writes nothing (SM-1).
/// </summary>
/// <remarks>
/// <para>
/// <b>A sibling of <see cref="TapeCalibrator"/>, deliberately NOT a <c>TapeAgentBase</c>.</b> The symmetry
///  is close to exact: the calibrator WRITES a self-describing trail and reads it back to measure; the
///  scanner READS whatever trail it finds and describes it. Both drive only the public
///  <see cref="TapeDrive"/> surface, both are backend-agnostic, and neither has files, sets, or a TOC.
/// </para>
/// <para>
/// Living outside the agent hierarchy is what makes the walk honest. <see cref="TapeNavigator"/> is
///  TOC-bound by construction — believed positions, <c>CurrentContentSet</c>, <c>FirstSetOnVolume</c> —
///  and the scanner needs RAW mark hops and absolute block numbers. Inheriting it would mean spending the
///  whole walk keeping it quiet.
/// </para>
/// <para>
/// The ONE place an agent still earns its keep is block 0 (<see cref="IdentifyBomFragment"/>): a throwaway
///  <see cref="TapeAgentBase"/> owns the "rewind, select the partition, read the BOM block" ceremony, and
///  duplicating it here is exactly how two copies drift apart. Mirrors
///  <c>TapeServiceBase.RefreshLoadedHeader</c>, which does the same for the same reason.
/// </para>
/// <para>
/// Conceptually create-use-discard: <c>new TapeScanner(drive).Scan()</c>. Cancellation is cooperative via
///  <see cref="IsAbortRequested"/>. This class does NOT judge what it finds — no verdicts (SM-3); the
///  comparison against a TOC is a separate, pure step.
/// </para>
/// </remarks>
public sealed partial class TapeScanner : TapeDriveHolder<TapeScanner>
{
    #region *** Error latching and result building ***

    /// <summary>Builder for the current scan's result, latching on the FIRST failure.</summary>
    /// <remarks>
    /// The scan deliberately TOLERATES failures and resets the error — a failed TOC harvest, a failed
    ///  small-block probe, an unidentifiable fragment — so the live error state is a poor witness by the
    ///  time the walk ends: it reflects the last tolerated step, not the thing that went wrong. Mirrors
    ///  <c>TapeCalibrator._resultBuilder</c> and <c>TapeAgentBase._resultBuilder</c>.
    /// </remarks>
    private readonly TapeResultBuilder _resultBuilder;

    private void LatchFailure() => _resultBuilder.LatchFailure();
    private void ResetLatchedFailure() => _resultBuilder.Reset();

    /// <summary>
    /// Diagnosis of the current (or most recent) scan: its first failure, or <see cref="TapeResult.OK"/>.
    /// </summary>
    /// <remarks>
    /// Matters here for the reason it matters on the calibrator: <see cref="Scan"/> returns a nullable
    ///  map, so null is the only signal the caller gets — this property turns it into an explanation.
    ///  Note a TRUNCATED map is still returned, so null means "no map at all", not "an imperfect scan".
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
    /// Set by the caller — or by a progress sink that threw (SM-10) — to request a graceful abort. Polled
    ///  between reads, so the walk always stops at a clean boundary.
    /// </summary>
    public bool IsAbortRequested { get; set; }

    #endregion

    #region *** Construction ***

    public TapeScanner(TapeDrive drive) : base(drive)
    {
        _resultBuilder = new(this);
    }

    #endregion

    #region *** Public API ***

    /// <summary>
    /// Walks the loaded cartridge from BOM to EOD and returns what it found. READ-ONLY throughout.
    /// </summary>
    /// <param name="progress">
    /// Optional sink, fired once per fragment — when that fragment is FINAL, i.e. one read behind the head.
    /// </param>
    /// <returns>
    /// The map, or <see langword="null"/> when no scan was possible at all (no media, cannot position) —
    ///  see <see cref="LastResult"/>. A map whose walk ended early is still RETURNED, flagged
    ///  <see cref="MediaScanMap.Truncated"/> (SM-5).
    /// </returns>
    public MediaScanMap? Scan(IProgress<TapeScanProgress>? progress = null)
    {
        ResetError();
        ResetLatchedFailure();      // fresh verb ⇒ fresh diagnosis
        IsAbortRequested = false;

        // Derived from capabilities alone, BEFORE the tape moves — so it is reportable even on a cartridge
        //  that then yields nothing at all, where knowing what SHOULD have been there is the diagnosis.
        TapeMediaLayout layout = TapeMediaLayout.Predict(Drive);

        if (!Drive.IsMediaLoaded)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            LatchFailure();
            LogErrorAsDebug("Scan: no media loaded");
            return null;
        }

        // The scan never writes, so the block size is the only drive state it disturbs (the TOC harvest
        //  sets it deliberately). Restore it whatever happens — SM-9.
        BlockSizeGuard guard = new(this);

        try
        {
            return ScanCore(layout, progress);
        }
        catch (Exception ex)
        {
            // A survey must not throw at its caller: the map gathered so far is worth more than the
            //  exception. Latch, log, and report a null map with a real diagnosis.
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

    #endregion

    #region *** The walk ***

    /// <summary>
    /// The SM §3 walk: identify block 0, then alternate between crossing whatever closes the current
    ///  fragment and reading what follows, until EOD or a terminating condition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No end-of-content bound, deliberately.</b> Walking to EOD needs no bound: the trailing TOC
    ///  copies are IDENTIFIED (§4.3), not avoided.
    /// </para>
    /// <para>
    /// <b>No retry, no resync-by-seeking.</b> Skipping a bad region by seeking blind is guessing at block
    ///  numbers on damaged tape, and every fragment after the guess inherits the error. Stop, record where,
    ///  and say the map is truncated.
    /// </para>
    /// <para>
    /// <b>No mark fishing.</b> Adjacent marks are detected by the identification read itself: a read that
    ///  returns nothing but a tapemark has already crossed it. So each position is read exactly once, the
    ///  head never steps back, and a drive that cannot write consecutive marks pays nothing for the
    ///  possibility. Such marks are gathered into a pending run and committed once the next data block or
    ///  the end shows where the run stops (<see cref="FoldMarkRun"/>).
    /// </para>
    /// <para>
    /// <b>Two ways to end, both honest, neither adding a fragment.</b> End-of-data after a mark is the
    ///  normal end of a sound cartridge. A closing-mark hop that runs into end-of-data instead means the
    ///  last fragment was NEVER closed — on a set header, a backup that died mid-set. Either way the fact
    ///  lives on the fragment (<see cref="TapeMediaFragment.ClosedBySeparator"/>) and on the map
    ///  (<see cref="MediaScanMap.TerminatorWin32"/>).
    /// </para>
    /// <para>
    /// <b>A fragment is reported once it is FINAL</b> — when the next one is committed, and the last one
    ///  when the walk ends. Its closing state, its span, and a <see cref="FragmentKind.TocMark"/> retype are
    ///  all decided by reads that come AFTER it; reporting earlier would hand the sink a value that later
    ///  changes silently.
    /// </para>
    /// </remarks>
    private MediaScanMap ScanCore(TapeMediaLayout layout, IProgress<TapeScanProgress>? progress)
    {
        ScanMediaOptions options = Options;

        List<TapeMediaFragment> fragments = [];
        bool truncated = false;
        uint terminator = (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED;
        ScannedMediaKind kind;

        // ── Block 0 ──────────────────────────────────────────────────────────────────────────────────
        //  Via a throwaway agent, so the rewind + partition + BOM-read ceremony stays defined once.
        TapeMediaFragment first = IdentifyBomFragment(out int bomBytesRead);

        if (bomBytesRead <= 0)
        {
            // Nothing readable at block 0 at all. A clean, successful finding — never a failure (SM-6).
            //  "Blank" and "broken" must not be the same result.
            m_logger.LogInformation("{Prefix}: Scan: medium is blank (no data at block 0)", LogPrefix);

            ResetError();
            return BuildMap(layout, ScannedMediaKind.Blank, fragments,
                truncated: false, (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED);
        }

        fragments.Add(first);
        kind = KindFromFirstFragment(first);

        // A calibration cartridge is a COMPLETE, correct conclusion — identified, not walked (SM-7). Past
        //  the header a calibration trail is filemark-delimited checkpoints separated by gigabytes of
        //  random padding: walking it would yield hundreds of Unknown fragments and tell nobody anything.
        if (first.Kind == FragmentKind.CalibrationHeader)
        {
            m_logger.LogInformation("{Prefix}: Scan: calibration cartridge — {Fragment}", LogPrefix, first);
            Report(progress, first);

            TapeCalibrationMediaInfo? calInfo = options.InspectCalibrationTrail
                ? InspectCalibrationTrail(progress)
                : null;

            ResetError();   // the inspection is best-effort; a failed one is not the scan's error
            return BuildMap(layout, ScannedMediaKind.CalibrationCartridge, fragments,
                truncated: false, (uint)WIN32_ERROR.NO_ERROR,
                calibrationInfo: calInfo);
        }

        // ── Walk ─────────────────────────────────────────────────────────────────────────────────────
        bool crossClosingMark = true;   // the last fragment is data: its closing mark lies ahead
        int runMarks = 0;               // adjacent marks read since the last data fragment
        long runStart = -1L;            // block of the first of them

        do // while (true)
        {
            if (IsAbortRequested)
            {
                SetError(WIN32_ERROR.ERROR_CANCELLED);
                LatchFailure();
                m_logger.LogWarning("{Prefix}: Scan aborted by caller after {N} fragment(s)",
                    LogPrefix, fragments.Count);

                truncated = true;
                terminator = (uint)WIN32_ERROR.ERROR_CANCELLED;
                break;
            }

            // The run counter is guarded too: a pathological medium can yield marks indefinitely without
            //  ever adding a fragment.
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

                    // A positional error means "the tape ends here" — a FINDING. Anything else means the
                    //  drive went away, and a map built on a dead drive's silence is fiction (SM-5): the
                    //  comparison phase would read that silence as ABSENCE.
                    if (!IsPositionalEnd(LastErrorWin32))
                    {
                        LatchFailure();
                        m_logger.LogError("{Prefix}: Scan: transport fault after {N} fragment(s) ({Err}) — map truncated",
                            LogPrefix, fragments.Count, LastErrorWin32);
                        truncated = true;
                    }
                    else
                    {
                        m_logger.LogTrace("{Prefix}: Scan: {Fragment} is not closed — the tape ends inside it",
                            LogPrefix, fragments[^1]);
                        ResetError();   // reaching the end of the tape is not this operation's error
                    }

                    // Recorded ON the fragment itself. On a set header this is precisely the "backup died
                    //  mid-set" signature; no pseudo-fragment is needed to say so.
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
                // The read has already crossed the mark — nothing to hop. Just note it and read on.
                if (runMarks++ == 0)
                    runStart = at;
                continue;
            }

            if (outcome == ReadOutcome.EndOfData)
            {
                // End-of-data after a mark: the NORMAL end of a sound cartridge. The last fragment is
                //  already recorded as closed; there is nothing further to describe.
                m_logger.LogTrace("{Prefix}: Scan: end of data after {N} fragment(s)", LogPrefix, fragments.Count);
                ResetError();
                break;
            }

            // `next` is non-null here. ReadFragmentAt sets its fragment EXACTLY when it returns
            //  ReadOutcome.Fragment, and above we've checked for the enum's other values.
            Debug.Assert(outcome == ReadOutcome.Fragment && next is not null,
                $"ReadFragmentAt returned {outcome} with {(next is null ? "no" : "a")} fragment");
            TapeMediaFragment fragment = next!;

            // A data fragment: the run before it (if any) now has a known end.
            FoldMarkRun(fragments, layout, ref runMarks, runStart, endBlock: at, progress);
            Commit(fragments, fragment, progress);

            // TOC recovery is a step of the WALK, not of identification: it moves the head over many
            //  blocks, and returns it. A head that cannot be returned ends the walk truncated (SM-5).
            if (fragment.Kind == FragmentKind.TOC && options.HarvestTocCopies
                && !HarvestLastTocCopy(fragments, progress))
            {
                LatchFailure();
                truncated = true;
                terminator = (uint)LastErrorWin32;
                break;
            }

            if (fragment.Kind is FragmentKind.SetHeader or FragmentKind.MediaHeader)
                kind = ScannedMediaKind.Backup;

            crossClosingMark = true;

        } while (true);

        // Whatever ended the walk, a run still pending ends here.
        FoldMarkRun(fragments, layout, ref runMarks, runStart, endBlock: Drive.CurrentBlock, progress);

        // The last fragment is final only now.
        Report(progress, fragments[^1]);

        if (kind == ScannedMediaKind.Blank && fragments.Count > 0)
            kind = ScannedMediaKind.Foreign;    // readable, but nothing on it is ours

        return BuildMap(layout, kind, fragments, truncated, terminator);
    }

    /// <summary>
    /// Crosses the mark that closes <paramref name="current"/>.
    /// </summary>
    /// <returns>
    /// False when no closing mark was found. The scanner's error then carries the DRIVE's reason, so the
    ///  caller can tell a positional end from a transport fault.
    /// </returns>
    private bool CrossClosingMark(TapeMediaFragment current, TapeMediaLayout layout)
    {
        bool hopped = ClosingMarkIsSetmark(current, layout)
            ? Drive.MoveToNextSetmark(1)
            : Drive.MoveToNextFilemark(1);

        if (!hopped)
        {
            // The DRIVE carries the reason; the scanner's own error channel sees nothing unless synced.
            //  Without this the caller read NO_ERROR, took it for a transport fault, and truncated a
            //  perfectly complete map.
            SyncErrorFrom(Drive);
            return false;
        }

        ResetError();
        return true;
    }

    /// <summary>Which mark closes a fragment of this kind — a setmark, or a filemark.</summary>
    /// <remarks>
    /// <para>
    /// <b>The kind decides, not only the layout.</b> Two records carry their own filemark whatever the
    ///  layout's set separator is:
    ///  <list type="bullet">
    ///   <item>the media header — <see cref="TapeHeaderBlock.WritesTrailingMark"/>, so the first content
    ///    write is post-mark on every drive;</item>
    ///   <item>a TOC copy — the TOC region is filemark-delimited on every in-set layout, setmark ones
    ///    included (<c>[SM][toc1][FM][toc2][FM]</c>).</item>
    ///  </list>
    ///  Hopping a SETMARK from either on a setmark layout overshoots: past the header's filemark, the whole
    ///  first set and its closing setmark; or past both TOC copies to end-of-data, leaving the first
    ///  reported as unclosed and the second never seen.
    /// </para>
    /// <para>
    /// Everything else — set headers, and the unidentified content of legacy media — closes with the
    ///  layout's separator. Mark runs and TOC marks never reach here: they consist of marks the reads have
    ///  already crossed.
    /// </para>
    /// </remarks>
    private static bool ClosingMarkIsSetmark(TapeMediaFragment fragment, TapeMediaLayout layout)
        => fragment.Kind switch
        {
            FragmentKind.MediaHeader => false,
            FragmentKind.TOC => false,
            _ => layout.UseSmks,
        };

    /// <summary>
    /// Whether <paramref name="error"/> says "there is no more tape that way" — the tape ENDED, rather
    ///  than the drive or the cartridge having failed.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>TapeAgentBase.IsPositionalNavigationError</c> exactly, and for the same reason: treating
    ///  a dead drive as a tape end would present a map as complete when the walk was cut short.
    ///  Deliberately a local copy rather than a widened visibility — the agent's version is a member of a
    ///  hierarchy this class is not in, and the two lists answer questions in different vocabularies.
    /// </remarks>
    private static bool IsPositionalEnd(WIN32_ERROR error) => error is
        WIN32_ERROR.ERROR_NO_DATA_DETECTED          // ran past EOD hunting a mark that is not there
        or WIN32_ERROR.ERROR_END_OF_MEDIA           // the same, physically
        or WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA
        or WIN32_ERROR.ERROR_FILEMARK_DETECTED
        or WIN32_ERROR.ERROR_SETMARK_DETECTED
        or WIN32_ERROR.ERROR_NOT_SUPPORTED;         // e.g. setmark spacing on a drive that lost the cap

    #endregion

    #region *** Fragment assembly ***

    /// <summary>
    /// Commits a pending run of adjacent marks, now that its end is known — as a
    ///  <see cref="FragmentKind.TocMark"/> folded into the gap before it, or as a
    ///  <see cref="FragmentKind.MarkRun"/> of its own. No-op when no run is pending.
    /// </summary>
    /// <param name="endBlock">Where the run stops: the next data block, or where the walk ended.</param>
    /// <remarks>
    /// <para>
    /// <b>The TOC-mark fold.</b> On a <see cref="TapeMediaLayout.HasTocMark"/> layout the navigator writes
    ///  <c>[gap][FM][FM][FM]</c> before the first TOC copy. The walk sees the gap as an
    ///  <see cref="FragmentKind.Unknown"/> block closed by the first filemark, then reads the other two as a
    ///  run. The fold names that shape for what it is, instead of leaving an "unidentified" block behind on
    ///  every healthy cartridge of that layout.
    /// </para>
    /// <para>
    /// <b>Guarded three ways, each for a reason.</b> The layout must write TOC marks at all. The run must
    ///  hold at least two marks, so a legacy double-filemark end of data (one extra mark) is not mistaken
    ///  for one. And the block before it must have been READ cleanly — a damaged header or an unreadable
    ///  block is also <see cref="FragmentKind.Unknown"/>, but it is no gap, and a fold would hide the
    ///  damage.
    /// </para>
    /// <para>
    /// <see cref="TapeMediaFragment.MarkCount"/> is informational only: nothing depends on a drive reporting
    ///  exactly one mark per read, and "two or more" is all the fold asks.
    /// </para>
    /// </remarks>
    private void FoldMarkRun(List<TapeMediaFragment> fragments, TapeMediaLayout layout,
                             ref int runMarks, long runStart, long endBlock,
                             IProgress<TapeScanProgress>? progress)
    {
        if (runMarks == 0)
            return;

        TapeMediaFragment last = fragments[^1];

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
                Kind = FragmentKind.MarkRun,
                MarkCount = runMarks,
                ClosedBySeparator = true,   // a run consists of marks; it cannot be left open
                BlockSpan = endBlock - runStart,
            }, progress);
        }

        runMarks = 0;
    }

    /// <summary>
    /// Appends <paramref name="fragment"/>, reporting its predecessor — which is final from this moment on.
    /// </summary>
    /// <remarks>
    /// Assigns the ordinal itself. The identification read ran BEFORE any pending mark run was committed,
    ///  so the ordinal it chose may already be taken; the list position is the only authority.
    /// </remarks>
    private void Commit(List<TapeMediaFragment> fragments, TapeMediaFragment fragment,
                        IProgress<TapeScanProgress>? progress)
    {
        if (fragments.Count > 0)
            Report(progress, fragments[^1]);

        fragments.Add(fragment with { Ordinal = fragments.Count });
    }

    /// <summary>
    /// Fills in what only the NEXT position can tell us about the last fragment: whether a separator
    ///  closed it, and how many blocks it spanned.
    /// </summary>
    /// <remarks>
    /// <see cref="TapeMediaFragment.BlockSpan"/> is an upper bound INCLUDING marks (SM-11) — nothing here
    ///  measures a payload, and reporting it as a size would put a plausible, wrong number in front of a
    ///  user making a capacity decision.
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

    private MediaScanMap BuildMap(
        TapeMediaLayout layout, ScannedMediaKind kind, List<TapeMediaFragment> fragments,
        bool truncated, uint terminator, TapeCalibrationMediaInfo? calibrationInfo = null)
    {
        var map = new MediaScanMap
        {
            Layout = layout,
            Kind = kind,
            Fragments = fragments,
            ScannedUtc = DateTime.UtcNow,
            Truncated = truncated,
            TerminatorWin32 = terminator,
            CalibrationInfo = calibrationInfo,
        };

        m_logger.LogInformation("{Prefix}: Scan done — {Map}", LogPrefix, map);
        return map;
    }

    /// <summary>What the cartridge looks like, judged from block 0 alone.</summary>
    private static ScannedMediaKind KindFromFirstFragment(TapeMediaFragment first) => first.Kind switch
    {
        FragmentKind.MediaHeader => ScannedMediaKind.Backup,
        FragmentKind.SetHeader => ScannedMediaKind.Backup,      // headerless volume carrying set headers
        FragmentKind.CalibrationHeader => ScannedMediaKind.CalibrationCartridge,
        _ => ScannedMediaKind.Foreign,                          // refined by the walk if a header turns up
    };

    #endregion

    #region *** Progress ***

    /// <summary>
    /// Reports one fragment, converting an abort request into the cooperative flag and swallowing
    ///  everything else (SM-10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The deliberate divergence from <see cref="TapeCalibrator"/>.</b> Its
    ///  <c>TapeAbortRequestedException</c> handling is documented as purely defensive — the calibrator
    ///  never throws one. The scanner's is REAL: a WPF sink marshals to the UI thread and may well throw,
    ///  and a service-side handler may bridge a <c>CancellationToken</c> that way.
    /// </para>
    /// <para>
    /// The exception is caught HERE and converted, never tossed to the caller: the walk then stops at the
    ///  next clean boundary rather than unwinding mid-read. A progress sink can abort a scan; it can never
    ///  fail one.
    /// </para>
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
            m_logger.LogInformation("{Prefix}: Abort requested while reporting a fragment: {Exception}",
                LogPrefix, ex);
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

    /// <summary>
    /// Restores the drive's block size on the way out (SM-9), whatever happened in between.
    /// </summary>
    /// <remarks>
    /// <see cref="TapeHeaderBlock.Read"/> restores its own, but the small-block fallback and the TOC
    ///  harvest both set the size deliberately. Mirrors <c>TapeCalibrator.RunGuard</c> in shape — the
    ///  scanner needs no calibration neutralizing, since it never writes.
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
