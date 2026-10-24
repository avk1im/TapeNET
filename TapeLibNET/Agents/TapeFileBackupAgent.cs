using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices;

using TapeLibNET.Compression;
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Media;
using TapeLibNET.Packer;
using TapeLibNET.Streams;
using TapeLibNET.Toc;
using TapeLibNET.Encryption;

namespace TapeLibNET.Agents;


/// <summary>
/// Backup agent — writes file lists to tape content sets with per-file CRC hashing,
///  incremental detection, and automatic multi-volume continuation on end-of-media.
/// </summary>
public class TapeFileBackupAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeAgentBase(drive, legacyTOC)
{
    // bytes backed up so far before we start writing a new set
    private long BytesBackedupMarker { get; set; } = 0L;
    /// <summary>
    /// Media bytes written since the current set began — the one genuinely PER-SET figure the agent
    ///  exposes, and the counterpart to the cumulative <see cref="TapeAgentBase.BytesBackedup"/>.
    /// </summary>
    /// <remarks>
    /// Anchored once, by <see cref="BeginWriteContentForCurrentSet"/>, and deliberately not re-anchored at set
    ///  end: the value must survive until the caller has reported it. Includes this set's TOC bytes when
    ///  read after the TOC save, hence reports exactly "what has been written to this volume".
    /// </remarks>
    public long BytesBackedupInCurrentSet => BytesBackedup - BytesBackedupMarker;

    // ── Software-compression session state ───────────────────────────────
    //  Allocated lazily on first use of the Software compression path and
    //  reused across every file in the session to avoid per-file GC pressure.
    private ProbingCompressionStream.Session? _compressionSession;

    /// <summary>
    /// Returns (and lazily creates) the session-scoped compression helper.
    /// </summary>
    private ProbingCompressionStream.Session GetOrCreateCompressionSession()
        => _compressionSession ??= new ProbingCompressionStream.Session();

    // ── Background total-size estimation ──────────────────────────────────
    //  Source files can be scanned (stat'd) concurrently with the backup itself,
    //  progressively growing Statistics.BytesTotal as the scan proceeds. Restore
    //  doesn't need this since all file sizes are already known upfront from the TOC.
    private FileSizeAggregator? _sizeAggregator;

    /// <summary>
    /// Whether this agent has stamped a media header — and with it the TOC's <c>MediaId</c> — onto ANY
    ///  volume during the current operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Operation-scoped, deliberately NOT reset per volume</b>, despite the header itself being a
    ///  per-volume artifact. Its one consumer is the service's TOC-rollback guard, and the thing that
    ///  rollback would restore is the <c>MediaId</c> — a SERIES identity. Once any volume carries the new
    ///  id, reverting the TOC to the old one describes a series that no longer exists, whatever the
    ///  current volume's state.
    /// </para>
    /// <para>
    /// Distinct from <see cref="TapeStreamManager.ContentWritten"/>: the header is written as a RAW BLOCK
    ///  at the top of <see cref="BeginWriteContentForCurrentSet"/>, before the content session opens, so a
    ///  failure between the two leaves a fresh header on tape with <c>ContentWritten == false</c>.
    /// </para>
    /// </remarks>
    public bool MediaHeaderStamped { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// A backup destroys what it lands on, and lands there by counting marks. Nothing it could learn
    ///  from an unverifiable block justifies proceeding (SH-13).
    /// </remarks>
    protected override bool BlocksOnUnverifiableSet => true;

    /// <summary>
    /// Starts (or restarts) a background estimate of the total logical size of <paramref name="files"/>,
    ///  progressively refreshed into <see cref="TapeFileStatistics.BytesTotal"/> — see
    ///  <see cref="RefreshBytesTotalEstimate"/>.
    /// </summary>
    protected void StartBackgroundSizeEstimate(IEnumerable<string?> files)
    {
        _sizeAggregator?.Dispose();
        _sizeAggregator = new FileSizeAggregator(files);
        _sizeAggregator.Start();
    }

    /// <summary>Stops and releases any active background size estimate.</summary>
    protected void StopBackgroundSizeEstimate()
    {
        _sizeAggregator?.Dispose();
        _sizeAggregator = null;
    }

    /// <inheritdoc/>
    protected override void RefreshBytesTotalEstimate()
    {
        if (_sizeAggregator != null)
            _stats.BytesTotal = _sizeAggregator.TotalSize;
    }

    protected override void Dispose(bool disposing)
    {
        if (!IsDisposed && disposing)
        {
            _compressionSession?.Dispose();
            _compressionSession = null;
            _sizeAggregator?.Dispose();
            _sizeAggregator = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Computes the remaining content capacity for the current set, based on the drive's calibrated estimate.
    /// </summary>
    /// <returns>The remaining content capacity in bytes.</returns>
    /// <remarks>
    /// The authoritative remaining-capacity figure is the drive's calibrated ESTIMATE
    ///  (quantity (6)). The TOC reserve is NOT subtracted here: it is armed once, at the drive,
    ///  via <see cref="TapeDrive.SetEarlyWarning"/> in <see cref="BeginWriteContentForCurrentSet"/>.
    ///  Subtracting it again would reserve room for the TOC twice and prematurely cut the set short
    ///  (see the multi-volume regression). Early warning is the real stop signal; this value is advisory
    ///  only (see <seealso cref="TapeStreamManager.CapacityForCurrentSet"/>).
    /// </remarks>
    private long ComputeRemainingCapacity()
    {
        return Math.Max(Drive.EstimatedContentRemaining, 0L);
    }

    /// <summary>
    /// Whether the set before the current one on this volume ran to end-of-media (F32): nothing fits behind it,
    ///  and "end of content" there is the START of that set — appending would overwrite it!
    /// </summary>
    private bool VolumeIsFullBeforeCurrentSet(bool newSet)
        => newSet
           && Navigator is TapeNavigatorTOCInPartition
           && TOC.CurrentSetIndexOnVolume > 0
           && TOC[TOC.CurrentSetIndex - 1] is { ClosedByEom: true } previous
           && previous.Volume == TOC.Volume;

    /// <summary>
    /// Prepares the <see cref="Drive"/>, <see cref="TapeAgentBase.Navigator"/>, and
    ///  <see cref="TapeAgentBase.Manager"/> for writing content to the current set, and leaves the head at
    ///  the set's first content block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it does, in order:</b> resolves media-header presence; applies the set's block size,
    ///  compression interlock and early-warning reserve; positions at the target set (recovering a failed
    ///  backward count, SH-20); <b>verifies</b> the set header already there when the write is
    ///  destructive; writes the media header (fresh volume) and the set header; then opens the content
    ///  write session.
    /// </para>
    /// <para>
    /// <b>Verification (SH-13).</b> On an OVERWRITE (<paramref name="newSet"/> <see langword="false"/>)
    ///  of a volume declaring set headers, the set header standing at the target is read and classified
    ///  before anything is destroyed. Only <c>Match</c> authorizes the write; every other verdict —
    ///  including <c>Unreadable</c>, which on a mark-counted write is the miscount's own signature —
    ///  fails the method with the tape untouched. A fresh set appended at end-of-data has no predecessor
    ///  record to read, so that path performs no verification and costs nothing.
    ///  <see cref="TapeAgentBase.VerifiesSetHeader"/> opts out.
    /// </para>
    /// <para>
    /// <b>The positioning may recover (SH-20).</b> A backward count that fails outright with a positional
    ///  error is the same damaged-tail fault the set-header recovery repairs, so it gets the same cure —
    ///  see <see cref="TapeAgentBase.NavigateToTargetContentSet"/>. The recovery only changes WHERE the
    ///  head is; the verification below is unchanged and still blocks on anything but <c>Match</c>.
    /// </para>
    /// <para>
    /// <b>The media header is written LAST on the overwrite path.</b> It lives at BOM, and on a
    ///  TOC-in-set layout a BOM write truncates everything beyond it — so stamping it before the
    ///  verification would destroy the very record the verification reads, and the check would then
    ///  inspect the wreckage of the tape it exists to protect. Overwriting the first set of a volume
    ///  therefore defers the header write until after a positive verdict; every other path writes it up
    ///  front, where nothing is at risk.
    /// </para>
    /// <para>
    /// <b>The head returns to the set start (SH-15).</b> The verifying read advances one block, at least:
    ///  the set correction might've even moved to a different (corrected) set altogether; hence the write
    ///  must walk 1 block back to the set begin, or the set header would be stamped one block late and
    ///  every file address in the set would be off by one.
    /// </para>
    /// <para>
    /// The positioning is hoisted ahead of <c>Manager.BeginWriteContent</c> so verification and the set
    ///  header both have a window before the packer anchors on <see cref="TapeDrive.CurrentBlock"/>. That
    ///  positioning is idempotent (SH-4), so the manager's own call costs no second transport move.
    /// </para>
    /// </remarks>
    /// <param name="newSet">Whether the current set is a new set (append) rather than a rewrite.</param>
    /// <param name="fileNotify">Optional callback, for the set-level anomaly channel.</param>
    /// <returns><see langword="true"/> if preparation succeeded; otherwise <see langword="false"/>.</returns>
    private bool BeginWriteContentForCurrentSet(bool newSet, ITapeFileNotifiable? fileNotify)
    {
        // Heading a volume means writing the media header at BOM. On an overwrite we must NOT do that
        //  yet -- see the remarks: it would truncate what verification is about to read.
        bool headsVolume = TOC.CurrentSetIndex == TOC.FirstSetOnVolume && WritesMediaHeader;
        bool deferMediaHeaderWrite = headsVolume && !newSet;

        if (headsVolume && !deferMediaHeaderWrite)
        {
            bool stamped = WriteMediaHeader();      // sets presence Present, positions at block 1
            MediaHeaderStamped |= stamped;          // operation-scoped: never reset by a later volume
            if (!stamped)
            {
                // A volume whose header could not be written must not receive content: nothing could identify it
                m_logger.LogWarning("Failed to write the media header in {Method}",
                    nameof(BeginWriteContentForCurrentSet));
                return false;   // WriteMediaHeader has already set the error
            }
        }
        else
            EnsureMediaHeaderResolved();            // existing / headerless / deferred: probe → Present or Absent

        // If we were reading or writing, end it first - before setting the new set's parameters
        if (!Manager.EndReadWrite())
        {
            m_logger.LogWarning("Failed to end read/write in {Method}",
                nameof(BeginWriteContentForCurrentSet));
            SyncErrorFrom(Manager);
            return false;
        }

        // Optimization: set the target content set BEFORE transition to Content writing
        //  so that Navigator can optimize moving to the target content set once we call BeginWriteContent()
        Navigator.TargetContentSet = newSet
            ? ((TOC.CurrentSetIndexOnVolume > 0) ? -1 : 0)
            : CurrentSetAsNavigatorContentSet();

        BytesBackedupMarker = BytesBackedup; // important in case of multi-volume backup continuation

        // Design-TocLocation §8.1 (TL-3): an append goes where the TOC says content ends on THIS volume
        //  — confirmed by the tape, or cross-checked against the marks below when the tape couldn't confirm it.
        long recordedEnd = -1;
        RecordedEndOfContent endCheck = RecordedEndOfContent.NotApplicable;
        if (newSet && Navigator.TargetContentSet == -1)
        {
            recordedEnd = TOC.FirstBlockAfterContentOnVolume(TOC.Volume);
            endCheck = Navigator.CheckRecordedEndOfContent(recordedEnd);
        }
        bool crossCheckEnd = endCheck is RecordedEndOfContent.Unverifiable or RecordedEndOfContent.Disputed;

        // Apply the set's preferred block size BEFORE entering the writing-content state.
        //  This matters for the packed path: TapeStreamManager.EnsurePackerCreated() (called
        //  from BeginWriteContent) captures Drive.BlockSize at packer-creation time. If we
        //  set the block size only afterwards, the packer ends up with a stale value (e.g.
        //  the TOC's 16 KB block size) while the drive writes the new set's larger blocks --
        //  leading to BlocksWritten == 0 in the sink, no FilesCommitted events, and pending
        //  files that never get promoted.
        if (!Drive.SetBlockSize(TOC.CurrentSetTOC.BlockSize))
            m_logger.LogWarning("Failed to set block size to {Size0} in {Method}; proceeding with {Size1}",
                TOC.CurrentSetTOC.BlockSize, nameof(BeginWriteContentForCurrentSet), Drive.BlockSize);
        TOC.CurrentSetTOC.BlockSize = Drive.BlockSize; // in any case, ensure the set has what the manager has

        // Hardware compression interlock: disable HW compression for Software/None sets so the
        //  drive doesn't double-compress or interfere; leave it enabled for Hardware sets — unless the set's
        //  encryption scheme rules it out (software-encrypted data does not compress, §9.1).
        Drive.SetHardwareCompression(TOC.CurrentSetTOC.Compression == TapeCompression.Hardware
            && CipherFor(TOC.CurrentSetTOC).AllowsHardwareCompression);

        // Phase 3: arm the drive's logical early warning to reserve room for the TOC when it is
        //  co-located with content (TOC-in-set). The drive selects the best available mechanism
        //  (matching calibration, physical EW, or an a-priori estimate), so WriteDirect raises EW
        //  ~one TOC-reserve before EOM and the content write can stop cleanly and write the TOC.
        //  With an Initiator partition the TOC lives elsewhere, so no reserve is needed.
        Drive.SetEarlyWarning(Drive.HasInitiatorPartition ? 0L : Navigator.TOCCapacity);

        // Overwriting existing content (not appending at EOD) leaves the drive reporting capacity−EOD,
        //  which would trip a premature early warning and clamp the set to zero bytes. Anchor the EW
        //  logic at the approximate write position until the first write resets EOD:
        //   - newSet after other sets on this volume ⇒ append at EOD: the drive's figure is right (no anchor);
        //   - newSet heading the volume, header JUST written ⇒ the header write reset EOD: no anchor either;
        //   - newSet as first-on-volume otherwise ⇒ from begin-of-content: the media header (if any) lies behind
        //     the head and is NOT available to the TOC (Design-TocLocation F3 — a flat 0 forgot it);
        //   - !newSet ⇒ overwrite the current set (the TOC's sum already counts the header block).
        //  Irrelevant with an Initiator partition (no TOC reserve), so skip it there.
        if (!Drive.HasInitiatorPartition)
        {
            long headerFootprint = Navigator.MediaHeaderPresence == TapeHeaderPresence.Present
                ? TapeHeaderBlock.Size : 0L;
            bool headerJustWritten = headsVolume && !deferMediaHeaderWrite;
            long approxWritten = newSet
                ? (TOC.CurrentSetIndexOnVolume > 0 || headerJustWritten ? -1L : headerFootprint)
                : TOC.ComputeContentSizeOnTapeBeforeCurrentSet(Drive.BlockSize,
                    withSetHeaders: Navigator.SetHeadersExpected); // by now the header presence has
                                                                   //  already been resolved
            Drive.NotifyNextContentWritePosition(approxWritten);
        }

        // ── Position, verify, head (SH-4, SH-13, SH-15, SH-20) ───────────
        //  All three need the head at the set's first block, and all three must complete BEFORE
        //   Manager.BeginWriteContent(): it runs MoveToTargetContentSet() and EnsurePackerCreated() back
        //   to back with no seam, and the packer anchors on Drive.CurrentBlock -- so both headers must
        //   already be on tape for every file address to land past them (no TOC-address surgery).
        //  Why MediaHeaderPresence rather than the flag alone (SH-1): on a legacy volume nothing
        //   declares a set header's existence, so writing one would produce a block no reader can be
        //   told about. Presence is resolved by the gate at the top of this method.
        bool verifies = !newSet && VerifiesSetHeader && Navigator.SetHeadersExpected;
        bool writesSetHeader = WritesSetHeaders && Navigator.MediaHeaderPresence == TapeHeaderPresence.Present;

        if (verifies || writesSetHeader || deferMediaHeaderWrite || crossCheckEnd)
        {
            // SH-4: idempotent, so BeginWriteContent's own call below is a genuine no-op.
            // SH-20: a backward count that fails positionally is retried from begin-of-content.
            if (!NavigateToTargetContentSet(fileNotify))
            {
                m_logger.LogWarning("Failed to position at the target content set in {Method}",
                    nameof(BeginWriteContentForCurrentSet));
                return false;   // NavigateToTargetContentSet already synced the error
            }

            if (crossCheckEnd && !CrossCheckEndOfContent(recordedEnd, endCheck))
                return false;   // error set and latched; nothing written
        }

        if (verifies)
        {
            if (!VerifySetHeaderForCurrentSet(fileNotify))
            {
                // Nothing has been written yet -- the tape is exactly as we found it.
                m_logger.LogError("Refusing to overwrite set #{Set}: its set header did not verify",
                    TOC.CurrentSetIndex);
                LatchFailure();   // the verdict set the error; latch it past any later success
                return false;
            }

            // SH-15: undo the verifying read's one-block advance. DERIVED, not captured beforehand:
            //  the ladder (set correction) may have repositioned -> a pre-read block would then go stale.
            //  A positive verdict always ends on a successful set-header read -> the set start is 1 block back.
            long setStartBlock = Drive.CurrentBlock - 1;
            if (!Drive.MoveToBlock(setStartBlock))
            {
                m_logger.LogWarning("Failed to return to block {Block} after verifying set #{Set}",
                    setStartBlock, TOC.CurrentSetIndex);
                SyncErrorFrom(Drive);
                LatchFailure();
                return false;
            }
        }

        if (deferMediaHeaderWrite)
        {
            // Now safe: verification is done. WriteMediaHeader rewinds to BOM, writes, and leaves the
            //  head at begin-of-content -- which IS the target set here (CurrentSetIndexOnVolume == 0).
            bool stamped = WriteMediaHeader();
            MediaHeaderStamped |= stamped;
            if (!stamped)
            {
                m_logger.LogWarning("Failed to write the media header in {Method}",
                    nameof(BeginWriteContentForCurrentSet));
                return false;   // WriteMediaHeader has already set the error
            }
        }

        if (writesSetHeader)
        {
            // Placed AFTER SetBlockSize + the TOC reconciliation, so the header records the block size
            //  the drive ACTUALLY accepted; and AFTER SetEarlyWarning / NotifyNextContentWritePosition,
            //  so its 16 KiB counts against the TOC reserve like any other content byte. TapeHeaderBlock
            //  sets and restores its own block size, so the set's survives untouched.
            if (!WriteSetHeader())
            {
                m_logger.LogWarning("Failed to write the set header in {Method}",
                    nameof(BeginWriteContentForCurrentSet));
                return false;   // WriteSetHeader already set the error; SH-6 reset the position
            }
        }

        // The set's encryption session (Design-Encryption §9.1, E16): AFTER the set header — a drive key (v2) must
        //  never apply to it — and BEFORE the first content byte. Closed again below if the content session fails.
        if (!BeginSetEncryption(forWrite: true))
            return false;   // BeginSetEncryption set and latched the error; nothing written past the header

        long remainingCapacity = ComputeRemainingCapacity();
        if (!Manager.BeginWriteContent(remainingCapacity))
        {
            m_logger.LogWarning("Failed to transition to writing content in {Method}",
                nameof(BeginWriteContentForCurrentSet));
            SyncErrorFrom(Manager);
            EndSetEncryption();
            return false;
        }

        return true;
    } // BeginWriteContentForCurrentSet()

    /// <summary>
    /// The recorded end of content could not be confirmed on tape; the marks located one instead. Agreement of the two
    ///  independent sources authorizes the append; disagreement refuses it — one of them is wrong, and either could
    ///  name a set (TL-3).
    /// </summary>
    private bool CrossCheckEndOfContent(long recorded, RecordedEndOfContent check)
    {
        long landed = Drive.CurrentBlock;
        string why = check == RecordedEndOfContent.Unverifiable
            ? "the confirming read failed" : "the TOC area there is not as written";
        if (landed == recorded)
        {
            m_logger.LogWarning("End of content at block {Block}: {Why}, but the marks agree with the TOC — appending there",
                recorded, why);
            ResetError();
            return true;
        }

        SetError(WIN32_ERROR.ERROR_INVALID_DATA,
            $"End of content disputed: the TOC records block {recorded}, the marks lead to block {landed} ({why}) — " +
            "refusing to append at an unconfirmed position. Scan the media, or reload its table of contents.");
        m_logger.LogError("{Message}", LastErrorMessage);
        LatchFailure();
        return false;
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="pattern"/> contains <c>*</c> or <c>?</c> wildcards.</summary>
    public static bool HasWildcards(string pattern)
    {
        return pattern.Contains('*') || pattern.Contains('?');
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="pattern"/> denotes a directory (trailing backslash or existing directory).</summary>
    public static bool IsDirectory(string pattern)
    {
        return pattern.TrimEnd().EndsWith('\\') || Directory.Exists(pattern);
    }

    /// <summary>
    /// Expands file/directory patterns (with optional wildcards) into a deduplicated list of full file paths.
    /// </summary>
    /// <param name="fileAndDirectoryPatterns">Patterns: plain files, directories (trailing <c>\</c>), or wildcards.</param>
    /// <param name="recursive">When <see langword="true"/>, recurse into subdirectories.</param>
    public static List<string> BuildFileNameList(List<string> fileAndDirectoryPatterns, bool recursive)
    {
        List<string> fileNames = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void AddFile(string path)
        {
            if (seen.Add(path))
                fileNames.Add(path);
        }

        foreach (var pattern in fileAndDirectoryPatterns)
        {
            try
            {
                if (HasWildcards(pattern))
                {
                    // Split name into dirname and filename with wildcards
                    var directoryName = Path.GetDirectoryName(pattern);
                    var fileNameWithWildcards = Path.GetFileName(pattern);

                    // Expand wildcards and add files (recursively if requested)
                    var directoryPath = Directory.Exists(directoryName) ? Path.GetFullPath(directoryName) : 
                        string.IsNullOrEmpty(directoryName)? Directory.GetCurrentDirectory() : null;
                    if (!string.IsNullOrEmpty(directoryPath))
                    {
                        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        foreach (var file in Directory.EnumerateFiles(directoryPath, fileNameWithWildcards, searchOption))
                            AddFile(file);
                    }
                    // else non-existing directory -> ignore
                }
                else if (IsDirectory(pattern))
                {
                    // If it's a directory, add files (recursively if requested)
                    var directoryPath = Path.GetFullPath(pattern);
                    var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    foreach (var file in Directory.EnumerateFiles(directoryPath, "*", searchOption))
                        AddFile(file);
                }
                else
                {
                    // Otherwise, assume it's a file name
                    AddFile(Path.GetFullPath(pattern));
                }
            }
            catch
            {
                // cannot log since it's a static method -> simply ignore and continue with other patterns
                //m_logger.LogWarning("Exception {Exception} while building file name list for >{Pattern}<", ex, pattern);
            }
        } // foreach pattern

        return fileNames;
    }

    // The context with which we can resume backup on the next volume
    private struct TapeBackupContext(List<string> fileList, bool ignoreFailures, ITapeFileNotifiable? fileNotify, bool incremental)
    {
        internal readonly List<string> fileList = fileList;
        internal readonly bool ignoreFailures = ignoreFailures;
        internal readonly ITapeFileNotifiable? fileNotify = fileNotify;
        internal readonly bool incremental = incremental; // preserve original incremental flag across volume swaps

        internal int fileIndex = 0;
        internal bool overallSuccess = true;

        /// <summary>
        /// <see langword="true"/> once this context has survived an EOM and is resuming on a fresh volume.
        ///  Explicit rather than inferred from <see cref="fileIndex"/>, which can legitimately be <c>0</c>
        ///  on a continuation when end-of-media struck on the very first file.
        /// </summary>
        internal bool isContinuation = false;
        /// <summary><see langword="true"/> when the set on the previous volume had files written.</summary>
        internal bool prevVolumeHasFiles = false;

        /// <summary>
        /// Snapshot of the previous-volume set's metadata, captured at EOM time. The next
        ///  volume creates a fresh continuation set from these params (no cloning) -- the
        ///  previous-volume set instance may have been removed (RemoveLastEmptySet) before
        ///  saving the full volume's TOC, so we cannot rely on it being there at resume.
        /// </summary>
        internal TapeSetTOCParams? continuationSetParams = null;
    }

    private TapeBackupContext? MultiVolumeContext { get; set; } = null;

    // The fresh cartridge offered for the continuation could not be written — see IsVolumeRejected.
    private bool m_volumeRejected;

    // The operation's diagnosis when it stopped for a volume change. The caller saves the full volume's TOC with the
    //  public BackupTOC(), which resets the latch; the resume restores it, so an earlier failure survives the swap.
    private TapeResult m_carriedFailure = TapeResult.OK;

    /// <summary>
    /// Whether the agent ASKS for a fresh volume: save the TOC, load one, then call <see cref="ResumeBackupToNextVolume"/>.
    ///  <see langword="false"/> while the offered cartridge is rejected — so a caller looping on this property ends
    ///  instead of saving a TOC onto the refused cartridge and asking for yet another.
    /// </summary>
    public bool CanResumeToNextVolume => MultiVolumeContext is not null && !m_volumeRejected;

    /// <summary>
    /// Whether the fresh cartridge offered for the continuation could not be written. Nothing of the backup is on it;
    ///  the TOC is rolled back to the previous volume, whose TOC is already saved. The caller decides: load another
    ///  cartridge and call <see cref="ResumeBackupToNextVolume"/> again, or end — never save the TOC now.
    /// </summary>
    public bool IsVolumeRejected => MultiVolumeContext is not null && m_volumeRejected;

    /// <summary>Whether a multi-volume backup is pending — asking for a volume, or with a rejected one.</summary>
    public bool IsMultiVolumePending => MultiVolumeContext is not null;

    /// <summary>
    /// Continues the backup onto a new volume after the caller has loaded fresh media.
    /// <para>Renews the navigator, increments the volume number, creates a fresh
    ///  continuation set TOC seeded from the metadata captured at EOM time, and resumes
    ///  from the file that triggered end-of-media.</para>
    /// <para>No cloning of the previous-volume set is performed: per policy, the previous
    ///  volume's TOC may have had its trailing empty set removed before being saved, so
    ///  there may not be a suitable set to clone. Instead the metadata
    ///  (<see cref="TapeSetTOCParams"/>) is carried over via the multi-volume context.</para>
    /// <para>
    /// Anything that fails BEFORE the first content byte reaches the new cartridge REJECTS it
    ///  (<see cref="IsVolumeRejected"/>): the continuation set and the volume number are rolled back, nothing is on the
    ///  cartridge, and the caller may retry with another one — or end, with the previous volume's TOC already saved.
    /// </para>
    /// </summary>
    /// <summary>
    /// Continues the backup onto a new volume after the caller has loaded fresh media.
    /// <para>Renews the navigator, increments the volume number, creates a fresh
    ///  continuation set TOC seeded from the metadata captured at EOM time, and resumes
    ///  from the file that triggered end-of-media.</para>
    /// <para>No cloning of the previous-volume set is performed: per policy, the previous
    ///  volume's TOC may have had its trailing empty set removed before being saved, so
    ///  there may not be a suitable set to clone. Instead the metadata
    ///  (<see cref="TapeSetTOCParams"/>) is carried over via the multi-volume context.</para>
    /// <para>
    /// Anything that fails BEFORE the first content byte reaches the new cartridge REJECTS it
    ///  (<see cref="IsVolumeRejected"/>): the continuation set and the volume number are rolled back, nothing is on the
    ///  cartridge, and the caller may retry with another one — or end, with the previous volume's TOC already saved.
    /// </para>
    /// </summary>
    public TapeResult ResumeBackupToNextVolume()
    {
        // A multi-volume backup is ONE operation: the failure latched before the volume change must survive it. The
        //  caller's BackupTOC() between the volumes reset the latch — put it back.
        // NO MediaHeaderStamped = false; // the header is a series-level artifact, not per-volume

        // IsMultiVolumePending, not CanResumeToNextVolume: a retry after a rejection resumes too
        if (MultiVolumeContext is not { continuationSetParams: not null } context)
            return FailedOperationResult;

        // The live error that brought us here — END_OF_MEDIA, or the previous attempt's rejection — was a signal TO THE
        //  CALLER, and the caller has acted on it by calling us. Consume it: unlike restore, nothing on a successful
        //  backup resume clears the live error again, so it would otherwise outlive the retry and surface in LastResult.
        //  Only the latched failure crosses the volume boundary.
        ResetError();
        RestoreLatchedFailure(m_carriedFailure);

        int volume = TOC.Volume + 1;
        m_volumeRejected = false;
        m_logger.LogTrace("Resuming multi-volume backup for volume #{Volume}", volume);

        // since we're on the new media volume, renew Navigator
        if (!Manager.RenewNavigator())
        {
            SyncErrorFrom(Manager);
            return RejectVolume(volume, $"the drive could not be prepared for the cartridge ({LastErrorMessage})");
        }

        TOC.Volume++;
        TOC.ContinuedOnNextVolume = false;

        // Create the new set TOC from the metadata captured on EOM.
        //  contFromPrevVolume is true only when the set on the previous volume actually
        //  had files written -- otherwise the trailing empty set was (or should be)
        //  removed and the new set is logically a fresh start, not a continuation.
        var setParams = context.continuationSetParams with
        {
            Description = context.continuationSetParams.Description + $" ({TOC.Volume})",
            Incremental = context.incremental,
        };
        TOC.AddContinuationSetTOC(setParams,
            contFromPrevVolume: context.prevVolumeHasFiles);

        // A continuation set is created for the next volume; the emitter may differ here (mixed series).
        //  Stamp it before anything is written. A refusal here writes nothing either: reject, so the caller never saves
        //  a TOC onto the new cartridge.
        if (!EnsureSetFormat(TOC.CurrentSetTOC))
        {
            UndoContinuationSet();
            return RejectVolume(volume, LastErrorMessage);
        }

        if (BackupFilesToCurrentSet(newSet: true))
            return TapeResult.OK;

        if (m_volumeRejected)
        {
            // The cartridge refused the first write (BackupFilesToCurrentSet kept the context): nothing of the backup
            //  is on it. Back to the state before this call, so a retry starts clean.
            UndoContinuationSet();
            return RejectVolume(volume, LastErrorMessage);
        }

        return FailedOperationResult;
    }

    /// <summary>Removes the (empty) continuation set of a failed resume and returns to the previous volume.</summary>
    private void UndoContinuationSet()
    {
        TOC.RemoveLastEmptySet();
        TOC.Volume--;
        TOC.ContinuedOnNextVolume = true;   // as saved on the previous volume
    }

    /// <summary>
    /// Marks <paramref name="volume"/> rejected and returns why. The latch goes back to the carried failure: a rejected
    ///  cartridge is no part of the operation's diagnosis — unless the caller gives up, in which case the returned
    ///  result is it.
    /// </summary>
    private TapeResult RejectVolume(int volume, string reason)
    {
        m_volumeRejected = true;
        uint code = WentBad ? LastError : (uint)WIN32_ERROR.ERROR_NOT_READY;
        string message = $"Volume #{volume} cannot be written: {reason}";
        RestoreLatchedFailure(m_carriedFailure);
        SetError(code, message);
        m_logger.LogWarning("{Message}", message);
        return TapeResult.Fail(code, message);
    }

    // =====================================================================
    //  Packed backup (s. also doc comment of BackupFile):
    //
    //  Routes content writes through the shared-block packer, so small files can share tape blocks. The packer
    //  surfaces final TapeAddress values asynchronously via Manager.FilesCommitted, so:
    //    * TapeFileInfo for each file is constructed with its REAL address inside the commit handler
    //      (the loop's per-file template carries FileId + FileDescr only; its Address is unused),
    //    * NotifyPostProcessFile is deferred until the file is committed.
    // =====================================================================

    /// <summary>
    /// Writes the file (2.1 header + codec byte + hashed body) via the packer's per-file façade.
    ///  Returns the CommitToken so the caller can register the pending entry.
    ///  Throws on any failure -- the caller catches and decides recovery.
    /// </summary>
    /// <param name="template">The template TapeFileInfo containing file metadata.</param>
    /// <param name="hash">The output hash of the file's contents.</param>
    /// <param name="codec">The codec used for compression.</param>
    /// <returns>The CommitToken representing the pending commit operation.</returns>
    /// <exception cref="Exception">
    /// Throws on any failure -- the caller catches and decides recovery.
    /// </exception>
    /// <remarks>
    /// <b>Packed backup</b> routes content writes through the shared-block packer, so small files
    ///  can share tape blocks. The packer surfaces final <see cref="TapeAddress"/> values asynchronously
    ///  via <see cref="TapeStreamManager.FilesCommitted"/>, so that:
    /// <list type="bullet">
    ///    <item><see cref="TapeFileInfo"/> for each file is constructed with its REAL address inside the commit handler
    ///      (the loop's per-file template carries FileId + FileDescr only; its Address is unused),</item>
    ///    <item><see cref="TapeAgentBase.NotifyPostProcessFile"/> is deferred until the file is committed.</item>
    /// </list>
    /// <para>
    /// <b>On-tape layout</b> of a V2.1 file (Design-FormatV21 §5.3): self-describing, CRC-64 framed,
    ///  excluded from the hash, checked on restore against the set's <see cref="TapeSetTOC.SetId"/>
    ///  and the file's <see cref="TapeFileInfo.FileId"/>.
    ///  <code>
    ///   [file header frame: TpN# · kind 0x0201 · fields · CRC-64][codec u8][body]
    ///  </code>
    /// </para>
    /// </remarks>
    private CommitToken BackupFile(TapeFileInfo template, out byte[]? hash, out TapeFileCodec codec)
    {
        m_logger.LogTrace("Backing up (packed) file >{File}< in {Method}",
            template.FileDescr.FullName, nameof(BackupFile));

        var wstream = Manager.BeginPackedFile() ??
            throw new TapeIOException(this, this, $"failed to open packed write stream for >{template.FileDescr.FullName}<");

        CommitToken token = wstream.CommitToken;
        hash = null;
        codec = TapeFileCodec.Stored;

        // The record's sink: the packer façade itself (plain, drive-encrypted), or the cipher's stream over it
        //  (software-encrypted) — Design-Encryption §9.3. Opened INSIDE the try, so a failure discards the packer slot.
        ITapeSetCipher cipher = ActiveSetCipher;
        Stream? sink = null;
        try
        {
            var setTOC = TOC.CurrentSetTOC;
            sink = cipher.OpenWrite(wstream, setTOC, template);
            var hasher = CreateHasher(setTOC.HashAlgorithm);

            // The per-file header (2.1 frame, or the 12-byte legacy header) — excluded from the hash.
            RecordEmitter.WriteFileHeader(sink, setTOC, template);

#if TEST_HOOKS
            if (SimulateFileFailures.ShouldFailNow())
            {
                m_logger.LogWarning("SIMULATED failure for file #{Counter} >{File}<",
                    SimulateFileFailures.Counter, template.FileDescr.FullName);
                throw new TapeIOException((uint)WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION,
                    $"Simulated backup failure for testing (file #{SimulateFileFailures.Counter})");
            }
#endif

            // No BufferedTapeWriteStream needed: the packer's worker-thread backend
            //  already overlaps reads and writes.

            // Use TapeBackupSourceStream to capture all NTFS streams (ACL, ADS, EA, etc.)
            //  via BackupRead as an opaque blob.
            var fileInfo = template.FileDescr.CreateFileInfo();

            if (setTOC.Compression == TapeCompression.Software)
            {
                // Software-ZSTD path via ProbingCompressionStream:
                //  - Probes the first 128 KiB (one ZSTD block) to decide compress vs store.
                //  - Writes the decision as the body's codec prefix (EmitsCodecPrefix), then the body.
                //  - Remainder is piped directly to the sink without any intermediate buffer.
                //  - Session (codec + probe buffers) is created once and reused across files.
                //  - Hashing always covers the uncompressed bytes (codec-independent hash).
                var session = GetOrCreateCompressionSession();
                using var probing = new ProbingCompressionStream(sink, session, setTOC.CompressionLevel)
                {
                    EmitsCodecPrefix = RecordEmitter.WritesCodecPrefix,
                };
                using var srcFileStream = TapeBackupSourceStream.Open(fileInfo, m_logger);
                if (hasher == null)
                {
                    srcFileStream.CopyTo(probing);
                }
                else
                {
                    using var hashingStream = new HashingStream(srcFileStream, hasher);
                    hashingStream.CopyTo(probing);
                    hash = hasher.GetCurrentHash();
                }

                // Explicitly dispose here so that Commit() runs (sealing the ZSTD frame and
                //  setting FinalCodec) before we read it. The using-var Dispose() below is a no-op
                //  because ProbingCompressionStream guards against double-disposal via _disposed.
                //  Should it dispose its inner stream, the encrypting sink ignores that: only
                //  CompleteWrite / AbandonWrite end a record.
                probing.Dispose();
                codec = probing.FinalCodec;
            }
            else
            {
                // None / Hardware path — no software compression; the body is stored as is.
                codec = TapeFileCodec.Stored;
                if (RecordEmitter.WritesCodecPrefix)
                    TapeFileHeader.WriteCodec(sink, codec);
                if (hasher == null)
                {
                    using var srcFileStream = TapeBackupSourceStream.Open(fileInfo, m_logger);
                    srcFileStream.CopyTo(sink);
                }
                else
                {
                    var srcFileStream = TapeBackupSourceStream.Open(fileInfo, m_logger);
                    using var hashingStream = new HashingStream(srcFileStream, hasher, ownInner: true);
                    hashingStream.CopyTo(sink);
                    hash = hasher.GetCurrentHash();
                }
            }

            // Seal the record (software: the final AEAD chunk) BEFORE counting: the façade's length is then complete.
            cipher.CompleteWrite(sink);
            BytesBackedup += wstream.Length;

            // Close the packer file slot. The actual TapeAddress will be reported
            //  later via Manager.FilesCommitted, keyed by the returned token.
            Manager.EndPackedFile();

            m_logger.LogTrace("File >{File}< handed off to packer ok", template.FileDescr.FullName);
            return token;
        }
        catch
        {
            // Never a valid-looking end for a failed record: the software cipher writes no final chunk.
            if (sink is not null)
            {
                try { cipher.AbandonWrite(sink); }
                catch (Exception aex) { m_logger.LogDebug(aex, "AbandonWrite threw"); }
            }

            // Discard the open file from the packer. NoRollback mode (the default)
            //  truncates the still-buffered tail; bytes already flushed remain as
            //  on-tape garbage but earlier files in the same fill buffer survive.
            //  Critically, do NOT MoveToBlock here: that would clobber prior files.
            try { Manager.Packer?.DiscardOpenFile(); }
            catch (Exception dex) { m_logger.LogDebug(dex, "Packer.DiscardOpenFile threw"); }
            throw;
        }
    } // BackupFile()

    private bool BackupFilesToCurrentSet(bool newSet = true)
    {
        Debug.Assert(MultiVolumeContext != null);
        TapeBackupContext bc = MultiVolumeContext.Value;

        if (bc.fileList.Count == 0)
        {
            NotifySetEnd(bc.fileNotify);
            m_logger.LogWarning("No files found to backup in {Method}", nameof(BackupFilesToCurrentSet));
            return true;
        }

        // One Start per SET, matching the Ends below. The count is contributed only by the first set of
        //  the operation — see NotifySetStart.
        NotifySetStart(bc.fileNotify, filesAdded: bc.isContinuation ? 0 : bc.fileList.Count);

        // F32: the previous set on this volume ran to end-of-media — the volume is full, and appending would overwrite
        //  that set. Continue on the next volume straight away, exactly as an EOM on the first file would.
        if (VolumeIsFullBeforeCurrentSet(newSet))
        {
            m_logger.LogInformation("{Method}: volume #{Volume} ends inside set #{Set} (closed by end-of-media) — " +
                "the new set starts on the next volume", nameof(BackupFilesToCurrentSet), TOC.Volume, TOC.CurrentSetIndex - 1);
            SetError(WIN32_ERROR.ERROR_END_OF_MEDIA, $"Volume #{TOC.Volume} is full");

            bc.prevVolumeHasFiles = false;      // nothing of THIS set is on the full volume
            bc.continuationSetParams = TOC.CurrentSetTOC.ToParams();
            bc.isContinuation = true;           // files already counted by the NotifySetStart above
            MultiVolumeContext = bc;

            NotifySetEnd(bc.fileNotify);
            TOC.ContinuedOnNextVolume = true;
            Debug.Assert(CanResumeToNextVolume);

            m_carriedFailure = LatchedFailure;  // survives the caller's BackupTOC() between the volumes --
                                                //  but the latch only: the live EOM is a continuation, not a fault!
            return false;                       // do NOT LatchFailure(): this is a continuation, not a fault
        }

        if (!BeginWriteContentForCurrentSet(newSet, bc.fileNotify))
        {
            NotifySetEnd(bc.fileNotify);

            if (bc.isContinuation && !IsAbortRequested)
            {
                // A FRESH continuation cartridge refused the first write (write-protected, damaged, not ours to write):
                //  nothing of this backup is on it. Keep the context untouched — the caller may offer another cartridge
                //  (IsVolumeRejected), and the retry starts this set again, so it is not counted now.
                StatsUndoSet();
                m_volumeRejected = true;
                m_logger.LogWarning("Failed to begin writing content on continuation volume #{Volume}", TOC.Volume);
                return false;
            }

            LatchFailure();
            m_logger.LogWarning("Failed to begin writing content in {Method}", nameof(BackupFilesToCurrentSet));
            // We MUST clear MultiVolumeContext to NOT indicate multivolume continuation!
            MultiVolumeContext = null;
            return false;
        }

        // Consolidated bookkeeping: pending tokens, TOC promotion, and post-process queue.
        var tracker = new PackedCommitTracker(TOC.CurrentSetTOC, m_logger);

        void OnCommitted(IReadOnlyList<CommittedFile> committed) => tracker.OnCommitted(committed);

        Manager.FilesCommitted += OnCommitted;

        bool eomHandled = false; // EOM has been handled -- MultiVolumeContext has been set up

        // Single handler for EOM raised by the packer at any point: from inside the
        //  per-file write loop OR from the final flush during Manager.EndWriteContent().
        //  Returns true iff the caller should report failure and bail out (the only
        //  case where it returns false is when the user chose Abort during the
        //  failure notification, in which case bc.overallSuccess is set instead).
        //
        //  The handler is responsible for:
        //   (1) capturing continuation metadata (set params, file index, prev-vol-has-files)
        //   (2) ensuring Manager.EndWriteContent() runs so the write session is closed
        //       before the caller swaps volumes -- otherwise the packer / write state
        //       would leak across volumes.
        //   (3) adjusting statistics (skipped, error) to undo any rolled back files
        //   (4) notifying the failed file and the batch end.
        bool HandleEom(TapePackerEndOfMediaException eomEx, int currentFileIndex, TapeFileInfo? currentTemplate)
        {
            SetError((uint)WIN32_ERROR.ERROR_END_OF_MEDIA);

            int earliestRolledIndex = tracker.RemoveRolledBack(
                eomEx.RolledBackTokens, currentFileIndex, out var earliestTemplate);

            // Prefer the template carried by the current iteration (if any); otherwise
            //  fall back to the earliest rolled-back file's template so the failure
            //  notification can name a real file.
            var reportTemplate = currentTemplate ?? earliestTemplate;

            m_logger.LogWarning("{Method}: EOM at file #{Number} >{File}<; {RolledBack} pending file(s) rolled back",
                nameof(BackupFilesToCurrentSet), earliestRolledIndex + 1,
                reportTemplate?.FileDescr.FullName ?? "(unknown)", eomEx.RolledBackTokens.Count);

            // Undo skips that occurred between the earliest rolled-back file and the
            //  current file index. These files will be re-processed on the next volume,
            //  so their skip counts must not carry forward. The number of skipped files
            //  in the range is the total files in [earliestRolledIndex, currentFileIndex)
            //  minus the ones that were actually written (and are now rolled back).
            int filesInRange = currentFileIndex - earliestRolledIndex;
            int skipsToUndo = filesInRange - eomEx.RolledBackTokens.Count;
            if (skipsToUndo > 0)
                StatsUndoSkips(skipsToUndo);

            // Do NOT BytesBackedupMarker = BytesBackedup here -- we anchor ONLY in BeginWriteContentForCurrentSet()!
            
            // Snapshot continuation metadata BEFORE EndWriteContent (which may finalize/clear
            //  the current set). prevVolumeHasFiles reflects what was actually committed.
            bc.prevVolumeHasFiles = TOC.CurrentSetTOC.Count > 0;
            bc.continuationSetParams = TOC.CurrentSetTOC.ToParams();
            bc.fileIndex = earliestRolledIndex;
            bc.isContinuation = true; // the next pass through this method continues the same batch
            MultiVolumeContext = bc;

            // Always close the write session so the packer/state are clean for the
            //  next volume. EndWriteContent() handles the post-EOM case (state was
            //  already transitioned by the throwing path) and is idempotent on
            //  MediaPrepared. A second EOM from final flush is unlikely here (the
            //  packer just rolled back) but still possible if a partially-flushed
            //  block is pending; treat it as already-handled.
            try
            {
                if (!Manager.EndWriteContent())
                {
                    SyncErrorFrom(Manager);
                    m_logger.LogDebug("EndWriteContent reported failure during EOM handling in {Method}",
                        nameof(BackupFilesToCurrentSet));
                }
            }
            catch (TapePackerException second)
            {
                // A second EOM — or a write fault on the remains — while closing after an EOM: the files are rolled
                //  back already and continue on the next volume. Nothing more to account for here.
                m_logger.LogDebug(second, "Packer exception during EndWriteContent in EOM handler -- ignored");
            }

            // Do NOT treat the reporting file as failed as it will be re-tried on the next volume
            //  The user will be notified via the "new media" flow
            /*
            if (reportTemplate is not null)
            {
                if (NotifyFileFailed(bc.fileNotify, reportTemplate, eomEx) == FileFailedAction.Abort)
                {
                    bc.overallSuccess = false;
                    return false;
                }
                StatsUndoFailure(); // file will be re-tried on next volume
            }
            */

            NotifySetEnd(bc.fileNotify);

            TOC.ContinuedOnNextVolume = true;
            Debug.Assert(CanResumeToNextVolume);
            return true;
        } // HandleEom()

        // A media write fault ends the set: the drive already retried, and writing on behind the failed region would
        //  scatter the next files over damaged tape. Every file the fault took is reported — with the drive's cause, with
        //  no retry offered — and counted as failed; files committed before it stay in the TOC.
        //  `ignoreFailures` does not apply: it governs SOURCE errors, which are per file.
        void HandleWriteFault(TapePackerWriteFaultException wf, TapeFileInfo? openTemplate)
        {
            SetError(wf);
            LatchFailure();             // the first failure wins: the drive's own diagnosis

            List<TapeFileInfo> lost = tracker.TakeRolledBack(wf.RolledBackTokens);
            if (openTemplate is not null)
                lost.Add(openTemplate); // the file being written: discarded by BackupFile, never registered

            m_logger.LogError("{Method}: media write fault — {Lost} file(s) lost, set #{Set} ends here: {Cause}",
                nameof(BackupFilesToCurrentSet), lost.Count, TOC.CurrentSetIndex, wf.ErrorMessage);

            var reason = TapeResult.Fail(wf);
            foreach (TapeFileInfo tfi in lost)
                NotifyFileLost(bc.fileNotify, tfi, reason);

            bc.overallSuccess = false;
        } // HandleWriteFault()

        try
        {
            m_logger.LogTrace("Continuing (multi-volume, packed) backup from file #{Number} >{File}<",
                bc.fileIndex + 1, bc.fileList[bc.fileIndex]);

            for (; bc.fileIndex < bc.fileList.Count; bc.fileIndex++)
            {
                var fileName = bc.fileList[bc.fileIndex];

                FileInfo fileInfo = new(fileName);
                // Address field is unused on the packed path -- the real address comes
                //  from FilesCommitted. Use Zero as a placeholder.
                TapeFileInfo template = new(TOC.CurrentSetTOC.GenerateFileId(), TapeAddress.Zero, fileInfo);

                try
                {
                    ThrowIfAbortRequested(nameof(BackupFilesToCurrentSet));

                    if (!NotifyPreProcessFile(bc.fileNotify, template))
                    {
                        NotifyFileSkipped(bc.fileNotify, template);
                        m_logger.LogTrace("File #{Number} >{File}< skipped per pre-processor request",
                            bc.fileIndex + 1, fileName);
                        continue;
                    }

                    if (!fileInfo.Exists)
                        throw new FileNotFoundException("File not found", fileName);

                    m_logger.LogTrace("Backing up (packed) file #{Number} >{File}< of length {Length}",
                        bc.fileIndex + 1, fileName, Helpers.BytesToString(fileInfo.Length));

                    if (TOC.CurrentSetTOC.Incremental && TOC.IsFileUptodateInc(fileInfo))
                    {
                        NotifyFileSkipped(bc.fileNotify, template);
                        m_logger.LogTrace("File #{Number} >{File}< found up-to-date in an incremental set -> skipping",
                            bc.fileIndex + 1, fileName);
                        continue;
                    }

                    var token = BackupFile(template, out var hash, out var codec);
                    tracker.Register(token, template, bc.fileIndex, hash, codec);
                }
                catch (TapeAbortRequestedException)
                {
                    m_logger.LogTrace("{Method}: Abort requested before file #{Number} >{File}< was written",
                        nameof(BackupFilesToCurrentSet), bc.fileIndex + 1, fileName);
                    // A callback threw to request the abort — the only channel a void notification has.
                    //  Record it so FailedOperationResult reports ERROR_CANCELLED and the service classifies
                    //  the operation as aborted, exactly as when the flag was set directly.
                    IsAbortRequested = true; // the callback wrapper might've already set it - but we want to be sure
                    bc.overallSuccess = false;
                    // no need for caller-requested abort to LatchFailure()
                    break;
                }
                catch (TapePackerEndOfMediaException eomEx)
                {
                    // EOM during packer harvest inside the loop. The packer rolled
                    //  back the listed pending tokens. Delegate to the shared handler.
                    eomHandled = HandleEom(eomEx, bc.fileIndex, template);
                    // do NOT return here since we still need to go thru tracker.DrainPostProcess()!
                    break;
                }
                catch (TapePackerWriteFaultException wfEx)
                {
                    // Surfaced while THIS file was being started or written — but the fault belongs to the buffer in
                    //  flight, which held earlier files. Report them all; then end the set.
                    HandleWriteFault(wfEx, template);
                    // do NOT return here since we still need to close the set and go thru tracker.DrainPostProcess()!
                    break;
                }
                catch (Exception ex)
                {
                    SetError(ex);

                    m_logger.LogWarning("{Method}: File #{Number} >{File}< backup failed. Exception: {Ex}",
                        nameof(BackupFilesToCurrentSet), bc.fileIndex + 1, fileName, ex);

                    // Per-file failure on the packed path: BackupFilePacked already called
                    //  Packer.DiscardOpenFile to truncate the open file's tail. We do NOT
                    //  rewind the drive here -- earlier files queued in the same fill
                    //  buffer must remain intact to commit later.
                    var retryAction = NotifyFileFailed(bc.fileNotify, template, ex);
                    if (retryAction == FileFailedAction.Abort)
                    {
                        bc.overallSuccess = false;
                        LatchFailure();  // latch on the ORIGINAL error, abort notwithstanding
                        break;
                    }
                    else if (retryAction == FileFailedAction.Retry)
                    {
                        ResetError(); // give the retry a clean slate

                        bc.fileIndex--;
                        StatsUndoFailure();
                        continue;
                    }
                    // else Skip - continue the loop to the next file

                    bc.overallSuccess = false;
                    LatchFailure(); // latch on the ORIGINAL error
                    if (!bc.ignoreFailures)
                        break;
                }

                // Drain post-process notifications for files that committed during this
                //  iteration. Same abort semantics as the legacy path: an abort here
                //  breaks the loop without rewinding -- the file is already on tape and
                //  in the TOC.
                //  If the caller throws a TapeAbortRequestedException during post-notify,
                //  NotifyPostProcessFile will register it and set IsAbortRequested,
                //  while tracker.DrainPostProcess will NOT re-throw it. So all good.
                if (!tracker.DrainPostProcess(tfi => NotifyPostProcessFile(bc.fileNotify, tfi)))
                {
                    bc.overallSuccess = false;
                    if (!IsAbortRequested) // user-requested abort is not a failure
                        LatchFailure();
                    break;
                }

            } // for bc.fileIndex

            // Final flush: closing the content session forces the packer to drain its
            //  fill buffer; FilesCommitted fires synchronously from EndWriteContent for
            //  the tail commits, so the TOC is fully populated before we proceed.
            //  The subscription must remain active here -- otherwise tail commits would
            //  bypass the tracker and PendingCount would stay non-zero.
            //  EOM may surface here too: due to packer buffering the tape may not run
            //  out until the very last flush. The shared HandleEom takes care of it.
            try
            {
                if (!Manager.EndWriteContent())
                {
                    SyncErrorFrom(Manager);
                    m_logger.LogWarning("EndWriteContent failed during {Method}", nameof(BackupFilesToCurrentSet));
                    bc.overallSuccess = false;
                    LatchFailure();
                }
            }
            catch (TapePackerEndOfMediaException eomEx)
            {
                eomHandled = HandleEom(eomEx, bc.fileIndex, currentTemplate: null);
            }
            catch (TapePackerWriteFaultException wfEx)
            {
                // The common case with few files: they all sat in the pipeline until this final flush. The manager has
                //  already closed the set; report the lost files.
                HandleWriteFault(wfEx, openTemplate: null);
            }

            // And now the rest: after a fault the loop has stopped, the final EndWriteContent
            //  closes the set (nothing left to flush), DrainPostProcess reports files committed
            //  before the fault, and PendingCount is 0 — the lost files were taken out.

            // F32: record how the set ended — by its setmark, or by end-of-media on
            //  a TOC-in-partition volume. flag survives the idempotent EndWriteContent
            //  calls of the EOM path; only a new write session clears it.
            TOC.CurrentSetTOC.ClosedByEom = Manager.LastSetClosedByEom;
            // Design-TocLocation §6: where this set ends on tape — or −1 when no mark closed it.
            TOC.CurrentSetTOC.FirstBlockAfterSet = Manager.LastSetFirstBlockAfter;

            // Drain post-process for tail commits that arrived during EndWriteContent.
            //  If the caller throws a TapeAbortRequestedException during post-notify,
            //  NotifyPostProcessFile will register it and set IsAbortRequested,
            //  while tracker.DrainPostProcess will NOT re-throw it. So all good.
            if (!tracker.DrainPostProcess(tfi => NotifyPostProcessFile(bc.fileNotify, tfi)))
            {
                bc.overallSuccess = false;
                if (!IsAbortRequested) // user-requested abort is not a failure
                    LatchFailure();
            }

            if (tracker.PendingCount > 0)
            {
                m_logger.LogWarning("{Method}: {Count} pending file(s) never received commit notification",
                    nameof(BackupFilesToCurrentSet), tracker.PendingCount);
                bc.overallSuccess = false;
                LatchFailure();
            }
        } // try around the file loop
        finally
        {
            Manager.FilesCommitted -= OnCommitted;
            EndSetEncryption();     // E16: on success, EOM, write fault and abort alike — before any TOC write
        }

        if (eomHandled)
        {
            Debug.Assert(CanResumeToNextVolume);
            bc.overallSuccess = false;

            // do NOT LatchFailure(): we can continue to the next volume, hence no hard failure

            m_carriedFailure = LatchedFailure;  // survives the caller's BackupTOC() between the volumes --
                                                //  but the latch only: the live EOM is a continuation, not a fault!
            
            m_logger.LogTrace("{Method}: EOM was encountered & handled during backup; MultiVolumeContext set up",
                nameof(BackupFilesToCurrentSet));
            return false;
        }

        // Do NOT BytesBackedupMarker = BytesBackedup here -- we anchor ONLY in BeginWriteContentForCurrentSet()!
        NotifySetEnd(bc.fileNotify);

        // We MUST clear MultiVolumeContext to not indicate multivolume continuation
        MultiVolumeContext = null;

        return bc.overallSuccess;
    } // BackupFilesToCurrentSet()

    /// <summary>
    /// Expands file/directory patterns and backs them up to the current set.
    ///  Routes content writes through the shared-block packer.
    /// </summary>
    public TapeResult BackupFilesToCurrentSet(bool newSet, List<string> fileAndDirectoryPatterns, bool recurseSubdirs,
        bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        var fileList = BuildFileNameList(fileAndDirectoryPatterns, recurseSubdirs);

        return BackupFileListToCurrentSet(newSet, fileList, ignoreFailures, fileNotify);
    }

    /// <summary>
    /// Makes <paramref name="set"/> carry this agent's data format before its first file is written. An EMPTY set of
    ///  another format is re-stamped (a trailing legacy slot, or a continuation after a format switch); a set that
    ///  already holds files cannot change format — one set, one format (F4).
    /// </summary>
    /// <summary>
    /// Makes <paramref name="set"/> carry this agent's data format before its first file is written. An EMPTY set of
    ///  another format is re-stamped (a trailing legacy slot, or a continuation after a format switch); a set that
    ///  already holds files cannot change format — one set, one format (F4).
    /// </summary>
    /// <remarks>
    /// Runs before ANY tape movement, so it is also where encryption refuses (E9, E14): first the scheme — a locked set
    ///  answers nothing else — then an incremental's chain, which the up-to-date check must be able to read.
    /// </remarks>
    private bool EnsureSetFormat(TapeSetTOC set)
    {
        if (!ValidateSetEncryption(forWrite: true))
            return false;   // set and latched

        if (TOC.IsIncrementalBaseLocked)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PASSWORD,
                $"Set #{TOC.CurrentSetIndex} is incremental, but its base is locked — unlock the chain first");
            LatchFailure();
            return false;
        }

        TapeDataFormat format = RecordEmitter.DataFormat;
        if (set.DataFormat != format)
        {
            if (set.Count > 0)
            {
                SetError(WIN32_ERROR.ERROR_INVALID_STATE,
                    $"Set #{TOC.CurrentSetIndex} holds {set.DataFormat} files — new files must be written in the same format");
                LatchFailure();
                return false;
            }

            if (format == TapeDataFormat.V2)
                m_logger.LogInformation("Empty {Format} set #{Set} upgraded to format 2.1 before backup",
                    set.DataFormat, TOC.CurrentSetIndex);
            set.DataFormat = format;
        }

        // Identity follows the format: a V2 set needs one, a legacy set has none.
        if (format == TapeDataFormat.V2 && set.SetId == Guid.Empty)
            set.SetId = Guid.NewGuid();
        else if (format == TapeDataFormat.Legacy)
            set.SetId = Guid.Empty;

        if (!RecordEmitter.WritesCodecPrefix && set.Compression == TapeCompression.Software)
        {
            SetError(WIN32_ERROR.ERROR_NOT_SUPPORTED,
                "Software compression needs the 2.1 codec prefix; this set is written without one");
            LatchFailure();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Backs up a pre-built file list to the current set.
    ///  Routes content writes through the shared-block packer; supports multi-volume continuation.
    /// </summary>
    /// <remarks>
    /// Files are written in format 2.1 only. An EMPTY legacy set (e.g. a trailing slot from a legacy TOC) is upgraded
    ///  to 2.1 in place. A legacy set that already holds files cannot be upgraded — its existing files carry legacy
    ///  headers, and one set has one data format (§6.1) -> hence refuse adding files to a legacy set.
    /// </remarks>
    public TapeResult BackupFileListToCurrentSet(bool newSet, List<string> fileList, bool ignoreFailures = true,
        ITapeFileNotifiable? fileNotify = null)
    {
        if (fileList.Count == 0)
        {
            m_logger.LogWarning("No files found to backup in {Method}", nameof(BackupFileListToCurrentSet));
            return TapeResult.OK;
        }

        _stats.Reset();
        ClearAnomalies();
        ResetLatchedFailure();

        MediaHeaderStamped = false; // reset for the new series (per-series, cross-volume artifact)
        
        m_carriedFailure = TapeResult.OK;
        m_volumeRejected = false;

        if (!EnsureSetFormat(TOC.CurrentSetTOC))
            return FailedOperationResult;

        // Background estimation: source files can be scanned (stat'd) concurrently with the
        //  backup itself, progressively growing Statistics.BytesTotal as the scan proceeds.
        StartBackgroundSizeEstimate(fileList);

        // Do NOT call NotifySetStart(fileNotify, fileList.Count) here -- we do so once per set,
        //  ONLY in our private BackupFilesToCurrentSet()

        m_logger.LogTrace("Starting backing up (packed) {Count} files to current set #{Set}",
            fileList.Count, TOC.CurrentSetIndex);
        if (TOC.CurrentSetTOC.Incremental)
            m_logger.LogTrace("Performing incremental (packed) backup to incremental set #{Set}", TOC.CurrentSetIndex);

        MultiVolumeContext = new(fileList, ignoreFailures, fileNotify, TOC.CurrentSetTOC.Incremental);
        var result = BackupFilesToCurrentSet(newSet)
            ? TapeResult.OK : FailedOperationResult;

        // Only stop the estimate once the operation is truly done (not just paused for a
        //  multi-volume media swap) — ResumeBackupToNextVolume continues the same fileList.
        if (!CanResumeToNextVolume)
            StopBackgroundSizeEstimate();

        return result;
    }

} // class TapeFileBackupAgent


// namespace TapeNET
