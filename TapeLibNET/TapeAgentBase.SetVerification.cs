using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32.Foundation;

namespace TapeLibNET;

public partial class TapeAgentBase
{
    #region *** Set Header Verification ***

    /// <summary>What the verdict ladder decided about the set just verified.</summary>
    /// <remarks>
    /// A plain <see langword="bool"/> cannot carry this: "not settled YET, a recovery stage remains" and
    ///  "unusable, stop" are both failures to the caller but opposite instructions to the recovery, and
    ///  the read path's proceed-unverified is a THIRD thing again — it must not fire while a stage is
    ///  still untried.
    /// </remarks>
    private enum SetVerdictOutcome
    {
        /// <summary>The set may be used — verified, or deliberately proceeding unverified.</summary>
        Proceed,
        /// <summary>Not settled, but a recovery stage remains untried. The error is NOT yet final.</summary>
        Recoverable,
        /// <summary>Unusable, and no stage can help. The error and the anomaly are final.</summary>
        Terminal,
    }

    // Guards the one-retry correction bound (SH-10). Set while a correction is being verified, so the re-verify
    //  cannot itself trigger another correction — a tape whose mark structure defeats a simple relative
    //  move is inconsistent, not noisy, and a second attempt would only walk further into the unknown.
    private bool m_correctingSetNavigation = false;

    // SH-14 stage 2: set once the renavigation from begin-of-content has been spent for the CURRENT
    //  verification. Reset at the top of VerifySetHeaderForCurrentSet, which owns the whole ladder for
    //  one set. Belt-and-braces beside the structural bound (the second pass targets a non-negative
    //  index, so it cannot re-enter) — kept because the bound would silently vanish if the anchor
    //  arithmetic ever changed.
    private bool m_renavigatedFromBom = false;

    /// <summary>
    /// Whether a detected <see cref="TapeSetHeaderVerdict.SetIndexDrift"/> is repaired in place (SH-10)
    ///  or reported as a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see langword="true"/>: a bounded relative correction turns a failed restore into a
    ///  successful one, and the drift is logged at Warning either way.
    /// </para>
    /// <para>
    /// Set <see langword="false"/> when a disagreement should HALT rather than heal — diagnosing a drive
    ///  that miscounts marks, auditing an archive under a strict-verification policy, or preserving the
    ///  landing position of a suspect cartridge for forensics. Correction never hides the fault, but it
    ///  does move the head, which is sometimes exactly what an investigator does not want.
    /// </para>
    /// <para>
    /// Never applies to the backup path, which has no verification at all (§15.3).
    /// </para>
    /// </remarks>
    public bool CorrectsSetNavigation { get; set; } = true;

    /// <summary>
    /// Whether this agent verifies the set header standing at a destructive write position before
    ///  destroying it (SH-13). Mirrors <see cref="WritesSetHeaders"/> in shape and lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arms ONLY where something already exists at the target: an overwrite (<c>newSet: false</c>) or a
    ///  delete. A set appended at end-of-data has no predecessor record to read, so the
    ///  performance-critical path pays nothing for this being <see langword="true"/> by default.
    /// </para>
    /// <para>
    /// Set <see langword="false"/> for the deliberate, informed override — repairing a cartridge whose
    ///  set headers are themselves damaged, where the verification would block the very operation that
    ///  would fix it. It disables the CHECK, not merely a prompt.
    /// </para>
    /// </remarks>
    public bool VerifiesSetHeader { get; set; } = true;

    /// <summary>
    /// Whether a set that cannot be positively verified BLOCKS the operation (write side) or merely
    ///  warns and proceeds (read side).
    /// </summary>
    /// <remarks>
    /// The base returns <see langword="false"/>. Overridden to <see langword="true"/> by writing
    ///  descendants e.g. <see cref="TapeFileBackupAgent"/>.
    /// <para>
    /// The read side's golden rule — a record we cannot verify never blocks — rests on a fact that does
    ///  not survive the crossing: <b>restore positions absolutely</b>.
    ///  <see cref="TapeFileRestoreBaseAgent.RestoreNextFile"/> seeks to the
    ///  file's exact <c>(block, offset)</c>, so an unverified set costs a safety net and nothing more. A
    ///  destructive write positions <b>relatively</b>, by counting marks, and an unreadable block at the
    ///  presumed set start is exactly the symptom a miscount produces when it lands somewhere that is not
    ///  a set start at all. Proceeding there would take the strongest available signal that the head is
    ///  lost and treat it as permission.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> if the operation blocks on an unverifiable set;
    ///  otherwise, <see langword="false"/>. The base always returns <see langword="false"/>. The writing
    ///  descendants e.g. <seealso cref="TapeFileBackupAgent"/> override it to <see langword="true"/>.
    /// </returns>
    protected virtual bool BlocksOnUnverifiableSet => false;

    // Which set has already raised OnSetAnomaly (SH-18: at most once per set). Keyed on the SET INDEX
    //  rather than a bool, so the Step 5 re-navigation — which re-enters the ladder for the SAME set —
    //  cannot re-arm it, while a genuinely different set can. Needs no reset anywhere.
    private int m_setAnomalyRaisedForSet = 0;   // 0 == none; set indices are 1-based
    // Whether the set currently being processed hit an anomaly, so NotifySetEnd knows whether to count
    //  it as a success. Per-set; cleared by NotifySetStart.
    private bool m_setAnomalyInCurrentSet = false;

    /// <summary>
    /// Set anomalies observed during the current operation, corrected or not — the records behind
    ///  <see cref="TapeFileStatistics.Sets"/>'s counters. It's a forensic trail of all anomaly
    ///  reports - hence a single anomaly can be registered several times as we attempt to correct it.
    ///  Therefore, <c><see cref="SetAnomalies"/>.Count >= <see cref="TapeSetStatistics.AnomaliesDetected"/></c>
    ///  (as contained by <see cref="_stats"/>) by design.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in <see cref="TapeSetStatistics"/> because that struct is copied by value
    ///  into every callback: a list field would alias across every copy.
    /// </remarks>
    public IReadOnlyList<TapeSetAnomaly> SetAnomalies => _setAnomalies;
    protected readonly List<TapeSetAnomaly> _setAnomalies = [];

    /// <summary>
    /// Reads the block at the CURRENT position and returns it as a <see cref="TapeSetHeader"/>, or
    ///  <see langword="null"/> when the read failed or the block does not classify as one.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="TapeAgentBase.ReadBomHeader"/>: the manager delivers raw bytes, the AGENT
    ///  classifies (INV-12).
    /// </remarks>
    private TapeSetHeader? ReadSetHeader()
    {
        var buffer = new byte[TapeHeaderBlock.Size];

        int read = Manager.ReadSetHeaderBlock(buffer);
        if (read <= 0)
        {
            // SH-6 already reset the content position; the caller decides how to recover.
            SyncErrorFrom(Manager);
            m_logger.LogWarning("Failed to read the set header block for set #{Set}", TOC.CurrentSetIndex);
            return null;
        }

        return TapeHeaderBlock.Classify(buffer, read) as TapeSetHeader;
    }

    /// <summary>
    /// Classifies <paramref name="header"/> against what the TOC expects for
    ///  <see cref="TapeTOC.CurrentSetIndex"/>. Pure — no I/O, no state change — so the ladder is unit
    ///  testable without a tape.
    ///  <para>
    ///  The check is skipped if <c>TOC.MediaId</c> is <see cref="Guid.Empty"/> (imported / legacy media).
    ///  </para>
    /// </summary>
    /// <remarks>
    /// Checked in order of what each field can tell us: identity first (is this even the right
    ///  cartridge?), then position (are we where we think we are?). Reversing the order would make a
    ///  swapped cartridge look like a navigation miscount and invite a correction that cannot help.
    /// </remarks>
    internal TapeSetHeaderVerdict ClassifySetHeader(TapeSetHeader? header)
    {
        if (header is null)
            return TapeSetHeaderVerdict.Unreadable;

        // A TOC with no media id predates identity stamping (imported / legacy). Comparing against
        //  Guid.Empty would fail every such restore, so the identity check is skipped — the positional
        //  checks below still apply, and they are the ones that carry the feature.
        if (TOC.MediaId != Guid.Empty && header.MediaId != TOC.MediaId)
            return TapeSetHeaderVerdict.WrongMedia;

        if (header.Volume != TOC.Volume)
            return TapeSetHeaderVerdict.WrongVolume;

        if (header.VolumeSetIndex != TOC.CurrentSetIndexOnVolume)
            return TapeSetHeaderVerdict.SetIndexDrift;

        return TapeSetHeaderVerdict.Match;
    }

    /// <summary>
    /// Reads, classifies and verifies the set header for the set just positioned at, recovering from a
    ///  mis-navigation where it can. Returns <see langword="false"/> only when the set must not be used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Preconditions (SH-7).</b> Called with the head at the set's first block and with NO packer of
    ///  either kind alive — immediately after <c>Manager.BeginReadContent()</c> and strictly before the
    ///  first <c>BeginPackedFileRead</c> on the read path, or in <c>MediaPrepared</c> before a
    ///  destructive write.
    /// </para>
    /// <para>
    /// <b>Two recovery stages (SH-14), in this order:</b>
    /// <list type="number">
    /// <item><description>
    /// <b>The relative delta</b> (<see cref="CorrectSetNavigation"/>, SH-10) — available whenever a
    ///  HEALTHY set header was read. It is the stronger move because it is EARNED: a framed, CRC-checked
    ///  record whose identity matches is the most trustworthy fact obtainable on a cartridge whose marks
    ///  no longer agree with the TOC, and it states where the head physically is.
    /// </description></item>
    /// <item><description>
    /// <b>The renavigation from begin-of-content</b> — available only when the failed navigation counted
    ///  BACKWARD. It addresses a different fault: not a miscounted mark but a wrong counting DIRECTION.
    ///  Damage from a backup that died mid-set accumulates at the TAIL, so a backward count from EOD
    ///  crosses the damaged region while a forward count from BOM traverses only the healthy part. The
    ///  second pass is a full verification, so it gets its own delta stage.
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>The anchor is captured BEFORE anything moves.</b>
    ///  <see cref="TapeNavigator.ReconcileContentSetAndMove"/> assigns a non-negative
    ///  <c>TargetContentSet</c> as part of correcting, so reading the anchor afterwards would report
    ///  "begin-anchored" for every navigation that had reached stage 1 — silently disabling stage 2 in
    ///  exactly the scenario it exists for.
    /// </para>
    /// <para>
    /// <b>Identical on every agent.</b> A restore's failure costs time where a delete's costs data, but
    ///  that is an argument about consequences, not about whether the repair works. Only the TERMINAL
    ///  action differs, and only for <c>Unreadable</c> (<see cref="BlocksOnUnverifiableSet"/>).
    /// </para>
    /// <para>
    /// On success the head sits ONE BLOCK PAST the set start — the verifying read consumed the header
    ///  block — whether or not a recovery moved it. Callers that must write there derive the set start
    ///  as <c>Drive.CurrentBlock - 1</c> AFTERWARDS (SH-15); a block captured before the call is stale
    ///  once a recovery has repositioned.
    /// </para>
    /// </remarks>
    /// <param name="fileNotify">Optional callback, for the set-level anomaly channel.</param>
    protected bool VerifySetHeaderForCurrentSet(ITapeFileNotifiable? fileNotify)
    {
        // Capture the anchor NOW: stage 1 rewrites TargetContentSet (see the remarks).
        bool endAnchored = Navigator.TargetContentSet < 0;
        m_renavigatedFromBom = false;

        var header = ReadSetHeader();
        var verdict = ClassifySetHeader(header);

        // A renavigation is available only for POSITIONAL verdicts, only from a backward count, and only
        //  when corrections are permitted at all. Identity verdicts are excluded here rather than inside
        //  the ladder, so the ladder never has to ask why it was called.
        bool canRenavigate = endAnchored
            && CorrectsSetNavigation
            && verdict is TapeSetHeaderVerdict.SetIndexDrift or TapeSetHeaderVerdict.Unreadable;

        var outcome = HandleSetHeaderVerdict(verdict, header, recoveryAvailable: canRenavigate, fileNotify);
        if (outcome == SetVerdictOutcome.Proceed)
            return true;
        if (outcome == SetVerdictOutcome.Terminal)
            return false;

        // ── Stage 2 (SH-14) ──────────────────────────────────────────────
        Debug.Assert(canRenavigate, "Recoverable outcome requires an available stage");

        if (IsAbortRequested)
            return false;           // the user declined at the prompt; do not move the head

        m_logger.LogWarning(
            "Set #{Set}: the backward count did not settle — renavigating forward from begin-of-content. " +
            "This indicates the TAIL of the volume is unreliable",
            TOC.CurrentSetIndex);

        // SH-19. The navigator does NOT always reset itself: when the delta MOVED successfully but the
        //  re-verify still disagreed, CurrentContentSet holds the target it believes it reached. Without
        //  this reset the re-target below equals that belief, MoveToTargetContentSet trips its SH-4
        //  idempotence check, and the whole stage becomes a silent no-op. The reset also satisfies the
        //  TapeNavigatorTOCInSet fast path (CurrentContentSet < 0), making this a rewind plus one merged
        //  forward space on the filemark layouts.
        Navigator.ResetContentSet();
        Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet(fromBeginOnly: true);
        m_renavigatedFromBom = true;

        if (!Navigator.MoveToTargetContentSet())
        {
            SyncErrorFrom(Navigator);
            m_logger.LogError("Failed to renavigate from begin-of-content for set #{Set}", TOC.CurrentSetIndex);
            // Do NOT LatchFailure() here: Upon failed navigation we've done SetErrorFrom(Navigator), and
            //  the caller will latch it
            return false;
        }

        // A full second verification — including its own delta stage. recoveryAvailable is false, so the
        //  terminal actions of §5.5 finally apply.
        header = ReadSetHeader();
        verdict = ClassifySetHeader(header);

        if (HandleSetHeaderVerdict(verdict, header, recoveryAvailable: false, fileNotify)
                != SetVerdictOutcome.Proceed)
            return false;

        // Settled by the renavigation. Report it as its own stage: a drift the DELTA fixed indicts a
        //  mark, one that needed the renavigation indicts the tail — different facts about the cartridge,
        //  and the second is the one that argues for retiring it (SH-16).
        if (verdict == TapeSetHeaderVerdict.Match)
        {
            NotifySetAnomalyRecovered(fileNotify,
                BuildSetAnomaly(TapeSetHeaderVerdict.SetIndexDrift, header,
                    TapeSetAnomalyStage.Renavigated, canAttemptRecovery: false,
                    diagnosis: TapeResult.OK)); // <- sic!
                                                // diagnosis: OK — this payload reports the set's state NOW, and the set is now sound. The FAULT
                                                //  was already recorded in the detection anomaly the prompt carried; repeating it here would
                                                //  say "recovered, but still broken".            

            ResetError();   // repaired — not this operation's error; the payload's Diagnosis reads OK
        }
        return true;
    }

    /// <summary>
    /// Logs, records the error, and raises the anomaly — in that order, so the payload's
    ///  <see cref="TapeSetAnomaly.Diagnosis"/> carries the real code and message rather than whatever
    ///  happened to be current. Returns the notifiable's answer.
    /// </summary>
    private SetAnomalyAction RaiseSetAnomaly(TapeSetHeaderVerdict verdict, TapeSetHeader? header,
        ITapeFileNotifiable? fileNotify, WIN32_ERROR error, string message, bool canAttemptRecovery = false)
    {
        m_logger.LogError("Set #{Set}: {Message}", TOC.CurrentSetIndex, message);
        SetError(error, message);

        // Which stage OBSERVED this — the trail's whole value is telling a first sighting apart from a
        //  recovery that failed to settle. The two flags between them name the pass we are in.
        var stage = m_renavigatedFromBom ? TapeSetAnomalyStage.Renavigated
                  : m_correctingSetNavigation ? TapeSetAnomalyStage.Delta
                  : TapeSetAnomalyStage.Detected;

        return NotifySetAnomaly(fileNotify, BuildSetAnomaly(verdict, header, stage, canAttemptRecovery));
    }

    /// <summary>
    /// <see cref="RaiseSetAnomaly"/> for the verdicts that offer no choice: the answer cannot change the
    ///  outcome, so it is discarded — though an <c>Abort</c> still records
    ///  <see cref="IsAbortRequested"/>, which is what turns the diagnosis into <c>ERROR_CANCELLED</c>.
    /// </summary>
    private bool RejectSet(TapeSetHeaderVerdict verdict, TapeSetHeader? header,
        ITapeFileNotifiable? fileNotify, WIN32_ERROR error, string message)
    {
        RaiseSetAnomaly(verdict, header, fileNotify, error, message, canAttemptRecovery: false);

        if (BlocksOnUnverifiableSet)
            _stats.Sets.SetWriteBlocked = true; // a destructive write was actually refused
        return false;
    }

    /// <summary>
    /// Acts on a set-header verdict: proceeds, repairs, or refuses — and surfaces anything that is not a
    ///  clean <see cref="TapeSetHeaderVerdict.Match"/> through the set-level notification channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="VerifySetHeaderForCurrentSet"/> so both the delta correction and the
    ///  renavigation can re-enter it with a freshly read header. That re-entry is the whole reason this
    ///  method exists as a unit: every failure mode's log line, error code, message and notification are
    ///  defined here EXACTLY ONCE, so a recovery that fails to settle reports the same diagnosis as a
    ///  first-pass failure of the same kind.
    /// </para>
    /// <para>
    /// <b>Order within each arm: log → <c>SetError</c> → notify.</b> The anomaly payload's
    ///  <see cref="TapeSetAnomaly.Diagnosis"/> snapshots the agent's current error, so the error must be
    ///  set BEFORE the prompt — otherwise the host is asked to decide while holding a stale diagnosis.
    ///  <see cref="RaiseSetAnomaly"/> enforces the order; no arm sets an error by hand.
    /// </para>
    /// <para>
    /// <b><paramref name="recoveryAvailable"/> defers the terminal action.</b> With a stage still untried,
    ///  an unsettled verdict returns <see cref="SetVerdictOutcome.Recoverable"/> — including
    ///  <c>Unreadable</c>, which on the read path would otherwise proceed unverified and never reach the
    ///  renavigation that is its ONLY recovery.
    /// </para>
    /// <para>
    /// <b>The read/write split lives in one place:</b> <see cref="BlocksOnUnverifiableSet"/>, consulted
    ///  only at the terminal. Everything above it is identical on every path (SH-14).
    /// </para>
    /// </remarks>
    /// <param name="verdict">The classification from <see cref="ClassifySetHeader"/>.</param>
    /// <param name="header">The header that produced it; <see langword="null"/> when unreadable.</param>
    /// <param name="recoveryAvailable">Whether a recovery stage remains untried for this verdict.</param>
    /// <param name="fileNotify">Optional callback, for the set-level anomaly channel.</param>
    private SetVerdictOutcome HandleSetHeaderVerdict(TapeSetHeaderVerdict verdict, TapeSetHeader? header,
        bool recoveryAvailable, ITapeFileNotifiable? fileNotify)
    {
        SetVerdictOutcome TerminalHere()
        {
            if (BlocksOnUnverifiableSet)
                _stats.Sets.SetWriteBlocked = true; // RejectSet() sets this, too, but just to be sure
            return SetVerdictOutcome.Terminal;
        }

        switch (verdict)
        {
            case TapeSetHeaderVerdict.Match:
                m_logger.LogTrace("Set header verified for set #{Set}: {Header}",
                    TOC.CurrentSetIndex, header);

                // Advisory only (SH-11): logged on mismatch, never gating, never overriding the TOC.
                if (header!.GlobalSetIndex != TOC.CurrentSetIndex)
                    m_logger.LogWarning(
                        "Set header attribution differs for set #{Set}: header says global index {Actual}. " +
                        "Advisory only — attribution can legitimately shift after a TOC import",
                        TOC.CurrentSetIndex, header.GlobalSetIndex);

                if (header.SetBlockSize != TOC.CurrentSetTOC.BlockSize)
                    m_logger.LogWarning(
                        "Set header block size differs for set #{Set}: header says {Actual} B, TOC says {Expected} B. " +
                        "Advisory only — the TOC stays authoritative",
                        TOC.CurrentSetIndex, header.SetBlockSize, TOC.CurrentSetTOC.BlockSize);
                return SetVerdictOutcome.Proceed;

            case TapeSetHeaderVerdict.Unreadable:
                // Stage 2 is this verdict's ONLY recovery — there is no healthy header to believe, so no
                //  delta. A block that fails to classify is also the exact symptom of having counted into
                //  a damaged tail, which is what the renavigation escapes.
                if (recoveryAvailable)
                {
                    RaiseSetAnomaly(verdict, header, fileNotify, WIN32_ERROR.ERROR_INVALID_DATA,
                        "set header could not be read or classified — renavigating before deciding",
                        canAttemptRecovery: true);
                    return IsAbortRequested ? SetVerdictOutcome.Terminal : SetVerdictOutcome.Recoverable;
                }

                // ── Terminal ──
                //  A destructive write positions by COUNTING MARKS, so an unclassifiable block at the
                //   presumed set start is the miscount's own signature — not a lost safety net.
                if (BlocksOnUnverifiableSet)
                {
                    RejectSet(verdict, header, fileNotify, WIN32_ERROR.ERROR_INVALID_DATA,
                        "set header could not be read or classified — refusing a destructive write at " +
                        "an unconfirmed position");
                    return TerminalHere();
                }

                // The golden rule: a record we cannot verify never blocks a READ. Files position
                //  absolutely, so proceeding costs a safety net and nothing more.
                m_logger.LogWarning(
                    "Set header for set #{Set} could not be read or classified; proceeding unverified",
                    TOC.CurrentSetIndex);

                // An I/O-level failure reset the content position (SH-6). Leaving it Unknown would
                //  corrupt the later EndReadContentSet advance, so re-anchor before proceeding.
                if (Navigator.CurrentContentSet == TapeNavigator.UnknownSet)
                {
                    m_logger.LogTrace("Re-anchoring the navigator after an unreadable set header");
                    if (!Navigator.MoveToTargetContentSet())
                    {
                        SyncErrorFrom(Navigator);
                        m_logger.LogWarning("Failed to re-anchor after an unreadable set header for set #{Set}",
                            TOC.CurrentSetIndex);
                        return TerminalHere();
                    }
                }

                ResetError();   // the failed read is not this operation's error
                return SetVerdictOutcome.Proceed;

            case TapeSetHeaderVerdict.WrongMedia:
                // Every in-memory assumption is void, the TOC included — nothing is correctable, and
                //  moving the head on a foreign cartridge is the last thing anyone wants.
                RejectSet(verdict, header, fileNotify, WIN32_ERROR.ERROR_INVALID_DATA,
                    $"media identity mismatch — header carries media id {header!.MediaId:N}, expected " +
                    $"{TOC.MediaId:N}; the cartridge appears to have been swapped mid-operation");
                return TerminalHere();

            case TapeSetHeaderVerdict.WrongVolume:
                // File addresses are physical-per-volume, so every address in the TOC would resolve to
                //  garbage on this volume.
                RejectSet(verdict, header, fileNotify, WIN32_ERROR.ERROR_INVALID_DATA,
                    $"volume mismatch — tape carries volume {header!.Volume}, expected volume {TOC.Volume}");
                return TerminalHere();

            case TapeSetHeaderVerdict.SetIndexDrift:
                // Identity is confirmed, so the disagreement is POSITIONAL — the one verdict a
                //  repositioning can actually repair (SH-9).
                bool canCorrect = CorrectsSetNavigation;

                // Composed for the TERMINAL case and set BEFORE the prompt, so the caller / host sees a real
                //  diagnosis while deciding. A stage that settles clears it again.
                var action = RaiseSetAnomaly(verdict, header, fileNotify, WIN32_ERROR.ERROR_INVALID_DATA,
                    $"set navigation drift — positioned at on-volume set {header!.VolumeSetIndex}, " +
                    $"expected {TOC.CurrentSetIndexOnVolume} " +
                    $"(delta {TOC.CurrentSetIndexOnVolume - header.VolumeSetIndex})" +
                    (canCorrect ? "" : " — correction DISABLED"),
                    canAttemptRecovery: canCorrect || recoveryAvailable);

                // Asking BEFORE acting means "correction impossible" and "correction declined" converge
                //  on one code path rather than two.
                if (action != SetAnomalyAction.Proceed)
                    return TerminalHere();     // the user declined; do not move the head

                // Stage 1: believe the header and move the delta.
                if (canCorrect && CorrectSetNavigation(header, recoveryAvailable, fileNotify))
                    return SetVerdictOutcome.Proceed;

                // Unsettled. Stage 2 may still rescue it; otherwise the error above stands.
                return recoveryAvailable ? SetVerdictOutcome.Recoverable : TerminalHere();

            case TapeSetHeaderVerdict.NotExpected:
            default:
                return SetVerdictOutcome.Proceed;   // unreachable: the caller gates on SetHeadersExpected
        }
    }

    /// <summary>
    /// Stage 1 of the recovery: adopt the position the set header proves, move the remaining delta, and
    ///  re-verify. Returns <see langword="true"/> when the head is confirmed at the intended set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Relative, never absolute (SH-10).</b> If navigation reached the wrong set while aiming at the
    ///  right one, re-navigating from the SAME anchor would reproduce the miscount exactly — the fault is
    ///  in the physical mark structure, not the arithmetic. Only the delta exploits the new information.
    ///  (Stage 2 changes the ANCHOR, which is a different move for a different fault — see
    ///  <see cref="VerifySetHeaderForCurrentSet"/>.)
    /// </para>
    /// <para>
    /// <b>Bounded to one retry.</b> Correct, re-read, re-verify. On a second disagreement this stage is
    ///  spent: a tape whose mark structure defeats a short relative move is inconsistent rather than
    ///  noisy. The bound is PER POSITION, not per operation — stage 2 reaches a new position by a
    ///  different route and legitimately gets a fresh delta attempt, which is why
    ///  <see cref="m_correctingSetNavigation"/> is released by its <c>finally</c>.
    /// </para>
    /// <para>
    /// Logged at Warning, never Trace: a successfully corrected drift means the drive or the medium
    ///  miscounted marks — a tape that corrects on every set is a tape to retire (SH-16).
    /// </para>
    /// </remarks>
    /// <param name="header">
    /// The positively classified header just read — identity already confirmed by
    ///  <see cref="ClassifySetHeader"/>, which is what makes the drift interpretable as positional (SH-9).
    /// </param>
    /// <param name="recoveryAvailable">Passed through, so a re-verify failure defers its terminal action.</param>
    /// <param name="fileNotify">Optional callback, for the set-level anomaly channel.</param>
    private bool CorrectSetNavigation(TapeSetHeader header, bool recoveryAvailable, ITapeFileNotifiable? fileNotify)
    {
        int actual = header.VolumeSetIndex;
        int expected = TOC.CurrentSetIndexOnVolume;

        if (m_correctingSetNavigation)
        {
            // Second disagreement within one correction — this stage is spent.
            m_logger.LogError(
                "Set navigation could not be corrected for set #{Set}: after correcting, the header still " +
                "reports on-volume set {Actual}, expected {Expected}",
                TOC.CurrentSetIndex, actual, expected);
            SetError(WIN32_ERROR.ERROR_INVALID_DATA,
                $"Set navigation could not be corrected for set #{TOC.CurrentSetIndex}");
            return false;
        }

        m_logger.LogWarning(
            "Set navigation drift at set #{Set}: navigator reported on-volume set {Expected}, header says " +
            "{Actual}; correcting by {Delta}. This indicates the drive or the medium miscounted marks",
            TOC.CurrentSetIndex, expected, actual, expected - actual);

        if (!Navigator.ReconcileContentSetAndMove(actual, expected))
        {
            SyncErrorFrom(Navigator);
            m_logger.LogError("Failed to reposition while correcting set navigation for set #{Set}",
                TOC.CurrentSetIndex);
            return false;
        }

        // Re-verify exactly once. The guard makes any further drift terminal rather than recursive.
        m_correctingSetNavigation = true;
        try
        {
            var againHeader = ReadSetHeader();
            var verdict = ClassifySetHeader(againHeader);

            if (verdict != TapeSetHeaderVerdict.Match)
            {
                m_logger.LogError(
                    "Set navigation correction for set #{Set} did not settle: re-verification returned {Verdict}",
                    TOC.CurrentSetIndex, verdict);
                // Route through the ladder so each failure mode keeps its own diagnosis and error code.
                //  A drift here re-enters this method, which the guard turns into a clean stop.
                return HandleSetHeaderVerdict(verdict, againHeader, recoveryAvailable, fileNotify)
                    == SetVerdictOutcome.Proceed;
            }

            // Stage is reported by WHICH pass settled it: inside a renavigated pass this delta belongs to
            //  stage 2, and the tail — not a single mark — is what the user needs to hear about (SH-16).
            m_logger.LogWarning("Set navigation corrected — now positioned at on-volume set {Set} (set #{Global})",
                expected, TOC.CurrentSetIndex);

            // Report BEFORE ResetError(): the payload's Diagnosis names the fault that was REPAIRED, which is
            //  the whole content of a recovery notification. Clearing first would hand the host a failure
            //  with no code and no message.
            NotifySetAnomalyRecovered(fileNotify,
                BuildSetAnomaly(TapeSetHeaderVerdict.SetIndexDrift, againHeader,
                    m_renavigatedFromBom ? TapeSetAnomalyStage.Renavigated : TapeSetAnomalyStage.Delta,
                    canAttemptRecovery: false, diagnosis: TapeResult.OK)); // <- sic!
            // ^ diagnosis: OK — this payload reports the set's state NOW, and the set is now sound. The FAULT
            //  was already recorded in the detection anomaly the prompt carried; repeating it here would
            //  say "recovered, but still broken".

            ResetError();   // the drift was repaired — it is not this OPERATION's error
            return true;
        }
        finally
        {
            m_correctingSetNavigation = false;
        }
    }

    #endregion // *** Set header verification ***

    #region ** Set header positional correction ***

    /// <summary>
    /// Whether <paramref name="error"/> says "there is no more tape that way" — i.e. the navigation ran
    ///  out of marks or medium, rather than the drive or the cartridge having failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate for SH-20. Testing <c>WentBad</c> alone would send a dead drive, an ejected cartridge or a
    ///  bus reset on a full-length rewind before surfacing the real error — a pointless transport pass
    ///  that also buries the diagnosis the user needs.
    /// </para>
    /// <para>
    /// Each code here means the mark structure is SHORTER or otherwise different from what the count
    ///  assumed, which is precisely the damaged-tail signature a forward count from BOM escapes.
    /// </para>
    /// </remarks>
    private static bool IsPositionalNavigationError(WIN32_ERROR error) => error is
        WIN32_ERROR.ERROR_NO_DATA_DETECTED      // ran past EOD hunting a mark that is not there
        or WIN32_ERROR.ERROR_END_OF_MEDIA       // the same, physically
        or WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA // counted back past BOM — too many marks demanded
        or WIN32_ERROR.ERROR_FILEMARK_DETECTED  // the structure is not what the count assumed
        or WIN32_ERROR.ERROR_SETMARK_DETECTED;

    /// <summary>
    /// Navigates to <see cref="TapeNavigator.TargetContentSet"/>, retrying ONCE from begin-of-content when
    ///  a BACKWARD count fails with a positional error (SH-20). Returns <see langword="false"/> only when
    ///  the head could not be placed at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failed-navigation twin of SH-14's stage 2.</b> That stage repairs a navigation that COMPLETED
    ///  and landed wrong; this one repairs a navigation that never completed. Both are the same physical
    ///  fault — a tail whose mark structure no longer matches the TOC — reported through different
    ///  channels, so both get the same cure: count forward from begin-of-content, across the healthy part
    ///  of the volume only.
    /// </para>
    /// <para>
    /// <b>Gated on the ERROR CODE, not on failure.</b> See <see cref="IsPositionalNavigationError"/>: a
    ///  drive that went offline fails to move too, and a rewind cannot help it.
    /// </para>
    /// <para>
    /// <b>Bounded structurally.</b> The retry targets a non-negative index, so its own end-anchored
    ///  precondition is false and it cannot recurse — the same argument that bounds stage 2.
    /// </para>
    /// <para>
    /// Sets <see cref="m_renavigatedFromBom"/> on the retry, so the verification that follows attributes
    ///  any anomaly it finds to <see cref="TapeSetAnomalyStage.Renavigated"/> — correct, because the tail
    ///  is what just proved unreliable.
    /// </para>
    /// </remarks>
    /// <param name="fileNotify">Optional callback, for the set-level anomaly channel.</param>
    protected bool NavigateToTargetContentSet(ITapeFileNotifiable? fileNotify)
    {
        // Capture the anchor BEFORE moving: a failed move resets the navigator, and the retry below
        //  rewrites TargetContentSet — so afterwards there is no way to tell which way we counted.
        bool endAnchored = Navigator.TargetContentSet < 0;

        // Whether the navigator settled at begin-of-content instead of completing a backward count.
        //  The head is then PROVEN to be at set 0 — which makes the forward retry below cheap.
        bool settledAtBom = false;

        if (Navigator.MoveToTargetContentSet())
        {
            // A backward count that reports 0 means OnMovedIntoBom settled it: the navigator ran into
            //  BOM and reported the only index it can PROVE. It cannot tell "arrived at the oldest set"
            //  from "the volume holds fewer sets than the count demanded" — that needs the set
            //  accounting, which THIS layer has.
            if (!endAnchored || Navigator.CurrentContentSet != 0)
                return true;    // ordinary success, nothing ambiguous

            if (TOC.CurrentSetIndexOnVolume == 0)
            {
                // The TOC confirms the target IS the volume's first set, so BOM is genuinely its
                //  leading boundary. Restore the end-anchored index rather than adopting the 0, so
                //  CurrentSetAsNavigatorContentSet() keeps anchoring later sets from the END as chosen.
                m_logger.LogTrace(
                    "Navigator settled at begin-of-content for requested target {Target}; the TOC confirms " +
                    "this IS the volume's first set — restoring the end-anchored index",
                    Navigator.TargetContentSet);
                Navigator.AssumeAtTargetContentSet();
                return true;
            }

            // The TOC says otherwise: we are at set 0 and the target is on-volume set
            //  TOC.CurrentSetIndexOnVolume > 0, so the head is DEMONSTRABLY at the wrong set. Same
            //  damaged-tail fault as a failed backward count (SH-20), merely reported as a successful
            //  navigation — so it takes the same route: prompt, then count forward from BOM.
            //  Returning true here would leave the manager to "fix" a head it has no authority over,
            //  bypassing the anomaly channel entirely.
            settledAtBom = true;
            SetError(WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA,
                $"Set #{TOC.CurrentSetIndex} could not be reached by counting back from end-of-content — " +
                "the count ran into begin-of-content, so the volume's tail is shorter than the TOC describes");
        }
        else
        {
            SyncErrorFrom(Navigator);
        }

        // ── SH-20: is this the damaged-tail signature, and is there another direction to try? ──
        if (!endAnchored || !CorrectsSetNavigation || !IsPositionalNavigationError(LastErrorWin32))
        {
            m_logger.LogWarning(
                "Failed to position at content set #{Set}; no recovery applies (endAnchored={Anchored}, error={Error})",
                TOC.CurrentSetIndex, endAnchored, LastErrorWin32);
            return false;
        }

        m_logger.LogWarning(
            "Set #{Set}: the backward seek {Outcome} ({Error}) — considering a renavigation forward from " +
            "begin-of-content. This indicates the TAIL of the volume is unreliable",
            TOC.CurrentSetIndex, settledAtBom ? "ran into BOM" : "did not even complete", LastErrorWin32);

        var action = RaiseSetAnomaly(TapeSetHeaderVerdict.Unreadable, header: null, fileNotify,
            LastErrorWin32, $"Set #{TOC.CurrentSetIndex} " +
            $"could not be reached by seeking back from end-of-content ({LastErrorWin32}) — " +
            "the volume's tail seems shorter than the TOC describes",
            canAttemptRecovery: true);

        if (action != SetAnomalyAction.Proceed)
        {
            m_logger.LogWarning("Renavigation declined for set #{Set}", TOC.CurrentSetIndex);
            return false;
        }

        // SH-19: a FAILED navigation already left CurrentContentSet at UnknownSet, but saying so keeps
        //  the precondition local — and satisfies the TapeNavigatorTOCInSet fast path, which needs
        //  CurrentContentSet < 0 to merge the rewind with the forward space.
        //  The settled-at-BOM case is different: the head is PROVEN to be at set 0, so keeping that
        //  lets the forward count start from where we stand — no rewind, just the remaining hops.
        if (!settledAtBom)
            Navigator.ResetContentSet();

        Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet(fromBeginOnly: true);
        m_renavigatedFromBom = true;

        ResetError();   // give the retry a clean slate; the first error is superseded either way

        if (!Navigator.MoveToTargetContentSet())
        {
            SyncErrorFrom(Navigator);
            m_logger.LogError(
                "Failed to renavigate from begin-of-content for set #{Set} — the set is unreachable " +
                "from either direction",
                TOC.CurrentSetIndex);
            return false;
        }

        NotifySetAnomalyRecovered(fileNotify,
            BuildSetAnomaly(TapeSetHeaderVerdict.Unreadable, header: null,
                TapeSetAnomalyStage.Renavigated, canAttemptRecovery: false,
                diagnosis: TapeResult.OK));

        return true;
    }

    #endregion // *** Set header positional correction ***

}
