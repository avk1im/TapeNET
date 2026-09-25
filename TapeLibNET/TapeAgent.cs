using System.ComponentModel;
using System.IO.Hashing;
using System.Diagnostics;
using Windows.Win32.Foundation;
using Microsoft.Extensions.Logging;

namespace TapeLibNET;


/// <summary>
/// Base agent handling TOC backup/restore (dual-copy with CRC), TOC file I/O,
///  set deletion, and the <see cref="ITapeFileNotifiable"/> notification wrappers.
/// <para>Subclasses: <see cref="TapeFileBackupAgent"/> (backup), <see cref="TapeFileRestoreBaseAgent"/>
///  (restore/verify). Owns a <see cref="TapeStreamManager"/> and a <see cref="TapeTOC"/>.</para>
/// </summary>
public class TapeFileAgent : TapeDriveHolder<TapeFileAgent>, IDisposable
{
    /// <summary>BlockSize used for header and TOC read / write, fixed since it needs to be known upfront.</summary>
    private const uint c_fixedTOCBlockSize = 16 * 1024; // 16 KiB

    /// <summary>Hashing for TOC, fixed since it needs to be known upfront for each tape.</summary>
    private readonly TapeHashAlgorithm c_hashForTOC = TapeHashAlgorithm.Crc64;

    #region Properties

    /// <summary>Table of contents for this tape session.</summary>
    public TapeTOC TOC { get; init; }
    /// <summary>
    /// Where the TOC is stored on tape, determined based on the type of <see cref="Navigator"/> in use.
    /// </summary>
    public TapeTocPlacement TOCPlacement =>
        Navigator is TapeNavigatorTOCInPartition ? TapeTocPlacement.InPartition : TapeTocPlacement.InSet;
    /// <summary>Stream manager providing state-guarded read/write stream provisioning.</summary>
    public TapeStreamManager Manager { get; init; }
    /// <summary>Shortcut to <see cref="Manager"/>.<see cref="TapeStreamManager.Navigator"/>.</summary>
    public TapeNavigator Navigator => Manager.Navigator;

    /// <summary>Cumulative bytes written to tape (content + TOC) during this agent's lifetime.</summary>
    public long BytesBackedup { get; protected set; } = 0L;
    /// <summary>Cumulative bytes read from tape (content + TOC) during this agent's lifetime.</summary>
    public long BytesRestored { get; protected set; } = 0L;


    /// <summary>
    /// Cumulative file-operation statistics. Updated by the Notify* methods;
    /// a snapshot is passed to every <see cref="ITapeFileNotifiable"/> callback.
    /// </summary>
    protected TapeFileStatistics _stats;
    /// <summary>Read-only reference to the current statistics.</summary>
    public ref readonly TapeFileStatistics Statistics => ref _stats;

    /// <summary>
    /// Refreshes <see cref="TapeFileStatistics.BytesTotal"/> from whatever total-size estimation
    ///  mechanism the derived agent uses. Called before every <see cref="ITapeFileNotifiable"/>
    ///  notification so subscribers always see the latest estimate.
    /// </summary>
    /// <remarks><see cref="TapeFileBackupAgent"/> overrides this to pull from a background
    ///  <see cref="FileSizeAggregator"/> (source files are scanned concurrently with the backup).
    ///  Restore doesn't need to override it: all file sizes are known upfront from the TOC, so
    ///  <see cref="TapeFileRestoreBaseAgent"/> sets <see cref="TapeFileStatistics.BytesTotal"/>
    ///  once, synchronously, before the notification loop begins.
    /// </remarks>
    protected virtual void RefreshBytesTotalEstimate()
    {
        // no-op by default; overridden by TapeFileBackupAgent
    }

    /// <summary>
    /// Abort flag checked periodically by file-processing loops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Records that an abort was requested, by whatever channel.</b> The caller may set it directly
    ///  (UI abort button, <c>Ctrl+C</c> bridge); <see cref="NotifyFileFailed"/> sets it when the callback returns
    ///  <see cref="FileFailedAction.Abort"/>; and the catch handlers set it when a callback throws
    ///  <see cref="TapeAbortRequestedException"/> — the only way a void notification CAN request an abort.
    /// </para>
    /// <para>
    /// Uniformity matters beyond tidiness: <see cref="FailedOperationResult"/> reads this flag to report
    ///  <c>ERROR_CANCELLED</c> rather than a generic failure, and the service reads it to classify the
    ///  operation as aborted rather than failed. A request that arrives by exception is the same user
    ///  decision as one that arrives by flag, and must produce the same diagnosis.
    /// </para>
    /// <para>
    /// A plain auto-property suffices despite cross-thread writes: every reader reaches it through a call
    ///  that already crosses a memory barrier (tape I/O, lock acquisition, or a notification hop), and a
    ///  momentarily stale read costs at most one extra file before the next check. The abort is
    ///  cooperative, not real-time.
    /// </para>
    /// </remarks>
    public bool IsAbortRequested { get; set; }

#if DEBUG
    /// <summary>
    /// Simulates file operation failures for testing error handling.
    /// Instance-level so concurrent agents (or parallel tests) don't interfere.
    /// Replaces the former SimulateFailures / FailEveryNthFile / SimulatedFailureCounter fields.
    /// </summary>
    public FailureSimulator SimulateFileFailures { get; } = new();

    /// <summary>
    /// Bitmask controlling which TOC copies fail during backup/restore.
    /// Bit 0 (value 1) = 1st copy fails, bit 1 (value 2) = 2nd copy fails.
    /// 0 = no simulation, 3 = both copies fail.
    /// Instance-level so concurrent agents don't interfere.
    /// </summary>
    public int SimulateTOCFailureMask { get; set; } = 0;

    /// <summary>
    /// Tracks which TOC copy is being processed (0 = 1st, 1 = 2nd).
    /// Reset at the start of <see cref="BackupTOC"/> and <see cref="RestoreTOC"/>.
    /// </summary>
    protected int _tocCopyCounter = 0;
#endif

    /// <summary>
    /// Translates <see cref="TapeTOC.CurrentSetIndex"/> to a <see cref="TapeNavigator.TargetContentSet"/> value,
    ///  choosing the most efficient seek direction (from begin, end, or current position).
    ///  <para>Used by both backup and restore agents.</para>
    /// </summary>
    protected int CurrentSetAsNavigatorContentSet(bool fromBeginOnly = false)
    {
        int toBegin = TOC.CurrentSetIndexOnVolume; // same as TOC.CurrentSetIndex - TOC.FirstSetOnVolume

        if (toBegin == 0)
            return 0; // the first content set on volume should always be accessed from the beginning

        if (fromBeginOnly)
            return toBegin;

        // Optimization: consider Navigator's current position when chosing how to specify the content set for Navigator
        int toCurr; // use to determine if current set is closer to Navigator's current position than to begin or end
        if (!Navigator.CurrentContentSetIsSentinel)
        {
            // translate Navigator.CurrentContentSet to the index on volume
            int navCurr = (Navigator.CurrentContentSet >= 0) ? Navigator.CurrentContentSet :
                TOC.SetIndexToStd(Navigator.CurrentContentSet + 2) - TOC.FirstSetOnVolume; // consider (-2)-based index
            Debug.Assert(navCurr >= 0);

            toCurr = Math.Abs(navCurr - toBegin); // notice here toBegin == TOC.CurrentSetIndexOnVolume
        }
        else
            toCurr = int.MaxValue;

        int toEnd = TOC.LastSetOnVolume - TOC.CurrentSetIndex;

        if (toCurr <= toBegin && toCurr <= toEnd)
        {
            // do NOT use toCurr directly, much rather retain the sign of Navigator.CurrentContentSet
            //  to ensure that Navigator will move based on Navigator.CurrentContentSet:
            //  if it was 0 or positive, continue counting from the beginning, if negative - from the end
            return (Navigator.CurrentContentSet >= 0)? toBegin : -2 - toEnd; // remember (-2)-based index
        }

        // if current set is closer to end of content, return (-2)-based index (-1 means end of content)
        return (toEnd < toBegin) ? -2 - toEnd : toBegin;
}

    #endregion

    #region Constructors

    public TapeFileAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : base(drive)
    {
        _resultBuilder = new(this);
        TOC = legacyTOC ?? [];
        Manager = new(drive);
    }

    #endregion

    #region IDisposable and destructor

    // implement IDisposable - do not override
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public bool IsDisposed { get; private set; } = false;

    // overridable IDisposable implementation via virtual Dispose(bool)
    protected virtual void Dispose(bool disposing)
    {
        if (!IsDisposed)
        {
            m_logger.LogTrace("Disposing TapeFileAgent with disposing parameter = {Parametr}", disposing);

            if (disposing)
            {
                // dispose managed resources
            }
            // dispose unmanaged resources
            // no umanaged resources

            IsDisposed = true;
        }
    }

    // do not override
    ~TapeFileAgent()
    {
        Dispose(disposing: false);
    }

    #endregion

    #region *** Error latching and result building

    /// <summary>
    /// Builder for the current file-operation result latching on the first error.
    /// </summary>
    private readonly TapeResultBuilder _resultBuilder;
    /// <summary>
    /// Latches the first failure encountered during a multi-step operation. Call whenever a failure
    ///  is detected, AFTER the error on <see langword="this"/> has been set (e.g. via <see cref="ErrorManageableBase.SetError"/>).
    /// </summary>
    protected void LatchFailure() => _resultBuilder.LatchFailure();
    /// <summary>
    /// Clears the latched failure. Call at the start of a multi-step operation to reset the latch.
    /// </summary>
    protected void ResetLatchedFailure() => _resultBuilder.Reset();
    /// <summary>
    /// Build the result of the failed operation, considering first the history captured by
    ///  <see cref="_resultBuilder"/>, then the current error state of <see langword="this"/>.
    ///  Use everywhere to return a <see cref="TapeResult"/> from a failed multi-step operation
    ///  (instead of <c>TapeResult.Fail(this)</c>).
    /// </summary>
    protected TapeResult FailedOperationResult => _resultBuilder.BuildFailure(
        IsAbortRequested ? (uint)WIN32_ERROR.ERROR_CANCELLED : (uint)WIN32_ERROR.ERROR_INVALID_STATE,
        IsAbortRequested ? "Operation aborted by user request" : "Operation did not complete");

    /// <summary>
    /// Diagnosis of the current (or most recent) compound operation: its first failure, or
    ///  <see cref="TapeResult.OK"/> when none occurred.
    /// </summary>
    /// <remarks>
    /// <para>
    /// COMPLEMENTS the <see cref="TapeResult"/> returned by each public verb — it does not replace it.
    ///  The return value is a snapshot immune to later state changes; this property is LIVE and is reset
    ///  by the next verb that starts. Where they disagree, the return value wins.
    /// </para>
    /// <para>
    /// Chiefly useful for capturing a diagnosis mid-operation — e.g. before calling
    ///  <see cref="BackupTOC"/>, which resets the latch.
    /// </para>
    /// </remarks>
    public TapeResult LastResult => _resultBuilder.Result;

    #endregion

    #region *** Media Header ***

    /// <summary>
    /// Whether to auto-write a media header at the start of a new volume via <see cref="BackupInitialTOC"/>.
    /// Also defines whether <see cref="TapeFileBackupAgent"/> auto-writes header at the start of a new and continuation volumes.
    /// <para>Set to <see langword="true"/> at the construction by default.</para>
    /// </summary>
    public bool WritesMediaHeader { get; set; } = true;

    /// <summary>
    /// Writes the media header at BOM. Invoked from <see cref="BackupInitialTOC"/> on the
    ///  format / fresh-media path (when heading is requested), and from <see cref="TapeFileBackupAgent"/>
    ///  at the start of a fresh content set on a new volume. Builds the header via the TOC (the sole
    ///  header authority), frames it, pads it to the fixed header block, and hands it to the manager.
    /// </summary>
    public TapeResult WriteMediaHeader()
    {
        var header = TOC.CreateHeader(tocBlockSize: c_fixedTOCBlockSize, tapeTocPlacement: TOCPlacement,
            hasSetHeaders: WritesSetHeaders);
        byte[]? block = TapeHeaderBlock.Frame(header);      // pack + size guard + pad, single-point
        if (block is null)
        {
            m_logger.LogError("Media header frame exceeds the standard header block ({Bs} B)", TapeHeaderBlock.Size);
            SetError(WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER, "Media header too large for its block");
            return FailedOperationResult;
        }

        // Passing header.HasSetHeaders rather than WritesSetHeaders keeps the navigator describing what
        //  actually went on tape, even if the property were mutated between framing and writing.
        if (!Manager.WriteMediaHeaderBlock(block, setHeadersExpected: header.HasSetHeaders))
        {
            SyncErrorFrom(Manager);
            return FailedOperationResult;
        }

        m_logger.LogTrace("Media header written: {Header}", header);
        return TapeResult.OK;
    }


    /// <summary>
    /// Reads and classifies the BOM header, returning the polymorphic <see cref="TapeHeader"/> (media,
    ///  calibration, or null for legacy/blank/foreign) and caching presence on the navigator. One block read.
    /// </summary>
    /// <remarks>The service inspects the returned kind for its verdict; the navigator only learns
    ///  Present (a media header) vs Absent (anything else). Evaluation stays the service's job (D16).</remarks>
    public TapeHeader? ReadBomHeader()
    {
        var buffer = new byte[TapeHeaderBlock.Size];
        int read = Manager.ReadBomHeaderBlock(buffer);

        if (read <= 0)
        {
            // Reaching BOM and finding NO data is the blank / legacy / at-EOD case — a DEFINITIVE
            //  "no media header", i.e. Absent. It is NOT an unresolved state: nothing retries
            //  EnsureMediaHeaderResolved, so leaving Unknown wedges all later navigation
            //  (MoveToBeginOfContentFromBom rejects Unknown). Absent lets navigation proceed and
            //  skip nothing — exactly right for headerless media.
            Navigator.ResolveMediaHeaderPresence(TapeHeaderPresence.Absent);
            return null;
        }

        TapeHeader? header = TapeHeaderBlock.Classify(buffer, read);

        // A readable block that is NOT our media header (calibration / set / foreign / torn) is
        //  likewise "no media header here" for navigation = Absent; the service still learns the
        //  concrete kind. Only a media header can declare set-header presence (SH-1).
        if (header is TapeMediaHeader media)
            Navigator.ResolveMediaHeaderPresence(TapeHeaderPresence.Present, media.HasSetHeaders);
        else
            Navigator.ResolveMediaHeaderPresence(TapeHeaderPresence.Absent);

        if (header is not null)
            m_logger.LogTrace("BOM header read: {Header}", header);
        else
            m_logger.LogTrace("BOM header read: none (legacy/blank/foreign)");

        return header;
    }

    /// <summary>Convenience: reads the header and returns just the resulting presence.</summary>
    public TapeHeaderPresence ProbeMediaHeaderPresence()
    {
        ReadBomHeader();
        return Navigator.MediaHeaderPresence;
    }

    /// <summary>
    /// Resolves header presence before the first content/TOC navigation, if not already known. Idempotent
    ///  and best-effort: on I/O failure presence stays Unknown and downstream navigation surfaces the error.
    /// </summary>
    internal void EnsureMediaHeaderResolved()
    {
        if (Navigator.MediaHeaderPresence != TapeHeaderPresence.Unknown)
            return;

        ReadBomHeader();
    }

    #endregion // *** Media Header ***

    #region *** Set Header Read / Write ***

    /// <summary>
    /// Whether sets written by this agent carry their own <see cref="TapeSetHeader"/>. Recorded into
    ///  the media header as <see cref="TapeMediaHeader.HasSetHeaders"/>, so a reader knows without
    ///  probing (SH-1).
    /// </summary>
    /// <remarks>
    /// The flag is stamped into the media header when THAT header is written, and cannot be revised
    ///  afterwards — so heading a volume with one agent and then writing its sets with another whose
    ///  setting differs would leave the declaration lying. Both default to <see langword="true"/>, so
    ///  the mismatch cannot arise in practice.
    /// </remarks>
    public bool WritesSetHeaders { get; set; } = true;

    /// <summary>
    /// Writes the set header for <see cref="TapeTOC.CurrentSetIndex"/> at the CURRENT tape position.
    ///  Builds the record via the TOC (the sole set-header authority), frames it, and hands it to the
    ///  manager. Mirrors <see cref="WriteMediaHeader"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The caller owns the positioning.</b> This must run with the head at the set's first block and
    ///  with the manager still in <see cref="TapeState.MediaPrepared"/> — see
    ///  <c>TapeFileBackupAgent.BeginWriteContentForCurrentSet</c>, which hoists
    ///  <see cref="TapeNavigator.MoveToTargetContentSet"/> ahead of <c>Manager.BeginWriteContent</c>
    ///  precisely to create that window (SH-4).
    /// </para>
    /// <para>
    /// A failure here aborts the set: <see cref="TapeStreamManager.WriteSetHeaderBlock"/> resets the
    ///  content position on a torn write (SH-6), so the head is no longer where anyone believes it is
    ///  and writing content on top would be reckless.
    /// </para>
    /// </remarks>
    public TapeResult WriteSetHeader()
    {
        var header = TOC.CreateSetHeaderForCurrentSet();

        byte[]? block = TapeHeaderBlock.Frame(header);      // pack + size guard + pad, single-point
        if (block is null)
        {
            m_logger.LogError("Set header frame exceeds the standard header block ({Bs} B)", TapeHeaderBlock.Size);
            SetError(WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER, "Set header too large for its block");
            return FailedOperationResult;
        }

        if (!Manager.WriteSetHeaderBlock(block))
        {
            SyncErrorFrom(Manager);
            return FailedOperationResult;
        }

        m_logger.LogTrace("Set header written: {Header}", header);
        return TapeResult.OK;
    }

    #endregion // *** Set Header Read / Write ***

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
    /// Mirrors <see cref="TapeFileAgent.ReadBomHeader"/>: the manager delivers raw bytes, the AGENT
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

    #region *** TOC Backup ***

    private bool BeginWriteTOC()
    {
        // Do NOT EnsureMediaHeaderResolved() here — TOC navigation works from end-of-content and never
        //  needs header presence; resolving here would rewind to BOM and destroy the position.

        // If we were reading or writing, end it first - before setting the parameters for TOC writing
        if (!Manager.EndReadWrite())
        {
            m_logger.LogWarning("Failed to end read/write in {Method}",
                nameof(BeginWriteTOC));
            SyncErrorFrom(Manager);
            LatchFailure();
            return false;
        }

        if (!Manager.BeginWriteTOC())
        {
            m_logger.LogWarning("Failed to begin write TOC in {Method}",
                nameof(BeginWriteTOC));
            SyncErrorFrom(Manager);
            LatchFailure();
            return false;
        }

        Drive.SetBlockSize(c_fixedTOCBlockSize);

        return true;
    }
    private TapeWriteStream? OpenWriteTOCStream()
    {
        return Manager.ProduceWriteTOCStream();
    }

    private bool BackupTOCCore()
    {
        try
        {
            using var wstream = OpenWriteTOCStream();
            if (wstream == null)
                return false;

#if DEBUG
            // Simulate TOC copy failure based on bitmask
            if (SimulateTOCFailureMask != 0 && (SimulateTOCFailureMask & (1 << _tocCopyCounter++)) != 0)
                throw new IOException("Simulated TOC backup failure");
#endif

            // NOTE: no ThrowIfAbortRequested here — TOC writing is a critical
            // data-integrity operation and must never be aborted.

            var hasher = CreateHasher(c_hashForTOC);

            if (hasher == null)
            {
                // serialize the TOC without hashing
                var serializer = new TapeSerializer(wstream);
                serializer.Serialize(TOC);
            }
            else
            {
                // serialize the TOC with hashing; careful not to dispose wstream!
                using var hashingStream = new HashingStream(wstream, hasher, ownInner: false);
                var serializer = new TapeSerializer(hashingStream);
                serializer.Serialize(TOC);
                serializer.Serialize(hasher.GetCurrentHash()); // notice the hash bytes themselves aren't added to the hash!

/*#if DEBUG
                // TEST: serialize a 55 MB dummy array
                m_logger.LogTrace("***** Serializing dummy TOC array");
                byte[] dummy = new byte[55 * 1024 * 1024];
                serializer.Serialize(dummy);
#endif*/
            }

            BytesBackedup += wstream.Length;
            return true;
        }
        catch (Exception ex)
        {
            SetError(ex);

            m_logger.LogWarning("Exception {Exception} in {Method}", ex, nameof(BackupTOCCore));
            LatchFailure();
            return false;
        }
    }

    /// <summary>
    /// Writes two copies of the <see cref="TOC"/> to tape with CRC integrity hashing.
    /// <para>
    /// Succeeds if at least one copy is written successfully. The dual-copy
    ///  strategy ensures TOC recoverability even with partial media damage.
    /// </para>
    /// <para>
    /// <b>Notice</b> this public call <b>resets the latched error</b>, hence make sure
    ///  to capture it before calling.
    /// </para>
    /// </summary>
    /// <param name="enforce">When <see langword="true"/>, resets navigator state before writing
    ///  (use after operations that may leave the tape position uncertain).</param>
    public TapeResult BackupTOC(bool enforce = false)
    {
        ResetLatchedFailure();

        // We stamp a stable media identity before the first durable write. New media (just
        //  formatted) and legacy Guid-less media (just loaded) both reach here with an empty
        //  MediaId; we mint once, then it persists across every rewrite and across all volumes.
        //  Both TOC copies serialize the same value, since we mint into the in-memory TOC here.
        TOC.EnsureMediaId();

#if DEBUG
        _tocCopyCounter = 0;
#endif
        m_logger.LogTrace("Backing up TOC, 1st copy");

        if (enforce)
        {
            Manager.EndReadWrite();
            Navigator.ResetContentSet();

            if (Navigator.TOCUnlocated && Navigator is TapeNavigatorTOCInSet)
            {
                // Do NOT try to navigate if TOC-in-set has been invalidated -- we may end up overwriting content
                //  -> fail instead to allow the user to save TOC to a file
                m_logger.LogTrace("Cannot enforce TOC backup by resetting content set since TOC has been invalidated");
                return FailedOperationResult;
            }

            m_logger.LogTrace("Enforcing TOC backup by resetting content set");
        }

        if (!BeginWriteTOC())
        {
            m_logger.LogError("Failed to begin TOC write in {Method}", nameof(BackupTOC));
            return FailedOperationResult;
        }

        // To ensure TOC integrity, backup TOC twice
        bool result1 = BackupTOCCore();
        if (result1)
            m_logger.LogTrace("TOC 1st copy backed up ok");
        else
            m_logger.LogWarning("TOC 1st copy backup failed");

        m_logger.LogTrace("Backing up TOC, 2nd copy");
        ResetError();
        ResetLatchedFailure(); // if the 2nd copy succeeds, we treat it as the overall success

        bool result2 = BackupTOCCore();
        if (result2)
            m_logger.LogTrace("TOC 2nd copy backed up ok");
        else
            m_logger.LogWarning("TOC 2nd copy backup failed");

        return (result1 || result2) ? TapeResult.OK : FailedOperationResult;
    }

    /// <summary>
    /// Backs up the TOC onto media that is known to be blank (e.g. just formatted).
    /// Equivalent to <see cref="BackupTOC()"/> but tells the navigator that no
    /// existing TOC mark or content needs to be located first.
    /// </summary>
    /// <param name="writeHeader">
    /// Write the media header before the initial TOC. TRUE only on the format / fresh-media path;
    ///  FALSE on the delete-all path (which must preserve, never rewrite, the existing header — §10.8).
    ///  Defaults to <see cref="WritesMediaHeader"/> so continuation heading (which sets the flag) works.
    /// </param>
    public TapeResult BackupInitialTOC(bool? writeHeader = null)
    {
        Navigator.AssumeBlankMedia();

        if (writeHeader ?? WritesMediaHeader)
        {
            var hr = WriteMediaHeader();
            if (!hr) return hr;
        }

        return BackupTOC();
    }

    #endregion // *** TOC Backup ***

    #region *** TOC Restore ***

    private bool BeginReadTOC()
    {
        // Do NOT EnsureMediaHeaderResolved() here — TOC navigation works from end-of-content and never
        //  needs header presence; resolving here would rewind to BOM and destroy the position.

        // If we were reading or writing, end it first - before setting the parameters for TOC reading
        if (!Manager.EndReadWrite())
        {
            m_logger.LogWarning("Failed to end read/write in {Method}",
                nameof(BeginReadTOC));
            SyncErrorFrom(Manager);
            LatchFailure();
            return false;
        }

        if (!Manager.BeginReadTOC())
        {
            m_logger.LogWarning("Failed to begin read TOC in {Method}",
                nameof(BeginReadTOC));
            SyncErrorFrom(Manager);
            LatchFailure();
            return false;
        }

        Drive.SetBlockSize(c_fixedTOCBlockSize);

        return true;
    }
    private TapeReadStream? OpenReadTOCStream()
    {
        return Manager.ProduceReadTOCStream(textFileMode: false, lengthLimit: -1);
    }

    private bool RestoreTOCCore()
    {
        try
        {
            ThrowIfAbortRequested($"preparing to load TOC");

            using var rstream = OpenReadTOCStream();
            if (rstream == null)
            {
                m_logger.LogWarning("Failed to open TOC read stream in {Method}", nameof(RestoreTOCCore));
                // Capture Manager/Drive/Navigator error before retries reset it
                if (Manager.WentBad)
                    SetError(Manager.LastError, Manager.LastErrorMessage);
                else
                    SetError(WIN32_ERROR.ERROR_INVALID_STATE, "Failed to open TOC read stream");
                LatchFailure();
                return false;
            }

#if DEBUG
            // Simulate TOC copy failure based on bitmask
            if (SimulateTOCFailureMask != 0 && (SimulateTOCFailureMask & (1 << _tocCopyCounter++)) != 0)
                throw new IOException("Simulated TOC restore failure");
#endif

            ThrowIfAbortRequested($"load TOC core");

            var hasher = CreateHasher(c_hashForTOC);

            if (hasher == null)
            {
                var deserializer = new TapeDeserializer(rstream);
                var toc = deserializer.Deserialize<TapeTOC>();
                if (toc != null)
                {
                    TOC.CopyFrom(toc);
                    BytesRestored += rstream.Length;
                    return true;
                }
                else
                {
                    m_logger.LogWarning("Failed to deserialize TOC in {Method}", nameof(RestoreTOCCore));
                    SetError(WIN32_ERROR.ERROR_INVALID_DATA, "Failed to deserialize TOC: data not found or unreadable");
                    LatchFailure();
                    return false;
                }
            }
            else
            {
                using var hashingStream = new HashingStream(rstream, hasher, ownInner: false);
                var deserializer = new TapeDeserializer(hashingStream);
                var toc = deserializer.Deserialize<TapeTOC>();
                if (toc != null)
                {
                    // Careful! First get the hash, only then read the hash bytes from the stream!
                    byte[] hashBytesCheck1 = hasher.GetCurrentHash();
                    byte[]? hashBytesCheck2 = deserializer.DeserializeBytes(hasher.HashLengthInBytes);
                    if (hashBytesCheck2?.SequenceEqual(hashBytesCheck1) ?? false)
                    {
                        // CRC check passed
                        TOC.CopyFrom(toc);
                        BytesRestored += rstream.Length;
/*#if DEBUG
                        // TEST FIXME: deserialize a 55 MB dummy array
                        m_logger.LogTrace("***** Deserializing dummy TOC array");
                        byte[]? dummy = deserializer.DeserializeBytes(55 * 1024 * 1024);
#endif*/
                        return true;
                    }
                    else
                        throw new IOException($"CRC check failed for TOC. Hasher: {c_hashForTOC}",
                            (int)WIN32_ERROR.ERROR_CRC);
                }
                else
                {
                    m_logger.LogWarning("Failed to deserialize TOC in {Method}", nameof(RestoreTOCCore));
                    SetError(WIN32_ERROR.ERROR_INVALID_DATA, "Failed to deserialize TOC: data not found or unreadable");
                    LatchFailure();
                    return false;
                }

            }
        }
        catch (TapeAbortRequestedException)
        {
            m_logger.LogWarning("TOC restore aborted by user request");
            IsAbortRequested = true; // shopuld be set already, but doesn't hurt to ensure
            // No need for user-requested abort to LatchFailure()
            return false;
        }
        catch (Exception ex)
        {
            SetError(ex);
            LatchFailure();

            m_logger.LogWarning("Exception {Exception} while restoring TOC", ex);
            // Stream disposal (using var) already cleared Manager/Drive errors;
            //  capture the exception on the agent so callers see a meaningful message
            return false;
        }
    }

    /// <summary>
    /// Reads the <see cref="TOC"/> from tape, trying up to three strategies:
    ///  1st copy → 2nd copy (sequential) → 2nd copy (direct seek).
    /// <para>On success, replaces the current <see cref="TOC"/> content.</para>
    /// <para>
    /// <b>Notice</b> this public call <b>resets the latched error</b>, hence make sure
    ///  to capture it before calling.
    /// </para>
    /// </summary>
    public TapeResult RestoreTOC()
    {
        ResetLatchedFailure();

#if DEBUG
        _tocCopyCounter = 0;
#endif
        // Since TOC is stored twice, if the first attempt fails, try again
        m_logger.LogTrace("Restoring TOC from 1st copy");

        if (!BeginReadTOC())
        {
            m_logger.LogError("Failed to begin TOC read in {Method}", nameof(RestoreTOC));
            return FailedOperationResult;
        }

        bool result = RestoreTOCCore();

        if (result)
            m_logger.LogTrace("TOC restored from 1st copy");
        else
        {
            m_logger.LogWarning("TOC restore from 1st copy failed. Trying 2nd copy");
            // Notice we now must be at the beginning of the 2nd copy, as Manager calls Navigator.MoveToNextTOCFilemark()
            //  from Manager.EndReadFile() when disposing the 1st read TOC sytream
            ResetError();
            ResetLatchedFailure(); // if we succeed on the 2nd copy, we treat it as the overall success

            result = RestoreTOCCore();
            if (result)
                m_logger.LogTrace("TOC restored from 2nd copy");
            else
                m_logger.LogError("TOC restore from 2nd copy failed");

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
                    m_logger.LogError("TOC restore directly from 2nd copy failed");
            }
        }

        if (IsAbortRequested && WentOK)
        {
            m_logger.LogWarning("TOC restore aborted by user request");
            // Set error to indicate user aborted
            SetError(WIN32_ERROR.ERROR_CANCELLED, "TOC loading aborted by user");
        }

        return result ? TapeResult.OK : FailedOperationResult;
    }

    #endregion // *** TOC Restore ***

    #region *** TOC File I/O ***

    /// <summary>
    /// File extension for emergency TOC files.
    /// </summary>
    public const string TOCFileExtension = ".tapetoc";

    /// <summary>
    /// Saves the current TOC to a file using the same serialization format and CRC
    /// as the on-tape copy. The file is self-validating via the appended hash.
    /// </summary>
    /// <param name="filePath">Full path to the file to create/overwrite.</param>
    /// <returns>Result indicating success or failure with error details.</returns>
    public TapeResult SaveTOCToFile(string filePath)
    {
        try
        {
            m_logger.LogTrace("Saving TOC to file: {Path}", filePath);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            var hasher = CreateHasher(c_hashForTOC);

            if (hasher == null)
            {
                var serializer = new TapeSerializer(fs);
                serializer.Serialize(TOC);
            }
            else
            {
                using var hashingStream = new HashingStream(fs, hasher, ownInner: false);
                var serializer = new TapeSerializer(hashingStream);
                serializer.Serialize(TOC);
                serializer.Serialize(hasher.GetCurrentHash());
            }
        }
        catch (Exception ex)
        {
            SetError(ex, $"Failed to save TOC to file: {ex.Message}");
            m_logger.LogWarning("Exception {Exception} saving TOC to file {Path}", ex, filePath);
            return FailedOperationResult;
        }

        m_logger.LogTrace("TOC saved to file successfully");
        return TapeResult.OK;
    }

    /// <summary>
    /// Loads a TOC from a file previously saved by <see cref="SaveTOCToFile"/>.
    /// The file format and CRC validation are identical to the on-tape format.
    /// On success, the loaded TOC replaces the current <see cref="TOC"/> content.
    /// </summary>
    /// <param name="filePath">Full path to the TOC file to load.</param>
    /// <returns>Result indicating success or failure with error details.</returns>
    public TapeResult LoadTOCFromFile(string filePath)
    {
        try
        {
            m_logger.LogTrace("Loading TOC from file: {Path}", filePath);

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hasher = CreateHasher(c_hashForTOC);

            if (hasher == null)
            {
                var deserializer = new TapeDeserializer(fs);
                var toc = deserializer.Deserialize<TapeTOC>();
                if (toc == null)
                {
                    m_logger.LogWarning("Failed to deserialize TOC from file {Path}", filePath);
                    SetError(WIN32_ERROR.ERROR_INVALID_DATA, "Failed to deserialize TOC from file");
                    return FailedOperationResult;
                }
                TOC.CopyFrom(toc);
            }
            else
            {
                using var hashingStream = new HashingStream(fs, hasher, ownInner: false);
                var deserializer = new TapeDeserializer(hashingStream);
                var toc = deserializer.Deserialize<TapeTOC>();
                if (toc == null)
                {
                    m_logger.LogWarning("Failed to deserialize TOC from file {Path}", filePath);
                    SetError(WIN32_ERROR.ERROR_INVALID_DATA, "Failed to deserialize TOC from file");
                    return FailedOperationResult;
                }

                byte[] hashBytesCheck1 = hasher.GetCurrentHash();
                byte[]? hashBytesCheck2 = deserializer.DeserializeBytes(hasher.HashLengthInBytes);
                if (!(hashBytesCheck2?.SequenceEqual(hashBytesCheck1) ?? false))
                {
                    m_logger.LogWarning("CRC check failed for TOC file {Path}", filePath);
                    SetError(WIN32_ERROR.ERROR_CRC, $"CRC check failed for TOC file. Hasher: {c_hashForTOC}");
                    return FailedOperationResult;
                }

                TOC.CopyFrom(toc);
            }

            m_logger.LogTrace("TOC loaded from file successfully: {Sets} set(s)", TOC.Count);
            return TapeResult.OK;
        }
        catch (Exception ex)
        {
            SetError(ex, $"Failed to load TOC from file: {ex.Message}");
            m_logger.LogWarning("Exception {Exception} loading TOC from file {Path}", ex, filePath);
            return FailedOperationResult;
        }
    }

    #endregion // *** TOC File I/O ***

    #region *** Notification wrappers ***

    // Safe calls to ITapeFileNotifiable
    //  All exceptions are caught and logged as warnings -- except for TapeAbortRequestedException, which is rethrown
    //  The _stats struct is updated BEFORE the callback is invoked, so the callback always sees current totals.

    /// <param name="filesAdded">
    /// How many NEWLY discovered files this set contributes to the operation total. Restore passes each
    ///  set's own count, since sets hold distinct files. Backup passes the list count on the FIRST set
    ///  and ZERO on every continuation set: one file list spans all volumes, and a continuation
    ///  re-attempts files that were already counted (and un-counted by the EOM rollback).
    /// </param>
    protected void NotifySetStart(ITapeFileNotifiable? fileNotify, int filesAdded)
    {
        _stats.FilesTotal += filesAdded;
        _stats.Sets.SetsProcessed++;
        m_setAnomalyInCurrentSet = false; // ← per-set
        RefreshBytesTotalEstimate();

        if (fileNotify != null)
        {
            try
            {
                fileNotify.SetStart(TOC.CurrentSetIndex, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                // SetStart should NOT throw as the caller might be calling outside try / catch. But it
                //  may signal IsAbortRequested to abort the operation asap in the caller's file loop.
                m_logger.LogInformation("Abort requested while notifying set start: {Exception}", ex1);
                // Record the request HERE, at the point of observation — not in whichever catch handler
                //  happens to receive the rethrow.
                IsAbortRequested = true;
            }
            catch (Exception ex2)
            {
                // in statistics notification, we don't rethrow exceptions
                m_logger.LogWarning("Exception {Exception} while notifying set start", ex2);
            }
        }
    }

    protected void NotifySetEnd(ITapeFileNotifiable? fileNotify)
    {
        RefreshBytesTotalEstimate();
        if (!m_setAnomalyInCurrentSet)
            _stats.Sets.SetsSucceeded++;

        if (fileNotify != null)
        {
            try
            {
                fileNotify.SetEnd(TOC.CurrentSetIndex, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                // Record but do NOT rethrow: this runs on the EOM path between the continuation
                //  snapshot and TOC.ContinuedOnNextVolume, and on the normal exit just before
                //  MultiVolumeContext is cleared. An escaping exception there skips bookkeeping the
                //  next volume depends on. The set is over anyway — the flag stops the NEXT one.
                m_logger.LogInformation("Abort requested while notifying set end: {Exception}", ex1);
                IsAbortRequested = true;
            }
            catch (Exception ex2)
            {
                // in statistics notification, we don't rethrow exceptions
                m_logger.LogWarning("Exception {Exception} while notifying set end", ex2);
            }
        }
    }

    protected bool NotifyPreProcessFile(ITapeFileNotifiable? fileNotify, TapeFileInfo fileInfo)
    {
        RefreshBytesTotalEstimate();
        if (fileNotify != null)
        {
            try
            {
                return fileNotify.PreProcessFile(fileInfo, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                m_logger.LogInformation("Abort requested while notifying pre-process file: {Exception}", ex1);
                // Record the request HERE, at the point of observation — not in whichever catch handler
                //  happens to receive the rethrow.
                IsAbortRequested = true;
                throw; // rethrow to abort the entire operation
            }
            catch (Exception ex2)
            {
                m_logger.LogWarning("Exception {Exception} while notifying pre-process file", ex2);
            }
        }
        return true;
    }
    protected bool NotifyPostProcessFile(ITapeFileNotifiable? fileNotify, TapeFileInfo fileInfo)
    {
        _stats.FilesProcessed++;
        _stats.FilesSucceeded++;
        _stats.FileBytesProcessed += fileInfo.FileDescr.Length;
        // SizeOnTape reflects the actual on-tape footprint (post software-compression); fall
        //  back to the logical length when it hasn't been populated (e.g. legacy/aligned paths).
        _stats.BytesOnTapeProcessed += fileInfo.SizeOnTape > 0 ? fileInfo.SizeOnTape : fileInfo.FileDescr.Length;
        RefreshBytesTotalEstimate();

        if (fileNotify != null)
        {
            try
            {
                return fileNotify.PostProcessFile(fileInfo, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                m_logger.LogInformation("Abort requested while notifying post-process file: {Exception}", ex1);
                // Record the request HERE, at the point of observation — not in whichever catch handler
                //  happens to receive the rethrow. In particular, the packed path routes this through
                //  PackedCommitTracker.DrainPostProcess, which converts the exception into a bool and
                //  loses its identity; by then the loop can no longer tell an abort from a drain failure.
                IsAbortRequested = true;
                throw; // rethrow to abort the entire operation
            }
            catch (Exception ex2)
            {
                m_logger.LogWarning("Exception {Exception} while notifying post-process file", ex2);
            }
        }
        return true;
    }

    // Returns the desired action. Does NOT rethrow TapeAbortRequestedException —
    //  returns FileFailedAction.Abort instead, so the caller can handle it.
    //  Sets IsAbortRequested when the result is Abort, so outer loops and the
    //  service layer can detect the abort without relying solely on the return value.
    protected FileFailedAction NotifyFileFailed(ITapeFileNotifiable? fileNotify, TapeFileInfo fileInfo, Exception ex)
    {
        _stats.FilesProcessed++;
        _stats.FilesFailed++;
        RefreshBytesTotalEstimate();

        FileFailedAction result = FileFailedAction.Skip;
        var failResult = TapeResult.Fail(ex);

        if (fileNotify != null)
        {
            try
            {
                result = fileNotify.OnFileFailed(fileInfo, failResult, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                m_logger.LogInformation("Abort requested while notifying file failure: {Exception}", ex1);
                result = FileFailedAction.Abort;
            }
            catch (Exception ex2)
            {
                m_logger.LogWarning("Exception {Exception} while notifying file failure", ex2);
                return FileFailedAction.Skip; // do not abort the operation
            }
        }

        if (result == FileFailedAction.Abort)
            IsAbortRequested = true;

        return result;
    }
    protected void NotifyFileSkipped(ITapeFileNotifiable? fileNotify, TapeFileInfo fileInfo)
    {
        _stats.FilesProcessed++;
        _stats.FilesSkipped++;
        RefreshBytesTotalEstimate();

        if (fileNotify != null)
        {
            try
            {
                fileNotify.OnFileSkipped(fileInfo, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                m_logger.LogInformation("Abort requested while notifying file skipped: {Exception}", ex1);
                // Record the request HERE, at the point of observation — not in whichever catch handler
                //  happens to receive the rethrow.
                IsAbortRequested = true;
                throw; // rethrow to abort the entire operation
            }
            catch (Exception ex2)
            {
                m_logger.LogWarning("Exception {Exception} while notifying file skipped", ex2);
            }
        }
    }

    /// <summary>
    /// Undoes the last failure recorded by <see cref="NotifyFileFailed"/>.
    /// Call when a file will be retried (user chose Retry, or end-of-media → next volume).
    /// </summary>
    protected void StatsUndoFailure()
    {
        _stats.FilesProcessed--;
        _stats.FilesFailed--;
    }

    /// <summary>
    /// Undoes <paramref name="count"/> skip entries from the statistics.
    /// Call on EOM rollback when skipped files in the rolled-back range will be
    ///  re-processed on the next volume and their skips counted again.
    /// </summary>
    protected void StatsUndoSkips(int count)
    {
        _stats.FilesProcessed -= count;
        _stats.FilesSkipped -= count;
    }

    /// <summary>
    /// Records an anomaly and asks the notifiable how to proceed. Returns
    ///  <see cref="SetAnomalyAction.Abort"/> when the operation must stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ACCUMULATES on every call but PROMPTS only on the first per set: the answer authorizes the whole
    ///  remaining ladder (SH-18).
    /// </para>
    /// <para>
    /// Does NOT rethrow <see cref="TapeAbortRequestedException"/> — it converts it to
    ///  <see cref="SetAnomalyAction.Abort"/> and records <see cref="IsAbortRequested"/>, so the enum and
    ///  the exception converge on one code path. Mirrors <see cref="NotifyFileFailed"/> exactly.
    /// </para>
    /// </remarks>
    protected SetAnomalyAction NotifySetAnomaly(ITapeFileNotifiable? fileNotify, in TapeSetAnomaly anomaly)
    {
        _setAnomalies.Add(anomaly);          // every OBSERVATION — the forensic trail
        m_setAnomalyInCurrentSet = true;
        // Do NOT yet set _stats.Sets.SetWriteBlocked = true as we still might recover!
        //  do NOT! if (anomaly.IsDestructive) _stats.Sets.SetWriteBlocked = true;

        m_logger.LogWarning("Set anomaly notified: {Anomaly}", anomaly);

        if (m_setAnomalyRaisedForSet == TOC.CurrentSetIndex)
            return SetAnomalyAction.Proceed;   // already counted and authorized for this set
        
        // Not yet counted and authorized for this set: record it and prompt the user
        m_setAnomalyRaisedForSet = TOC.CurrentSetIndex;
        _stats.Sets.AnomaliesDetected++;       // count one per SET, the user-meaningful number

        RefreshBytesTotalEstimate();

        SetAnomalyAction result;
        if (fileNotify is not null)
        {
            try
            {
                result = fileNotify.OnSetAnomaly(in anomaly, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                m_logger.LogInformation("Abort requested while notifying set anomaly: {Exception}", ex1);
                result = SetAnomalyAction.Abort;
            }
            catch (Exception ex2)
            {
                m_logger.LogWarning("Exception {Exception} while notifying set anomaly", ex2);
                result = SetAnomalyAction.Abort;   // an unusable notifiable must not authorize a repair
            }
        }
        else
        {
            // No notifiable: nobody can authorize recovery, but nobody asked to abort either. Defer to
            //  the path's own policy — the READ path's terminal action is itself safe, the write path's
            //  is not. Blanket Abort here would break every caller that passes null, which is most of
            //  the library's own callers.
            result = BlocksOnUnverifiableSet ? SetAnomalyAction.Abort : SetAnomalyAction.Proceed;
        }

        // Record an abort ONLY when the notifiable ACTUALLY DECLINED something it could have
        //  authorized. With no recovery stage left, the ladder was informing rather than asking —
        //  and reporting that as "aborted per user request" credits the user with a decision they
        //  were never offered, while hiding the refusal behind the abort verdict.
        if (result == SetAnomalyAction.Abort && anomaly.CanAttemptRecovery)
            IsAbortRequested = true;

        return result;
    }

    /// <summary>Records and reports an anomaly that was corrected and re-verified (SH-16).</summary>
    /// <remarks>
    /// Reported at Warning on BOTH surfaces, never Trace: a successfully corrected drift means the drive
    ///  or the medium miscounted marks. A tape that corrects on every set is a tape to retire, and this
    ///  is the only place that fact reaches the person holding the cartridge.
    /// </remarks>
    protected void NotifySetAnomalyRecovered(ITapeFileNotifiable? fileNotify, in TapeSetAnomaly anomaly)
    {
        _stats.Sets.AnomaliesRecovered++;
        if (anomaly.Stage == TapeSetAnomalyStage.Renavigated)
            _stats.Sets.AnomaliesRecoveredFromBom++;

        m_logger.LogWarning("Set anomaly RECOVERED via {Stage}: {Anomaly}", anomaly.Stage, anomaly);
        RefreshBytesTotalEstimate();

        if (fileNotify is null)
            return;
        try
        {
            fileNotify.OnSetAnomalyRecovered(in anomaly, in _stats);
        }
        catch (TapeAbortRequestedException ex1)
        {
            m_logger.LogInformation("Abort requested while notifying set anomaly recovery: {Exception}", ex1);
            IsAbortRequested = true;
        }
        catch (Exception ex2)
        {
            m_logger.LogWarning("Exception {Exception} while notifying set anomaly recovery", ex2);
        }
    }

    /// <summary>Assembles the anomaly payload from a verdict and the header that produced it.</summary>
    /// <param name="diagnosis">
    /// The payload's <see cref="TapeSetAnomaly.Diagnosis"/>. Defaults to the agent's CURRENT error state
    ///  (<c>TapeResult.Fail(this)</c>) — correct for a detection, where the caller has just set the error
    ///  via <see cref="RaiseSetAnomaly"/>. A RECOVERY passes <see cref="TapeResult.OK"/> explicitly: the
    ///  payload describes the set's state NOW, and nothing is wrong with it any more. Never leave it to
    ///  the default on that path — by then <c>Navigator.ReconcileContentSetAndMove</c> has called
    ///  <c>ResetError()</c>, so the snapshot would be a failure with no code and no message.
    /// </param>
    private TapeSetAnomaly BuildSetAnomaly(TapeSetHeaderVerdict verdict, TapeSetHeader? header,
            TapeSetAnomalyStage stage, bool canAttemptRecovery, TapeResult? diagnosis = null)
        => new(
            Verdict: verdict,
            Stage: stage,
            SetIndex: TOC.CurrentSetIndex,
            ExpectedVolumeSetIndex: TOC.CurrentSetIndexOnVolume,
            ActualVolumeSetIndex: header?.VolumeSetIndex ?? -1,
            ExpectedDescription: string.IsNullOrWhiteSpace(TOC.CurrentSetTOC.Description)
                ? $"Set #{TOC.CurrentSetIndex}" : TOC.CurrentSetTOC.Description,
            ActualDescription: header?.DisplayName ?? "(unreadable)",
            ExpectedVolume: TOC.Volume,
            ActualVolume: header?.Volume ?? TOC.Volume,
            IsDestructive: BlocksOnUnverifiableSet,
            CanAttemptRecovery: canAttemptRecovery,
            Diagnosis: diagnosis ?? TapeResult.Fail(this));

    #endregion // *** Notification wrappers ***

    #region *** Protected helpers ***

    protected static NonCryptographicHashAlgorithm? CreateHasher(TapeHashAlgorithm hashAlgorithm)
    {
        NonCryptographicHashAlgorithm? hasher = hashAlgorithm switch
        {
            TapeHashAlgorithm.None => null,
            TapeHashAlgorithm.Crc32 => new Crc32(),
            TapeHashAlgorithm.Crc64 => new Crc64(),
            TapeHashAlgorithm.XxHash32 => new XxHash32(),
            TapeHashAlgorithm.XxHash3 => new XxHash3(),
            TapeHashAlgorithm.XxHash64 => new XxHash64(),
            TapeHashAlgorithm.XxHash128 => new XxHash128(),
            _ => throw new ArgumentException($"Unknown hash algorithm in {nameof(CreateHasher)}", nameof(hashAlgorithm)),
        };
        return hasher;
    }

    protected void ThrowIfAbortRequested(string where)
    {
        if (IsAbortRequested)
            throw new TapeAbortRequestedException($"Abort requested in {where}");
    }

    #endregion // *** Protected helpers ***

} // class TapeFileAgent

// namespace TapeNET
