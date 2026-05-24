using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Hashing;
using System.Net.Http.Headers;
using System.Runtime.Intrinsics.X86;
using TapeLibNET;
using Windows.Win32.Foundation;


namespace TapeLibNET
{
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
        // A flag that the last file has been skipped since it's not reported via the return value of RestoreNextFile()
        protected bool LastFileSkipped { get; private set; } = false;

        private TapeReadStream? OpenReadContentStream()
        {
            return Manager.ProduceReadContentStream(textFileMode: false, lengthLimit: -1);
        }
        private bool BeginReadContentForCurrentSet()
        {
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

            // Transition to Content mode before setting the set parameters
            if (!Manager.BeginReadContent())
            {
                m_logger.LogWarning("Failed to transition to reading content in {Method}",
                    nameof(BeginReadContentForCurrentSet));
                SyncErrorFrom(Manager);
                return false;
            }

            // set the block size from the set to the manager
            Drive.SetBlockSize(TOC.CurrentSetTOC.BlockSize);

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

                // Important: we must get the content read stream first, since this will reset the BlockCounter
                //  when changing to content read mode -- BlockCounter can be used laster to move to tfi.Block
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
        //  reads through TapeFileReadPacker so files that share tape blocks (or
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
                //  read window. Header + body are read through the same façade in one shot.
                long totalBytes = TapeFileInfo.EstimateSerializedHeaderSize() + tfi.FileDescr.Length;
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

                if (!RestoreFileCore(fileInfo, rstream, hasher))
                {
                    throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                        $"Processing failed for file >{tfi.FileDescr.FullName}<");
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

            NotifyBatchStart(fileNotify, tfis.Count);

            if (!BeginReadContentForCurrentSet())
            {
                NotifyBatchEnd(fileNotify);
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
                    m_logger.LogTrace("Retrying (packed) file >{File}< as per file failed action", tfi.FileDescr.FullName);
                    StatsUndoFailure();
                    goto RETRY;
                }

                overallSuccess = false;

                if (ignoreFailures && fileFailedAction == FileFailedAction.Skip)
                    continue;
                else
                    break;
            }

            NotifyBatchEnd(fileNotify);

            m_logger.LogTrace("RestoreFilesFromCurrentSet(tfis) exiting: overallSuccess={Success}", overallSuccess);
            return overallSuccess;
        } // RestoreFilesFromCurrentSet(List<TapeFileInfo>?)


        // Restore ALL files from the current set via the packer.
        private bool RestoreAllFilesFromCurrentSetInt(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            NotifyBatchStart(fileNotify, TOC.CurrentSetTOC.Count);

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
                    m_logger.LogTrace("Retrying (packed) file >{File}< as per file failed action", tfi.FileDescr.FullName);
                    StatsUndoFailure();
                    goto RETRY;
                }

                overallSuccess = false;

                if (ignoreFailures && fileFailedAction == FileFailedAction.Skip)
                    continue;
                else
                    break;
            }

            NotifyBatchEnd(fileNotify);

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
            return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: false, fileFilter), ignoreFailures, fileNotify, packed: true)
                ? TapeResult.OK : TapeResult.Fail(this);
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
            return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: false, filter: null), ignoreFailures, fileNotify, packed: true)
                ? TapeResult.OK : TapeResult.Fail(this);
        }


        // Restore the files specified by 'tfis' from the current set
        //  For optimal performance (to ensure moving always forward) tfis should follow the same order as in the SetTOC
        //  If tfis were selected by iterating thru SetTOC, this recommendation is met automatically
        [Obsolete("Use the non-Aligned (Packed) version")]
        private bool RestoreFilesFromCurrentSetAligned(List<TapeFileInfo>? tfis, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            if (tfis == null) // null means restore all files
                return RestoreFilesFromCurrentSetAligned(ignoreFailures, fileNotify);

            NotifyBatchStart(fileNotify, tfis.Count);

            if (!BeginReadContentForCurrentSet()) // start conent reading mode in tape manager so that tape positioning works correctly
            {
                NotifyBatchEnd(fileNotify);
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
                    long currentBlock = Drive.BlockCounter;
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
                    m_logger.LogTrace("Retrying file >{File}< as per file failed action", tfi.FileDescr.FullName);
                    StatsUndoFailure(); // don't double-count
                    goto RETRY;
               }

                overallSuccess = false;

                if (ignoreFailures && fileFailedAction == FileFailedAction.Skip)
                    continue;
                else
                    break;
            }

            NotifyBatchEnd(fileNotify);

            return overallSuccess;
        }

        // Restore ALL files from the current set
        [Obsolete("Use the non-Aligned (Packed) version")]
        private bool RestoreFilesFromCurrentSetAligned(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            NotifyBatchStart(fileNotify, TOC.CurrentSetTOC.Count);

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
                    m_logger.LogTrace("Retrying file >{File}< as per file failed action", tfi.FileDescr.FullName);
                    // Block-based positioning handles retry: the next iteration will MoveToBlock(tfi.Block)
                    StatsUndoFailure(); // don't double-count
                    goto RETRY;
                }

                overallSuccess = false;

                if (ignoreFailures && fileFailedAction == FileFailedAction.Skip)
                    continue;
                else
                    break;
            }

            NotifyBatchEnd(fileNotify);

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
                return TapeResult.Fail(this);

            m_logger.LogTrace("Resuming multi-volume restore from new volume #{Volume}", VolumeToResumeFrom);

            Debug.Assert(MultiVolumeContext != null);
            Debug.Assert(VolumeToResumeFrom >= 0);

            // since we're on the new media volume, renew Navigator
            if (!Manager.RenewNavigator())
            {
                LogErrorAsDebug("Failed to renew Navigator");
                return TapeResult.Fail(this);
            }

            // Check if the newly provided volume is the right one by analyzing its TOC
            //  Save the current TOC as it contains more sets than the one we're restoring
            var orgTOC = new TapeTOC(TOC);
            try
            {           
                if (!RestoreTOC())
                {
                    LogErrorAsWarning("Failed to restore TOC for new volume");
                    return TapeResult.Fail(this);
                }
                // Check if the new volume has the right volume number
                if (TOC.Volume != VolumeToResumeFrom)
                {
                    LogErrorAsWarning("Volume mismatch for new volume");
                    return TapeResult.Fail(this);
                }
                // As the final test, check the size of the next backup set to restore
                int setIdx = MultiVolumeContext.Value.initialCurrSetIdx - MultiVolumeContext.Value.filesSelectedIdx;
                if (TOC[setIdx].Count != orgTOC[setIdx].Count)
                {
                    LogErrorAsWarning($"Set size mismatch on new volume for set #{setIdx}");
                    return TapeResult.Fail(this);
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
                ? TapeResult.OK : TapeResult.Fail(this);
        } // ResumeRestoreOnAnotherVolume()

        private bool RestoreFilesFromCurrentSetDownInt(List<TapeFileInfo>?[]? filesSelected, bool ignoreFailures, ITapeFileNotifiable? fileNotify, bool packed)
        {
            m_logger.LogTrace("Starting restoring files from current set #{Set} down (packed={Packed})", TOC.CurrentSetIndex, packed);

            Debug.Assert(CanResumeFromAnotherVolume || filesSelected != null); // either resuming or restoring from a specified list

            TapeRestoreContext rc = CanResumeFromAnotherVolume ? MultiVolumeContext!.Value :
                new(filesSelected!, TOC.CurrentSetIndex, ignoreFailures, fileNotify, packed);

            m_logger.LogTrace("RestoreFilesFromCurrentSetDownInt: incoming rc.overallSuccess={Success}, filesSelectedIdx={Idx}, initialCurrSetIdx={Init}, isResume={Resume}",
                rc.overallSuccess, rc.filesSelectedIdx, rc.initialCurrSetIdx, CanResumeFromAnotherVolume);

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
                    m_logger.LogWarning("Inner restore returned false: filesSelectedIdx={Idx}, set #{Set}",
                        rc.filesSelectedIdx, TOC.CurrentSetIndex);
                    rc.overallSuccess = false;
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

            m_logger.LogTrace("RestoreFilesFromCurrentSetDownInt exiting: overallSuccess={Success}, filesSelectedIdx={Idx}, MultiVolumeContext set={Pending}",
                rc.overallSuccess, rc.filesSelectedIdx, MultiVolumeContext != null);
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
            return RestoreFilesFromCurrentSetDownInt(filesSelected, ignoreFailures, fileNotify, packed: false)
                ? TapeResult.OK : TapeResult.Fail(this);
        } // RestoreFilesFromCurrentSetDownAligned(List<string>)

        /// <summary>
        /// Packed pendant of <see cref="RestoreFilesFromCurrentSetDownAligned(List{TapeFileInfo}?[], bool, ITapeFileNotifiable?)"/>.
        ///  Routes per-set content reads through the shared-block read packer.
        /// </summary>
        public TapeResult RestoreFilesFromCurrentSetDown(List<TapeFileInfo>?[] filesSelected, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting restoring (packed) pre-selected files from current set #{Set} down", TOC.CurrentSetIndex);

            _stats.Reset();
            return RestoreFilesFromCurrentSetDownInt(filesSelected, ignoreFailures, fileNotify, packed: true)
                ? TapeResult.OK : TapeResult.Fail(this);
        }


        /// <summary>Restores filtered files from the current set, resolving multi-volume continuation chains.</summary>
        [Obsolete("Use the non-Aligned (Packed) version")]
        public TapeResult RestoreFilesFromCurrentSetAligned(ITapeFileFilter? fileFilter, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting restoring files from current set #{Set}", TOC.CurrentSetIndex);

            _stats.Reset();
            return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: false, fileFilter), ignoreFailures, fileNotify, packed: false)
                ? TapeResult.OK : TapeResult.Fail(this);
        } // RestoreFilesFromCurrentSetAligned(ITapeFileFilter?)

        /// <summary>Restores filtered files from the current set and its incremental chain.</summary>
        [Obsolete("Use the non-Aligned (Packed) version")]
        public TapeResult RestoreFilesFromCurrentSetIncAligned(ITapeFileFilter? fileFilter, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting incrementally restoring files from current set #{Set}", TOC.CurrentSetIndex);

            _stats.Reset();
            return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: true, fileFilter), ignoreFailures, fileNotify, packed: false)
                ? TapeResult.OK : TapeResult.Fail(this);
        } // RestoreFilesFromCurrentSetIncAligned(ITapeFileFilter?)

        /// <summary>Restores all files from the current set (no filter).</summary>
        [Obsolete("Use the non-Aligned (Packed) version")]
        public TapeResult RestoreAllFilesFromCurrentSetAligned(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting restoring all files from current set #{Set}", TOC.CurrentSetIndex);

            _stats.Reset();
            return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: false, filter: null), ignoreFailures, fileNotify, packed: false)
                ? TapeResult.OK : TapeResult.Fail(this);
        } // RestoreAllFilesFromCurrentSetAligned()

        /// <summary>Restores all files from the current set and its incremental chain (no filter).</summary>
        [Obsolete("Use the non-Aligned (Packed) version")]
        public TapeResult RestoreAllFilesFromCurrentSetIncAligned(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting incrementally restoring all files from current set #{Set}", TOC.CurrentSetIndex);

            _stats.Reset();
            return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: true, filter: null), ignoreFailures, fileNotify, packed: false)
                ? TapeResult.OK : TapeResult.Fail(this);
        } // RestoreAllFilesFromCurrentSetIncAligned()

        /// <summary>Packed pendant of <see cref="RestoreFilesFromCurrentSetIncAligned"/>.</summary>
        public TapeResult RestoreFilesFromCurrentSetInc(ITapeFileFilter? fileFilter, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting incrementally restoring (packed) files from current set #{Set}", TOC.CurrentSetIndex);

            _stats.Reset();
            return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: true, fileFilter), ignoreFailures, fileNotify, packed: true)
                ? TapeResult.OK : TapeResult.Fail(this);
        }

        /// <summary>Packed pendant of <see cref="RestoreAllFilesFromCurrentSetIncAligned"/>.</summary>
        public TapeResult RestoreAllFilesFromCurrentSetInc(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting incrementally restoring (packed) all files from current set #{Set}", TOC.CurrentSetIndex);

            _stats.Reset();
            return RestoreFilesFromCurrentSetDownInt(TOC.SelectFiles(incremental: true, filter: null), ignoreFailures, fileNotify, packed: true)
                ? TapeResult.OK : TapeResult.Fail(this);
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
            if (hasher == null)
            {
                using var dstFileStream = fileInfo.Create();
                rstream.CopyTo(dstFileStream);
            }
            else
            {
                var dstFileStream = fileInfo.Create();
                using var hashingStream = new HashingStream(dstFileStream, hasher, ownInner: true);
                rstream.CopyTo(hashingStream);
            }

            return base.RestoreFileCore(fileInfo, rstream, hasher);
        }

        protected override bool PostProcessFileInternal(TapeFileDescriptor fileDescr, FileInfo fileInfo)
        {
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

            if (hasher == null)
            {
                using var dstFileStream = fileInfo.OpenRead();
                if (!buffered.CompareTo(dstFileStream))
                    return false;
            }
            else
            {
                using var dstFileStream = fileInfo.OpenRead();
                // Since we're checking the tape stream, attach the hasher to the buffered tape read
                using var hashingStream = new HashingStream(buffered, hasher, ownInner: false); // do NOT dispose buffered!
                if (!dstFileStream.CompareTo(hashingStream))
                    return false;
            }

            return base.RestoreFileCoreAligned(fileInfo, rstream, hasher);
        }

        protected override bool RestoreFileCore(FileInfo fileInfo, Stream rstream, NonCryptographicHashAlgorithm? hasher)
        {
            if (hasher == null)
            {
                using var dstFileStream = fileInfo.OpenRead();
                if (!rstream.CompareTo(dstFileStream))
                    return false;
            }
            else
            {
                using var dstFileStream = fileInfo.OpenRead();
                using var hashingStream = new HashingStream(rstream, hasher, ownInner: false);
                if (!dstFileStream.CompareTo(hashingStream))
                    return false;
            }

            return base.RestoreFileCore(fileInfo, rstream, hasher);
        }

    } // class TapeFileVerifyAgent


} // namespace TapeNET
