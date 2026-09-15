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

    // Checked periodically if the entire operation should be aborted
    //  Uses olatile field instead of auto-property — fixes the theoretical data race
    private volatile bool _isAbortRequested = false;
    /// <summary>
    /// Volatile abort flag checked periodically by file-processing loops.
    /// <para>Set by <see cref="NotifyFileFailed"/> when the callback returns <see cref="FileFailedAction.Abort"/>,
    ///  or directly by the caller (e.g. UI abort button). Checked via <see cref="ThrowIfAbortRequested"/>.</para>
    /// </summary>
    public bool IsAbortRequested
    {
        get => _isAbortRequested;
        set => _isAbortRequested = value;
    }

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
    /// </summary>
    protected int CurrentSetAsNavigatorContentSet // used by both backup and restore agents
    {
        get
        {
            int toBegin = TOC.CurrentSetIndexOnVolume; // same as TOC.CurrentSetIndex - TOC.FirstSetOnVolume
            if (toBegin == 0)
                return 0; // the first content set on volume should always be accessed from the beginning

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

    #region *** Set Header ***

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

    #endregion // *** Set Header ***

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
    /// Deletes all backup sets from <see cref="TapeTOC.CurrentSetIndex"/> through the last
    /// set on the current volume. Physically overwrites the tape past the last retained set
    /// to move the end-of-data marker, then updates the TOC on tape.
    /// <para>
    /// Preconditions:
    /// <list type="bullet">
    ///   <item><see cref="TapeTOC.CurrentSetIndex"/> must be set to the first set to delete.</item>
    ///   <item>The current set must be on the current volume
    ///     (<see cref="TapeTOC.IsCurrentSetOnVolume"/>).</item>
    ///   <item>When the current set is the first set on the volume AND the drive uses an
    ///     initiator partition, the operation fails — the caller should format the media
    ///     instead.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <remarks>Takes a special precaution to NOT overwrite the media header.</remarks>
    /// <param name="navigateFromBegin">
    /// If <see langword="true"/>, enforces navigator to count from the beginning of media --
    ///     useful if TOC is missing or corrupted, hence its filemarks should not be trusted.
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

        try
        {
            // §17.3: this method navigates content DIRECTLY (not via a backup content choke-point),
            //  so resolve header presence up front. Otherwise MoveToBeginOfContent / MoveToTargetContentSet
            //  on a headed tape would meet an unresolved Unknown → permissive Absent → no header skip →
            //  land at block 0 and clobber the media header. Idempotent / no-op once resolved.
            EnsureMediaHeaderResolved();

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
                //  (§10.8 / INV-4). MoveToBeginOfContent already positioned us at block 1 (past the
                //  header at block 0), so the fresh initial TOC overwrites content only; the header survives.
                return BackupInitialTOC(writeHeader: false);
            }
            else
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
                    Navigator.TargetContentSet = TOC.CurrentSetIndexOnVolume;
                }
                else
                {
                    Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet;
                }

                Navigator.MoveToTargetContentSet();
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return FailedOperationResult;
                }

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
    } // DeleteSetsFromCurrentSetUp()

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
        catch (Exception ex)
        {
            SetError(ex);

            m_logger.LogWarning("Exception {Exception} while restoring TOC", ex);
            // Stream disposal (using var) already cleared Manager/Drive errors;
            //  capture the exception on the agent so callers see a meaningful message
            LatchFailure();
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

    protected void ThrowIfAbortRequested(string where)
    {
        if (IsAbortRequested)
            throw new TapeAbortRequestedException($"Abort requested in {where}");
    }

} // class TapeFileAgent

// namespace TapeNET
