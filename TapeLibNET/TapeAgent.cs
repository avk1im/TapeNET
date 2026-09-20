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
        if (Navigator.CurrentContentSet != TapeNavigator.UnknownSet && Navigator.CurrentContentSet != TapeNavigator.InTOCSet
            && Navigator.CurrentContentSet != TapeNavigator.AtBomHeader)
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

    // Guards the one-retry correction bound (SH-10). Set while a correction is being verified, so the re-verify
    //  cannot itself trigger another correction — a tape whose mark structure defeats a simple relative
    //  move is inconsistent, not noisy, and a second attempt would only walk further into the unknown.
    private bool m_correctingSetNavigation = false;

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
    /// The base returns <see langword="true"/> only while a set delete is in flight.
    /// <para>
    /// <para>
    /// Overridden to <see langword="true"/> by <see cref="TapeFileBackupAgent"/>.
    /// </para>
    /// The read side's golden rule — a record we cannot verify never blocks — rests on a fact that does
    ///  not survive the crossing: <b>restore positions absolutely</b>. <c>RestoreNextFile</c> seeks to the
    ///  file's exact <c>(block, offset)</c>, so an unverified set costs a safety net and nothing more. A
    ///  destructive write positions <b>relatively</b>, by counting marks, and an unreadable block at the
    ///  presumed set start is exactly the symptom a miscount produces when it lands somewhere that is not
    ///  a set start at all. Proceeding there would take the strongest available signal that the head is
    ///  lost and treat it as permission.
    /// </para>
    /// </remarks>
    protected virtual bool BlocksOnUnverifiableSet => m_verifyingDestructiveWrite;

    // Set for the duration of a destructive delete, so the shared verdict ladder applies WRITE-side
    //  policy. A private flag since DeleteSetsFromCurrentSetUp lives on the base and is
    //  inherited by backup and restore agents alike, so it has no natural home in either.
    private bool m_verifyingDestructiveWrite = false;

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
    /// Reads, classifies, and verifies the set header for the set just positioned at. Returns
    ///  <see langword="false"/> only when the set must not be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Preconditions (SH-7).</b> Called immediately after <c>Manager.BeginReadContent()</c> and
    ///  strictly before the first <c>BeginPackedFileRead</c> — the pipelined reader does not exist yet,
    ///  so the raw block read cannot race its prefetch worker.
    /// </para>
    /// <para>
    /// Uses:
    /// <list type="bullet">
    /// <item><description><see cref="ReadSetHeader"/></description> for reading the set header block.</item>
    /// <item><description><see cref="ClassifySetHeader"/></description> for classifying the set header.</item>
    /// <item><description><see cref="HandleSetHeaderVerdict"/></description> for handling the classification verdict.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Step 5 scope: <see cref="TapeSetHeaderVerdict.SetIndexDrift"/> FAILS the set. Step 6 replaces
    ///  that branch with the bounded relative correction (SH-10).
    /// </para>
    /// </remarks>
    protected bool VerifySetHeaderForCurrentSet()
    {
        var header = ReadSetHeader();
        var verdict = ClassifySetHeader(header);
        return HandleSetHeaderVerdict(verdict, header);
    }

    /// <summary>
    /// Acts on a verdict. Separate from <see cref="VerifySetHeaderForCurrentSet"/> so the correction path
    ///  can re-enter the ladder with an already-read header, keeping every failure mode's diagnosis and
    ///  error code defined exactly once.
    /// </summary>
    private bool HandleSetHeaderVerdict(TapeSetHeaderVerdict verdict, TapeSetHeader? header)
    {
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
                return true;

            case TapeSetHeaderVerdict.Unreadable:
                if (BlocksOnUnverifiableSet)
                {
                    // A destructive write positions by counting marks, so an unclassifiable block at the
                    //  presumed set start is the miscount's own signature. Refuse.
                    m_logger.LogError(
                        "Set header for set #{Set} could not be read or classified; refusing to write over it",
                        TOC.CurrentSetIndex);
                    SetError(WIN32_ERROR.ERROR_INVALID_DATA,
                        $"Set header at set #{TOC.CurrentSetIndex} could not be verified — " +
                        "refusing a destructive write at an unconfirmed position");
                    return false;
                }

                // The golden rule: a record we cannot verify never blocks. The files position absolutely,
                //  so proceeding is safe — we have merely lost the safety net for this set.
                m_logger.LogWarning(
                    "Set header for set #{Set} could not be read or classified; proceeding unverified",
                    TOC.CurrentSetIndex);
                // Carry on to reanchor:    
                // §0b: an I/O-level failure reset the content position (SH-6). Leaving it Unknown would
                //  corrupt the later EndReadContentSet advance, so re-anchor before proceeding.
                if (Navigator.CurrentContentSet == TapeNavigator.UnknownSet)
                {
                    m_logger.LogTrace("Re-anchoring the navigator after an unreadable set header");
                    if (!Navigator.MoveToTargetContentSet())
                    {
                        SyncErrorFrom(Navigator);
                        m_logger.LogWarning("Failed to re-anchor after an unreadable set header for set #{Set}",
                            TOC.CurrentSetIndex);
                        return false;
                    }
                }
                ResetError();   // the failed read is not this operation's error
                return true;

            case TapeSetHeaderVerdict.WrongMedia:
                m_logger.LogError(
                    "Media identity mismatch at set #{Set}: header carries media id {Actual}, expected {Expected}. " +
                    "The cartridge appears to have been swapped mid-operation",
                    TOC.CurrentSetIndex, header!.MediaId, TOC.MediaId);
                SetError(WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Media identity mismatch at set #{TOC.CurrentSetIndex}: the loaded cartridge is not the expected one");
                return false;

            case TapeSetHeaderVerdict.WrongVolume:
                m_logger.LogError(
                    "Volume mismatch at set #{Set}: header says volume {Actual}, expected volume {Expected}",
                    TOC.CurrentSetIndex, header!.Volume, TOC.Volume);
                SetError(WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Volume mismatch at set #{TOC.CurrentSetIndex}: tape carries volume {header.Volume}, expected {TOC.Volume}");
                return false;

            case TapeSetHeaderVerdict.SetIndexDrift:
                // Proceed with the bounded relative correction (Step 6). This replaces raising a hard
                //  failure error — restoring from the wrong set would silently deliver wrong bytes.
                m_logger.LogError(
                    "Set navigation drift at set #{Set}: navigator reported on-volume set {Expected}, " +
                    "header says {Actual} (delta {Delta})",
                    TOC.CurrentSetIndex, TOC.CurrentSetIndexOnVolume, header!.VolumeSetIndex,
                    TOC.CurrentSetIndexOnVolume - header.VolumeSetIndex);

                // Step 2 scope: the write path has NO recovery yet, so any drift blocks. Step 5 removes
                //  the second conjunct and gives both paths the unified two-stage recovery (SH-14).
                if (CorrectsSetNavigation && !BlocksOnUnverifiableSet)
                    return CorrectSetNavigation(header);
                // else Correction disabled: report rather than repair. Restoring from the wrong set would
                //  silently deliver wrong bytes, so the set fails.
                SetError(WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Set navigation drift at set #{TOC.CurrentSetIndex}: positioned at on-volume set " +
                    $"{header.VolumeSetIndex}, expected {TOC.CurrentSetIndexOnVolume} (no correction on this path)");
                return false;

            case TapeSetHeaderVerdict.NotExpected:
            default:
                return true;   // unreachable: the caller gates on SetHeadersExpected
        }
    }

    /// <summary>
    /// Repairs a detected navigation drift: adopt the verified position, move the remaining delta,
    ///  and re-verify. Returns <see langword="true"/> when the head is confirmed at the intended set.
    /// </summary>
    /// <param name="header">
    /// The positively classified header just read — identity already confirmed by
    ///  <see cref="ClassifySetHeader"/>, which is what makes the drift interpretable as positional
    ///  (SH-9).
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Bounded to one retry (SH-10).</b> Correct, re-read, re-verify. On a second disagreement the set
    ///  fails: a tape whose mark structure defeats a short relative move is inconsistent rather than
    ///  noisy, and a third attempt would only move further into the unknown.
    /// </para>
    /// <para>
    /// <b>Read-side only.</b> Nothing on the backup path calls this. A write-side miscount means the agent
    ///  is about to overwrite the wrong set, where failing is correct and correcting is reckless (§15.3).
    /// </para>
    /// <para>
    /// Logged at Warning, never Trace: a successfully corrected drift means the drive or the medium
    ///  miscounted marks — a hardware or media signal worth surfacing. A tape that corrects on every set
    ///  is a tape to retire, and the log is the only place the user will ever learn that (§15.2).
    /// </para>
    /// </remarks>
    private bool CorrectSetNavigation(TapeSetHeader header)
    {
        int actual = header.VolumeSetIndex;
        int expected = TOC.CurrentSetIndexOnVolume;

        if (m_correctingSetNavigation)
        {
            // Second disagreement within one correction — stop (SH-10).
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

        // Believe the header and move the RELATIVE delta (§9.3a). Re-navigating from an anchor would
        //  reproduce the very miscount we are correcting.
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
            var again = ReadSetHeader();
            var verdict = ClassifySetHeader(again);

            if (verdict != TapeSetHeaderVerdict.Match)
            {
                m_logger.LogError(
                    "Set navigation correction for set #{Set} did not settle: re-verification returned {Verdict}",
                    TOC.CurrentSetIndex, verdict);

                // Route through the ladder so each failure mode keeps its own diagnosis and error code.
                //  A drift here re-enters CorrectSetNavigation, which the guard turns into a clean stop.
                return HandleSetHeaderVerdict(verdict, again);
            }

            m_logger.LogWarning("Set navigation corrected — now positioned at on-volume set {Set} (set #{Global})",
                expected, TOC.CurrentSetIndex);
            return true;
        }
        finally
        {
            m_correctingSetNavigation = false;
        }
    }

    #endregion // *** Set header verification ***

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

            if (Navigator.TOCInvalidated && Navigator is TapeNavigatorTOCInSet)
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

    /// <summary>
    /// Deletes all backup sets from <see cref="TapeTOC.CurrentSetIndex"/> through the last set on the
    /// current volume, physically overwriting the tape past the last retained set to move the
    /// end-of-data marker, then updating the TOC on tape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two branches.</b> Deleting the volume's FIRST set erases everything: it positions at
    ///  begin-of-content and writes a fresh initial TOC there. Deleting TRAILING sets keeps at least one:
    ///  it positions at the first set to delete, steps back one setmark, and rewrites that setmark,
    ///  overwriting the zombie marks and advancing EOD.
    /// </para>
    /// <para>
    /// <b>Verification (SH-13).</b> On a volume declaring set headers, the set header standing at the
    ///  target is read and classified before anything is destroyed. Only <c>Match</c> authorizes the
    ///  write; every other verdict — including <c>Unreadable</c>, which on a mark-counted write is the
    ///  miscount's own signature — fails the method with the tape untouched.
    ///  <see cref="VerifiesSetHeader"/> opts out, which is the deliberate escape for a cartridge whose
    ///  set headers are themselves damaged.
    /// </para>
    /// <para>
    /// In the delete-ALL branch the verification is purely <b>positional</b>: begin-of-content is a
    ///  deterministic landing, so there is nothing to miscount — but the header found there must be the
    ///  volume's first set, confirming the head cleared the media header rather than standing on it.
    ///  That branch must never write a TOC over <see cref="TapeMediaHeader"/> (INV-4).
    /// </para>
    /// <para>
    /// <b>The head returns to the set start (SH-15)</b> after the verifying read, on both branches: the
    ///  delete-all branch writes its TOC from there, and the trailing branch counts its setmark step-back
    ///  from there.
    /// </para>
    /// <para>
    /// Preconditions: <see cref="TapeTOC.CurrentSetIndex"/> names the first set to delete and must be on
    ///  the current volume (<see cref="TapeTOC.IsCurrentSetOnVolume"/>); the delete-all branch is
    ///  unsupported with an initiator partition — format the media instead.
    /// </para>
    /// </remarks>
    /// <param name="navigateFromBegin">
    /// Forces the navigator to count from begin-of-content rather than choosing the nearest anchor —
    ///  useful when the TOC is missing or corrupted, so its filemark arithmetic should not be trusted.
    /// </param>
    /// <returns>A <see cref="TapeResult"/> indicating success or failure with error details.</returns>
    public TapeResult DeleteSetsFromCurrentSetUp(bool navigateFromBegin = false)
    {
        m_logger.LogTrace("Deleting sets from #{Set} up", TOC.CurrentSetIndex);

        // --- Precondition checks (before any tape I/O) ---
        if (!TOC.IsCurrentSetOnVolume)
        {
            m_logger.LogWarning("Current set #{Set} is not on volume #{Volume}", TOC.CurrentSetIndex, TOC.Volume);
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER,
                $"Current set #{TOC.CurrentSetIndex} is not on volume #{TOC.Volume}");
            return FailedOperationResult;
        }

        bool deletingAll = TOC.CurrentSetIndex == TOC.FirstSetOnVolume;

        if (deletingAll && TOCPlacement == TapeTocPlacement.InPartition)
        {
            // Cannot erase all content when TOC is in a separate partition —
            //  the caller should format the media instead.
            m_logger.LogWarning("Cannot delete all sets when TOC is in partition — format the media instead");
            SetError(WIN32_ERROR.ERROR_NOT_SUPPORTED,
                "Cannot delete all sets when TOC is in partition — format the media instead");
            return FailedOperationResult;
        }

        // Applies WRITE-side verdict policy for the whole operation: an unverifiable set BLOCKS here,
        //  where on the read path it would merely warn.
        m_verifyingDestructiveWrite = true;
        try
        {
            // §17.3: this method navigates content DIRECTLY (not via a backup content choke-point),
            //  so resolve header presence up front. Otherwise MoveToBeginOfContent / MoveToTargetContentSet
            //  on a headed tape would meet an unresolved Unknown → permissive Absent → no header skip →
            //  land at block 0 and clobber the media header. Idempotent / no-op once resolved.
            EnsureMediaHeaderResolved();

            bool verifies = VerifiesSetHeader && Navigator.SetHeadersExpected;

            if (deletingAll)
            {
                // --- Delete ALL sets on volume (TOC in set only) ---
                //  Navigate to the very beginning of content, then write an initial TOC
                //  which overwrites everything from the first content block.
                m_logger.LogTrace("Deleting all sets — navigating to beginning of content");
                Manager.EndReadWrite();

                Navigator.MoveToBeginOfContent();   // Present ⇒ skips the header, lands at block 1
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return FailedOperationResult;
                }

                // Positional assertion: the block here must be THIS volume's first set header. A failure
                //  means the head never cleared the media header -- and the TOC write below would then
                //  land on it (INV-4).
                if (verifies && !VerifyBeforeDestructiveWrite())
                    return FailedOperationResult;

                // Remove sets on this volume from the TOC.
                //  If there are sets from previous volumes, keep them.
                if (TOC.CurrentSetIndex > 1)
                {
                    TOC.CurrentSetIndex = TOC.CurrentSetIndex - 1;
                    TOC.RemoveSetsAfterCurrent();
                }
                else
                {
                    TOC.RemoveAllSets();
                }

                // Write the TOC as if this were blank media — but do NOT rewrite the media header
                //  (§10.8 / INV-4). We stand at block 1 (past the header at block 0), so the fresh
                //  initial TOC overwrites content only; the header survives.
                return BackupInitialTOC(writeHeader: false);
            }
            else // deleting not all sets
            {
                // --- Delete trailing sets (at least one set remains) ---
                //  Navigate to the first set to be deleted, step back one setmark, then rewrite the
                //  content setmark there. This overwrites the zombie setmarks and moves the EOD marker.
                //  Then update the TOC and write it to tape.
                m_logger.LogTrace("Navigating to set #{Set} for deletion", TOC.CurrentSetIndex);
                Manager.EndReadWrite();

                if (navigateFromBegin)
                {
                    m_logger.LogTrace("Enforced navigating to beginning of content");
                    Navigator.MoveToBeginOfContent();   // Present ⇒ skips the header
                    Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet(fromBeginOnly: true);
                }
                else
                {
                    Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet();
                }

                Navigator.MoveToTargetContentSet();
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return FailedOperationResult;
                }

                // The set we are about to delete from must be the one the TOC describes -- this count
                //  typically ran BACKWARD from end-of-content, across the very region a failed backup
                //  would have damaged.
                if (verifies && !VerifyBeforeDestructiveWrite())
                    return FailedOperationResult;

                // Step back one setmark — to just before the setmark separating the last retained set
                //  from the first set to delete.
                Navigator.MoveToNextContentSetmark(-1);
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return FailedOperationResult;
                }

                // Rewrite the content setmark here — physically overwrites the zombie data and advances EOD.
                Navigator.WriteContentSetmark();
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return FailedOperationResult;
                }

                // The navigator now thinks we're past the end of content.
                Navigator.OnContentWritten();

                // Remove the sets from the TOC: position to the set before the first to delete, then
                //  remove everything after it.
                TOC.CurrentSetIndex = TOC.CurrentSetIndex - 1;
                TOC.RemoveSetsAfterCurrent();

                // Save the updated TOC to tape. (Trailing delete never touches BOM, so the header is
                //  untouched — no writeHeader flag involved here.)
                return BackupTOC();
            }
        }
        catch (System.Exception ex)
        {
            m_logger.LogWarning("Exception {Exception} in {Method}", ex, nameof(DeleteSetsFromCurrentSetUp));
            SetError(ex);
            return FailedOperationResult;
        }
        finally
        {
            m_verifyingDestructiveWrite = false;
        }
    } // DeleteSetsFromCurrentSetUp()

    /// <summary>
    /// Verifies the set header at the current position and returns the head to the set's first block,
    ///  so the caller may write there. Returns <see langword="false"/> when the write must not proceed.
    /// </summary>
    /// <remarks>
    /// The block is DERIVED after the read rather than captured before it: the verdict ladder may itself
    ///  reposition, and a pre-read block would then be stale. A positive verdict always ends on a
    ///  successful set-header read, so the set start is unambiguously one block back (SH-15).
    /// </remarks>
    private bool VerifyBeforeDestructiveWrite()
    {
        long setStartBlock = Drive.CurrentBlock;

        if (!VerifySetHeaderForCurrentSet())
        {
            // Nothing has been written yet -- the tape is exactly as we found it.
            m_logger.LogError("Refusing to delete from set #{Set}: its set header did not verify",
                TOC.CurrentSetIndex);
            LatchFailure();   // the verdict set the error; latch it past any later success
            return false;
        }

        //long setStartBlock = Drive.CurrentBlock - 1;
        if (!Drive.MoveToBlock(setStartBlock))
        {
            m_logger.LogWarning("Failed to return to block {Block} after verifying set #{Set}",
                setStartBlock, TOC.CurrentSetIndex);
            SyncErrorFrom(Drive);
            LatchFailure();
            return false;
        }
        return true;
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
        RefreshBytesTotalEstimate();
        if (fileNotify != null)
        {
            try
            {
                fileNotify.SetStart(TOC.CurrentSetIndex, in _stats);
            }
            catch (Exception ex2)
            {
                // in statistics notification, we don't rethrow TapeAbortRequestedException
                m_logger.LogWarning("Exception {Exception} while notifying batch start", ex2);
            }
        }
    }
    protected void NotifySetEnd(ITapeFileNotifiable? fileNotify)
    {
        RefreshBytesTotalEstimate();
        if (fileNotify != null)
        {
            try
            {
                fileNotify.SetEnd(TOC.CurrentSetIndex, in _stats);
            }
            catch (Exception ex2)
            {
                // in statistics notification, we don't rethrow TapeAbortRequestedException
                m_logger.LogWarning("Exception {Exception} while notifying batch end", ex2);
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
