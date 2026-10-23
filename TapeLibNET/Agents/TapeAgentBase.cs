using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using TapeLibNET.Drive;
using TapeLibNET.Format;
using TapeLibNET.Headers;
using TapeLibNET.Media;
using TapeLibNET.Streams;
using TapeLibNET.Toc;
using Windows.Win32.Foundation;

namespace TapeLibNET.Agents;


/// <summary>
/// Base agent handling TOC backup/restore (dual-copy with CRC), TOC file I/O,
///  media / set header I/O, set verification, and the <see cref="ITapeFileNotifiable"/> notification wrappers.
/// <para>Subclasses: <see cref="TapeFileBackupAgent"/> (backup), <see cref="TapeSetAgent"/> (set deletion), <see cref="TapeFileRestoreBaseAgent"/>
///  (restore/verify). Owns a <see cref="TapeStreamManager"/> and a <see cref="TapeTOC"/>.</para>
/// </summary>
public partial class TapeAgentBase : TapeDriveHolder<TapeAgentBase>, IDisposable
{
    /// <summary>BlockSize used for header and TOC read / write, fixed since it needs to be known upfront.</summary>
    private const uint c_fixedTOCBlockSize = TapeHeader.FixedHeaderBlockSize; // 16 KiB — TOC and headers share one block size (§5.7)

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

    // The agent's own emitter: created with the agent, disposed with it.
    private readonly TapeRecordEmitter21 m_emitterOwn = new();
    // A borrowed emitter (tests: the legacy one), or null = use our own. Never disposed by the agent.
    private ITapeRecordEmitter? m_emitterOverride;
    /// <summary>
    /// Turns records into bytes: per-file header, codec prefix, header blocks, TOC copies. Format 2.1 in the product,
    ///  through the agent's OWN emitter; tests set another to write genuine legacy media (<see cref="ITapeRecordEmitter"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Setting <see langword="null"/> returns to the agent's own emitter</b> — how a mixed-series fixture switches back
    ///  from a legacy volume to 2.1 on the same agent.
    /// </para>
    /// <para>
    /// <b>Ownership.</b> The agent disposes only its own emitter. An emitter set from outside belongs to whoever set it.
    /// </para>
    /// </remarks>
    [AllowNull]
    internal ITapeRecordEmitter RecordEmitter
    {
        get => m_emitterOverride ?? m_emitterOwn;
        set => m_emitterOverride = ReferenceEquals(value, m_emitterOwn) ? null : value;
    }

    /// <summary>Cumulative bytes written to tape (content + TOC) during this agent's lifetime.</summary>
    public long BytesBackedup { get; protected set; } = 0L;
    /// <summary>
    /// Bytes of the last TOC copy written by <see cref="BackupTOC"/>; 0 before any. For the F4 footprint check
    /// .</summary>
    public long LastTOCCopyBytes { get; private set; }
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

#if TEST_HOOKS
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
    /// Whether the mounted volume ends INSIDE its last set: that set was closed by end-of-media, not by a setmark (F32).
    ///  Honoured on TOC-in-partition media only; a TOC claiming it for another layout is ignored rather than trusted.
    /// </summary>
    protected bool VolumeEndsInsideLastSet
    {
        get
        {
            if (Navigator is not TapeNavigatorTOCInPartition || TOC.Count == 0)
                return false;
            TapeSetTOC last = TOC[TOC.LastSetOnVolume];
            return last.Volume == TOC.Volume && last.ClosedByEom;
        }
    }

    /// <summary>
    /// Translates <see cref="TapeTOC.CurrentSetIndex"/> to a <see cref="TapeNavigator.TargetContentSet"/> value,
    ///  choosing the most efficient seek direction (from begin, end, or current position).
    /// </summary>
    /// <remarks>
    /// <para>
    /// End-relative indices count setmarks back from end of content: −1 lies after the last setmark, −2 is the newest set,
    ///  −3 the one before, …  On a volume whose last set ended at end-of-media (F32) no setmark follows that set, so the
    ///  newest set is −1 and every end-relative index shifts by one. The navigator knows no TOC; this is the one place
    ///  that translates.
    /// </para>
    /// <para>
    /// The navigator's position is converted with the volume's OWN last set, not the TOC's: on an earlier volume of a
    ///  series the TOC holds later sets that are not on this tape.
    /// </para>
    /// </remarks>
    protected int CurrentSetAsNavigatorContentSet(bool fromBeginOnly = false)
    {
        int toBegin = TOC.CurrentSetIndexOnVolume; // same as TOC.CurrentSetIndex - TOC.FirstSetOnVolume
        if (toBegin == 0)
            return 0; // the first content set on volume should always be accessed from the beginning
        if (fromBeginOnly)
            return toBegin;

        int newest = VolumeEndsInsideLastSet ? -1 : -2;                // end-relative index of the volume's newest set
        int lastOnVolume = TOC.LastSetOnVolume - TOC.FirstSetOnVolume;  // its on-volume index
        int toEnd = lastOnVolume - toBegin;

        // Optimization: consider Navigator's current position when choosing how to specify the content set
        int toCurr = int.MaxValue;
        if (!Navigator.CurrentContentSetIsSentinel)
        {
            int navCurr = (Navigator.CurrentContentSet >= 0)
                ? Navigator.CurrentContentSet
                : lastOnVolume - (newest - Navigator.CurrentContentSet);
            if (navCurr >= 0)
                toCurr = Math.Abs(navCurr - toBegin);
        }

        if (toCurr <= toBegin && toCurr <= toEnd)
        {
            // retain the sign of Navigator.CurrentContentSet: continue counting from the side it counted from
            return (Navigator.CurrentContentSet >= 0) ? toBegin : newest - toEnd;
        }

        return (toEnd < toBegin) ? newest - toEnd : toBegin;
    }

    #endregion

    #region Constructors

    public TapeAgentBase(TapeDrive drive, TapeTOC? legacyTOC = null) : base(drive)
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

    /// <summary>
    /// Overridable <see cref="IDisposable"/> implementation via <c>virtual Dispose(bool)</c>.
    /// </summary>
    /// <param name="disposing"></param>
    protected virtual void Dispose(bool disposing)
    {
        if (!IsDisposed)
        {
            m_logger.LogTrace("Disposing TapeAgentBase with disposing parameter = {Parametr}", disposing);
            if (disposing)
            {
                // Dispose managed resources: our own emitter returns its pooled scratch.
                //  A borrowed emitter belongs to whoever lent it. The ciphers zero their chunk buffers.
                m_emitterOwn.Dispose();
                DisposeEncryption();
            }
            // Dispose unmanaged resources
            //  (no unmanaged resources)
            IsDisposed = true;
        }
    }

    // do not override
    ~TapeAgentBase()
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
    ///  is detected, AFTER the error on <see langword="this"/> has been set (e.g. via
    ///  <see cref="ErrorManageableBase.SetError(uint, string?)"/>).
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

    /// <summary>
    /// The LATCHED failure only — the operation's earliest genuine fault — ignoring the live error.
    /// </summary>
    /// <remarks>
    ///  <see cref="LastResult"/> falls back to the live error when nothing is latched; across a volume
    ///  change that live error is a mere continuation signal (end-of-media, a volume request) or
    ///  a rejected cartridge, and must never be promoted into the latch. Leaves the live error untouched.
    /// </remarks>
    protected TapeResult LatchedFailure => _resultBuilder.FirstFailure;

    #endregion

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

    /// <summary>
    /// Writes a single TOC copy to the tape.
    /// </summary>
    /// <returns><see langword="true"/> only if every byte reached the tape.</returns>
    private bool BackupTOCCore()
    {
        try
        {
            using var wstream = OpenWriteTOCStream();
            if (wstream == null)
                return false;

#if TEST_HOOKS
            // Simulate TOC copy failure based on bitmask
            if (SimulateTOCFailureMask != 0 && (SimulateTOCFailureMask & (1 << _tocCopyCounter++)) != 0)
                throw new IOException("Simulated TOC backup failure");
#endif

            // NOTE: no ThrowIfAbortRequested here — TOC writing is a critical
            // data-integrity operation and must never be aborted.

            // The emitter owns the format and the integrity trailer; it never disposes wstream.
            RecordEmitter.WriteTOC(TOC, wstream);
            LastTOCCopyBytes = wstream.Length;
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
    /// Writes two copies of the <see cref="TOC"/> to tape, each a 2.1 TOC stream with its CRC-64.
    /// <para>
    /// Succeeds if at least one copy is written successfully. The dual-copy
    ///  strategy ensures TOC recoverability even with partial media damage.
    /// </para>
    /// <para>
    /// The 2nd copy is not attempted when the 1st ran into end-of-media — its data or its closing filemark: it cannot fit,
    ///  and the attempt would only leave a stray partial block (Design-TocLocation W1).
    /// </para>
    /// <para>
    /// <b>Every copy that does not reach the tape is a TOC anomaly</b> (Design-TocLocation §5): one copy missing is a
    ///  success with <see cref="TapeTocAnomalyKind.CopyWriteFailed"/>, none is a failure with
    ///  <see cref="TapeTocAnomalyKind.NoCopyWritten"/> — each carrying the drive's own error. A later success on the same
    ///  volume settles them (<see cref="TapeTocAnomalyStage.Retried"/>), e.g. the service's enforced rewrite.
    /// </para>
    /// <para>
    /// On success, clears <see cref="TapeTOC.LoadedFromLegacy"/>: from here on the tape carries a 2.1 TOC.
    /// </para>
    /// <para>
    /// <b>Notice</b> this public call <b>resets the latched error</b>, hence make sure
    ///  to capture it before calling. It does NOT clear the TOC anomaly records: they belong to the operation.
    /// </para>
    /// </summary>
    /// <param name="enforce">When <see langword="true"/>, resets navigator state before writing
    ///  (use after operations that may leave the tape position uncertain).</param>
    /// <param name="tocNotify">Optional callback for TOC anomalies.</param>
    public TapeResult BackupTOC(bool enforce = false, ITapeTocNotifiable? tocNotify = null)
    {
        ResetLatchedFailure();
        LastTOCCopyBytes = 0;

        // We stamp a stable media identity before the first durable write. New media (just
        //  formatted) and legacy Guid-less media (just loaded) both reach here with an empty
        //  MediaId; we mint once, then it persists across every rewrite and across all volumes.
        //  Both TOC copies serialize the same value, since we mint into the in-memory TOC here.
        TOC.EnsureMediaId();

#if TEST_HOOKS
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
                TapeResult refused = FailedOperationResult;
                NotifyTocAnomaly(tocNotify, BuildTocAnomaly(TapeTocAnomalyKind.NoCopyWritten, TOC.Volume, 0, refused));
                return refused;
            }

            m_logger.LogTrace("Enforcing TOC backup by resetting content set");
        }

        if (!BeginWriteTOC())
        {
            m_logger.LogError("Failed to begin TOC write in {Method}", nameof(BackupTOC));
            TapeResult notPositioned = FailedOperationResult;
            NotifyTocAnomaly(tocNotify, BuildTocAnomaly(TapeTocAnomalyKind.NoCopyWritten, TOC.Volume, 0, notPositioned));
            return notPositioned;
        }

        // To ensure TOC integrity, backup TOC twice
        bool result1 = BackupTOCCore();

        // W1: did the 1st copy run into end-of-media — its data (result1 false: the agent's own error, set by
        //  BackupTOCCore from the stream's exception), or only its closing filemark (the drive's error, left by the
        //  failed WriteFilemark after the stream signed out)?
        bool copy1HitEom = result1 ? Drive.RanToEOM : RanToEOM;

        // Captured NOW: the error is reset before the 2nd copy. The drive's own error, wherever it reported one.
        TapeResult failure1 = result1 ? TapeResult.OK : TapeResult.Fail(this);

        if (result1 && !copy1HitEom)
            m_logger.LogTrace("TOC 1st copy backed up ok");
        else if (result1)
            m_logger.LogWarning("TOC 1st copy written, but its closing filemark did not fit (end-of-media)");
        else
            m_logger.LogWarning("TOC 1st copy backup failed");

        bool result2 = false;
        TapeResult failure2;
        if (copy1HitEom)
        {
            m_logger.LogWarning("TOC 2nd copy skipped: the volume is full — only {Copies} copy on tape",
                result1 ? "one" : "no");
            failure2 = TapeResult.Fail((uint)WIN32_ERROR.ERROR_END_OF_MEDIA,
                "TOC copy 2 not attempted: the volume is full");
        }
        else
        {
            m_logger.LogTrace("Backing up TOC, 2nd copy");
            ResetError();
            if (result1)
                ResetLatchedFailure(); // copy 1 is on tape: a failing copy 2 is no failure of the operation

            result2 = BackupTOCCore();
            failure2 = result2 ? TapeResult.OK : TapeResult.Fail(this);
            if (result2)
                m_logger.LogTrace("TOC 2nd copy backed up ok");
            else
                m_logger.LogWarning("TOC 2nd copy backup failed");
        }

        // F4: does this layout's full TOC footprint fit the reserve armed for content? A TOC that outgrew it loses its
        //  2nd copy on a full volume. (TOC-in-set only: with an initiator partition there is no reserve.)
        if (Navigator is TapeNavigatorTOCInSet && LastTOCCopyBytes > 0)
        {
            long footprint = Navigator.TOCFootprint(LastTOCCopyBytes);
            if (footprint > Navigator.TOCCapacity)
                m_logger.LogWarning(
                    "The TOC ({Bytes:N0} B) needs {Footprint:N0} B in this layout, more than its reserve of {Reserve:N0} B — " +
                    "on a full volume only one copy may fit", LastTOCCopyBytes, footprint, Navigator.TOCCapacity);
        }

        // ── Copy accounting (Design-TocLocation §5.1) ──
        int written = (result1 ? 1 : 0) + (result2 ? 1 : 0);
        _stats.TocStats.CopiesWritten += written;
        _stats.TocStats.CopyWriteFailures += 2 - written;

        if (written == 0)
        {
            TapeResult failed = FailedOperationResult;
            NotifyTocAnomaly(tocNotify, BuildTocAnomaly(TapeTocAnomalyKind.NoCopyWritten, TOC.Volume, 0,
                failure1.Success ? failed : failure1));
            return failed;
        }

        if (!result1)
            ResetLatchedFailure(); // copy 2 is on tape: succeed — the missing copy is an anomaly, not a failure

        // A TOC is on this volume now: settle the earlier failures of this operation it makes good — a lost TOC by any
        //  copy, a missing copy only by both.
        if (written == 2)
            ResolveTocAnomalies(tocNotify, TOC.Volume, TapeTocAnomalyStage.Retried,
                TapeTocAnomalyKind.NoCopyWritten, TapeTocAnomalyKind.CopyWriteFailed, TapeTocAnomalyKind.NotFoundNatively);
        else
            ResolveTocAnomalies(tocNotify, TOC.Volume, TapeTocAnomalyStage.Retried,
                TapeTocAnomalyKind.NoCopyWritten, TapeTocAnomalyKind.NotFoundNatively);

        if (written == 1)
            NotifyTocAnomaly(tocNotify, BuildTocAnomaly(TapeTocAnomalyKind.CopyWriteFailed, TOC.Volume,
                copyIndex: result1 ? (byte)2 : (byte)1, result1 ? failure2 : failure1));

        // What now stands on tape: 2.1 in the product — a legacy tape is upgraded (the service logs it, §9.2, F28).
        TOC.LoadedFromLegacy = RecordEmitter.DataFormat == TapeDataFormat.Legacy;
        return TapeResult.OK;
    } // BackupTOC()

    /// <summary>
    /// Backs up the TOC onto media that is known to be blank (e.g. just formatted).
    ///  Equivalent to <see cref="BackupTOC"/> but tells the navigator that no
    ///  existing TOC mark or content needs to be located first.
    /// </summary>
    /// <param name="writeHeader">
    /// Write the media header before the initial TOC. TRUE only on the format / fresh-media path;
    ///  FALSE on the delete-all path (which must preserve, never rewrite, the existing header — §10.8).
    ///  Defaults to <see cref="WritesMediaHeader"/> so continuation heading (which sets the flag) works.
    /// </param>
    /// <param name="tocNotify">Optional callback for TOC anomalies, passed to <see cref="BackupTOC"/>.</param>
    public TapeResult BackupInitialTOC(bool? writeHeader = null, ITapeTocNotifiable? tocNotify = null)
    {
        Navigator.AssumeBlankMedia();

        if (writeHeader ?? WritesMediaHeader)
        {
            var hr = WriteMediaHeader();
            if (!hr) return hr;
        }

        return BackupTOC(tocNotify: tocNotify);
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

#if TEST_HOOKS
            // Simulate TOC copy failure based on bitmask
            if (SimulateTOCFailureMask != 0 && (SimulateTOCFailureMask & (1 << _tocCopyCounter++)) != 0)
                throw new IOException("Simulated TOC restore failure");
#endif

            ThrowIfAbortRequested($"load TOC core");

            // Either format: a 2.1 TOC stream, or a legacy TOC (layout detection) - both CRC-64 verified
            TapeTOC toc = TapeTOC.LoadFrom(rstream);
            TOC.CopyFrom(toc);
            BytesRestored += rstream.Length;
            return true;
        }
        catch (TapeAbortRequestedException)
        {
            m_logger.LogWarning("TOC restore aborted by user request");
            IsAbortRequested = true; // should be set already, but doesn't hurt to ensure
                                     // No need for user-requested abort to LatchFailure()
            return false;
        }
        catch (TapeFormatException ex)
        {
            // ANY format error means THIS copy is bad - damage usually surfaces before the CRC is compared.
            //  RestoreTOC() then tries the other copy.
            SetError(ex.Kind == FormatErrorKind.CrcMismatch ? WIN32_ERROR.ERROR_CRC : WIN32_ERROR.ERROR_INVALID_DATA,
                $"TOC copy unreadable: {ex.Message}");
            LatchFailure();
            m_logger.LogWarning("TOC copy unreadable ({Kind}): {Message}", ex.Kind, ex.Message);
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
    /// <b>Every deviation is a TOC anomaly</b> (Design-TocLocation §5): a 1st copy that did not read while the 2nd did
    ///  (<see cref="TapeTocAnomalyKind.CopyUnreadable"/>, recovered by <see cref="TapeTocAnomalyStage.Sibling"/>); a TOC
    ///  position that could not be reached (<see cref="TapeTocAnomalyKind.NotFoundNatively"/>); no copy readable there
    ///  (<see cref="TapeTocAnomalyKind.NoReadableCopy"/>). An abort is none of them.
    /// </para>
    /// <para>
    /// <b>Notice</b> this public call <b>resets the latched error</b>, hence make sure
    ///  to capture it before calling. It does NOT clear the TOC anomaly records: they belong to the operation.
    /// </para>
    /// </summary>
    /// <param name="tocNotify">Optional callback for TOC anomalies.</param>
    public TapeResult RestoreTOC(ITapeTocNotifiable? tocNotify = null)
        => RestoreTOCFromMedia(expectedVolume: null, tocNotify);

    /// <summary>
    /// <see cref="RestoreTOC"/>, knowing which volume the cartridge should be — a multi-volume resume. Names the volume
    ///  in an anomaly when no copy reads, which is the only case where the cartridge cannot tell it itself (TL-10).
    /// </summary>
    /// <param name="expectedVolume">The volume requested; <see langword="null"/> when nothing is expected.</param>
    /// <param name="tocNotify">Optional callback for TOC anomalies.</param>
    internal TapeResult RestoreTOCFromMedia(int? expectedVolume, ITapeTocNotifiable? tocNotify)
    {
        ResetLatchedFailure();

#if TEST_HOOKS
        _tocCopyCounter = 0;
#endif
        // Since TOC is stored twice, if the first attempt fails, try again
        m_logger.LogTrace("Restoring TOC from 1st copy");
        if (!BeginReadTOC())
        {
            m_logger.LogError("Failed to begin TOC read in {Method}", nameof(RestoreTOC));
            TapeResult notFound = FailedOperationResult;
            if (!IsAbortRequested)
                NotifyTocAnomaly(tocNotify, BuildTocAnomaly(TapeTocAnomalyKind.NotFoundNatively,
                    expectedVolume ?? 0, 0, notFound));
            return notFound;
        }

        TapeResult copy1Failure = TapeResult.OK;
        bool result = RestoreTOCCore();
        if (result)
            m_logger.LogTrace("TOC restored from 1st copy");
        else
        {
            copy1Failure = TapeResult.Fail(this);   // captured before the retries reset it

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

        if (!result)
        {
            TapeResult failed = FailedOperationResult;
            if (!IsAbortRequested)
                NotifyTocAnomaly(tocNotify, BuildTocAnomaly(TapeTocAnomalyKind.NoReadableCopy,
                    expectedVolume ?? 0, 0, failed));
            return failed;
        }

        // The TOC read — it names its volume now. Settle what this operation could not read there before, then
        //  report the copy that did not read.
        int volume = TOC.Volume;
        ResolveTocAnomalies(tocNotify, volume, TapeTocAnomalyStage.Retried,
            TapeTocAnomalyKind.NotFoundNatively, TapeTocAnomalyKind.NoReadableCopy);

        if (!copy1Failure.Success && !IsAbortRequested)
        {
            TapeTocAnomaly unreadable = BuildTocAnomaly(TapeTocAnomalyKind.CopyUnreadable, volume, 1, copy1Failure);
            NotifyTocAnomaly(tocNotify, unreadable);
            NotifyTocAnomalyRecovered(tocNotify,
                unreadable with { Stage = TapeTocAnomalyStage.Sibling, Diagnosis = TapeResult.OK });
        }

        return TapeResult.OK;
    }

    /// <summary>Begins a TOC read at a caller-located copy. Mirrors <see cref="BeginReadTOC"/>.</summary>
    private bool BeginReadTOCAt(long block, MediaPartition partition)
    {
        // Do NOT EnsureMediaHeaderResolved() here — positioning is absolute; header presence plays no part.
        if (!Manager.BeginReadTOCAt(block, partition))
        {
            m_logger.LogWarning("Failed to begin reading the TOC copy at block {Block} of the {Partition} partition",
                block, partition);
            SyncErrorFrom(Manager);
            LatchFailure();
            return false;
        }

        Drive.SetBlockSize(c_fixedTOCBlockSize);
        return true;
    }

    /// <summary>
    /// Reads the <see cref="TOC"/> from the single copy starting at <paramref name="block"/> of
    ///  <paramref name="partition"/>, bypassing the navigator's own search for the TOC. On success, replaces the current
    ///  <see cref="TOC"/> content.
    /// </summary>
    /// <param name="block">
    /// The copy's first block — typically a <see cref="Scan.FragmentKind.TOC"/> fragment's <c>StartBlock</c>. To read
    ///  "where the head is", pass <c>Drive.CurrentBlock</c> explicitly.
    /// </param>
    /// <param name="partition">
    /// Where the copy stands — typically the fragment's <c>Partition</c>. <see cref="MediaPartition.Current"/> means "where
    ///  this layout keeps its TOC". A scan passes where it FOUND the copy: on a mixed-up cartridge that need not be the
    ///  layout's partition.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The same contract as <see cref="RestoreTOC"/></b>: returns a <see cref="TapeResult"/>, fills
    ///  <see cref="TOC"/> via <c>CopyFrom</c> on success, resets the latched error on entry.
    /// </para>
    /// <para>
    /// <b>One copy, no fallback.</b> <see cref="RestoreTOC"/> retries by stepping to the next filemark,
    ///  which assumes it started at the FIRST copy. Here the caller names the copy — it may be the second —
    ///  so a retry would read whatever follows it. To try another copy, call again with its block.
    /// </para>
    /// <para>
    /// <b>Leaves no believed position.</b> The block came from evidence the navigator cannot verify — a copy
    ///  found mid-tape may be a stale leftover, even another series'. So the read session is ended and the
    ///  navigator reset, whatever the outcome: the next operation re-locates from scratch.
    /// </para>
    /// <para>
    /// The drive's block size is left at the TOC block size, as <see cref="RestoreTOC"/> leaves it. The
    ///  scanner's own guard restores it on its path.
    /// </para>
    /// <para>
    /// <b>Why the block is not optional.</b> A default of "where the head is" would make the unanchored
    ///  read the easy one on the product's most sensitive path. Passing <c>Drive.CurrentBlock</c> costs a
    ///  caller one token and says what it means; <see cref="TapeDrive.MoveToBlock"/> makes it free.
    /// </para>
    /// </remarks>
    public TapeResult RestoreTOCAt(long block, MediaPartition partition = MediaPartition.Current)
    {
        ResetLatchedFailure();
#if TEST_HOOKS
        _tocCopyCounter = 0;
#endif
        m_logger.LogTrace("Restoring TOC from the copy at block {Block} of the {Partition} partition", block, partition);
        try
        {
            if (!BeginReadTOCAt(block, partition))
                return FailedOperationResult;

            bool result = RestoreTOCCore();
            if (result)
                m_logger.LogTrace("TOC restored from the copy at block {Block}", block);
            else
                m_logger.LogWarning("TOC restore from the copy at block {Block} failed", block);

            if (IsAbortRequested && WentOK)
                SetError(WIN32_ERROR.ERROR_CANCELLED, "TOC loading aborted by user");
            return result ? TapeResult.OK : FailedOperationResult;
        }
        finally
        {
            // Runs AFTER the return value is computed, so the diagnosis above is already captured.
            //  End the session and believe nothing about the position — see the remarks.
            Manager.EndReadWrite();
            Navigator.ResetContentSet();
        }
    }

    #endregion // *** TOC Restore ***

    #region *** TOC File I/O ***

    /// <summary>
    /// File extension for emergency TOC files.
    /// </summary>
    public const string TOCFileExtension = ".tapetoc";

    /// <summary>
    /// Saves the current TOC to a file in the same 2.1 format and with the same CRC-64 as the on-tape copy.
    ///  The file is self-validating via its CRC-64 trailer.
    /// </summary>
    /// <remarks>
    /// Does not clear <see cref="TapeTOC.LoadedFromLegacy"/>: an export upgrades nothing on tape.
    /// </remarks>
    /// <param name="filePath">Full path to the file to create/overwrite.</param>
    /// <returns>Result indicating success or failure with error details.</returns>
    public TapeResult SaveTOCToFile(string filePath)
    {
        try
        {
            m_logger.LogTrace("Saving TOC to file: {Path}", filePath);
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            TOC.SaveTo(fs);
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
    /// Loads a TOC from a file previously saved by <see cref="SaveTOCToFile"/> — or by a legacy build, whose
    ///  format is still read. CRC validation is identical to the on-tape copy.
    ///  On success, the loaded TOC replaces the current <see cref="TOC"/> content.
    /// </summary>
    /// <param name="filePath">Full path to the TOC file to load.</param>
    /// <returns>Result indicating success or failure with error details.</returns>
    public TapeResult LoadTOCFromFile(string filePath)
    {
        try
        {
            m_logger.LogTrace("Loading TOC from file: {Path}", filePath);
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            TOC.CopyFrom(TapeTOC.LoadFrom(fs));
            m_logger.LogTrace("TOC loaded from file successfully: {Sets} set(s)", TOC.Count);
            return TapeResult.OK;
        }
        catch (TapeFormatException ex) when (ex.Kind == FormatErrorKind.CrcMismatch)
        {
            m_logger.LogWarning("CRC check failed for TOC file {Path}", filePath);
            SetError(WIN32_ERROR.ERROR_CRC, "CRC check failed for TOC file");
            return FailedOperationResult;
        }
        catch (TapeFormatException ex)
        {
            m_logger.LogWarning("Failed to read TOC from file {Path}: {Message}", filePath, ex.Message);
            SetError(WIN32_ERROR.ERROR_INVALID_DATA, $"Failed to read TOC from file: {ex.Message}");
            return FailedOperationResult;
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
        _stats.SetStats.SetsProcessed++;
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
            _stats.SetStats.SetsSucceeded++;

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
    /// Records a file lost to a media write fault — counted as failed, reported without asking (see
    ///  <see cref="ITapeFileNotifiable.OnFileLost"/>). Mirrors <see cref="NotifyFileFailed"/>, minus the decision.
    /// </summary>
    protected void NotifyFileLost(ITapeFileNotifiable? fileNotify, TapeFileInfo fileInfo, TapeResult reason)
    {
        _stats.FilesProcessed++;
        _stats.FilesFailed++;
        RefreshBytesTotalEstimate();

        m_logger.LogWarning("File >{File}< lost to a media write fault: {Reason}",
            fileInfo.FileDescr.FullName, reason.ErrorMessage);

        if (fileNotify is null)
            return;
        try
        {
            fileNotify.OnFileLost(fileInfo, reason, in _stats);
        }
        catch (TapeAbortRequestedException ex1)
        {
            m_logger.LogInformation("Abort requested while notifying a lost file: {Exception}", ex1);
            IsAbortRequested = true;    // the set ends anyway; the flag makes the result say "aborted"
        }
        catch (Exception ex2)
        {
            m_logger.LogWarning("Exception {Exception} while notifying a lost file", ex2);
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
    protected SetAnomalyAction NotifySetAnomaly(ITapeSetNotifiable? setNotify, in TapeSetAnomaly anomaly)
    {
        _setAnomalies.Add(anomaly);          // every OBSERVATION — the forensic trail
        m_setAnomalyInCurrentSet = true;
        // Do NOT yet set _stats.SetStats.SetWriteBlocked = true as we still might recover!
        //  do NOT! if (anomaly.IsDestructive) _stats.SetStats.SetWriteBlocked = true;

        m_logger.LogWarning("Set anomaly notified: {Anomaly}", anomaly);

        if (m_setAnomalyRaisedForSet == TOC.CurrentSetIndex)
            return SetAnomalyAction.Proceed;   // already counted and authorized for this set
        
        // Not yet counted and authorized for this set: record it and prompt the user
        m_setAnomalyRaisedForSet = TOC.CurrentSetIndex;
        _stats.SetStats.AnomaliesDetected++;       // count one per SET, the user-meaningful number

        RefreshBytesTotalEstimate();

        SetAnomalyAction result;
        if (setNotify is not null)
        {
            try
            {
                result = setNotify.OnSetAnomaly(in anomaly, in _stats);
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
    protected void NotifySetAnomalyRecovered(ITapeSetNotifiable? setNotify, in TapeSetAnomaly anomaly)
    {
        _stats.SetStats.AnomaliesRecovered++;
        if (anomaly.Stage == TapeSetAnomalyStage.Renavigated)
            _stats.SetStats.AnomaliesRecoveredFromBom++;

        m_logger.LogWarning("Set anomaly RECOVERED via {Stage}: {Anomaly}", anomaly.Stage, anomaly);
        RefreshBytesTotalEstimate();

        if (setNotify is null)
            return;
        try
        {
            setNotify.OnSetAnomalyRecovered(in anomaly, in _stats);
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
            // DisplayName never throws: a locked set reads "[encrypted set]" (Design-Encryption §6.2)
            ExpectedDescription: string.IsNullOrWhiteSpace(TOC.CurrentSetTOC.DisplayName)
                ? $"Set #{TOC.CurrentSetIndex}" : TOC.CurrentSetTOC.DisplayName,
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

} // class TapeAgentBase

// namespace TapeNET
