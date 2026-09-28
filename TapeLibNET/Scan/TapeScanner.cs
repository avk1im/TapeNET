using Microsoft.Extensions.Logging;
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
    ///  between fragments, so the walk always stops at a clean fragment boundary.
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
    /// <param name="progress">Optional sink, fired once per identified fragment.</param>
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
    /// The SM §3 walk: identify block 0, cross the media header's own mark, then hop separators identifying
    ///  each landing place, until EOD or a terminating condition.
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
    /// <b>The first advance is NOT a separator hop.</b> A media header is closed by its own filemark, which
    ///  belongs to the header rather than to the content — see <see cref="CrossMediaHeaderMark"/>.
    /// </para>
    /// </remarks>
    private MediaScanMap ScanCore(TapeMediaLayout layout, IProgress<TapeScanProgress>? progress)
    {
        ScanMediaOptions options = Options;

        List<TapeMediaFragment> fragments = [];
        bool truncated = false;
        uint terminator;
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
        Report(progress, first);

        // A calibration cartridge is a COMPLETE, correct conclusion — identified, not walked (SM-7). Past
        //  the header a calibration trail is filemark-delimited checkpoints separated by gigabytes of
        //  random padding: walking it would yield hundreds of Unknown fragments and tell nobody anything.
        if (first.Kind == FragmentKind.CalibrationHeader)
        {
            m_logger.LogInformation("{Prefix}: Scan: calibration cartridge — {Fragment}", LogPrefix, first);

            TapeCalibrationMediaInfo? calInfo = options.InspectCalibrationTrail
                ? InspectCalibrationTrail(progress)
                : null;

            ResetError();   // the inspection is best-effort; a failed one is not the scan's error
            return BuildMap(layout, ScannedMediaKind.CalibrationCartridge, fragments,
                truncated: false, (uint)WIN32_ERROR.NO_ERROR,
                calibrationInfo: calInfo);
        }

        // A media header owns the filemark that follows it, whatever the layout's separator is. The very
        //  first advance must therefore cross THAT mark, not a separator — otherwise a setmark layout
        //  skips the header's filemark, the whole first set, and the first set's closing setmark at once.
        bool crossHeaderMark = first.Kind == FragmentKind.MediaHeader;

        // ── Walk ─────────────────────────────────────────────────────────────────────────────────────
        while (true)
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

            if (fragments.Count >= options.MaxFragments)
            {
                m_logger.LogWarning("{Prefix}: Scan stopped at the {Max}-fragment guard — the map is truncated",
                    LogPrefix, options.MaxFragments);

                truncated = true;
                terminator = (uint)WIN32_ERROR.ERROR_INVALID_DATA;
                break;
            }

            // Advance to whatever follows this fragment. Failure here is the NORMAL end of the walk.
            bool advanced = crossHeaderMark
                ? CrossMediaHeaderMark(out int marksCrossed)
                : MoveToNextSeparator(layout, out marksCrossed);

            crossHeaderMark = false;    // one-shot: only the media header carries its own mark

            if (!advanced)
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
                    m_logger.LogTrace("{Prefix}: Scan: walk ended at {Err} after {N} fragment(s)",
                        LogPrefix, LastErrorWin32, fragments.Count);
                    ResetError();   // reaching the end of the tape is not this operation's error
                }

                // The fragment we just left was never closed by a separator — on a set header, that is
                //  precisely the "backup died mid-set" signature.
                CloseLastFragment(fragments, closedBySeparator: false, nextStart: -1L);
                fragments.Add(TrailingRegionFragment(fragments.Count, terminator));
                break;
            }

            CloseLastFragment(fragments, closedBySeparator: true, nextStart: Drive.CurrentBlock);

            // Consecutive marks are a FRAGMENT, not an anomaly: the TOC mark's triple filemark, a
            //  double-filemark end convention, or the erased remains of a set (§3.1).
            TapeMediaFragment next = marksCrossed > 1
                ? MarkRunFragment(fragments.Count, Drive.CurrentBlock, marksCrossed)
                : IdentifyFragmentAt(fragments.Count, Drive.CurrentBlock);

            fragments.Add(next);
            Report(progress, next);

            if (next.Kind is FragmentKind.SetHeader or FragmentKind.MediaHeader)
                kind = ScannedMediaKind.Backup;
        }

        if (kind == ScannedMediaKind.Blank && fragments.Count > 0)
            kind = ScannedMediaKind.Foreign;    // readable, but nothing on it is ours

        return BuildMap(layout, kind, fragments, truncated, terminator);
    }

    /// <summary>
    /// Crosses the filemark that terminates the media header, landing at begin-of-content.
    /// </summary>
    /// <param name="marksCrossed">Always 1 — the header's mark is its own terminator, never a run.</param>
    /// <remarks>
    /// <para>
    /// <b>A filemark on EVERY layout.</b> <see cref="TapeHeaderBlock.WriteFramed"/> terminates the header
    ///  with a filemark unconditionally (<see cref="TapeHeaderBlock.WritesTrailingMark"/>), because a tape
    ///  drive accepts a write only at BOP, at EOD, or immediately after a mark — so the first content
    ///  write must be post-mark. The layout's SEPARATOR choice does not enter into it.
    /// </para>
    /// <para>
    /// Reported as <c><paramref name="marksCrossed"/> = 1</c> so the landing place is IDENTIFIED rather
    ///  than recorded as a <see cref="FragmentKind.MarkRun"/> — the header's filemark is part of the header,
    ///  not a region.
    /// </para>
    /// </remarks>
    private bool CrossMediaHeaderMark(out int marksCrossed)
    {
        marksCrossed = 0;

        if (!Drive.MoveToNextFilemark(1))
        {
            // A header with nothing behind it: the cartridge was formatted and then abandoned, or the
            //  write died immediately after. The caller records the header as unclosed and stops.
            m_logger.LogTrace("{Prefix}: Scan: nothing follows the media header ({Err})",
                LogPrefix, LastErrorWin32);
            return false;
        }

        marksCrossed = 1;
        ResetError();
        return true;
    }


    /// <summary>
    /// Hops to whatever follows the current fragment, counting consecutive marks (§3.1).
    /// </summary>
    /// <param name="marksCrossed">How many marks were crossed in one run; 1 in the ordinary case.</param>
    /// <remarks>
    /// The SEPARATOR TYPE comes from the predicted layout, never from probing: on the setmark layouts a
    ///  set is closed by a setmark while the TOC is filemark-delimited, and hopping the wrong kind would
    ///  either miss every set or march into the TOC counting its filemarks as set boundaries.
    /// </remarks>
    private bool MoveToNextSeparator(TapeMediaLayout layout, out int marksCrossed)
    {
        marksCrossed = 0;

        if (!HopOneSeparator(layout))
            return false;

        marksCrossed = 1;

        // Count a run of adjacent marks. Each extra hop that SUCCEEDS without data in between means
        //  another mark; the first failure ends the run — and is not itself an error, since we already
        //  have a valid landing place.
        while (marksCrossed < Options.MaxFragments)
        {
            long before = Drive.CurrentBlock;

            if (!IsAtAnotherMark())
                break;

            if (!HopOneSeparator(layout))
            {
                ResetError();   // the run simply ended; the position from `before` still stands
                break;
            }

            if (Drive.CurrentBlock == before)
                break;          // defensive: no progress ⇒ stop rather than spin

            marksCrossed++;
        }

        ResetError();
        return true;
    }

    /// <summary>One separator hop, of the type the layout dictates.</summary>
    private bool HopOneSeparator(TapeMediaLayout layout)
        => layout.UseSmks ? Drive.MoveToNextSetmark(1) : Drive.MoveToNextFilemark(1);

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
    /// Fills in what only the NEXT fragment's position can tell us about the previous one: whether a
    ///  separator closed it, and how many blocks it spanned.
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

    private static TapeMediaFragment MarkRunFragment(int ordinal, long startBlock, int markCount)
        => new()
        {
            Ordinal = ordinal,
            StartBlock = startBlock,
            Kind = FragmentKind.MarkRun,
            MarkCount = markCount,
        };

    private static TapeMediaFragment TrailingRegionFragment(int ordinal, uint terminator)
        => new()
        {
            Ordinal = ordinal,
            StartBlock = -1L,
            Kind = FragmentKind.TrailingRegion,
            Diagnosis = terminator == (uint)WIN32_ERROR.NO_ERROR
                     || terminator == (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED
                ? TapeResult.OK
                : TapeResult.Fail(terminator, $"Walk ended: {(WIN32_ERROR)terminator}"),
        };

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
    ///  next clean fragment boundary rather than unwinding mid-read. A progress sink can abort a scan; it
    ///  can never fail one.
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
            IsAbortRequested = true;    // honoured at the next poll, at a fragment boundary
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
