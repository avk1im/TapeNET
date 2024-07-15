using System.Diagnostics;
using System.IO.Hashing;
using Windows.Win32.Foundation;
using Microsoft.Extensions.Logging;


namespace TapeNET
{
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
            Manager.EndReadWrite();

            // set the filemarks and block size from the set to the manager
            Navigator.FmksMode = TOC.CurrentSetTOC.FmksMode;
            Drive.SetBlockSize(TOC.CurrentSetTOC.BlockSize);

            Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet;

            return Manager.BeginReadContent();
        }

        // for the decendant classes to override, to perform the actual file operation
        protected virtual bool RestoreFileCore(FileInfo fileInfo, TapeReadStream rstream, NonCryptographicHashAlgorithm? hasher)
        {
            if (rstream.IsDisposed)
                throw new IOException("May not dispose tape read stream while restoring", (int)WIN32_ERROR.ERROR_INVALID_HANDLE);

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

        private bool RestoreNextFile(TapeFileInfo tfi, ITapeFileNotifiable? fileNotify = null)
        {
            try
            {
                // Important: we must get the content read stream first, since this will reset the BlockCounter
                //  when changing to content read mode -- BlockCounter can be used laster to move to tfi.Block
                using var rstream = OpenReadContentStream();
                if (rstream == null)
                {
                    m_logger.LogWarning("Failed to open content read stream in {Method}", nameof(RestoreNextFile));
                    return false;
                }

                // check the UID and the length as the most important attributes
                var deserializer = new TapeDeserializer(rstream);
                if (!tfi.DeserializeAndCheckHeaderFrom(deserializer))
                {
                    throw new IOException($"Header mismatch for file >{tfi.FileDescr.FullName}<",
                        (int)WIN32_ERROR.ERROR_INVALID_DATA);
                }

                rstream.LengthLimit = rstream.Length + tfi.FileDescr.Length; // this will activate LengthLimitMode

                var hasher = CreateHasher(TOC.CurrentSetTOC.HashAlgorithm);

                // Now invoke the pre- call back for possible filename or path modification or skipping the file altogether
                var fileDescr = tfi.FileDescr; // we create a copy to avoid modifying the original tfi.FileDescr
                // call the internal pre-processor first to ensure it always runs
                if (!PreProcessFileInternal(ref fileDescr) || !NotifyPreProcessFile(fileNotify, ref fileDescr))
                {
                    FileSkippedInternal(tfi.FileDescr);
                    NotifyFileSkipped(fileNotify, tfi.FileDescr);

                    m_logger.LogTrace("Skipping file >{File}< per pre-processor request", tfi.FileDescr.FullName);

                    return true; // skip the file -- do not treat as failure, since this is per pre-processor request
                }
                FileInfo fileInfo = fileDescr.CreateFileInfo(); // Notice we shouldn't set fileInfo fields here since the file doesn't exist yet!

                // Now ready to do the actual restoring
                if (!RestoreFileCore(fileInfo, rstream, hasher))
                {
                    throw new IOException($"Processing failed for file >{tfi.FileDescr.FullName}<",
                        (int)WIN32_ERROR.ERROR_INVALID_DATA);
                }

                // check CRC
                if (hasher != null)
                {
                    if (tfi.Hash == null)
                        throw new IOException($"Hash missing in file info for file >{tfi.FileDescr.FullName}<",
                            (int)WIN32_ERROR.ERROR_INVALID_DATA);

                    if (tfi.Hash.SequenceEqual(hasher.GetCurrentHash()))
                    {
                        // CRC check passed
                    }
                    else
                    {
                        throw new IOException($"CRC check failed for file >{tfi.FileDescr.FullName}<. Hasher: {TOC.CurrentSetTOC.HashAlgorithm}",
                            (int)WIN32_ERROR.ERROR_CRC);
                    }
                }

                BytesRestored += rstream.Length;

                // Now invoke the post- call back for possible attribute modification
                if (NotifyPostProcessFile(fileNotify, ref fileDescr))
                    // now apply the attributes to the file -- an exception e.g. File Not Found can be thrown here
                    return PostProcessFileInternal(fileDescr, fileInfo);

                return true;
            }
            catch (Exception ex)
            {
                SetError(ex); // we've already set the right error code & message in the exception

                NotifyFileFailed(fileNotify, tfi.FileDescr, ex);

                m_logger.LogWarning("Exception {Exception} while processing file >{File}<", ex, tfi.FileDescr.FullName);

                return false;
            }
        } // RestoreNextFile()


        // Restore the files specified by 'tfis' from the current set
        //  For optimal performance (to ensure moving always forward) tfis should follow the same order as in the SetTOC
        //  If tfis were selected by iterating thru SetTOC, this recommendation is met automatically
        private bool RestoreFilesFromCurrentSet(List<TapeFileInfo>? tfis, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            if (tfis == null) // null means restore all files
                return RestoreFilesFromCurrentSet(ignoreFailures, fileNotify);

            NotifyBatchStartStatistics(fileNotify, tfis.Count);

            if (!BeginReadContentForCurrentSet()) // start conent reading mode in tape manager so that tape positioning works correctly
            {
                NotifyBatchEndStatistics(fileNotify, 0, tfis.Count, 0);
                m_logger.LogWarning("Failed to begin reading content in {Method}", nameof(RestoreFilesFromCurrentSet));
                return false;
            }
            m_logger.LogTrace("Starting restoring {Count} select files from current set #{Set}", tfis.Count, TOC.CurrentSetIndex);

            bool overallSuccess = true;
            int filesProcessed = 0;
            int filesFailed = 0;
            long bytesProcessed = BytesRestored;
            LastFileSkipped = false;

            if (Navigator.FmksMode)
            {
                // In filemarks mode, we need to use indexes for tape positioning
                var indexes = TOC.CurrentSetTOC.RefsToIndexes(tfis); // indexes are sorted so that we only move tape forward,
                int lastIndex = 0;

                foreach (int index in indexes)
                {
                    var tfi = tfis[index];
                    filesProcessed++;

                    if (tfi == null || !tfi.IsValid)
                    {
                        m_logger.LogWarning("Invalid file info in {Method}", nameof(RestoreFilesFromCurrentSet));
                        goto FAILURE;
                    }

                    m_logger.LogTrace("Restoring file #{Number} >{File}< at index {Index}", filesProcessed, tfi.FileDescr.FullName, index);

                    int moveBy = index - lastIndex;
                    if (LastFileSkipped || moveBy != 0) // optimization: even though MoveToNextFilemark() will ignore moves by 0, it still takes time
                    {
                        if (!Navigator.MoveToNextContentFilemark(moveBy))
                        {
                            m_logger.LogWarning("Failed to move to filemark for file >{File} in {Method}", tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSet));
                            goto FAILURE;
                        }
                    }
                    lastIndex = index;

                    if (!RestoreNextFile(tfi, fileNotify))
                    {
                        m_logger.LogWarning("Failed to restore file >{File} in {Method}", tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSet));
                        goto FAILURE;
                    }

                    // success
                    m_logger.LogTrace("File >{File}< restored ok", tfi.FileDescr.FullName);
                    
                    continue;

                FAILURE:
                    overallSuccess = false;
                    filesFailed++;
                    
                    if (ignoreFailures)
                        continue;
                    else
                        break;
                }
            }
            else // !FmksMode
            {
                int lastIndex = -1; // used only for tape move optimization
                bool lastFileFailed = false;

                foreach (var tfi in tfis)
                {
                    filesProcessed++;

                    if (tfi == null || !tfi.IsValid)
                    {
                        m_logger.LogWarning("Invalid file info in {Method}", nameof(RestoreFilesFromCurrentSet));
                        goto FAILURE;
                    }

                    m_logger.LogTrace("Restoring file #{Number} >{File}< at block {Block}", filesProcessed, tfi.FileDescr.FullName, tfi.Block);
                    
                    // Optimization: determine if we're at the next tfi so that we can skip moving the tape
                    int index = (lastIndex >= 0)? TOC.CurrentSetTOC.IndexOf(tfi, lastIndex) : TOC.CurrentSetTOC.IndexOf(tfi);

                    // Do move if the previous file has been skipped, failed, or is not the next one
                    if (LastFileSkipped || lastFileFailed || index < 0 || lastIndex < 0 || index != lastIndex + 1) 
                    {
                        if (!Drive.MoveToBlock(tfi.Block))
                        {
                            m_logger.LogWarning("Failed to move to block {Block} for file >{File} in {Method}",
                                tfi.Block, tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSet));
                            goto FAILURE;
                        }
                    }
                    if (index >= 0)
                        lastIndex = index;

                    if (!RestoreNextFile(tfi, fileNotify))
                    {
                        m_logger.LogWarning("Failed to restore file >{File} in {Method}", tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSet));
                        goto FAILURE;
                    }
 
                    // success
                    lastFileFailed = false;

                    m_logger.LogTrace("File >{File}< restored ok", tfi.FileDescr.FullName);
                    
                    continue;

                FAILURE:
                    overallSuccess = false;
                    filesFailed++;
                    lastFileFailed = true;

                    if (ignoreFailures)
                        continue;
                    else
                        break;
                }
            }

            bytesProcessed = BytesRestored - bytesProcessed;
            NotifyBatchEndStatistics(fileNotify, filesProcessed, filesFailed, bytesProcessed);

            return overallSuccess;
        } // RestoreFilesFromCurrentSet(List<TapeFileInfo>)

        // Restore all files from the current set
        private bool RestoreFilesFromCurrentSet(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            NotifyBatchStartStatistics(fileNotify, TOC.CurrentSetTOC.Count);

            if (!BeginReadContentForCurrentSet())
            {
                m_logger.LogWarning("Failed to begin reading content in {Method}", nameof(RestoreFilesFromCurrentSet));
                return false;
            }
            m_logger.LogTrace("Starting restoring all files from current set #{Set}", TOC.CurrentSetIndex);

            bool overallSuccess = true;
            int filesProcessed = 0;
            int filesFailed = 0;
            long bytesProcessed = BytesRestored;
            bool lastFileFailed = false;
            LastFileSkipped = false;

            foreach (var tfi in TOC.CurrentSetTOC)
            {
                filesProcessed++;

                if (tfi == null || !tfi.IsValid)
                {
                    m_logger.LogWarning("Invalid file info in {Method}", nameof(RestoreFilesFromCurrentSet));
                    goto FAILURE;
                }

                m_logger.LogTrace("Restoring file #{Number} >{File}<", filesProcessed, tfi.FileDescr.FullName);

                // in non-Fmks mode, we might need to move tape if last file was skipped or failed
                if (!Navigator.FmksMode && (LastFileSkipped || lastFileFailed))
                {
                    if (!Drive.MoveToBlock(tfi.Block))
                    {
                        m_logger.LogWarning("Failed to move to block {Block} for file >{File} in {Method}",
                            tfi.Block, tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSet));
                        goto FAILURE;
                    }
                }

                if (!RestoreNextFile(tfi, fileNotify))
                {
                    m_logger.LogWarning("Failed to restore file >{File} in {Method}", tfi.FileDescr.FullName, nameof(RestoreFilesFromCurrentSet));
                    goto FAILURE;
                }

                // success
                m_logger.LogTrace("File >{File}< restored ok", tfi.FileDescr.FullName);
                continue;

            FAILURE:
                overallSuccess = false;
                filesFailed++;
                lastFileFailed = true;

                if (ignoreFailures)
                    continue;
                else
                    break;
            }

            bytesProcessed = BytesRestored - bytesProcessed;
            NotifyBatchEndStatistics(fileNotify, filesProcessed, filesFailed, bytesProcessed);

            return overallSuccess;
        } // RestoreFilesFromCurrentSet()


        // The context with which we can resume restore on the previous volume
        private struct TapeRestoreContext(List<TapeFileInfo>[] filesSelected, int currSetIdx, bool ignoreFailures, ITapeFileNotifiable? fileNotify)
        {
            internal List<TapeFileInfo>[] filesSelected = filesSelected;
            internal readonly bool ignoreFailures = ignoreFailures;
            internal readonly ITapeFileNotifiable? fileNotify = fileNotify;

            internal int initialCurrSetIdx = currSetIdx;
            internal int filesSelectedIdx = filesSelected.Length - 1; // we'll be counting down

            internal bool overallSuccess = true;
            //internal int filesProcessed = 0;
            //internal int filesFailed = 0;
            //internal long bytesProcessed = 0L;
        }
        private TapeRestoreContext? MultiVolumeContext { get; set; } = null;
        public bool CanResumeFromAnotherVolume => MultiVolumeContext != null;
        public int VolumeToResumeFrom { get; private set; } = -1; // only valid if CanResumeFromAnotherVolume

        public bool ResumeRestoreFromAnotherVolume()
        {
            if (!CanResumeFromAnotherVolume)
                return false;

            m_logger.LogTrace("Resuming multi-volume restore from new volume #{Volume}", VolumeToResumeFrom);

            Debug.Assert(MultiVolumeContext != null);
            Debug.Assert(VolumeToResumeFrom >= 0);

            // since we're on the new media volume, renew Navigator
            if (!Manager.RenewNavigator())
            {
                LogErrorAsDebug("Failed to renew Navigator");
                return false;
            }

            // Check if the newly provided volume is the right one by analyzing its TOC
            //  Save the current TOC as it contains more sets than the one we're restoring
            var orgTOC = new TapeTOC(TOC);
            try
            {           
                if (!RestoreTOC())
                {
                    LogErrorAsWarning("Failed to restore TOC for new volume");
                    return false;
                }
                // Check if the new volume has the right volume number
                if (TOC.Volume != VolumeToResumeFrom)
                {
                    LogErrorAsWarning("Volume mismatch for new volume");
                    return false;
                }
                // As the final test, check the size of the next backup set to restore
                int setIdx = MultiVolumeContext.Value.initialCurrSetIdx - MultiVolumeContext.Value.filesSelectedIdx;
                if (TOC[setIdx].Count != orgTOC[setIdx].Count)
                {
                    LogErrorAsWarning($"Set size mismatch on new volume for set #{setIdx}");
                    return false;
                }
            }
            finally
            {
                TOC.CopyFrom(orgTOC); // restore the original TOC
            }
            // update the volme
            TOC.Volume = VolumeToResumeFrom;
            // Ok to proceed with the new volume. Notice: Keep our current TOC, since we're restoring the whole file series using it

            return RestoreFilesFromCurrentSetDown(null, MultiVolumeContext.Value.ignoreFailures, MultiVolumeContext.Value.fileNotify);
        } // ResumeRestoreOnAnotherVolume()

        private bool RestoreFilesFromCurrentSetDown(List<TapeFileInfo>?[]? filesSelected, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting restoring files from current set #{Set} down", TOC.CurrentSetIndex);

            Debug.Assert(CanResumeFromAnotherVolume || filesSelected != null); // either resuming or restoring from a specified list

            TapeRestoreContext rc = CanResumeFromAnotherVolume ? MultiVolumeContext!.Value :
                new(filesSelected!, TOC.CurrentSetIndex, ignoreFailures, fileNotify);

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
                    return false;
                }

                bool result = (rc.filesSelected[rc.filesSelectedIdx] != null) ?
                    RestoreFilesFromCurrentSet(rc.filesSelected[rc.filesSelectedIdx], ignoreFailures, fileNotify) :
                    RestoreFilesFromCurrentSet(ignoreFailures, fileNotify); // null means restore all files
                if (!result)
                {
                    rc.overallSuccess = false;
                    if (!ignoreFailures)
                        break;
                }
            }

            if (rc.filesSelectedIdx < 0)
            {
                MultiVolumeContext = null; // we're done with [multi-volume] restore -> clear multi-volume context
                VolumeToResumeFrom = -1;
            }

            TOC.CurrentSetIndex = rc.initialCurrSetIdx; // restore the initial current set index

            return rc.overallSuccess;
        } // RestoreFilesFromCurrentSetDown(List<string>)


        // Considers multi-volume case
        public bool RestoreFilesFromCurrentSet(List<string> filePatterns, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting restoring files from current set #{Set}", TOC.CurrentSetIndex);

            return RestoreFilesFromCurrentSetDown(TOC.SelectFiles(incremental: false, filePatterns), ignoreFailures, fileNotify);
        } // RestoreFilesFromCurrentSet(List<string>)

        // Considers incremental backup sets, starting from the current set. Can resume from / continue onto another volume.
        public bool RestoreFilesFromCurrentSetInc(List<string>? filePatterns, bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting incrementally restoring files from current set #{Set}", TOC.CurrentSetIndex);

            return RestoreFilesFromCurrentSetDown(TOC.SelectFiles(incremental: true, filePatterns), ignoreFailures, fileNotify);
        } // RestoreFilesFromCurrentSetInc(List<string>)

        public bool RestoreAllFilesFromCurrentSet(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting restoring all files from current set #{Set}", TOC.CurrentSetIndex);

            return RestoreFilesFromCurrentSetDown(TOC.SelectFiles(incremental: false, filePatterns: null), ignoreFailures, fileNotify);
        } // RestoreAllFilesFromCurrentSet()

        // Considers incremental backup sets, starting from the current set
        public bool RestoreAllFilesFromCurrentSetInc(bool ignoreFailures = true, ITapeFileNotifiable? fileNotify = null)
        {
            m_logger.LogTrace("Starting incrementally restoring all files from current set #{Set}", TOC.CurrentSetIndex);

            return RestoreFilesFromCurrentSetDown(TOC.SelectFiles(incremental: true, filePatterns: null), ignoreFailures, fileNotify);
        } // RestoreAllFilesFromCurrentSetInc()

    } // class TapeFileRestoreBaseAgent


    public class TapeFileRestoreAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileRestoreBaseAgent(drive, legacyTOC)
    {
        protected override bool RestoreFileCore(FileInfo fileInfo, TapeReadStream rstream, NonCryptographicHashAlgorithm? hasher)
        {
            if (hasher == null)
            {
                using var dstFileStream = fileInfo.Create(); // fileInfo.Open(FileMode.OpenOrCreate, FileAccess.Write);
                rstream.CopyTo(dstFileStream);
            }
            else
            {
                var dstFileStream = fileInfo.Create(); // fileInfo.Open(FileMode.OpenOrCreate, FileAccess.Write);
                // Notice we can attach hasher to either rstream or dstFileStream -- we go for dstFileStream since it may get disposed
                using var hashingStream = new HashingStream(dstFileStream, hasher, disposeInnerToo: true); // will dispose dstFileStream
                rstream.CopyTo(hashingStream);
            }

            return base.RestoreFileCore(fileInfo, rstream, hasher);
        }

        protected override bool PostProcessFileInternal(TapeFileDescriptor fileDescr, FileInfo fileInfo)
        {
            return fileDescr.ApplyToFileInfo(fileInfo);
        }

    } // class TapeFileRestoreAgent

    public class TapeFileValidateAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileRestoreBaseAgent(drive, legacyTOC)
    {
        protected override bool RestoreFileCore(FileInfo fileInfo, TapeReadStream rstream, NonCryptographicHashAlgorithm? hasher)
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
                using var hashingStream = new HashingStream(dstFileStream, hasher, disposeInnerToo: true); // will dispose dstFileStream
                rstream.CopyTo(hashingStream);
            }

            return base.RestoreFileCore(fileInfo, rstream, hasher);
        }

    } // class TapeFileValidateAgent

    public class TapeFileVerifyAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileRestoreBaseAgent(drive, legacyTOC)
    {
        protected override bool RestoreFileCore(FileInfo fileInfo, TapeReadStream rstream, NonCryptographicHashAlgorithm? hasher)
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
                // Since we're checking the tape stream (rstream), we should attach the hasher to it
                using var hashingStream = new HashingStream(rstream, hasher, disposeInnerToo: false); // do NOT dispose rstream!
                if (!dstFileStream.CompareTo(hashingStream))
                    return false;
            }

            return base.RestoreFileCore(fileInfo, rstream, hasher);
        }

    } // class TapeFileVerifyAgent


} // namespace TapeNET
