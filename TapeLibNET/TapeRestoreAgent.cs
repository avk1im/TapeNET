using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Hashing;
using System.Net.Http.Headers;
using System.Runtime.Intrinsics.X86;
using TapeLibNET;
using Windows.Win32.Foundation;


namespace TapeLibNET;

/// <summary>
/// Outcome of verifying a set header against the TOC's expectation for the set just positioned at.
/// </summary>
/// <remarks>
/// Ordered by severity of the divergence, not by likelihood: <see cref="Match"/> and
///  <see cref="NotExpected"/> are the normal outcomes, everything below them describes a tape that
///  disagrees with what the library believes about it.
/// </remarks>
public enum TapeSetHeaderVerdict
{
    /// <summary>Media identity, volume and on-volume index all agree. The overwhelmingly common case.</summary>
    Match,

    /// <summary>Header-less volume — no set header was expected, so none was read (SH-1).</summary>
    NotExpected,

    /// <summary>
    /// A set header was expected but the block did not classify as one (torn write, host-path
    ///  corruption, or a read fault). Warn and proceed: an unverifiable record removes a safety net,
    ///  not the tape's data.
    /// </summary>
    Unreadable,

    /// <summary>
    /// The media id differs — a cartridge swapped mid-operation. Every in-memory assumption is void,
    ///  including the TOC; nothing is correctable.
    /// </summary>
    WrongMedia,

    /// <summary>
    /// Right series, wrong cartridge. File addresses are physical-per-volume, so every address in the
    ///  TOC would resolve to garbage on this volume.
    /// </summary>
    WrongVolume,

    /// <summary>
    /// Identity confirmed, on-volume index differs — a recoverable navigation miscount. Reported as a
    ///  failure in Step 5; corrected in Step 6 (SH-10).
    /// </summary>
    SetIndexDrift,
}

/// <summary>
/// Abstract restore agent — reads files from tape content sets, validates headers and CRC,
///  and delegates actual file processing to <see cref="RestoreFileCoreAligned"/>.
/// <para>Concrete subclasses: <see cref="TapeFileRestoreAgent"/> (restore to disk),
///  <see cref="TapeFileValidateAgent"/> (read + CRC only),
///  <see cref="TapeFileVerifyAgent"/> (compare tape vs. disk).
///  Extended by <see cref="TapeFileRestoreAgentEx"/> for target-directory and handle-existing logic.</para>
/// <para>Supports multi-volume restore via <see cref="CanResumeFromAnotherVolume"/> /
///  <see cref="ResumeRestoreFromAnotherVolume"/>.</para>
/// </summary>
public abstract class TapeFileRestoreBaseAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileAgent(drive, legacyTOC)
{
    #region *** Properties ***

    // Guards the one-retry correction bound (SH-10). Set while a correction is being verified, so the re-verify
    //  cannot itself trigger another correction — a tape whose mark structure defeats a simple relative
    //  move is inconsistent, not noisy, and a second attempt would only walk further into the unknown.
    private bool m_correctingSetNavigation = false;

    // A flag that the last file has been skipped since it's not reported via the return value of RestoreNextFile()
    protected bool LastFileSkipped { get; private set; } = false;

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

    #endregion

    #region *** Set header verification ***

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
    private bool VerifySetHeaderForCurrentSet()
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
                // The golden rule: a record we cannot verify never blocks. The files position absolutely,
                //  so proceeding is safe — we have merely lost the safety net for this set.
                m_logger.LogWarning(
                    "Set header for set #{Set} could not be read or classified; proceeding unverified",
                    TOC.CurrentSetIndex);

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

                if (CorrectsSetNavigation)
                    return CorrectSetNavigation(header);
                // else Correction disabled: report rather than repair. Restoring from the wrong set would
                //  silently deliver wrong bytes, so the set fails.
                SetError(WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Set navigation drift at set #{TOC.CurrentSetIndex}: positioned at on-volume set " +
                    $"{header.VolumeSetIndex}, expected {TOC.CurrentSetIndexOnVolume} (correction DISABLED)");
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


    private TapeReadStream? OpenReadContentStream()
    {
        return Manager.ProduceReadContentStream(textFileMode: false, lengthLimit: -1);
    }

    private bool BeginReadContentForCurrentSet()
    {
        EnsureMediaHeaderResolved(); // resolve presence AND SetHeadersExpected (§4.2) before
                                     //  any content navigation (blank-media fallbacks skip the header)

        // If we were reading or writing, end it first - before setting the new set's parameters
        if (!Manager.EndReadWrite())
        {
            m_logger.LogWarning("Failed to end read/write in {Method}",
                nameof(BeginReadContentForCurrentSet));
            SyncErrorFrom(Manager);
            return false;
        }

        // Optimization: set the target content set BEFORE transition to Content reading
        //  so that Navigator can optimize moving to the target content set once we call BeginReadContent()
        Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet;

        // SH-8 — capture BEFORE BeginReadContent, which is the only moment this is knowable.
        //  BeginReadContent moves the head IFF the target differs from the navigator's current belief:
        //  it early-returns when they are equal and already reading, and MoveToTargetContentSet is
        //  itself idempotent (SH-4) on every other path. So this one expression decides whether the
        //  head will land on the set's FIRST block — the only position where a set header sits.
        //  Reading unconditionally would consume a CONTENT block mid-set and corrupt the next file.
        bool willPositionAtSetStart = Navigator.TargetContentSet != Navigator.CurrentContentSet;

        // Transition to Content mode before setting the set parameters
        if (!Manager.BeginReadContent())
        {
            m_logger.LogWarning("Failed to transition to reading content in {Method}",
                nameof(BeginReadContentForCurrentSet));
            SyncErrorFrom(Manager);
            return false;
        }

        // Verify the set we actually landed on before delivering a single file byte (SH-7, SH-8).
        if (willPositionAtSetStart && Navigator.SetHeadersExpected && !VerifySetHeaderForCurrentSet())
            return false;

        // set the block size from the set to the manager
        Drive.SetBlockSize(TOC.CurrentSetTOC.BlockSize);

        // Hardware compression interlock: match the drive's HW compression state to what was
        //  used when this set was written. HW sets need compression on; SW/None sets must have
        //  it off so the drive doesn't attempt to decompress already-decompressed bytes.
        Drive.SetHardwareCompression(TOC.CurrentSetTOC.Compression == TapeCompression.Hardware);

        // Success
        ResetError();

        return true;
    }

    /// <summary>
    /// Performs the actual file processing (write to disk, validate, or verify).
    ///  Override in subclasses; base implementation only checks stream validity.
    /// </summary>
    [Obsolete("Use the non-Aligned (Packed) version")]
    protected virtual bool RestoreFileCoreAligned(FileInfo fileInfo, TapeReadStream rstream, NonCryptographicHashAlgorithm? hasher)
    {
        if (rstream.IsDisposed)
            throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_HANDLE, "May not dispose tape read stream while restoring");

        return true;
    }

    /// <summary>
    /// Packed-path pendant of <see cref="RestoreFileCoreAligned(FileInfo, TapeReadStream, NonCryptographicHashAlgorithm?)"/>.
    ///  Operates on a generic <see cref="Stream"/> (the packer-backed
    ///  <c>TapeReadStreamFacade</c>) so block boundaries and intra-block file offsets
    ///  remain hidden from the agent. Default base implementation is a no-op success.
    /// </summary>
    protected virtual bool RestoreFileCore(FileInfo fileInfo, Stream rstream, NonCryptographicHashAlgorithm? hasher)
    {
        return true;
    }

    protected virtual bool PreProcessFileInternal(ref TapeFileDescriptor fileDescr)
    {
        LastFileSkipped = false;
        return true;
    }
    protected virtual bool PostProcessFileInternal(TapeFileDescriptor fileDescr, FileInfo fileInfo)
    {
        // return fileDescr.ApplyToFileInfo(fileInfo); -- e.g. overload for a restoring agent
        return true;
    }
    protected virtual void FileSkippedInternal(TapeFileDescriptor fileDescr)
    {
        LastFileSkipped = true;
    }

    // Returns true if success, false if failure.
    //  Sets fileFailedAction to true only if the caller should abort entire operation, otherwise doesn't modify fileFailedAction
    [Obsolete("Use the non-Aligned (Packed) version")]
    private bool RestoreNextFileAligned(TapeFileInfo tfi, ref FileFailedAction fileFailedAction, ITapeFileNotifiable? fileNotify = null)
    {
        try
        {
            // first check if abort requested
            if (IsAbortRequested)
            {
                fileFailedAction = FileFailedAction.Abort;
                m_logger.LogTrace("Abort requested before restoring file >{File}< in {Method}", tfi.FileDescr.FullName, nameof(RestoreNextFileAligned));
                return false;
            }

            // Important: we must get the content read stream first, since this will reset the CurrentBlock
            //  when changing to content read mode -- CurrentBlock can be used laster to move to tfi.Block
            using var rstream = OpenReadContentStream();
            if (rstream == null)
            {
                m_logger.LogWarning("Failed to open content read stream in {Method}", nameof(RestoreNextFileAligned));
                return false;
            }

            // check for abort requested again, since openning file stream might've taken time
            if (IsAbortRequested)
            {
                fileFailedAction = FileFailedAction.Abort;
                m_logger.LogTrace("Abort requested after opening file stream before restoring file >{File}< in {Method}", tfi.FileDescr.FullName, nameof(RestoreNextFileAligned));
                return false;
            }

            // check the UID and the length as the most important attributes
            var deserializer = new TapeDeserializer(rstream);
            if (!tfi.DeserializeAndCheckHeaderFrom(deserializer))
            {
                throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Header mismatch for file >{tfi.FileDescr.FullName}<");
            }

            rstream.LengthLimit = rstream.Length + tfi.FileDescr.Length; // this will activate LengthLimitMode

#if DEBUG
            // Simulate file restore failure for testing error handling
            if (SimulateFileFailures.ShouldFailNow())
            {
                throw new TapeIOException((uint)WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION,
                    $"Simulated restore failure for file >{tfi.FileDescr.FullName}< (#{SimulateFileFailures.Counter})");
            }
#endif

            var hasher = CreateHasher(TOC.CurrentSetTOC.HashAlgorithm);

            // Now invoke the pre- call back for skipping the file altogether
            var fileDescr = tfi.FileDescr; // copy for internal pre-processing (target dir, handle existing)
            // call the internal pre-processor first to ensure it always runs
            if (!PreProcessFileInternal(ref fileDescr) || !NotifyPreProcessFile(fileNotify, tfi))
            {
                FileSkippedInternal(tfi.FileDescr);
                NotifyFileSkipped(fileNotify, tfi);

                m_logger.LogTrace("Skipping file >{File}< per pre-processor request", tfi.FileDescr.FullName);

                return true; // skip the file -- do not treat as failure, since this is per pre-processor request
            }
            FileInfo fileInfo = fileDescr.CreateFileInfo(); // Notice we shouldn't set fileInfo fields here since the file doesn't exist yet!

            // Now ready to do the actual restoring
            if (!RestoreFileCoreAligned(fileInfo, rstream, hasher))
            {
                throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Processing failed for file >{tfi.FileDescr.FullName}<");
            }

            // check CRC
            if (hasher != null)
            {
                if (tfi.Hash == null)
                    throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                        $"Hash missing in file info for file >{tfi.FileDescr.FullName}<");

                if (tfi.Hash.SequenceEqual(hasher.GetCurrentHash()))
                {
                    // CRC check passed
                }
                else
                {
                    throw new TapeIOException((uint)WIN32_ERROR.ERROR_CRC,
                        $"CRC check failed for file >{tfi.FileDescr.FullName}<. Hasher: {TOC.CurrentSetTOC.HashAlgorithm}");
                }
            }

            BytesRestored += rstream.Length;

            // Now invoke the post- call back
            if (NotifyPostProcessFile(fileNotify, tfi))
                // now apply the attributes to the file -- an exception e.g. File Not Found can be thrown here
                return PostProcessFileInternal(fileDescr, fileInfo);

            return true;
        }
        catch (Exception ex)
        {
            SetError(ex); // we've already set the right error code & message in the exception

            fileFailedAction = NotifyFileFailed(fileNotify, tfi, ex);

            m_logger.LogWarning("Exception {Exception} while processing file >{File}<", ex, tfi.FileDescr.FullName);

            return false;
        }
    } // RestoreNextFile()


    // =====================================================================
    //  Packed (Phase 2 Step E) restore pendants
    //
    //  Mirror RestoreNextFile / RestoreFilesFromCurrentSetAligned but route content
    //  reads through TapeFilePipelinedReader so files that share tape blocks (or
    //  start at non-zero intra-block offsets) restore transparently. Unlike
    //  the packed BACKUP path, packed restore needs no commit decoupling --
    //  reads are synchronous and notifications fire in-line.
    // =====================================================================

    // Returns true on success, false on failure.
    //  Sets fileFailedAction only if the caller should abort entire operation.
    private bool RestoreNextFile(TapeFileInfo tfi, ref FileFailedAction fileFailedAction, ITapeFileNotifiable? fileNotify = null)
    {
        try
        {
            if (IsAbortRequested)
            {
                fileFailedAction = FileFailedAction.Abort;
                m_logger.LogTrace("Abort requested before restoring (packed) file >{File}< in {Method}",
                    tfi.FileDescr.FullName, nameof(RestoreNextFile));
                return false;
            }

            // The packer needs the file's exact tape position and length to bound its
            //  read window. Use SizeOnTape (set during packed backup) when available;
            //  fall back to estimated size for legacy/aligned files.
            long totalBytes = (tfi.SizeOnTape > 0)
                ? tfi.SizeOnTape
                : TapeFileInfo.EstimateSerializedHeaderSize() + tfi.FileDescr.Length;
            using var rstream = Manager.BeginPackedFileRead(tfi.Address, totalBytes);
            if (rstream == null)
            {
                m_logger.LogWarning("Failed to open packed content read stream in {Method}",
                    nameof(RestoreNextFile));
                return false;
            }

            if (IsAbortRequested)
            {
                fileFailedAction = FileFailedAction.Abort;
                m_logger.LogTrace("Abort requested after opening packed file stream before restoring file >{File}< in {Method}",
                    tfi.FileDescr.FullName, nameof(RestoreNextFile));
                return false;
            }

            // Validate header (UID + signature) before delivering the body.
            var deserializer = new TapeDeserializer(rstream);
            if (!tfi.DeserializeAndCheckHeaderFrom(deserializer))
            {
                throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Header mismatch for file >{tfi.FileDescr.FullName}<");
            }

#if DEBUG
            if (SimulateFileFailures.ShouldFailNow())
            {
                throw new TapeIOException((uint)WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION,
                    $"Simulated restore failure for file >{tfi.FileDescr.FullName}< (#{SimulateFileFailures.Counter})");
            }
#endif

            var hasher = CreateHasher(TOC.CurrentSetTOC.HashAlgorithm);

            var fileDescr = tfi.FileDescr;
            if (!PreProcessFileInternal(ref fileDescr) || !NotifyPreProcessFile(fileNotify, tfi))
            {
                FileSkippedInternal(tfi.FileDescr);
                NotifyFileSkipped(fileNotify, tfi);
                m_logger.LogTrace("Skipping (packed) file >{File}< per pre-processor request", tfi.FileDescr.FullName);
                return true;
            }
            FileInfo fileInfo = fileDescr.CreateFileInfo();

            // Wrap rstream with a decompressor when the file was ZSTD-compressed at backup time.
            //  The hasher inside RestoreFileCore always sees decompressed bytes (codec-independent hash).
            //  For Stored files, bodyStream == rstream (no allocation, no copy).
            ZstdCodec? zstdCodec = tfi.Codec == TapeFileCodec.Zstd ? new ZstdCodec() : null;
            using (zstdCodec)
            {
                Stream bodyStream = zstdCodec != null
                    ? new DecompressionFilterStream(rstream)
                    : rstream;
                using (zstdCodec != null ? bodyStream : null) // only dispose when we own it
                {
                    if (!RestoreFileCore(fileInfo, bodyStream, hasher))
                    {
                        throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                            $"Processing failed for file >{tfi.FileDescr.FullName}<");
                    }
                }
            }

            if (hasher != null)
            {
                if (tfi.Hash == null)
                    throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                        $"Hash missing in file info for file >{tfi.FileDescr.FullName}<");

                if (!tfi.Hash.SequenceEqual(hasher.GetCurrentHash()))
                {
                    throw new TapeIOException((uint)WIN32_ERROR.ERROR_CRC,
                        $"CRC check failed for file >{tfi.FileDescr.FullName}<. Hasher: {TOC.CurrentSetTOC.HashAlgorithm}");
                }
            }

            BytesRestored += tfi.FileDescr.Length;

            if (NotifyPostProcessFile(fileNotify, tfi))
                return PostProcessFileInternal(fileDescr, fileInfo);

            return true;
        }
        catch (Exception ex)
        {
            SetError(ex);
            fileFailedAction = NotifyFileFailed(fileNotify, tfi, ex);
            m_logger.LogWarning("Exception {Exception} while processing (packed) file >{File}<", ex, tfi.FileDescr.FullName);
            return false;
        }
    } // RestoreNextFile()


    // Restore the files specified by 'tfis' from the current set via the packer.
    //  Mirrors RestoreFilesFromCurrentSetAligned(List<TapeFileInfo>?, ...) but uses TapeAddress
    //  positioning and the packed read façade. No tape MoveToBlock is needed here -- the
    //  packer seeks to the file's exact (block, offset) on BeginRead.
    private bool RestoreFilesFromCurrentSet(List<TapeFileInfo>? tfis, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        if (tfis == null) // null means restore all files
            return RestoreAllFilesFromCurrentSetInt(ignoreFailures, fileNotify);

        NotifySetStart(fileNotify, tfis.Count);

        if (!BeginReadContentForCurrentSet())
        {
            NotifySetEnd(fileNotify);
            m_logger.LogWarning("Failed to begin reading content in {Method}",
                nameof(RestoreFilesFromCurrentSet));
            return false;
        }
        m_logger.LogTrace("Starting restoring (packed) {Count} select files from current set #{Set}",
            tfis.Count, TOC.CurrentSetIndex);

        bool overallSuccess = true;
        FileFailedAction fileFailedAction;
        LastFileSkipped = false;

        foreach (var tfi in tfis)
        {
        RETRY:
            fileFailedAction = FileFailedAction.Skip;

            if (tfi == null || !tfi.IsValid)
            {
                m_logger.LogWarning("Invalid file info in {Method}", nameof(RestoreFilesFromCurrentSet));
                goto FAILURE;
            }

            m_logger.LogTrace("Restoring (packed) file #{Number} >{File}< at {Addr}",
                _stats.FilesProcessed + 1, tfi.FileDescr.FullName, tfi.Address);

            if (!RestoreNextFile(tfi, ref fileFailedAction, fileNotify))
            {
                m_logger.LogWarning("Failed to restore (packed) file >{File}< in {Method}",
                    tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSet));
                goto FAILURE;
            }

            m_logger.LogTrace("File (packed) >{File}< restored ok", tfi.FileDescr.FullName);
            continue;

        FAILURE:
            if (fileFailedAction == FileFailedAction.Retry && tfi != null && tfi.IsValid)
            {
                ResetError(); // give the retry a clean slate

                m_logger.LogTrace("Retrying (packed) file >{File}< as per file failed action", tfi.FileDescr.FullName);
                StatsUndoFailure();
                goto RETRY;
            }

            LatchFailure();
            overallSuccess = false;

            if (ignoreFailures && fileFailedAction == FileFailedAction.Skip)
                continue;
            else
                break;
        }

        NotifySetEnd(fileNotify);

        m_logger.LogTrace("RestoreFilesFromCurrentSet(tfis) exiting: overallSuccess={Success}", overallSuccess);
        return overallSuccess;
    } // RestoreFilesFromCurrentSet(List<TapeFileInfo>?)


    // Restore ALL files from the current set via the packer.
    private bool RestoreAllFilesFromCurrentSetInt(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        NotifySetStart(fileNotify, TOC.CurrentSetTOC.Count);

        if (!BeginReadContentForCurrentSet())
        {
            m_logger.LogWarning("Failed to begin reading content in {Method}",
                nameof(RestoreAllFilesFromCurrentSet));
            return false;
        }
        m_logger.LogTrace("Starting restoring (packed) all files from current set #{Set}",
            TOC.CurrentSetIndex);

        bool overallSuccess = true;
        FileFailedAction fileFailedAction;
        LastFileSkipped = false;

        foreach (var tfi in TOC.CurrentSetTOC)
        {
        RETRY:
            fileFailedAction = FileFailedAction.Skip;

            if (tfi == null || !tfi.IsValid)
            {
                m_logger.LogWarning("Invalid file info in {Method}", nameof(RestoreAllFilesFromCurrentSet));
                goto FAILURE;
            }

            m_logger.LogTrace("Restoring (packed) file #{Number} >{File}<",
                _stats.FilesProcessed + 1, tfi.FileDescr.FullName);

            if (!RestoreNextFile(tfi, ref fileFailedAction, fileNotify))
            {
                m_logger.LogWarning("Failed to restore (packed) file >{File}< in {Method}",
                    tfi.FileDescr.FullName, nameof(RestoreAllFilesFromCurrentSet));
                goto FAILURE;
            }

            m_logger.LogTrace("File (packed) >{File}< restored ok", tfi.FileDescr.FullName);
            continue;

        FAILURE:
            if (fileFailedAction == FileFailedAction.Retry && tfi != null && tfi.IsValid)
            {
                ResetError(); // give the retry a clean slate
                m_logger.LogTrace("Retrying (packed) file >{File}< as per file failed action", tfi.FileDescr.FullName);
                StatsUndoFailure();
                goto RETRY;
            }

            LatchFailure();
            overallSuccess = false;

            if (ignoreFailures && fileFailedAction == FileFailedAction.Skip)
                continue;
            else
                break;
        }

        NotifySetEnd(fileNotify);

        m_logger.LogTrace("RestoreAllFilesFromCurrentSetInt exiting: overallSuccess={Success}", overallSuccess);
        return overallSuccess;
    } // RestoreAllFilesFromCurrentSet()


    /// <summary>
    /// Packed pendant of <see cref="RestoreFilesFromCurrentSetAligned(ITapeFileFilter?, bool, ITapeFileNotifiable?)"/>.
    ///  Routes content reads through the shared-block read packer; supports multi-volume continuation.
    /// </summary>
    public TapeResult RestoreFilesFromCurrentSet(ITapeFileFilter? fileFilter,
        bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring (packed) files from current set #{Set}",
            TOC.CurrentSetIndex);

        _stats.Reset();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: false, fileFilter), ignoreFailures, fileNotify, packed: true)
            ? TapeResult.OK : FailedOperationResult;
    }

    /// <summary>
    /// Packed pendant of <see cref="RestoreAllFilesFromCurrentSetAligned(bool, ITapeFileNotifiable?)"/>.
    ///  Supports multi-volume continuation.
    /// </summary>
    public TapeResult RestoreAllFilesFromCurrentSet(
        bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring (packed) all files from current set #{Set}",
            TOC.CurrentSetIndex);

        _stats.Reset();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: false, filter: null), ignoreFailures, fileNotify, packed: true)
            ? TapeResult.OK : FailedOperationResult;
    }


    // Restore the files specified by 'tfis' from the current set
    //  For optimal performance (to ensure moving always forward) tfis should follow the same order as in the SetTOC
    //  If tfis were selected by iterating thru SetTOC, this recommendation is met automatically
    [Obsolete("Use the non-Aligned (Packed) version")]
    private bool RestoreFilesFromCurrentSetAligned(List<TapeFileInfo>? tfis, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        if (tfis == null) // null means restore all files
            return RestoreFilesFromCurrentSetAligned(ignoreFailures, fileNotify);

        NotifySetStart(fileNotify, tfis.Count);

        if (!BeginReadContentForCurrentSet()) // start conent reading mode in tape manager so that tape positioning works correctly
        {
            NotifySetEnd(fileNotify);
            m_logger.LogWarning("Failed to begin reading content in {Method}", nameof(RestoreFilesFromCurrentSetAligned));
            return false;
        }
        m_logger.LogTrace("Starting restoring {Count} select files from current set #{Set}", tfis.Count, TOC.CurrentSetIndex);

        bool overallSuccess = true;
        FileFailedAction fileFailedAction;
        LastFileSkipped = false;

        int lastIndex = -1; // used only for tape move optimization
        bool lastFileFailed = false;

        foreach (var tfi in tfis)
        {
        RETRY:
            fileFailedAction = FileFailedAction.Skip; // reset to skip to avoid infinite loop

            if (tfi == null || !tfi.IsValid)
            {
                m_logger.LogWarning("Invalid file info in {Method}", nameof(RestoreFilesFromCurrentSetAligned));
                goto FAILURE;
            }

            m_logger.LogTrace("Restoring file #{Number} >{File}< at block {Block}", _stats.FilesProcessed + 1, tfi.FileDescr.FullName, tfi.Block);

            // Optimization: determine if we're at the next tfi so that we can skip moving the tape
            int index = (lastIndex >= 0)? TOC.CurrentSetTOC.IndexOf(tfi, lastIndex) : TOC.CurrentSetTOC.IndexOf(tfi);

            // Do move if the previous file has been skipped, failed, or is not the next one
            bool doMove = LastFileSkipped || lastFileFailed || index < 0 || lastIndex < 0 || index != lastIndex + 1;

            if (!doMove) // validate we're at the right block
            {
                long currentBlock = Drive.CurrentBlock;
                if (currentBlock != tfi.Block)
                {
                    m_logger.LogWarning("Unexpected block {Block} (expected {ExpectedBlock}) for file >{File}< in {Method}",
                        currentBlock, tfi.Block, tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSetAligned));
                    doMove = true;
                }
            }

            if (doMove)
            {
                if (Drive.MoveToBlock(tfi.Block))
                {
                    m_logger.LogTrace("Moved to block {Block} for file >{File}<", tfi.Block, tfi.FileDescr.FullName);
                }
                else
                {
                    m_logger.LogWarning("Failed to move to block {Block} for file >{File}< in {Method}",
                        tfi.Block, tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSetAligned));
                    goto FAILURE;
                }
            }

            if (!RestoreNextFileAligned(tfi, ref fileFailedAction, fileNotify))
            {
                m_logger.LogWarning("Failed to restore file >{File}< in {Method}", tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSetAligned));
                goto FAILURE;
            }

            // success
            if (index >= 0)
                lastIndex = index;

            lastFileFailed = false;
            m_logger.LogTrace("File >{File}< restored ok", tfi.FileDescr.FullName);
            continue;

        FAILURE:
            lastFileFailed = true; // must indicate this so that the tape moves back if we retry

            if (fileFailedAction == FileFailedAction.Retry && tfi != null && tfi.IsValid)
            {
                ResetError(); // give the retry a clean slate

                m_logger.LogTrace("Retrying file >{File}< as per file failed action", tfi.FileDescr.FullName);
                StatsUndoFailure(); // don't double-count
                goto RETRY;
            }

            LatchFailure();
            overallSuccess = false;

            if (ignoreFailures && fileFailedAction == FileFailedAction.Skip)
                continue;
            else
                break;
        }

        NotifySetEnd(fileNotify);

        return overallSuccess;
    }

    // Restore ALL files from the current set
    [Obsolete("Use the non-Aligned (Packed) version")]
    private bool RestoreFilesFromCurrentSetAligned(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        NotifySetStart(fileNotify, TOC.CurrentSetTOC.Count);

        if (!BeginReadContentForCurrentSet())
        {
            m_logger.LogWarning("Failed to begin reading content in {Method}", nameof(RestoreFilesFromCurrentSetAligned));
            return false;
        }
        m_logger.LogTrace("Starting restoring all files from current set #{Set}", TOC.CurrentSetIndex);

        bool overallSuccess = true;
        FileFailedAction fileFailedAction;
        bool lastFileFailed = false;
        LastFileSkipped = false;
        int fileIndex = -1;

        foreach (var tfi in TOC.CurrentSetTOC)
        {
            fileIndex++;
        RETRY:
            fileFailedAction = FileFailedAction.Skip; // reset to skip to avoid infinite loop

            if (tfi == null || !tfi.IsValid)
            {
                m_logger.LogWarning("Invalid file info in {Method}", nameof(RestoreFilesFromCurrentSetAligned));
                goto FAILURE;
            }

            m_logger.LogTrace("Restoring file #{Number} >{File}<", _stats.FilesProcessed + 1, tfi.FileDescr.FullName);

            // move tape if last file was skipped or failed
            if (LastFileSkipped || lastFileFailed)
            {
                if (!Drive.MoveToBlock(tfi.Block))
                {
                    m_logger.LogWarning("Failed to move to block {Block} for file >{File}< in {Method}",
                        tfi.Block, tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSetAligned));
                    goto FAILURE;
                }
            }

            if (!RestoreNextFileAligned(tfi, ref fileFailedAction, fileNotify))
            {
                m_logger.LogWarning("Failed to restore file >{File}< in {Method}", tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSetAligned));
                goto FAILURE;
            }

            // success
            lastFileFailed = false;
            m_logger.LogTrace("File >{File}< restored ok", tfi.FileDescr.FullName);
            continue;

        FAILURE:
            lastFileFailed = true; // must indicate this so that the tape moves back if we retry

            if (fileFailedAction == FileFailedAction.Retry && tfi != null && tfi.IsValid)
            {
                ResetError(); // give the retry a clean slate

                m_logger.LogTrace("Retrying file >{File}< as per file failed action", tfi.FileDescr.FullName);
                // Block-based positioning handles retry: the next iteration will MoveToBlock(tfi.Block)
                StatsUndoFailure(); // don't double-count
                goto RETRY;
            }

            LatchFailure();
            overallSuccess = false;

            if (ignoreFailures && fileFailedAction == FileFailedAction.Skip)
                continue;
            else
                break;
        }

        NotifySetEnd(fileNotify);

        return overallSuccess;
    }


    // The context with which we can resume restore on the previous volume
    private struct TapeRestoreContext(List<TapeFileInfo>[] filesSelected, int currSetIdx, bool ignoreFailures, ITapeFileNotifiable? fileNotify, bool packed)
    {
        internal List<TapeFileInfo>[] filesSelected = filesSelected;
        internal readonly bool ignoreFailures = ignoreFailures;
        internal readonly ITapeFileNotifiable? fileNotify = fileNotify;
        internal readonly bool packed = packed;

        internal int initialCurrSetIdx = currSetIdx;
        internal int filesSelectedIdx = filesSelected.Length - 1; // we'll be counting down

        internal bool overallSuccess = true;
    }
    private TapeRestoreContext? MultiVolumeContext { get; set; } = null;
    /// <summary>Whether a multi-volume continuation context is pending (earlier volume needed).</summary>
    public bool CanResumeFromAnotherVolume => MultiVolumeContext != null;
    /// <summary>Volume number to load next; valid only when <see cref="CanResumeFromAnotherVolume"/> is <see langword="true"/>.</summary>
    public int VolumeToResumeFrom { get; private set; } = -1;

    /// <summary>
    /// Continues the restore from a different volume after the caller has loaded the requested media.
    /// <para>Validates the new volume's TOC (volume number and set sizes) before proceeding.
    ///  Preserves the original TOC for consistent file addressing across volumes.</para>
    /// </summary>
    public TapeResult ResumeRestoreFromAnotherVolume()
    {
        if (!CanResumeFromAnotherVolume)
            return FailedOperationResult;

        m_logger.LogTrace("Resuming multi-volume restore from new volume #{Volume}", VolumeToResumeFrom);

        Debug.Assert(MultiVolumeContext != null);
        Debug.Assert(VolumeToResumeFrom >= 0);

        // since we're on the new media volume, renew Navigator
        if (!Manager.RenewNavigator())
        {
            LogErrorAsDebug("Failed to renew Navigator");
            return FailedOperationResult;
        }

        // Check if the newly provided volume is the right one by analyzing its TOC
        //  Save the current TOC as it contains more sets than the one we're restoring
        var orgTOC = new TapeTOC(TOC);
        try
        {           
            if (!RestoreTOC())
            {
                LogErrorAsWarning("Failed to restore TOC for new volume");
                return FailedOperationResult;
            }
            // Check if the new volume has the right volume number
            if (TOC.Volume != VolumeToResumeFrom)
            {
                LogErrorAsWarning("Volume mismatch for new volume");
                return FailedOperationResult;
            }
            // As the final test, check the size of the next backup set to restore
            int setIdx = MultiVolumeContext.Value.initialCurrSetIdx - MultiVolumeContext.Value.filesSelectedIdx;
            if (TOC[setIdx].Count != orgTOC[setIdx].Count)
            {
                LogErrorAsWarning($"Set size mismatch on new volume for set #{setIdx}");
                return FailedOperationResult;
            }
        }
        finally
        {
            TOC.CopyFrom(orgTOC); // restore the original TOC
        }
        // update the volme
        TOC.Volume = VolumeToResumeFrom;
        // Ok to proceed with the new volume. Notice: Keep our current TOC, since we're restoring the whole file series using it

        return RestoreFilesFromCurrentSetDownInt(null, MultiVolumeContext.Value.ignoreFailures, MultiVolumeContext.Value.fileNotify, MultiVolumeContext.Value.packed)
            ? TapeResult.OK : FailedOperationResult;
    } // ResumeRestoreOnAnotherVolume()

    private bool RestoreFilesFromCurrentSetDownInt(List<TapeFileInfo>?[]? filesSelected, bool ignoreFailures, ITapeFileNotifiable? fileNotify, bool packed)
    {
        m_logger.LogTrace("Starting restoring files from current set #{Set} down (packed={Packed})", TOC.CurrentSetIndex, packed);

        Debug.Assert(CanResumeFromAnotherVolume || filesSelected != null); // either resuming or restoring from a specified list

        TapeRestoreContext rc = CanResumeFromAnotherVolume ? MultiVolumeContext!.Value :
            new(filesSelected!, TOC.CurrentSetIndex, ignoreFailures, fileNotify, packed);

        // Total file size is fully known upfront from the TOC (unlike backup, where source
        //  files must be scanned in the background) — compute it synchronously, once, on a
        //  fresh start. Resumes reuse the estimate already stored in _stats.
        if (!CanResumeFromAnotherVolume)
            _stats.BytesTotal = TOC.GetTotalFileSize(filesSelected!, TOC.CurrentSetIndex);

        m_logger.LogTrace("{Method}: incoming rc.overallSuccess={Success}, filesSelectedIdx={Idx}, initialCurrSetIdx={Init}, isResume={Resume}",
            nameof(RestoreFilesFromCurrentSetDownInt), rc.overallSuccess, rc.filesSelectedIdx, rc.initialCurrSetIdx, CanResumeFromAnotherVolume);

        for (int s = 0; s < TOC.Count; s++)
            m_logger.LogTrace("  TOC set #{Idx}: Volume={Vol}, ContFromPrev={CFP}, Count={Count}",
                s, TOC[s].Volume, TOC[s].ContinuedFromPrevVolume, TOC[s].Count);

        // start from the oldest set, so that we only move tape forward
        for (; rc.filesSelectedIdx >= 0; rc.filesSelectedIdx--)
        {
            if (rc.filesSelected[rc.filesSelectedIdx]?.Count == 0)
                continue; // optimization: don't bother with sets from which we restore no files

            TOC.CurrentSetIndex = rc.initialCurrSetIdx - rc.filesSelectedIdx;
            // check if the current set is present on this volume
            if (!TOC.IsCurrentSetOnVolume)
            {
                // Set up continuation on a previous volume for multi-volume restore
                m_logger.LogTrace("Setting up multi-volume restore from set #{Set}", TOC.CurrentSetIndex);
                MultiVolumeContext = rc;
                VolumeToResumeFrom = TOC.CurrentSetTOC.Volume;
                TOC.CurrentSetIndex = rc.initialCurrSetIdx; // restore the initial current set index
                Debug.Assert(CanResumeFromAnotherVolume); // we're ready to continue with multi-volume restore
                // Tear down any active read session before yielding for volume swap.
                //  The pipelined read backend has a worker thread that would otherwise
                //  keep prefetching while the host unloads media, racing the drive teardown.
                Manager.EndReadWrite();
                return false;
            }

#pragma warning disable CS0618 // Type or member is obsolete -- FIXME transition period
            bool result = rc.packed
                ? ((rc.filesSelected[rc.filesSelectedIdx] != null) ?
                    RestoreFilesFromCurrentSet(rc.filesSelected[rc.filesSelectedIdx], ignoreFailures, fileNotify) :
                    RestoreAllFilesFromCurrentSetInt(ignoreFailures, fileNotify))
                : ((rc.filesSelected[rc.filesSelectedIdx] != null) ?
                    RestoreFilesFromCurrentSetAligned(rc.filesSelected[rc.filesSelectedIdx], ignoreFailures, fileNotify) :
                    RestoreFilesFromCurrentSetAligned(ignoreFailures, fileNotify)); // null means restore all files
#pragma warning restore CS0618 // Type or member is obsolete
            if (!result)
            {
                m_logger.LogWarning("{Method}: Inner restore returned false: filesSelectedIdx={Idx}, set #{Set}",
                    nameof(RestoreFilesFromCurrentSetDownInt), rc.filesSelectedIdx, TOC.CurrentSetIndex);

                rc.overallSuccess = false;
                if (!CanResumeFromAnotherVolume) // media full isn't an unrecoverable error
                    LatchFailure(); // latch the failure for the final result
                
                if (!ignoreFailures || IsAbortRequested)
                    break;
            }
        }

        if (rc.filesSelectedIdx < 0)
        {
            MultiVolumeContext = null; // we're done with [multi-volume] restore -> clear multi-volume context
            VolumeToResumeFrom = -1;
        }

        TOC.CurrentSetIndex = rc.initialCurrSetIdx; // restore the initial current set index

        m_logger.LogTrace("{Method} exiting: overallSuccess={Success}, filesSelectedIdx={Idx}, MultiVolumeContext set={Pending}",
            nameof(RestoreFilesFromCurrentSetDownInt), rc.overallSuccess, rc.filesSelectedIdx, MultiVolumeContext != null);
        return rc.overallSuccess;
    } // RestoreFilesFromCurrentSetDownInt(List<string>)


    /// <summary>
    /// Restores a pre-assembled selection of files from the current set downward (newest → oldest).
    /// <para>The array is indexed newest-first; a <see langword="null"/> entry means all files
    ///  from the corresponding set. Supports multi-volume continuation.</para>
    /// </summary>
    [Obsolete("Use the non-Aligned (Packed) version")]
    public TapeResult RestoreFilesFromCurrentSetDownAligned(List<TapeFileInfo>?[] filesSelected, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring pre-selected files from current set #{Set} down", TOC.CurrentSetIndex);

        _stats.Reset();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(filesSelected, ignoreFailures, fileNotify, packed: false)
            ? TapeResult.OK : FailedOperationResult;
    } // RestoreFilesFromCurrentSetDownAligned(List<string>)

    /// <summary>
    /// Packed pendant of <see cref="RestoreFilesFromCurrentSetDownAligned(List{TapeFileInfo}?[], bool, ITapeFileNotifiable?)"/>.
    ///  Routes per-set content reads through the shared-block read packer.
    /// </summary>
    public TapeResult RestoreFilesFromCurrentSetDown(List<TapeFileInfo>?[] filesSelected, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring (packed) pre-selected files from current set #{Set} down", TOC.CurrentSetIndex);

        _stats.Reset();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(filesSelected, ignoreFailures, fileNotify, packed: true)
            ? TapeResult.OK : FailedOperationResult;
    }


    /// <summary>Restores filtered files from the current set, resolving multi-volume continuation chains.</summary>
    [Obsolete("Use the non-Aligned (Packed) version")]
    public TapeResult RestoreFilesFromCurrentSetAligned(ITapeFileFilter? fileFilter, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring files from current set #{Set}", TOC.CurrentSetIndex);

        _stats.Reset();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: false, fileFilter), ignoreFailures, fileNotify, packed: false)
            ? TapeResult.OK : FailedOperationResult;
    } // RestoreFilesFromCurrentSetAligned(ITapeFileFilter?)

    /// <summary>Restores filtered files from the current set and its incremental chain.</summary>
    [Obsolete("Use the non-Aligned (Packed) version")]
    public TapeResult RestoreFilesFromCurrentSetIncAligned(ITapeFileFilter? fileFilter, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting incrementally restoring files from current set #{Set}", TOC.CurrentSetIndex);

        _stats.Reset();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: true, fileFilter), ignoreFailures, fileNotify, packed: false)
            ? TapeResult.OK : FailedOperationResult;
    } // RestoreFilesFromCurrentSetIncAligned(ITapeFileFilter?)

    /// <summary>Restores all files from the current set (no filter).</summary>
    [Obsolete("Use the non-Aligned (Packed) version")]
    public TapeResult RestoreAllFilesFromCurrentSetAligned(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring all files from current set #{Set}", TOC.CurrentSetIndex);

        _stats.Reset();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: false, filter: null), ignoreFailures, fileNotify, packed: false)
            ? TapeResult.OK : FailedOperationResult;
    } // RestoreAllFilesFromCurrentSetAligned()

    /// <summary>Restores all files from the current set and its incremental chain (no filter).</summary>
    [Obsolete("Use the non-Aligned (Packed) version")]
    public TapeResult RestoreAllFilesFromCurrentSetIncAligned(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting incrementally restoring all files from current set #{Set}", TOC.CurrentSetIndex);

        _stats.Reset();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: true, filter: null), ignoreFailures, fileNotify, packed: false)
            ? TapeResult.OK : FailedOperationResult;
    } // RestoreAllFilesFromCurrentSetIncAligned()

    /// <summary>Packed pendant of <see cref="RestoreFilesFromCurrentSetIncAligned"/>.</summary>
    public TapeResult RestoreFilesFromCurrentSetInc(ITapeFileFilter? fileFilter, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting incrementally restoring (packed) files from current set #{Set}", TOC.CurrentSetIndex);

        _stats.Reset();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: true, fileFilter), ignoreFailures, fileNotify, packed: true)
            ? TapeResult.OK : FailedOperationResult;
    }

    /// <summary>Packed pendant of <see cref="RestoreAllFilesFromCurrentSetIncAligned"/>.</summary>
    public TapeResult RestoreAllFilesFromCurrentSetInc(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting incrementally restoring (packed) all files from current set #{Set}", TOC.CurrentSetIndex);

        _stats.Reset();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: true, filter: null), ignoreFailures, fileNotify, packed: true)
            ? TapeResult.OK : FailedOperationResult;
    }


    /// <summary>
    /// Restores files from multiple backup sets, combining their file selections.
    /// Each set's incremental chain is resolved independently, then the selections are merged
    /// so that files are read from tape in a single forward pass.
    /// Uses <see cref="TapeTOC.SelectFilesFromSets"/> for centralized file selection logic.
    /// </summary>
    /// <param name="setIndexes">List of set indexes to restore (1-based standard indexes).</param>
    /// <param name="incremental">Whether to traverse each set's incremental chain.</param>
    /// <param name="fileFilter">Optional file filter (null = all files).</param>
    /// <param name="ignoreFailures">If true, continue past individual file failures.</param>
    /// <param name="fileNotify">Optional progress/error notification callback.</param>
    [Obsolete("Use the non-Aligned (Packed) version")]
    public TapeResult RestoreFilesFromSetsAligned(
        List<int> setIndexes,
        bool incremental,
        ITapeFileFilter? fileFilter = null,
        bool ignoreFailures = true,
        ITapeFileNotifiable? fileNotify = null)
    {
        if (setIndexes.Count == 0)
            return TapeResult.OK;

        _stats.Reset();
        ResetLatchedFailure();

        m_logger.LogTrace("Restoring files from {Count} set(s): {Sets}",
            setIndexes.Count, string.Join(", ", setIndexes.Select(i => $"#{i}")));

        // Build the dictionary expected by TapeTOC.SelectFilesFromSets.
        //  null value = all files matching the filter for that set.
        //  When a filter is present, pre-select matching files per set.
        var checkedFilesBySet = new Dictionary<int, IReadOnlyList<TapeFileInfo>?>(setIndexes.Count);
        foreach (int idx in setIndexes)
        {
            int stdIdx = TOC.SetIndexToStd(idx);
            if (checkedFilesBySet.ContainsKey(stdIdx))
                continue; // deduplicate

            if (fileFilter is null)
            {
                checkedFilesBySet[stdIdx] = null; // all files
            }
            else
            {
                // Pre-filter the set's files through the ITapeFileFilter
                var setTOC = TOC[stdIdx];
                var matching = setTOC.SelectFiles(fileFilter);
                checkedFilesBySet[stdIdx] = matching; // null = all match, list = subset
            }
        }

        var combined = TOC.SelectFilesFromSets(incremental, checkedFilesBySet);

        // SelectFilesFromSets preserves CurrentSetIndex. Set it to the newest
        //  selected set for RestoreFilesFromCurrentSetDownAligned, which iterates downward.
        int newestIdx = checkedFilesBySet.Keys.Select(TOC.SetIndexToStd).Max();
        TOC.CurrentSetIndex = newestIdx;
        return RestoreFilesFromCurrentSetDownAligned(combined, ignoreFailures, fileNotify);
    }

    /// <summary>
    /// Packed pendant of <see cref="RestoreFilesFromSetsAligned"/>.
    ///  Routes content reads through the shared-block read packer; supports multi-volume continuation.
    /// </summary>
    public TapeResult RestoreFilesFromSets(
        List<int> setIndexes,
        bool incremental,
        ITapeFileFilter? fileFilter = null,
        bool ignoreFailures = true,
        ITapeFileNotifiable? fileNotify = null)
    {
        if (setIndexes.Count == 0)
            return TapeResult.OK;

        _stats.Reset();
        ResetLatchedFailure();

        m_logger.LogTrace("Restoring (packed) files from {Count} set(s): {Sets}",
            setIndexes.Count, string.Join(", ", setIndexes.Select(i => $"#{i}")));

        var checkedFilesBySet = new Dictionary<int, IReadOnlyList<TapeFileInfo>?>(setIndexes.Count);
        foreach (int idx in setIndexes)
        {
            int stdIdx = TOC.SetIndexToStd(idx);
            if (checkedFilesBySet.ContainsKey(stdIdx))
                continue; // deduplicate

            if (fileFilter is null)
            {
                checkedFilesBySet[stdIdx] = null; // all files
            }
            else
            {
                var setTOC = TOC[stdIdx];
                var matching = setTOC.SelectFiles(fileFilter);
                checkedFilesBySet[stdIdx] = matching;
            }
        }

        var combined = TOC.SelectFilesFromSets(incremental, checkedFilesBySet);

        int newestIdx = checkedFilesBySet.Keys.Select(TOC.SetIndexToStd).Max();
        TOC.CurrentSetIndex = newestIdx;
        return RestoreFilesFromCurrentSetDown(combined, ignoreFailures, fileNotify);
    }

}


/// <summary>
/// Restore agent that writes tape data to disk files and applies original file attributes.
///  Uses double-buffered reads via <see cref="BufferedTapeReadStream"/>.
/// </summary>
public class TapeFileRestoreAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileRestoreBaseAgent(drive, legacyTOC)
{
    [Obsolete("Use the non-Aligned (Packed) version")]
    protected override bool RestoreFileCoreAligned(FileInfo fileInfo, TapeReadStream rstream, NonCryptographicHashAlgorithm? hasher)
    {
        // Double-buffer tape reads to overlap with file writes
        using var buffered = new BufferedTapeReadStream(rstream, Drive.BlockSize);

        if (hasher == null)
        {
            using var dstFileStream = fileInfo.Create(); // fileInfo.Open(FileMode.OpenOrCreate, FileAccess.Write);
            buffered.CopyTo(dstFileStream);
        }
        else
        {
            var dstFileStream = fileInfo.Create(); // fileInfo.Open(FileMode.OpenOrCreate, FileAccess.Write);
            // Notice we can attach hasher to either rstream or dstFileStream -- we go for dstFileStream since it may get disposed
            using var hashingStream = new HashingStream(dstFileStream, hasher, ownInner: true); // will dispose dstFileStream
            buffered.CopyTo(hashingStream);
        }

        return base.RestoreFileCoreAligned(fileInfo, rstream, hasher);
    }

    protected override bool RestoreFileCore(FileInfo fileInfo, Stream rstream, NonCryptographicHashAlgorithm? hasher)
    {
        // Packer-backed reads come from a small ring cache; no extra buffering needed.
        // Use TapeBackupTargetStream so BackupWrite restores all NTFS streams (ACL, ADS, EA, etc.)
        //  in addition to the default data stream.
        if (hasher == null)
        {
            using var dstFileStream = TapeBackupTargetStream.Create(fileInfo, m_logger);
            rstream.CopyTo(dstFileStream);
        }
        else
        {
            var dstFileStream = TapeBackupTargetStream.Create(fileInfo, m_logger);
            using var hashingStream = new HashingStream(dstFileStream, hasher, ownInner: true);
            rstream.CopyTo(hashingStream);
        }

        return base.RestoreFileCore(fileInfo, rstream, hasher);
    }

    protected override bool PostProcessFileInternal(TapeFileDescriptor fileDescr, FileInfo fileInfo)
    {
        // Apply timestamps and attributes explicitly; these complement BackupWrite's
        //  security-descriptor restoration with reliable TOC-sourced values.
        return fileDescr.ApplyToFileInfo(fileInfo);
    }

} // class TapeFileRestoreAgent

/// <summary>
/// Validate agent that reads tape data and verifies CRC integrity without writing to disk.
///  File data is discarded to <see cref="Stream.Null"/>.
/// </summary>
public class TapeFileValidateAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileRestoreBaseAgent(drive, legacyTOC)
{
    [Obsolete("Use the non-Aligned (Packed) version")]
    protected override bool RestoreFileCoreAligned(FileInfo fileInfo, TapeReadStream rstream, NonCryptographicHashAlgorithm? hasher)
    {
        if (hasher == null)
        {
            using var dstFileStream = Stream.Null;
            rstream.CopyTo(dstFileStream);
        }
        else
        {
            var dstFileStream = Stream.Null;
            // Notice we can attach hasher to either rstream or dstFileStream -- we go for dstFileStream since it may get disposed
            using var hashingStream = new HashingStream(dstFileStream, hasher, ownInner: true); // will dispose dstFileStream
            rstream.CopyTo(hashingStream);
        }

        return base.RestoreFileCoreAligned(fileInfo, rstream, hasher);
    }

    protected override bool RestoreFileCore(FileInfo fileInfo, Stream rstream, NonCryptographicHashAlgorithm? hasher)
    {
        if (hasher == null)
        {
            using var dstFileStream = Stream.Null;
            rstream.CopyTo(dstFileStream);
        }
        else
        {
            var dstFileStream = Stream.Null;
            using var hashingStream = new HashingStream(dstFileStream, hasher, ownInner: true);
            rstream.CopyTo(hashingStream);
        }

        return base.RestoreFileCore(fileInfo, rstream, hasher);
    }

} // class TapeFileValidateAgent

/// <summary>
/// Verify agent that compares tape data byte-by-byte against existing disk files
///  (via <see cref="StreamHelpers.CompareTo"/>) and validates CRC.
/// </summary>
public class TapeFileVerifyAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileRestoreBaseAgent(drive, legacyTOC)
{
    [Obsolete("Use the non-Aligned (Packed) version")]
    protected override bool RestoreFileCoreAligned(FileInfo fileInfo, TapeReadStream rstream, NonCryptographicHashAlgorithm? hasher)
    {
        // Double-buffer tape reads to overlap with file reads and comparison
        using var buffered = new BufferedTapeReadStream(rstream, Drive.BlockSize);
        bool match;

        if (hasher == null)
        {
            using var dstFileStream = fileInfo.OpenRead();
            match = buffered.CompareTo(dstFileStream);
        }
        else
        {
            using var dstFileStream = fileInfo.OpenRead();
            // Since we're checking the tape stream, attach the hasher to the buffered tape read
            using var hashingStream = new HashingStream(buffered, hasher, ownInner: false); // do NOT dispose buffered!
            match = dstFileStream.CompareTo(hashingStream);
        }

        if (!match)
            throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                $"Data mismatch vs. source file >{fileInfo.FullName}<");

        return base.RestoreFileCoreAligned(fileInfo, rstream, hasher);
    }

    protected override bool RestoreFileCore(FileInfo fileInfo, Stream rstream, NonCryptographicHashAlgorithm? hasher)
    {
        bool match;
        // Open the disk file via TapeBackupSourceStream so BackupRead produces the same
        //  opaque blob format as was written during backup — enabling accurate byte comparison.
        if (hasher == null)
        {
            using var dstFileStream = TapeBackupSourceStream.Open(fileInfo, m_logger);
            match = rstream.CompareTo(dstFileStream);

        }
        else
        {
            using var dstFileStream = TapeBackupSourceStream.Open(fileInfo, m_logger);
            using var hashingStream = new HashingStream(rstream, hasher, ownInner: false);
            match = dstFileStream.CompareTo(hashingStream);
        }

        if (!match)
            throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                $"Data mismatch vs. source file >{fileInfo.FullName}<");

        return base.RestoreFileCore(fileInfo, rstream, hasher);
    }

} // class TapeFileVerifyAgent


// namespace TapeNET
