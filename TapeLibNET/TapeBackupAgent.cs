using System.Diagnostics;
using Windows.Win32.Foundation;
using Microsoft.Extensions.Logging;
using Windows.Win32.System.SystemServices;


namespace TapeNET
{
    public class TapeFileBackupAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileAgent(drive, legacyTOC)
    {
        private TapeWriteStream? OpenWriteContentStream(long length)
        {
            return Manager.ProduceWriteContentStream(length);
        }
        private bool BeginWriteContentForCurrentSet(bool newSet)
        {
            // Try to set filemark mode -- the manager has the final say.
            Navigator.FmksMode = TOC.CurrentSetTOC.FmksMode;
            if (TOC.CurrentSetTOC.FmksMode != Navigator.FmksMode)
                m_logger.LogWarning("Failed to set filemark mode to {Mode0} in {Method}; proceeding with {Mode1}",
                    TOC.CurrentSetTOC.FmksMode, nameof(BeginWriteContentForCurrentSet), Navigator.FmksMode);
            TOC.CurrentSetTOC.FmksMode = Navigator.FmksMode; // in any case, ensure the set has what the manager has

            // Try to set block size -- the manager has the final say.
            if (!Drive.SetBlockSize(TOC.CurrentSetTOC.BlockSize))
                m_logger.LogWarning("Failed to set block size to {Size0} in {Method}; proceeding with {Size1}",
                    TOC.CurrentSetTOC.BlockSize, nameof(BeginWriteContentForCurrentSet), Drive.BlockSize);
            TOC.CurrentSetTOC.BlockSize = Drive.BlockSize; // in any case, ensure the set has what the manager has

            Navigator.TargetContentSet = newSet ? ((TOC.CurrentSetIndexOnVolume > 0) ? -1 : 0) : CurrentSetAsNavigatorContentSet;

            return Manager.BeginWriteContent();
        }


        public bool BackupFile(FileInfo fileInfo)
        {
            m_logger.LogTrace("Backing up file >{File}< in {Method}", fileInfo.FullName, nameof(BackupFile));

            try
            {
                using var wstream = OpenWriteContentStream(fileInfo.Length);
                if (wstream == null)
                {
                    m_logger.LogWarning("Failed to open content write stream in {Method}", nameof(BackupFile));
                    return false;
                }

                TapeFileInfo tfi = new(TOC.GenerateUID(), Drive.BlockCounter, fileInfo);
                var hasher = CreateHasher(TOC.CurrentSetTOC.HashAlgorithm);

                TapeSerializer ts = new(wstream);
                tfi.SerializeHeaderTo(ts);
                    // needn't include header serialization in CRC hashing, since it's validated via DeserializeAndCheckHeaderFrom()

                if (hasher == null)
                {
                    using var srcFileStream = fileInfo.OpenRead();
                    srcFileStream.CopyTo(wstream);
                }
                else
                {
                    // Note we apply hasher to the file stream since we need to keep tape stream around
                    var srcFileStream = fileInfo.OpenRead();
                    using var hashingStream = new HashingStream(srcFileStream, hasher, disposeInnerToo: true);
                    hashingStream.CopyTo(wstream);
                }

                // now the hasher has the hash ready
                if (hasher != null)
                    tfi.Hash = hasher.GetCurrentHash();

                // only if all went well, add the TOC entry
                TOC.CurrentSetTOC.Append(tfi);
                BytesBackedup += wstream.Length;
            }
            catch (Exception ex)
            {
                m_logger.LogWarning("Exception {Exception} in {Method}", ex, nameof(BackupFile));
                return false;
            }

            // success
            m_logger.LogTrace("File >{File}< backed up ok", fileInfo.FullName);

            return true;
        } // BackupFile()

        private List<string> BuildFileNameList(List<string> fileAndDirectoryPatterns, bool recursive)
        {
            List<string> fileNames = [];

            foreach (var pattern in fileAndDirectoryPatterns)
            {
                try
                {
                    if (pattern.Contains('*') || pattern.Contains('?'))
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
                            var filesInDirectory = Directory.EnumerateFiles(directoryPath, fileNameWithWildcards, searchOption);
                            fileNames.AddRange(filesInDirectory);
                        }
                        // else non-existing directory -> ignore
                    }
                    else if (pattern.TrimEnd().EndsWith('\\') || Directory.Exists(pattern))
                    {
                        // If it's a directory, add files (recursively if requested)
                        var directoryPath = Path.GetFullPath(pattern);
                        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        var filesInDirectory = Directory.EnumerateFiles(directoryPath, "*", searchOption);
                        fileNames.AddRange(filesInDirectory);
                    }
                    else
                    {
                        // Otherwise, assume it's a file name
                        fileNames.Add(Path.GetFullPath(pattern));
                    }
                }
                catch (Exception ex)
                {
                    m_logger.LogWarning("Exception {Exception} while building file name list for >{Pattern}<", ex, pattern);
                }
            } // foreach pattern

            return fileNames;
        }

        // The context with which we can resume backup on the next volume
        private struct TapeBackupContext(List<string> fileList, bool recurseSubdirs, bool ignoreFailures, ITapeFileNotifiable? fileNotify)
        {
            internal readonly List<string> fileList = fileList;
            internal readonly bool recurseSubdirs = recurseSubdirs;
            internal readonly bool ignoreFailures = ignoreFailures;
            internal readonly ITapeFileNotifiable? fileNotify = fileNotify;

            internal int fileIndex = 0;
            internal bool overallSuccess = true;
            internal int filesProcessed = 0;
            internal int filesFailed = 0;
            internal long bytesProcessed = 0L;
        }
        private TapeBackupContext? MultiVolumeContext { get; set; } = null;
        public bool CanResumeToNextVolume => MultiVolumeContext != null;

        public bool ResumeBackupToNextVolume()
        {
            if (!CanResumeToNextVolume)
                return false;

            m_logger.LogTrace("Resuming multi-volume backup for volume #{Volume}", TOC.Volume + 1);

            // since we're on the new media volume, renew Navigator
            if (!Manager.RenewNavigator())
            {
                LogErrorAsDebug("Failed to renew Navigator");
                return false;
            }

            Debug.Assert(MultiVolumeContext != null);
            Debug.Assert(TOC.Count > 0); // we must have at least one set from previous volume

            TOC.Volume++;
            TOC.ContinuedOnNextVolume = false;

            // create the new set TOC with the same settings as the last set of the previous volume
            TOC.CloneCurrentSetTOC(contFromPrevVolume: true);
            TOC.CurrentSetTOC.Description += $" ({TOC.Volume})";

            return BackupFilesToCurrentSet(newSet: true);
        }

        private bool BackupFilesToCurrentSet(bool newSet = true)
        {
            Debug.Assert(MultiVolumeContext != null);

            TapeBackupContext bc = MultiVolumeContext.Value;

            if (bc.fileList.Count == 0)
            {
                m_logger.LogWarning("No files found to backup in {Method}", nameof(BackupFilesToCurrentSet));
                return true; // no files found to back up -> treat as success
            }

            if (!BeginWriteContentForCurrentSet(newSet)) // start conent writing mode in tape manager so that tape positioning works correctly
            {
                NotifyBatchEndStatistics(bc.fileNotify, 0, bc.fileList.Count, 0);
                m_logger.LogWarning("Failed to begin writing content in {Method}", nameof(BackupFilesToCurrentSet));
                return false;
            }

            m_logger.LogTrace("Continuing (multi-volume) backup from file #{Number} >{File}<",
                bc.fileIndex + 1, bc.fileList[bc.fileIndex]);

            long bytesBackedupMarker = BytesBackedup;

            // The main loop thru the file list
            for (; bc.fileIndex < bc.fileList.Count; bc.fileIndex++) // use for int instead if foreach to know the index for multi-volume backup
            {
                var fileName = bc.fileList[bc.fileIndex]; // use an additional variable since we might modify file name
                bc.filesProcessed++; // filesProcessed is the global multi-volume index (1-based); fileIndex is the local index (0-based)

                FileInfo fileInfo = new(fileName);
                TapeFileDescriptor fileDescr = new (fileInfo); // the constructor will check if the file exists

                try
                {
                    if (bc.fileNotify != null)
                    {
                        if (!NotifyPreProcessFile(bc.fileNotify, ref fileDescr))
                        {
                            NotifyFileSkipped(bc.fileNotify, fileDescr);
                            m_logger.LogTrace("File #{Number} >{File}< skipped per pre-processor request", bc.filesProcessed, fileName);
                            continue; // not a failure, yet post-processing not called
                        }
                        // the pre-processor may have modified the file name => re-create fileInfo
                        fileName = fileDescr.FullName;
                        fileInfo = fileDescr.CreateFileInfo();
                    }

                    if (!fileInfo.Exists)
                        throw new FileNotFoundException($"File #{bc.filesProcessed} not found", fileName);

                    fileDescr.FillFrom(fileInfo); // fill in the rest of the file descriptor from the file info, now that we know it exists

                    m_logger.LogTrace("Backing up file #{Number} >{File}< of length {Length}",
                        bc.filesProcessed, fileName, Helpers.BytesToString(fileInfo.Length));

                    if (TOC.CurrentSetTOC.Incremental && TOC.IsFileUptodateInc(fileInfo))
                    {
                        NotifyFileSkipped(bc.fileNotify, fileDescr);
                        m_logger.LogTrace("File #{Number} >{File}< found up-to-date in an incremental set -> skipping", bc.filesProcessed, fileName);
                        continue; // not a failure, yet post-processing not called
                    }

                    if (!BackupFile(fileInfo))
                        throw new IOException($"File #{bc.filesProcessed} backup failed. Error: 0x{LastError:X8} >{LastErrorMessage}<", (int)LastError);

                    // success
                    NotifyPostProcessFile(bc.fileNotify, ref fileDescr);

                    m_logger.LogTrace("File #{Number} >{File}< backed up ok", bc.filesProcessed, fileName);
                }
                catch (Exception ex)
                {
                    SetError(ex);

                    m_logger.LogWarning("{Method}: File #{Number} >{File}< backup failed. Exception: {Ex}",
                        nameof(BackupFilesToCurrentSet), bc.filesProcessed, fileName, ex);

                    if (LastError == (uint)WIN32_ERROR.ERROR_END_OF_MEDIA || ex is IOException ioex && ioex.HResult == (int)WIN32_ERROR.ERROR_END_OF_MEDIA)
                    {
                        // Set up continuation on the next volume for multi-volume backup
                        m_logger.LogTrace("Setting up multi-volume backup from file #{Number} >{File}<", bc.filesProcessed, bc.fileList[bc.fileIndex]);
                        bc.bytesProcessed += BytesBackedup - bytesBackedupMarker; // update statistics

                        // do not count the failed file in multi-volumen context -- as it will be re-tried on next volume
                        bc.filesProcessed--;
                        MultiVolumeContext = bc;

                        // however for the current batch statistics do count and report the file as failed
                        bc.filesProcessed++;
                        bc.filesFailed++;

                        NotifyFileFailed(bc.fileNotify, fileDescr, ex);
                        NotifyBatchEndStatistics(bc.fileNotify, bc.filesProcessed, bc.filesFailed, bc.bytesProcessed);

                        TOC.ContinuedOnNextVolume = true;
                        Debug.Assert(CanResumeToNextVolume); // we're ready to continue with multi-volume backup
                        return false;
                    }

                    bc.overallSuccess = false;
                    bc.filesFailed++;

                    NotifyFileFailed(bc.fileNotify, fileDescr, ex);

                    if (!bc.ignoreFailures)
                        break;
                } // catch

            } // foreach fileName

            bc.bytesProcessed += BytesBackedup - bytesBackedupMarker; // update statistics
            NotifyBatchEndStatistics(bc.fileNotify, bc.filesProcessed, bc.filesFailed, bc.bytesProcessed);

            MultiVolumeContext = null; // clear multi-volume context -- if we got here we're done with [multi-volume] backup

            return bc.overallSuccess;
        } // BackupFilesToCurrentSet()

        public bool BackupFilesToCurrentSet(bool newSet, List<string> fileAndDirectoryPatterns, bool recurseSubdirs, bool ignoreFailures = true,
            ITapeFileNotifiable? fileNotify = null)
        {
            var fileList = BuildFileNameList(fileAndDirectoryPatterns, recurseSubdirs);

            if (fileList.Count == 0)
            {
                m_logger.LogWarning("No files found to backup in {Method}", nameof(BackupFilesToCurrentSet));
                return true; // no files found to back up -> treat as success
            }

            NotifyBatchStartStatistics(fileNotify, fileList.Count);

            m_logger.LogTrace("Starting backing up {Count} files to current set #{Set}", fileList.Count, TOC.CurrentSetIndex);
            if (TOC.CurrentSetTOC.Incremental)
                m_logger.LogTrace("Performing incremental backup to incremental set #{Set}", TOC.CurrentSetIndex);

            MultiVolumeContext = new(fileList, recurseSubdirs, ignoreFailures, fileNotify);

            return BackupFilesToCurrentSet(newSet);
        } // BackupFilesToCurrentSet()

#if OLDCODE
        public bool BackupFilesToCurrentSetOLD(List<string> fileAndDirectoryPatterns, bool recurseSubdirs, bool ignoreFailures = true,
            ITapeFileNotifiable? fileNotify = null)
        {
            var fileList = CanResumeToNextVolume ? MultiVolumeContext!.Value.fileList : BuildFileNameList(fileAndDirectoryPatterns, recurseSubdirs);
            
            if (fileList.Count == 0)
            {
                m_logger.LogWarning("No files found to backup in {Method}", nameof(BackupFilesToCurrentSet));
                return true; // no files found to back up -> treat as success
            }

            if (!CanResumeToNextVolume)
                fileNotify?.BatchStartStatistics(m_toc.CurrentSetIndex, fileList.Count);

            if (!BeginWriteContentForCurrentSet()) // start conent writing mode in tape manager so that tape positioning works correctly
            {
                fileNotify?.BatchEndStatistics(m_toc.CurrentSetIndex, 0, fileList.Count, 0);
                m_logger.LogWarning("Failed to begin writing content in {Method}", nameof(BackupFilesToCurrentSet));
                return false;
            }

            if (!CanResumeToNextVolume)
            {
                m_logger.LogTrace("Starting backing up {Count} files to current set #{Set}", fileList.Count, m_toc.CurrentSetIndex);
                if (m_toc.CurrentSetTOC.Incremental)
                    m_logger.LogTrace("Performing incremental backup to incremental set #{Set}", m_toc.CurrentSetIndex);
            }
            else
                m_logger.LogTrace("Continuing multi-volume backup from file #{Number} >{File}<",
                    MultiVolumeContext!.Value.fileIndex + 1, fileList[MultiVolumeContext!.Value.fileIndex]);

            TapeBackupContext bc = CanResumeToNextVolume ? MultiVolumeContext!.Value :
                new(fileList, recurseSubdirs, ignoreFailures, fileNotify);
            long bytesBackedupMarker = BytesBackedup;

            // The main loop thru the file list
            for ( ; bc.fileIndex < bc.fileList.Count; bc.fileIndex++) // use for int instead if foreach to know the index for multi-volume backup
            {
                var fileName = fileList[bc.fileIndex]; // use an additional variable since we might modify file name
                bc.filesProcessed++; // filesProcessed is the global multi-volume index (1-based); fileIndex is the local index (0-based)

                FileInfo fileInfo = new(fileName);
                var fileDescr = new TapeFileDescriptor(fileName); // do NOT create from fileInfo since the file may not exist

                try
                {
                    if (fileNotify != null)
                    {
                        if (!fileNotify.PreProcessFile(ref fileDescr))
                        {
                            m_logger.LogTrace("File #{Number} >{File}< skipped per pre-processor request", bc.filesProcessed, fileName);
                            continue; // not a failure, yet post-processing not called
                        }
                        // the pre-processor may have modified the file name => re-create fileInfo
                        fileName = fileDescr.FullName;
                        fileInfo = fileDescr.CreateFileInfo();
                    }

                    if (!fileInfo.Exists)
                        throw new FileNotFoundException($"File #{bc.filesProcessed} not found", fileName);

                    fileDescr.FillFrom(fileInfo); // fill in the rest of the file descriptor from the file info, now that we knwo it exists

                    m_logger.LogTrace("Backing up file #{Number} >{File}< of length {Length}",
                        bc.filesProcessed, fileName, Helpers.BytesToString(fileInfo.Length));

                    if (m_toc.CurrentSetTOC.Incremental && m_toc.IsFileUptodateInc(fileInfo))
                    {
                        fileNotify?.OnFileSkipped(fileDescr);
                        m_logger.LogTrace("File #{Number} >{File}< found up-to-date in an incremental set -> skipping", bc.filesProcessed, fileName);
                        continue; // not a failure, yet post-processing not called
                    }

                    if (!BackupFile(fileInfo))
                        throw new IOException($"File #{bc.filesProcessed} backup failed. Error: 0x{LastError:X8} >{LastErrorMessage}<", (int)LastError);

                    // success
                    if (fileNotify != null)
                    {
                        if (fileNotify.PostProcessFile(ref fileDescr))
                            fileDescr.ApplyToFileInfo(fileInfo);
                    }

                    m_logger.LogTrace("File #{Number} >{File}< backed up ok", bc.filesProcessed, fileName);
                }
                catch (Exception ex)
                {
                    m_logger.LogWarning("{Method}: File #{Number} >{File}< backup failed. Exception: {Ex}",
                        nameof(BackupFilesToCurrentSet), bc.filesProcessed, fileName, ex);
                    
                    if (LastError == (uint)WIN32_ERROR.ERROR_END_OF_MEDIA || ex is IOException ioex && ioex.HResult == (int)WIN32_ERROR.ERROR_END_OF_MEDIA)
                    {
                        // Set up continuation on the next volume for multi-volume backup
                        m_logger.LogTrace("Setting up multi-volume backup from file #{Number} >{File}<", bc.filesProcessed, fileList[bc.fileIndex]);
                        bc.bytesProcessed += BytesBackedup - bytesBackedupMarker; // update statistics
                        
                        // do not count the failed file in multi-volumen context -- as it will be re-tried on next volume
                        bc.filesProcessed--;
                        MultiVolumeContext = bc;
                        
                        // however for the current batch statistics do count and report the file as failed
                        bc.filesProcessed++;
                        bc.filesFailed++;
                        fileNotify?.OnFileFailed(fileDescr, ex);
                        
                        // call statistics since we end this set on this volume
                        fileNotify?.BatchEndStatistics(m_toc.CurrentSetIndex, bc.filesProcessed, bc.filesFailed, bc.bytesProcessed);

                        m_toc.ContinuedOnNextVolume = true;
                        Debug.Assert(CanResumeToNextVolume); // we're ready to continue with multi-volume backup
                        return false;
                    }

                    bc.overallSuccess = false;
                    bc.filesFailed++;

                    fileNotify?.OnFileFailed(fileDescr, ex);

                    if (!ignoreFailures)
                        break;
                } // catch

            } // foreach fileName

            bc.bytesProcessed += BytesBackedup - bytesBackedupMarker; // update statistics
            fileNotify?.BatchEndStatistics(m_toc.CurrentSetIndex, bc.filesProcessed, bc.filesFailed, bc.bytesProcessed);
            MultiVolumeContext = null; // clear multi-volume context -- if we got here we're done with [multi-volume] backup

            return bc.overallSuccess;
        } // BackupFilesToCurrentSetOLD()
#endif

    } // class TapeFileBackupAgent


} // namespace TapeNET
