using System.Diagnostics;
using System.IO.Hashing;

using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;

using TapeLibNET.Compression;
using TapeLibNET.Drive;
using TapeLibNET.Format;
using TapeLibNET.Headers;
using TapeLibNET.Legacy;
using TapeLibNET.Media;
using TapeLibNET.Streams;
using TapeLibNET.Toc;
using TapeLibNET.Encryption;

namespace TapeLibNET.Agents;


/// <summary>
/// Abstract restore agent — reads files from tape content sets, validates headers and CRC,
///  and delegates actual file processing to <see cref="RestoreFileCore"/>.
/// <para>Concrete subclasses: <see cref="TapeFileRestoreAgent"/> (restore to disk),
///  <see cref="TapeFileValidateAgent"/> (read + CRC only),
///  <see cref="TapeFileVerifyAgent"/> (compare tape vs. disk).
///  Extended by <see cref="TapeFileRestoreAgentEx"/> for target-directory and handle-existing logic.</para>
/// <para>Supports multi-volume restore via <see cref="CanResumeFromAnotherVolume"/> /
///  <see cref="ResumeRestoreFromAnotherVolume"/>.</para>
/// </summary>
public abstract class TapeFileRestoreBaseAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeAgentBase(drive, legacyTOC)
{
    #region *** Properties ***

    // A flag that the last file has been skipped since it's not reported via the return value of RestoreNextFile()
    protected bool LastFileSkipped { get; private set; } = false;

    #endregion

    /// <summary>
    /// Positions at the current set and opens the content read session, verifying the set header before
    ///  a single file byte is delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Navigates EXPLICITLY, before handing control to the manager (SH-20).</b> The manager's
    ///  <c>BeginReadContent</c> would position internally, burying any navigation error two layers down —
    ///  where the recovery cannot see it. Navigating here mirrors what
    ///  <c>TapeFileBackupAgent.BeginWriteContentForCurrentSet</c> has always done, and costs nothing: the
    ///  manager's own <c>MoveToTargetContentSet</c> then finds target == current and returns without
    ///  touching the transport (SH-4).
    /// </para>
    /// <para>
    /// <b>SH-8:</b> the set header is read ONLY when the head actually lands on a set's first block.
    ///  Reading unconditionally would consume a CONTENT block mid-set and corrupt the next file.
    /// </para>
    /// </remarks>
    /// <param name="fileNotify">Optional callback, for the set-level anomaly channel.</param>
    private bool BeginReadContentForCurrentSet(ITapeFileNotifiable? fileNotify)
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

        Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet();

        // SH-8 — capture BEFORE positioning, which is the only moment this is knowable. The head lands on
        //  the set's FIRST block — the only position where a set header sits — exactly when the target
        //  differs from the navigator's current belief. (Kept even though we now navigate ourselves: a
        //  recovery may change CurrentContentSet, so the answer must be taken up front.)
        bool willPositionAtSetStart = Navigator.TargetContentSet != Navigator.CurrentContentSet;

        // Position OURSELVES rather than leaving it to Manager.BeginReadContent -- see the remarks.
        //  SH-20: a backward count that fails positionally is retried from begin-of-content.
        if (!NavigateToTargetContentSet(fileNotify))
        {
            m_logger.LogWarning("Failed to position at the target content set in {Method}",
                nameof(BeginReadContentForCurrentSet));
            return false;   // NavigateToTargetContentSet already synced the error
        }

        // Transition to Content mode. Its internal MoveToTargetContentSet is now a no-op (SH-4).
        if (!Manager.BeginReadContent())
        {
            m_logger.LogWarning("Failed to transition to reading content in {Method}",
                nameof(BeginReadContentForCurrentSet));
            SyncErrorFrom(Manager);
            return false;
        }

        // Verify the set we actually landed on before delivering a single file byte (SH-7, SH-8).
        if (willPositionAtSetStart && Navigator.SetHeadersExpected
                && !VerifySetHeaderForCurrentSet(fileNotify))
            return false;

        // set the block size from the set to the manager
        Drive.SetBlockSize(TOC.CurrentSetTOC.BlockSize);

        // Hardware compression interlock: match the drive's HW compression state to what was
        //  used when this set was written. HW sets need compression on; SW/None sets must have
        //  it off so the drive doesn't attempt to decompress already-decompressed bytes.
        Drive.SetHardwareCompression(TOC.CurrentSetTOC.Compression == TapeCompression.Hardware
            && CipherFor(TOC.CurrentSetTOC).AllowsHardwareCompression);

        // The set's encryption session (Design-Encryption §9.1, E16): AFTER the set header was read — it is never
        //  encrypted — and before the first file byte. Closed at the end of the set.
        if (!BeginSetEncryption(forWrite: false))
            return false;   // set and latched

        // Success
        ResetError();
        return true;
    }

    /// <summary>
    /// Performs the actual file processing (write to disk, validate, or verify).
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

    // =====================================================================
    //  Packed restore: route content
    //  reads through TapeFilePipelinedReader so files that share tape blocks (or
    //  start at non-zero intra-block offsets) restore transparently. Unlike
    //  the packed BACKUP path, packed restore needs no commit decoupling --
    //  reads are synchronous and notifications fire in-line.
    // =====================================================================

    /// <summary>
    /// Returns <see langword="true"/> on success, <see langword="false"/> on failure.
    ///  Sets <paramref name="fileFailedAction"/> only if the caller should abort entire operation.
    /// </summary>
    /// <param name="tfi">The tape file info.</param>
    /// <param name="fileFailedAction">The action to take if the file fails.</param>
    /// <param name="fileNotify">The file notifiable.</param>
    /// <returns><see langword="true"/> if the file was restored successfully; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="TapeIOException"></exception>
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

            TapeSetTOC setTOC = TOC.CurrentSetTOC;
            ITapeSetCipher cipher = ActiveSetCipher;

            // The packer needs the file's exact tape position and length to bound its
            //  read window. Use SizeOnTape (set during packed backup) when available;
            //  fall back to an estimate in the set's own data format otherwise — unless the scheme needs the exact
            //  end of the record (software encryption: the final chunk, Design-Encryption §5.7).
            if (tfi.SizeOnTape <= 0 && !cipher.AllowsEstimatedSize)
                throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                    $"No recorded size on tape for encrypted file >{tfi.FileDescr.FullName}< — it cannot be bounded");
            long totalBytes = (tfi.SizeOnTape > 0)
                ? tfi.SizeOnTape
                : setTOC.EstimateFileOverhead(tfi) + tfi.FileDescr.Length;

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

            // The record as written: the façade itself, or the cipher's decrypting stream over it.
            Stream src = cipher.OpenRead(rstream, setTOC, tfi);
            using (ReferenceEquals(src, rstream) ? null : src)      // dispose only what the cipher created
            {
                // Validate the file header (per data format) and read the body's codec, before delivering the body.
                CheckFileHeader(src, setTOC, tfi);
                TapeFileCodec codec = ReadBodyCodec(src, setTOC, tfi);

#if TEST_HOOKS
                if (SimulateFileFailures.ShouldFailNow())
                {
                    throw new TapeIOException((uint)WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION,
                        $"Simulated restore failure for file >{tfi.FileDescr.FullName}< (#{SimulateFileFailures.Counter})");
                }
#endif
                var hasher = CreateHasher(setTOC.HashAlgorithm);
                var fileDescr = tfi.FileDescr;

                if (!PreProcessFileInternal(ref fileDescr) || !NotifyPreProcessFile(fileNotify, tfi))
                {
                    FileSkippedInternal(tfi.FileDescr);
                    NotifyFileSkipped(fileNotify, tfi);
                    m_logger.LogTrace("Skipping (packed) file >{File}< per pre-processor request", tfi.FileDescr.FullName);
                    return true;
                }

                FileInfo fileInfo = fileDescr.CreateFileInfo();

                // Wrap src with a decompressor when the file was ZSTD-compressed at backup time.
                //  The hasher inside RestoreFileCore always sees decompressed bytes (codec-independent hash).
                //  For Stored files, bodyStream == src (no allocation, no copy).
                ZstdCodec? zstdCodec = codec == TapeFileCodec.Zstd ? new ZstdCodec() : null;
                using (zstdCodec)
                {
                    Stream bodyStream = zstdCodec != null
                        ? new DecompressionFilterStream(src)
                        : src;
                    using (zstdCodec != null ? bodyStream : null) // only dispose when we own it
                    {
                        if (!RestoreFileCore(fileInfo, bodyStream, hasher))
                        {
                            throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                                $"Processing failed for file >{tfi.FileDescr.FullName}<");
                        }

                        // E5: ZSTD stops at its frame end, Verify stops when the disk file ends — authenticate the rest
                        //  of the record, up to and including its final chunk, while the streams are still open.
                        try
                        {
                            cipher.CompleteRead(src);
                        }
                        catch (TapeIOException ex)
                        {
                            throw new TapeIOException(ex.Error,
                                $"Authentication failed for file >{tfi.FileDescr.FullName}<: {ex.ErrorMessage}", ex);
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
                            $"CRC check failed for file >{tfi.FileDescr.FullName}<. Hasher: {setTOC.HashAlgorithm}");
                    }
                }

                BytesRestored += tfi.FileDescr.Length;

                if (NotifyPostProcessFile(fileNotify, tfi))
                    return PostProcessFileInternal(fileDescr, fileInfo);

                return true;
            }
        }
        catch (TapeAbortRequestedException)
        {
            IsAbortRequested = true; // the callback wrapper might've already set it - but we want to be sure
            fileFailedAction = FileFailedAction.Abort;
            m_logger.LogTrace("Abort requested while processing (packed) file >{File}< in {Method}",
                tfi.FileDescr.FullName, nameof(RestoreNextFile));
            return false;
        }
        catch (Exception ex)
        {
            SetError(ex);
            // NotifyFileFailed wrapper will catch TapeAbortRequestedException and set IsAbortRequested
            //  if the caller requested abort, so we don't need to worry here
            fileFailedAction = NotifyFileFailed(fileNotify, tfi, ex);
            m_logger.LogWarning("Exception {Exception} while processing (packed) file >{File}<", ex, tfi.FileDescr.FullName);
            return false;
        }
    } // RestoreNextFile()

    /// <summary>
    /// Reads and checks the per-file header in front of <paramref name="tfi"/>'s body, in the set's data format.
    ///  Leaves <paramref name="rstream"/> at the first byte after the header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>V2:</b> the frame's CRC-64, then identity — <c>SetId</c> and <c>FileId</c> must both match. A path mismatch is
    ///  only logged: the TOC is authoritative for names, and a later TOC-level rename is conceivable.
    /// </para>
    /// <para><b>Legacy:</b> the 12-byte <c>TF</c> header must carry the file's legacy UID (its <c>FileId</c>).</para>
    /// </remarks>
    /// <exception cref="TapeIOException">ERROR_CRC for a damaged frame, ERROR_INVALID_DATA for anything else.</exception>
    private void CheckFileHeader(Stream rstream, TapeSetTOC setTOC, TapeFileInfo tfi)
    {
        if (setTOC.DataFormat == TapeDataFormat.Legacy)
        {
            if (!LegacyFileHeader.Matches(new LegacyDeserializer(rstream), tfi.FileId))
                throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Header mismatch for file >{tfi.FileDescr.FullName}<");
            return;
        }

        TapeFileHeader header;
        try
        {
            header = TapeFileHeader.Read(rstream);
        }
        catch (TapeFormatException ex)
        {
            throw new TapeIOException(
                ex.Kind == FormatErrorKind.CrcMismatch ? (uint)WIN32_ERROR.ERROR_CRC : (uint)WIN32_ERROR.ERROR_INVALID_DATA,
                $"File header unreadable for file >{tfi.FileDescr.FullName}<: {ex.Message}");
        }

        if (header.SetId != setTOC.SetId || header.FileId != tfi.FileId)
            throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                $"Header mismatch for file >{tfi.FileDescr.FullName}<: tape carries set {header.SetId:N} file " +
                $"{header.FileId}, expected set {setTOC.SetId:N} file {tfi.FileId}");

        if (!string.Equals(header.Name, tfi.FileDescr.FullName, StringComparison.Ordinal))
            m_logger.LogWarning("File header name >{TapeName}< differs from the TOC name >{TocName}< (FileId {FileId}); " +
                "the TOC is authoritative", header.Name, tfi.FileDescr.FullName, tfi.FileId);
    }

    /// <summary>
    /// Reads the codec byte at the start of a V2 body and checks it against the TOC. Legacy bodies carry no codec byte:
    ///  their codec comes from the TOC alone.
    /// </summary>
    /// <exception cref="TapeIOException">ERROR_INVALID_DATA — missing, unknown, or disagreeing codec.</exception>
    private static TapeFileCodec ReadBodyCodec(Stream rstream, TapeSetTOC setTOC, TapeFileInfo tfi)
    {
        if (setTOC.DataFormat == TapeDataFormat.Legacy)
            return tfi.Codec;

        TapeFileCodec codec;
        try
        {
            codec = TapeFileHeader.ReadCodec(rstream);
        }
        catch (TapeFormatException ex)
        {
            throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                $"Codec byte unreadable for file >{tfi.FileDescr.FullName}<: {ex.Message}");
        }

        if (codec != tfi.Codec)
            throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                $"Codec mismatch for file >{tfi.FileDescr.FullName}<: tape says {codec}, TOC says {tfi.Codec}");
        return codec;
    }

    /// <summary>
    /// Restore the files specified by 'tfis' from the current set via the packer.
    ///  Uses <see cref="TapeAddress"/> positioning and the packed read façade.
    ///  No <see cref="TapeDrive.MoveToBlock"/> is needed here -- the packer seeks to the file's
    ///  exact <c>(block, offset)</c> on <see cref="TapeStreamManager.BeginPackedFileRead"/> .
    /// </summary>
    /// <param name="tfis">The list of TapeFileInfo objects representing the files to restore.</param>
    /// <param name="ignoreFailures">Whether to ignore failures during the restore process.</param>
    /// <param name="fileNotify">An optional notifier for file restore progress.</param>
    /// <returns>True if all files were restored successfully; otherwise, false.</returns>
    private bool RestoreFilesFromCurrentSet(List<TapeFileInfo>? tfis, bool ignoreFailures = true,
        ITapeFileNotifiable? fileNotify = null)
    {
        if (tfis == null) // null means restore all files
            return RestoreAllFilesFromCurrentSetCore(ignoreFailures, fileNotify);

        NotifySetStart(fileNotify, tfis.Count);

        if (!BeginReadContentForCurrentSet(fileNotify))
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
                SetError(WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Invalid file entry in set #{TOC.CurrentSetIndex} — the TOC describes a file that " +
                    "cannot be located");
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

            // Latch only a REAL fault. This label is also reached with no error at all — an invalid
            //  TapeFileInfo, or an abort observed before the file was touched — and latching there
            //  would manufacture a diagnosis out of a user decision. IsAbortRequested is the channel
            //  that says "aborted"; the latched error says "why", and sometimes there is no why.
            if (WentBad)
                LatchFailure();  // latch on the ORIGINAL error if one occured, abort notwithstanding
            overallSuccess = false;

            if (ignoreFailures && fileFailedAction == FileFailedAction.Skip)
                continue;
            else
                break;
        }

        EndSetEncryption();     // E16
        NotifySetEnd(fileNotify);

        m_logger.LogTrace("RestoreFilesFromCurrentSet(tfis) exiting: overallSuccess={Success}", overallSuccess);
        return overallSuccess;
    } // RestoreFilesFromCurrentSet(List<TapeFileInfo>?)


    // Restore ALL files from the current set via the packer.
    private bool RestoreAllFilesFromCurrentSetCore(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        NotifySetStart(fileNotify, TOC.CurrentSetTOC.Count);

        if (!BeginReadContentForCurrentSet(fileNotify))
        {
            NotifySetEnd(fileNotify);
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
                SetError(WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Invalid file entry in set #{TOC.CurrentSetIndex} — the TOC describes a file that " +
                    "cannot be located");
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

            // Latch only a REAL fault
            if (WentBad)
                LatchFailure();  // latch on the ORIGINAL error if one occured, abort notwithstanding
            overallSuccess = false;

            if (ignoreFailures && fileFailedAction == FileFailedAction.Skip)
                continue;
            else
                break;
        }

        EndSetEncryption();     // E16
        NotifySetEnd(fileNotify);

        m_logger.LogTrace("RestoreAllFilesFromCurrentSetCore exiting: overallSuccess={Success}", overallSuccess);
        return overallSuccess;
    } // RestoreAllFilesFromCurrentSetCore()


    // The context with which we can resume restore on the previous volume
    private struct TapeRestoreContext(List<TapeFileInfo>[] filesSelected, int currSetIdx, bool ignoreFailures, ITapeFileNotifiable? fileNotify)
    {
        internal List<TapeFileInfo>[] filesSelected = filesSelected;
        internal readonly bool ignoreFailures = ignoreFailures;
        internal readonly ITapeFileNotifiable? fileNotify = fileNotify;
        internal int initialCurrSetIdx = currSetIdx;
        internal int filesSelectedIdx = filesSelected.Length - 1; // we'll be counting down
        internal bool overallSuccess = true;
    }

    private TapeRestoreContext? MultiVolumeContext { get; set; } = null;

    // The volume loaded for the pending continuation cannot be used — see IsVolumeRejected.
    private bool m_volumeRejected;

    // Why not; carried into the diagnosis when the caller skips the volume.
    private TapeResult m_volumeRejection = TapeResult.OK;

    /// <summary>
    /// Whether the agent ASKS for another volume: load <see cref="VolumeToResumeFrom"/>, then call
    ///  <see cref="ResumeRestoreFromAnotherVolume"/> — or give it up with <see cref="SkipRequestedVolume"/>.
    ///  <see langword="false"/> while the loaded volume is rejected — so a caller looping on this property ends instead
    ///  of re-offering the same cartridge forever.
    /// </summary>
    public bool CanResumeFromAnotherVolume => MultiVolumeContext is not null && !m_volumeRejected;

    /// <summary>
    /// Whether the volume loaded for the continuation CANNOT be used — its TOC unreadable (or undecryptable), another
    ///  series, the wrong volume number, or not holding the set the TOC describes. The restore is still pending
    ///  (<see cref="VolumeToResumeFrom"/> is still needed). The caller decides: load a cartridge and call
    ///  <see cref="ResumeRestoreFromAnotherVolume"/> again, <see cref="SkipRequestedVolume"/>, or end.
    /// </summary>
    public bool IsVolumeRejected => MultiVolumeContext is not null && m_volumeRejected;

    /// <summary>Whether a multi-volume restore is pending — asking for a volume, or with a rejected one.</summary>
    public bool IsMultiVolumePending => MultiVolumeContext is not null;

    /// <summary>Volume number needed next; valid while <see cref="IsMultiVolumePending"/>.</summary>
    public int VolumeToResumeFrom { get; private set; } = -1;

    /// <summary>
    /// Whether <paramref name="onVolume"/> (freshly loaded — encrypted sets locked) describes the same set as
    ///  <paramref name="expected"/>: by file count for plain sets, by identity, scheme and size on tape for encrypted ones.
    /// </summary>
    private static bool SameSetOnTape(TapeSetTOC onVolume, TapeSetTOC expected)
        => onVolume.IsEncrypted || expected.IsEncrypted
            ? onVolume.SetId == expected.SetId
              && onVolume.Encryption == expected.Encryption
              && onVolume.SizeOnTapeTotal == expected.SizeOnTapeTotal
            : onVolume.Count == expected.Count;

    /// <summary>
    /// Continues the restore from a different volume after the caller has loaded the requested media.
    /// <para>
    /// Identifies the cartridge first — its own TOC must read, belong to this series, carry the requested volume number
    ///  and the next set as the TOC describes it. A cartridge that fails any of these is REJECTED
    ///  (<see cref="IsVolumeRejected"/>): nothing has been restored from it, and the caller may retry, skip the volume,
    ///  or end.
    /// </para>
    /// <para>The original TOC is kept: it addresses the whole series.</para>
    /// </summary>
    public TapeResult ResumeRestoreFromAnotherVolume()
    {
        if (MultiVolumeContext is not { } context)
            return FailedOperationResult;

        int volume = VolumeToResumeFrom;
        m_volumeRejected = false;
        m_logger.LogTrace("Resuming multi-volume restore from new volume #{Volume}", volume);

        // Identifying the cartridge reads its TOC through RestoreTOC(), a public verb that resets the failure latch:
        //  save the latch and put it back afterwards. Only the LATCH — NOT the live error, which here is the previous
        //  attempt's volume request or rejection. So a genuine failure from an earlier volume survives the check, while a
        //  rejected cartridge that this retry replaces leaves no trace.
        TapeResult earliest = LatchedFailure;
        TapeResult check = CheckVolumeToResumeFrom(volume);
        RestoreLatchedFailure(earliest);

        if (!check)
        {
            if (IsAbortRequested)
            {
                MultiVolumeContext = null;      // an abort ends the operation: no volume is pending any more
                VolumeToResumeFrom = -1;
                return FailedOperationResult;
            }

            m_volumeRejected = true;
            m_volumeRejection = check;
            m_logger.LogWarning("Volume #{Volume} rejected for the continuation: {Reason}", volume, check.ErrorMessage);
            return check;
        }

        // update the volume
        TOC.Volume = volume;

        // Ok to proceed with the new volume. Notice: Keep our current TOC, since we're restoring the whole file series using it
        return RestoreFilesFromCurrentSetDownCore(null, context.ignoreFailures, context.fileNotify)
            ? TapeResult.OK : FailedOperationResult;
    } // ResumeRestoreFromAnotherVolume()

    /// <summary>
    /// Identifies the loaded cartridge as <paramref name="volume"/> of this series, holding the next set to restore.
    ///  Leaves <see cref="TapeAgentBase.TOC"/> as it was.
    /// </summary>
    private TapeResult CheckVolumeToResumeFrom(int volume)
    {
        // since we're on the new media volume, renew Navigator
        if (!Manager.RenewNavigator())
        {
            SyncErrorFrom(Manager);
            return RejectVolume($"Volume #{volume}: the drive could not be prepared for the cartridge ({LastErrorMessage})");
        }

        // Read the cartridge's own TOC to identify it — then put ours back: it contains more sets than this volume's
        var orgTOC = new TapeTOC(TOC);
        try
        {
            if (!RestoreTOC())
                return RejectVolume($"Volume #{volume}: its table of contents could not be read ({LastErrorMessage})");

            if (orgTOC.MediaId != Guid.Empty && TOC.MediaId != Guid.Empty && TOC.MediaId != orgTOC.MediaId)
                return RejectVolume("The inserted cartridge belongs to another backup series", WIN32_ERROR.ERROR_WRONG_DISK);

            if (TOC.Volume != volume)
                return RejectVolume($"The inserted cartridge is volume #{TOC.Volume}, not volume #{volume}",
                    WIN32_ERROR.ERROR_WRONG_DISK);

            // As the final test, check the next backup set to restore is the same on the new volume. The freshly loaded
            //  TOC has its encrypted sets LOCKED — compare what a stub can tell (Design-Encryption §9.5).
            int setIdx = MultiVolumeContext!.Value.initialCurrSetIdx - MultiVolumeContext.Value.filesSelectedIdx;
            if (setIdx > TOC.Count || !SameSetOnTape(TOC[setIdx], orgTOC[setIdx]))
                return RejectVolume($"Volume #{volume} does not hold set #{setIdx} as the table of contents describes it",
                    WIN32_ERROR.ERROR_WRONG_DISK);

            return TapeResult.OK;
        }
        finally
        {
            TOC.CopyFrom(orgTOC); // restore the original TOC
        }
    }

    /// <summary>Sets and returns a rejection: <paramref name="error"/>, else the current error, else invalid data.</summary>
    private TapeResult RejectVolume(string message, WIN32_ERROR? error = null)
    {
        uint code = error is { } e ? (uint)e : WentBad ? LastError : (uint)WIN32_ERROR.ERROR_INVALID_DATA;
        SetError(code, message);
        return TapeResult.Fail(code, message);
    }

    /// <summary>
    /// Gives up the sets on <see cref="VolumeToResumeFrom"/> — after a rejection (the user puts up with the loss) or
    ///  voluntarily (the cartridge is not at hand). Their selected files are not processed.
    /// </summary>
    /// <param name="setsSkipped">How many sets of the work package were given up.</param>
    /// <param name="filesSkipped">How many selected files they hold (a locked set counts none — it was never readable).</param>
    /// <returns>
    /// Afterwards the agent either asks for the next volume (<see cref="CanResumeFromAnotherVolume"/>, result: failure, as
    ///  on any volume request) or is done (result: the operation's outcome).
    /// </returns>
    /// <remarks>
    /// <b>Voluntary vs. after an error.</b> A voluntary skip counts the files as SKIPPED and is no fault. A skip after a
    ///  rejection counts them as FAILED and latches the rejection, named with the volume — the error did happen.
    /// </remarks>
    public TapeResult SkipRequestedVolume(out int setsSkipped, out int filesSkipped)
    {
        setsSkipped = 0;
        filesSkipped = 0;
        if (MultiVolumeContext is not { } rc)
            return FailedOperationResult;

        int skipped = VolumeToResumeFrom;
        bool afterError = m_volumeRejected;

        while (rc.filesSelectedIdx >= 0)
        {
            List<TapeFileInfo>? selected = rc.filesSelected[rc.filesSelectedIdx];
            TapeSetTOC set = TOC[rc.initialCurrSetIdx - rc.filesSelectedIdx];
            if (selected?.Count != 0)       // null = all files of the set
            {
                if (set.Volume != skipped)
                    break;                  // the first set beyond the skipped volume
                setsSkipped++;
                filesSkipped += selected?.Count ?? (set.IsLocked ? 0 : set.Count);
            }
            rc.filesSelectedIdx--;
        }

        StatsAddUnprocessed(filesSkipped, asFailed: afterError);
        if (afterError)
        {
            uint code = m_volumeRejection.Success ? (uint)WIN32_ERROR.ERROR_WRONG_DISK : m_volumeRejection.ErrorCode;
            SetError(code, $"Volume #{skipped} skipped — {setsSkipped} set(s), {filesSkipped} file(s) not processed: " +
                m_volumeRejection.ErrorMessage);
            LatchFailure();
            rc.overallSuccess = false;
        }
        m_logger.LogWarning("Volume #{Volume} skipped ({How}): {Sets} set(s), {Files} file(s) not processed",
            skipped, afterError ? "after an error" : "on request", setsSkipped, filesSkipped);

        m_volumeRejected = false;
        m_volumeRejection = TapeResult.OK;

        if (rc.filesSelectedIdx < 0)
        {
            MultiVolumeContext = null;      // nothing left — the operation is over
            VolumeToResumeFrom = -1;
            TOC.CurrentSetIndex = rc.initialCurrSetIdx;
            return rc.overallSuccess ? TapeResult.OK : FailedOperationResult;
        }

        // Ask for the volume of the next set — even if it is the one the TOC believes loaded: what is in the drive is
        //  not to be trusted after a skip (a rejected cartridge, or none the user meant to use).
        MultiVolumeContext = rc;
        VolumeToResumeFrom = TOC[rc.initialCurrSetIdx - rc.filesSelectedIdx].Volume;
        return FailedOperationResult;
    }

    private bool RestoreFilesFromCurrentSetDownCore(List<TapeFileInfo>?[]? filesSelected, bool ignoreFailures, ITapeFileNotifiable? fileNotify)
    {
        m_logger.LogTrace("Starting restoring files from current set #{Set} down", TOC.CurrentSetIndex);

        bool resuming = MultiVolumeContext is not null;
        Debug.Assert(resuming || filesSelected != null); // either resuming or restoring from a specified list

        TapeRestoreContext rc = resuming ? MultiVolumeContext!.Value :
            new(filesSelected!, TOC.CurrentSetIndex, ignoreFailures, fileNotify);

        // From here on THIS call owns the context: no volume is pending while it runs. A volume request below
        //  re-establishes it; every other exit — the end, an abort, a failure with !ignoreFailures — leaves none.
        //  (It also makes the failure latch below work on resumed volumes: it used to be gated on "no context".)
        MultiVolumeContext = null;
        VolumeToResumeFrom = -1;
        m_volumeRejected = false;

        // Total file size is fully known upfront from the TOC (unlike backup, where source
        //  files must be scanned in the background) — compute it synchronously, once, on a
        //  fresh start. Resumes reuse the estimate already stored in _stats.
        //  Locked sets count nothing: they are refused below, before they start.
        if (!resuming)
            _stats.BytesTotal = SelectedBytesTotal(filesSelected!, TOC.CurrentSetIndex);

        m_logger.LogTrace("{Method}: incoming rc.overallSuccess={Success}, filesSelectedIdx={Idx}, initialCurrSetIdx={Init}, isResume={Resume}",
            nameof(RestoreFilesFromCurrentSetDownCore), rc.overallSuccess, rc.filesSelectedIdx, rc.initialCurrSetIdx, resuming);
        for (int s = 1; s <= TOC.Count; s++)
            m_logger.LogTrace("  TOC set #{Idx}: Volume={Vol}, ContFromPrev={CFP}, Count={Count}",
                s, TOC[s].Volume, TOC[s].ContinuedFromPrevVolume, TOC[s].DisplayCount);

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

            // E9: a locked set, or a scheme this build / drive cannot read, fails HERE — before NotifySetStart, before
            //  the tape moves. The other sets of the selection carry on as ignoreFailures says.
            if (!ValidateSetEncryption(forWrite: false))
            {
                rc.overallSuccess = false;      // set and latched
                if (!ignoreFailures || IsAbortRequested)
                    break;
                continue;
            }

            bool result = rc.filesSelected[rc.filesSelectedIdx] != null
                ? RestoreFilesFromCurrentSet(rc.filesSelected[rc.filesSelectedIdx], ignoreFailures, fileNotify)
                : RestoreAllFilesFromCurrentSetCore(ignoreFailures, fileNotify); // null means restore all files

            if (!result)
            {
                m_logger.LogWarning("{Method}: Inner restore returned false: filesSelectedIdx={Idx}, set #{Set}",
                    nameof(RestoreFilesFromCurrentSetDownCore), rc.filesSelectedIdx, TOC.CurrentSetIndex);
                rc.overallSuccess = false;
                // Latch only a REAL fault — a user-requested abort is none. Latching is idempotent, hence it won't hurt
                //  if the original error has already been latched. (No volume request can be pending here: that path
                //  returns above.)
                if (WentBad)
                    LatchFailure(); // latch on the ORIGINAL error if one occured
                if (!ignoreFailures || IsAbortRequested)
                    break;
            }
        }

        TOC.CurrentSetIndex = rc.initialCurrSetIdx; // restore the initial current set index

        m_logger.LogTrace("{Method} exiting: overallSuccess={Success}, filesSelectedIdx={Idx}",
            nameof(RestoreFilesFromCurrentSetDownCore), rc.overallSuccess, rc.filesSelectedIdx);

        return rc.overallSuccess;
    } // RestoreFilesFromCurrentSetDownCore(List<string>)

    /// <summary>
    /// <see cref="TapeTOC.GetTotalFileSize"/> that skips LOCKED sets instead of throwing: they contribute nothing, and
    ///  are refused before they start.
    /// </summary>
    private long SelectedBytesTotal(List<TapeFileInfo>?[] combined, int newestSetIndex)
    {
        long total = 0;
        for (int i = 0; i < combined.Length; i++)
        {
            TapeSetTOC set = TOC[newestSetIndex - i];
            if (set.IsLocked)
                continue;
            total += combined[i]?.Sum(f => f.FileDescr.Length) ?? set.TotalFileSize;
        }
        return total;
    }


    /// <summary>
    /// Restores a pre-assembled selection of files from the current set downward (newest → oldest).
    /// <para>The array is indexed newest-first; a <see langword="null"/> entry means all files
    ///  from the corresponding set. Supports multi-volume continuation.</para>
    /// </summary>
    public TapeResult RestoreFilesFromCurrentSetDown(List<TapeFileInfo>?[] filesSelected, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring (packed) pre-selected files from current set #{Set} down", TOC.CurrentSetIndex);

        _stats.Reset();
        _setAnomalies.Clear();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownCore(filesSelected, ignoreFailures, fileNotify)
            ? TapeResult.OK : FailedOperationResult;
    }


#if _
    /// <summary>
    ///  Routes content reads through the shared-block read packer; supports multi-volume continuation.
    /// </summary>
    public TapeResult RestoreFilesFromCurrentSet(ITapeFileFilter? fileFilter,
        bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring (packed) files from current set #{Set}",
            TOC.CurrentSetIndex);

        _stats.Reset();
        _setAnomalies.Clear();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: false, fileFilter), ignoreFailures, fileNotify)
            ? TapeResult.OK : FailedOperationResult;
    }

    /// <summary>
    ///  Supports multi-volume continuation.
    /// </summary>
    public TapeResult RestoreAllFilesFromCurrentSet(
        bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring (packed) all files from current set #{Set}",
            TOC.CurrentSetIndex);

        _stats.Reset();
        _setAnomalies.Clear();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: false, filter: null), ignoreFailures, fileNotify)
            ? TapeResult.OK : FailedOperationResult;
    }

    /// <summary>Restores filtered files from the current set and its incremental chain.</summary>
    public TapeResult RestoreFilesFromCurrentSetInc(ITapeFileFilter? fileFilter, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting incrementally restoring (packed) files from current set #{Set}", TOC.CurrentSetIndex);

        _stats.Reset();
        _setAnomalies.Clear();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: true, fileFilter), ignoreFailures, fileNotify)
            ? TapeResult.OK : FailedOperationResult;
    }

    /// <summary>Restores filtered files from the current set and its incremental chain.</summary>
    public TapeResult RestoreAllFilesFromCurrentSetInc(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting incrementally restoring (packed) all files from current set #{Set}", TOC.CurrentSetIndex);

        _stats.Reset();
        _setAnomalies.Clear();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: true, filter: null), ignoreFailures, fileNotify)
            ? TapeResult.OK : FailedOperationResult;
    }
#endif

    /// <summary>
    /// Runs a file selection; a LOCKED set in it (E6) becomes <c>ERROR_INVALID_PASSWORD</c>, set and latched, before
    ///  anything moves.
    /// </summary>
    private bool TrySelectFiles(Func<List<TapeFileInfo>?[]> select, out List<TapeFileInfo>?[] selected)
    {
        try
        {
            selected = select();
            return true;
        }
        catch (TapeSetLockedException ex)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PASSWORD,
                $"Backup set {ex.SetId:N} is encrypted and locked — its password has not been supplied");
            LatchFailure();
            m_logger.LogWarning("Restore refused before moving the tape: a selected set is locked ({SetId:N})", ex.SetId);
            selected = [];
            return false;
        }
    }

    /// <summary>
    ///  Routes content reads through the shared-block read packer; supports multi-volume continuation.
    /// </summary>
    public TapeResult RestoreFilesFromCurrentSet(ITapeFileFilter? fileFilter,
        bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring (packed) files from current set #{Set}",
            TOC.CurrentSetIndex);

        _stats.Reset();
        _setAnomalies.Clear();
        ResetLatchedFailure();

        if (!TrySelectFiles(() => TOC.SelectFiles(incremental: false, fileFilter), out var selected))
            return FailedOperationResult;
        return RestoreFilesFromCurrentSetDownCore(selected, ignoreFailures, fileNotify)
            ? TapeResult.OK : FailedOperationResult;
    }

    /// <summary>
    ///  Supports multi-volume continuation.
    /// </summary>
    public TapeResult RestoreAllFilesFromCurrentSet(
        bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring (packed) all files from current set #{Set}",
            TOC.CurrentSetIndex);

        _stats.Reset();
        _setAnomalies.Clear();
        ResetLatchedFailure();

        if (!TrySelectFiles(() => TOC.SelectFiles(incremental: false, filter: null), out var selected))
            return FailedOperationResult;
        return RestoreFilesFromCurrentSetDownCore(selected, ignoreFailures, fileNotify)
            ? TapeResult.OK : FailedOperationResult;
    }

    /// <summary>Restores filtered files from the current set and its incremental chain.</summary>
    public TapeResult RestoreFilesFromCurrentSetInc(ITapeFileFilter? fileFilter, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting incrementally restoring (packed) files from current set #{Set}", TOC.CurrentSetIndex);

        _stats.Reset();
        _setAnomalies.Clear();
        ResetLatchedFailure();

        if (!TrySelectFiles(() => TOC.SelectFiles(incremental: true, fileFilter), out var selected))
            return FailedOperationResult;
        return RestoreFilesFromCurrentSetDownCore(selected, ignoreFailures, fileNotify)
            ? TapeResult.OK : FailedOperationResult;
    }

    /// <summary>Restores filtered files from the current set and its incremental chain.</summary>
    public TapeResult RestoreAllFilesFromCurrentSetInc(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting incrementally restoring (packed) all files from current set #{Set}", TOC.CurrentSetIndex);

        _stats.Reset();
        _setAnomalies.Clear();
        ResetLatchedFailure();

        if (!TrySelectFiles(() => TOC.SelectFiles(incremental: true, filter: null), out var selected))
            return FailedOperationResult;
        return RestoreFilesFromCurrentSetDownCore(selected, ignoreFailures, fileNotify)
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
        _setAnomalies.Clear();
        ResetLatchedFailure();

        m_logger.LogTrace("Restoring (packed) files from {Count} set(s): {Sets}",
            setIndexes.Count, string.Join(", ", setIndexes.Select(i => $"#{i}")));

        var checkedFilesBySet = new Dictionary<int, IReadOnlyList<TapeFileInfo>?>(setIndexes.Count);
        int newestIdx = 0;
        bool selectedOk = TrySelectFiles(() =>
        {
            foreach (int idx in setIndexes)
            {
                int stdIdx = TOC.SetIndexToStd(idx);
                if (checkedFilesBySet.ContainsKey(stdIdx))
                    continue; // deduplicate

                // null = all files; a filter selects (throws on a locked set — turned into a clean refusal)
                checkedFilesBySet[stdIdx] = fileFilter is null ? null : TOC[stdIdx].SelectFiles(fileFilter);
            }

            var combinedSel = TOC.SelectFilesFromSets(incremental, checkedFilesBySet);
            newestIdx = checkedFilesBySet.Keys.Select(TOC.SetIndexToStd).Max();
            return combinedSel;
        }, out var combined);
        if (!selectedOk)
            return FailedOperationResult;

        TOC.CurrentSetIndex = newestIdx;
        return RestoreFilesFromCurrentSetDown(combined, ignoreFailures, fileNotify);
    }

} // class TapeFileRestoreBaseAgent


/// <summary>
/// Restore agent that writes tape data to disk files and applies original file attributes.
///  Reads come through the packer's pipelined read ring; <see cref="TapeBackupTargetStream"/> restores all NTFS streams.
/// </summary>
public class TapeFileRestoreAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileRestoreBaseAgent(drive, legacyTOC)
{
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
