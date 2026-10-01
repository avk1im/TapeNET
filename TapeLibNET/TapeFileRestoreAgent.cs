using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Hashing;
using System.Net.Http.Headers;
using System.Runtime.Intrinsics.X86;
using TapeLibNET;
using Windows.Win32.Foundation;


namespace TapeLibNET;

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
        Drive.SetHardwareCompression(TOC.CurrentSetTOC.Compression == TapeCompression.Hardware);

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


    // Restore the files specified by 'tfis' from the current set via the packer.
    //  Mirrors RestoreFilesFromCurrentSet(List<TapeFileInfo>?, ...) but uses TapeAddress
    //  positioning and the packed read façade. No tape MoveToBlock is needed here -- the
    //  packer seeks to the file's exact (block, offset) on BeginRead.
    private bool RestoreFilesFromCurrentSet(List<TapeFileInfo>? tfis, bool ignoreFailures = true,
        ITapeFileNotifiable? fileNotify = null)
    {
        if (tfis == null) // null means restore all files
            return RestoreAllFilesFromCurrentSetInt(ignoreFailures, fileNotify);

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

        NotifySetEnd(fileNotify);

        m_logger.LogTrace("RestoreFilesFromCurrentSet(tfis) exiting: overallSuccess={Success}", overallSuccess);
        return overallSuccess;
    } // RestoreFilesFromCurrentSet(List<TapeFileInfo>?)


    // Restore ALL files from the current set via the packer.
    private bool RestoreAllFilesFromCurrentSetInt(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        NotifySetStart(fileNotify, TOC.CurrentSetTOC.Count);

        if (!BeginReadContentForCurrentSet(fileNotify))
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

        NotifySetEnd(fileNotify);

        m_logger.LogTrace("RestoreAllFilesFromCurrentSetInt exiting: overallSuccess={Success}", overallSuccess);
        return overallSuccess;
    } // RestoreAllFilesFromCurrentSet()


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
    /// <summary>Whether a multi-volume continuation context is pending (earlier volume needed).</summary>
    public bool CanResumeFromAnotherVolume => MultiVolumeContext is not null;
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

        return RestoreFilesFromCurrentSetDownInt(null, MultiVolumeContext.Value.ignoreFailures, MultiVolumeContext.Value.fileNotify)
            ? TapeResult.OK : FailedOperationResult;
    } // ResumeRestoreOnAnotherVolume()

    private bool RestoreFilesFromCurrentSetDownInt(List<TapeFileInfo>?[]? filesSelected, bool ignoreFailures, ITapeFileNotifiable? fileNotify)
    {
        m_logger.LogTrace("Starting restoring files from current set #{Set} down", TOC.CurrentSetIndex);

        Debug.Assert(CanResumeFromAnotherVolume || filesSelected != null); // either resuming or restoring from a specified list

        TapeRestoreContext rc = CanResumeFromAnotherVolume ? MultiVolumeContext!.Value :
            new(filesSelected!, TOC.CurrentSetIndex, ignoreFailures, fileNotify);

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

            bool result = rc.filesSelected[rc.filesSelectedIdx] != null
                ? RestoreFilesFromCurrentSet(rc.filesSelected[rc.filesSelectedIdx], ignoreFailures, fileNotify)
                : RestoreAllFilesFromCurrentSetInt(ignoreFailures, fileNotify); // null means restore all files
            if (!result)
            {
                m_logger.LogWarning("{Method}: Inner restore returned false: filesSelectedIdx={Idx}, set #{Set}",
                    nameof(RestoreFilesFromCurrentSetDownInt), rc.filesSelectedIdx, TOC.CurrentSetIndex);

                rc.overallSuccess = false;
                // Latch only a REAL fault.
                //  A media-full-please-swap isn't a failure, nor is a user-requested abort.
                //  Latching is indempotent, hence it won't hurt if the original error has already been latched.
                if (!CanResumeFromAnotherVolume && WentBad)
                    LatchFailure(); // latch on the ORIGINAL error if one occured

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
    public TapeResult RestoreFilesFromCurrentSetDown(List<TapeFileInfo>?[] filesSelected, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Starting restoring (packed) pre-selected files from current set #{Set} down", TOC.CurrentSetIndex);

        _stats.Reset();
        _setAnomalies.Clear();
        ResetLatchedFailure();
        return RestoreFilesFromCurrentSetDownInt(filesSelected, ignoreFailures, fileNotify)
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
