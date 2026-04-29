using System.Diagnostics;
using Windows.Win32.Foundation;
using Microsoft.Extensions.Logging;
using Windows.Win32.System.SystemServices;
using TapeLibNET;


namespace TapeLibNET
{
    /// <summary>
    /// Backup agent — writes file lists to tape content sets with per-file CRC hashing,
    ///  incremental detection, and automatic multi-volume continuation on end-of-media.
    /// </summary>
    public class TapeFileBackupAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileAgent(drive, legacyTOC)
    {
        // bytes backed up so far before we start writing a new set
        private long BytesBackedupMarker { get; set; } = 0L;
        // payload bytes backed up so far in the current set (since the last marker)
        private long BytesBackedupInCurrentSet => BytesBackedup - BytesBackedupMarker;

        private bool BeginWriteContentForCurrentSet(bool newSet)
        {
            // If we were reading or writing, end it first - before setting the new set's parameters
            if (!Manager.EndReadWrite())
            {
                m_logger.LogWarning("Failed to end read/write in {Method}",
                    nameof(BeginWriteContentForCurrentSet));
                SyncErrorFrom(Manager);
                return false;
            }

            // Optimization: set the target content set BEFORE transition to Content reading
            //  so that Navigator can optimize moving to the target content set once we call BeginWriteContent()
            Navigator.TargetContentSet = newSet ? ((TOC.CurrentSetIndexOnVolume > 0) ? -1 : 0) : CurrentSetAsNavigatorContentSet;

            var remainingCapacity = Drive.ContentCapacity - TOC.ComputeTotalFileSizeOnTape();
            if (!Drive.HasInitiatorPartition)
                remainingCapacity -= Navigator.TOCCapacity; // if TOC is in a content set, reserve space for it

            BytesBackedupMarker = BytesBackedup; // important in case of multi-volume backup continuation

            if (!Manager.BeginWriteContent(remainingCapacity))
            {
                m_logger.LogWarning("Failed to transition to writing content in {Method}",
                    nameof(BeginWriteContentForCurrentSet));
                SyncErrorFrom(Manager);
                return false;
            }

            // Try to set filemark mode -- Navigator has the final say.
            Navigator.FmksMode = TOC.CurrentSetTOC.FmksMode;
            if (TOC.CurrentSetTOC.FmksMode != Navigator.FmksMode)
                m_logger.LogWarning("Failed to set filemark mode to {Mode0} in {Method}; proceeding with {Mode1}",
                    TOC.CurrentSetTOC.FmksMode, nameof(BeginWriteContentForCurrentSet), Navigator.FmksMode);
            TOC.CurrentSetTOC.FmksMode = Navigator.FmksMode; // in any case, ensure the set has what the manager has

            // Try to set block size -- Drive has the final say.
            if (!Drive.SetBlockSize(TOC.CurrentSetTOC.BlockSize))
                m_logger.LogWarning("Failed to set block size to {Size0} in {Method}; proceeding with {Size1}",
                    TOC.CurrentSetTOC.BlockSize, nameof(BeginWriteContentForCurrentSet), Drive.BlockSize);
            TOC.CurrentSetTOC.BlockSize = Drive.BlockSize; // in any case, ensure the set has what the manager has

            return true;
        }
        private TapeWriteStream? OpenWriteContentStream(long length)
        {
            // Estimate actual tape footprint via the shared block-alignment formula.
            //  Use TOC.CurrentSetTOC.ComputeTotalFileSizeOnTape() as the single source of truth
            //  for bytes already consumed in this set (accounts for per-file block padding).
            long estimatedTapeSize = (length >= 0)
                ? TapeSetTOC.EstimateFileSizeOnTape(length, Drive.BlockSize)
                : length;

            var stream = Manager.ProduceWriteContentStream(estimatedTapeSize, TOC.CurrentSetTOC.ComputeTotalFileSizeOnTape());
            if (stream == null)
                SyncErrorFrom(Manager);
            return stream;
        }

        // Writes the file data to tape and sets the hash on tfi.
        //  Does not modify the TOC — the caller appends tfi on success.
        //  Throws if any failure — for the caller to catch.
        private void BackupFile(TapeFileInfo tfi)
        {
            m_logger.LogTrace("Backing up file >{File}< in {Method}", tfi.FileDescr.FullName, nameof(BackupFile));

            using var wstream = OpenWriteContentStream(tfi.FileDescr.Length) ??
                throw new TapeIOException(this, this, $"failed to open content write stream for >{tfi.FileDescr.FullName}<");

            try
            {
                var hasher = CreateHasher(TOC.CurrentSetTOC.HashAlgorithm);

                TapeSerializer ts = new(wstream);
                tfi.SerializeHeaderTo(ts);
                // needn't include header serialization in CRC hashing, since it's validated via DeserializeAndCheckHeaderFrom()

#if DEBUG
                // Simulate failures for testing error handling
                if (SimulateFileFailures.ShouldFailNow())
                {
                    m_logger.LogWarning("SIMULATED failure for file #{Counter} >{File}<",
                        SimulateFileFailures.Counter, tfi.FileDescr.FullName);
                    throw new TapeIOException((uint)WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION,
                        $"Simulated backup failure for testing (file #{SimulateFileFailures.Counter})");
                }
#endif

                // Double-buffer file data to overlap file reads with tape writes
                var fileInfo = tfi.FileDescr.CreateFileInfo();
                using (var buffered = new BufferedTapeWriteStream(wstream, Drive.BlockSize))
                {
                    if (hasher == null)
                    {
                        using var srcFileStream = fileInfo.OpenRead();
                        srcFileStream.CopyTo(buffered);
                    }
                    else
                    {
                        // Note we apply hasher to the file stream since we need to keep tape stream around
                        var srcFileStream = fileInfo.OpenRead();
                        using var hashingStream = new HashingStream(srcFileStream, hasher, ownInner: true);
                        hashingStream.CopyTo(buffered);
                    }
                } // buffered flushed here, before wstream.Length is read below

                // now the hasher has the hash ready
                if (hasher != null)
                    tfi.Hash = hasher.GetCurrentHash();

                BytesBackedup += wstream.Length;

                m_logger.LogTrace("File >{File}< backed up ok", tfi.FileDescr.FullName);
            }
            catch
            {
                // Mark the write as failed so that TapeStreamManager skips writing the trailing filemark
                //  — allows the tape to be repositioned to the start of this file for retry or next file
                wstream.WriteFailed = true;
                throw;
            }

        } // BackupFile()


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
            internal bool prevVolumeHasFiles = false; // true when the set on the previous volume had files written
        }
        private TapeBackupContext? MultiVolumeContext { get; set; } = null;
        /// <summary>Whether a multi-volume continuation context is available (end-of-media was hit during backup).</summary>
        public bool CanResumeToNextVolume => MultiVolumeContext != null;

        /// <summary>
        /// Continues the backup onto a new volume after the caller has loaded fresh media.
        /// <para>Renews the navigator, increments the volume number, clones the current set TOC,
        ///  and resumes from the file that triggered end-of-media.</para>
        /// </summary>
        public TapeResult ResumeBackupToNextVolume()
        {
            if (!CanResumeToNextVolume)
                return TapeResult.Fail(this);

            m_logger.LogTrace("Resuming multi-volume backup for volume #{Volume}", TOC.Volume + 1);

            // since we're on the new media volume, renew Navigator
            if (!Manager.RenewNavigator())
            {
                LogErrorAsDebug("Failed to renew Navigator");
                return TapeResult.Fail(this);
            }

            Debug.Assert(MultiVolumeContext != null);
            Debug.Assert(TOC.Count > 0); // we must have at least one set from previous volume

            TOC.Volume++;
            TOC.ContinuedOnNextVolume = false;

            // Create the new set TOC with the same settings as the last set of the previous volume.
            // contFromPrevVolume is true only when the set on the previous volume actually had files
            //  written — otherwise RemoveLastEmptySet may have removed the empty set, and the
            //  "current" set is now a different (earlier) one that should not be continued.
            bool isContinuation = MultiVolumeContext.Value.prevVolumeHasFiles;
            TOC.CloneCurrentSetTOC(contFromPrevVolume: isContinuation);
            TOC.CurrentSetTOC.Description += $" ({TOC.Volume})";

            // Preserve the original incremental flag — CloneCurrentSetTOC may have cloned
            //  a different set (e.g. after RemoveLastEmptySet removed the original empty set)
            TOC.MarkCurrentSetIncremental(MultiVolumeContext.Value.incremental);

            return BackupFilesToCurrentSet(newSet: true)
                ? TapeResult.OK : TapeResult.Fail(this);
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
                NotifyBatchEnd(bc.fileNotify);
                m_logger.LogWarning("Failed to begin writing content in {Method}", nameof(BackupFilesToCurrentSet));
                return false;
            }

            m_logger.LogTrace("Continuing (multi-volume) backup from file #{Number} >{File}<",
                bc.fileIndex + 1, bc.fileList[bc.fileIndex]);

            // The main loop thru the file list
            for (; bc.fileIndex < bc.fileList.Count; bc.fileIndex++) // use for int instead if foreach to know the index for multi-volume backup
            {
                var fileName = bc.fileList[bc.fileIndex];

                FileInfo fileInfo = new (fileName);
                // Create the real TapeFileInfo upfront — BackupFile() only handles tape I/O,
                //  TOC.Append() happens here on success.
                // Note: tfi.Block captures Drive.BlockCounter at construction time — used to
                //  rewind the tape on failure so the next file starts at the correct position.
                TapeFileInfo tfi = new(TOC.GenerateUID(), Drive.BlockCounter, fileInfo);

                // Track whether the file made it onto the tape AND into the TOC. Only then are
                //  we allowed to call NotifyPostProcessFile — and we MUST do so AFTER the
                //  per-file catch block so that a TapeAbortRequestedException raised by the
                //  notification cannot trigger the failure-cleanup path below (which rewinds
                //  the tape and would silently corrupt the just-written file: header and/or
                //  body would get clobbered by the next tape write, while the TOC entry would
                //  still claim the file is present).
                bool fileBackedUp = false;

                try
                {
                    // first check for abort request
                    ThrowIfAbortRequested(nameof(BackupFileListToCurrentSet));

                    if (!NotifyPreProcessFile(bc.fileNotify, tfi))
                    {
                        NotifyFileSkipped(bc.fileNotify, tfi);
                        m_logger.LogTrace("File #{Number} >{File}< skipped per pre-processor request", _stats.FilesProcessed, fileName);
                        continue; // not a failure, yet post-processing not called
                    }

                    if (!fileInfo.Exists)
                        throw new FileNotFoundException($"File not found", fileName);

                    m_logger.LogTrace("Backing up file #{Number} >{File}< of length {Length}",
                        _stats.FilesProcessed + 1, fileName, Helpers.BytesToString(fileInfo.Length));

                    if (TOC.CurrentSetTOC.Incremental && TOC.IsFileUptodateInc(fileInfo))
                    {
                        NotifyFileSkipped(bc.fileNotify, tfi);
                        m_logger.LogTrace("File #{Number} >{File}< found up-to-date in an incremental set -> skipping", _stats.FilesProcessed, fileName);
                        continue; // not a failure, yet post-processing not called
                    }

                    BackupFile(tfi);

                    // success — append to TOC; post-process notification is deferred until
                    //  AFTER the catch block so that an abort raised during notification
                    //  cannot run the per-file rewind/cleanup logic.
                    TOC.CurrentSetTOC.Append(tfi);
                    fileBackedUp = true;
                }
                catch (TapeAbortRequestedException)
                {
                    // Abort raised before BackupFile completed (i.e. from ThrowIfAbortRequested
                    //  at the top of the try, or rethrown by NotifyPreProcessFile). Nothing was
                    //  written for this file, no TOC entry was appended, so no tape rewind and
                    //  no NotifyFileFailed call (which would skew stats and itself rethrow).
                    //  We do NOT propagate the exception out of this method — the public API
                    //  is contractually bool/TapeResult based.
                    m_logger.LogTrace("{Method}: Abort requested before file #{Number} >{File}< was written",
                        nameof(BackupFilesToCurrentSet), _stats.FilesProcessed + 1, fileName);
                    bc.overallSuccess = false;
                    break;
                }
                catch (Exception ex)
                {
                    SetError(ex);

                    m_logger.LogWarning("{Method}: File #{Number} >{File}< backup failed. Exception: {Ex}",
                        nameof(BackupFilesToCurrentSet), _stats.FilesProcessed + 1, fileName, ex);

                    if (IsEOM || ex is TapeIOException { IsEOM: true })
                    {
                        // Rewind the tape over the partially-written file so that its remnants
                        //  do not consume space the volume's TOC may still need to fit on this
                        //  volume (e.g. when there is no Initiator partition). No TOC entry was
                        //  appended for this file, so the rewind is safe.
                        if (!Drive.MoveToBlock(tfi.Block))
                            m_logger.LogWarning("Failed to rewind tape to block {Block} after EOM on file >{File}<",
                                tfi.Block, fileName);
                        else
                            m_logger.LogTrace("Tape rewound to block {Block} after EOM on file >{File}<",
                                tfi.Block, fileName);

                        // Set up continuation on the next volume for multi-volume backup
                        m_logger.LogTrace("Setting up multi-volume backup from file #{Number} >{File}<",
                            _stats.FilesProcessed + 1, bc.fileList[bc.fileIndex]);
                        BytesBackedupMarker = BytesBackedup;

                        // Record whether the current set has files — needed by ResumeBackupToNextVolume
                        //  to decide contFromPrevVolume (an empty set removed by RemoveLastEmptySet
                        //  means the clone source will be a different, earlier set)
                        bc.prevVolumeHasFiles = TOC.CurrentSetTOC.Count > 0;

                        // Make sure to set MultiVolumeContext before calling NotifyFileFailed()
                        //  so that CanResumeToNextVolume indicates true already
                        MultiVolumeContext = bc;

                        // Report the file as failed (stats updated by NotifyFileFailed),
                        //  then undo the failure since the file will be re-tried on next volume
                        if (NotifyFileFailed(bc.fileNotify, tfi, ex) == FileFailedAction.Abort)
                            break;
                        StatsUndoFailure(); // the file will be re-tried on next volume

                        NotifyBatchEnd(bc.fileNotify);

                        TOC.ContinuedOnNextVolume = true;
                        Debug.Assert(CanResumeToNextVolume); // we're ready to continue with multi-volume backup
                        return false;
                    }

                    // Rewind tape to the block where this file started, so the next file
                    //  (retried or skipped) starts at the correct position without a dead block gap.
                    //  Safe because no TOC entry was appended for this file (BackupFile threw
                    //  before TOC.Append, so fileBackedUp stayed false).
                    if (!Drive.MoveToBlock(tfi.Block))
                        m_logger.LogWarning("Failed to rewind tape to block {Block} after failed file >{File}<",
                            tfi.Block, fileName);
                    else
                        m_logger.LogTrace("Tape rewound to block {Block} after failed file >{File}<",
                            tfi.Block, fileName);

                    var retryAction = NotifyFileFailed(bc.fileNotify, tfi, ex);
                    if (retryAction == FileFailedAction.Abort)
                    {
                        bc.overallSuccess = false;
                        break;
                    }
                    else if (retryAction == FileFailedAction.Retry)
                    {
                        bc.fileIndex--; // decrement to retry same file
                        StatsUndoFailure(); // don't double-count
                        continue;
                    }
                    // else Skip - continue to next file

                    bc.overallSuccess = false;
                    if (!bc.ignoreFailures)
                        break;
                } // catch

                // Post-process notification is deferred to here so it runs OUTSIDE the per-file
                //  catch block. If the notification throws TapeAbortRequestedException, we just
                //  break the loop — we do NOT rewind the tape or call NotifyFileFailed, since
                //  the file is already fully written and committed to the TOC.
                if (fileBackedUp)
                {
                    try
                    {
                        NotifyPostProcessFile(bc.fileNotify, tfi);
                    }
                    catch (TapeAbortRequestedException)
                    {
                        m_logger.LogTrace("{Method}: Abort requested while post-processing file #{Number} >{File}<",
                            nameof(BackupFilesToCurrentSet), _stats.FilesProcessed, fileName);
                        bc.overallSuccess = false;
                        break;
                    }

                    m_logger.LogTrace("File #{Number} >{File}< backed up ok", _stats.FilesProcessed, fileName);
                }

            } // foreach bc.fileIndex

            BytesBackedupMarker = BytesBackedup;
            NotifyBatchEnd(bc.fileNotify);

            MultiVolumeContext = null; // clear multi-volume context -- if we got here we're done with [multi-volume] backup

            return bc.overallSuccess;
        }

        /// <summary>
        /// Expands file/directory patterns via <see cref="BuildFileNameList"/> and backs them up to the current set.
        /// </summary>
        public TapeResult BackupFilesToCurrentSet(bool newSet, List<string> fileAndDirectoryPatterns, bool recurseSubdirs, bool ignoreFailures = true,
            ITapeFileNotifiable? fileNotify = null)
        {
            var fileList = BuildFileNameList(fileAndDirectoryPatterns, recurseSubdirs);

            return BackupFileListToCurrentSet(newSet, fileList, ignoreFailures, fileNotify);
        } // BackupFilesToCurrentSet()

        /// <summary>
        /// Backs up a pre-built file list to the current set. Resets statistics and starts
        ///  batch notifications. Supports multi-volume continuation via <see cref="ResumeBackupToNextVolume"/>.
        /// </summary>
        /// <param name="newSet">Whether to create a new content set or append to the existing one.</param>
        /// <param name="fileList">Fully resolved file paths to back up.</param>
        /// <param name="ignoreFailures">When <see langword="true"/>, continues after per-file errors.</param>
        /// <param name="fileNotify">Optional callback for progress, skip/retry/abort decisions.</param>
        public TapeResult BackupFileListToCurrentSet(bool newSet, List<string> fileList, bool ignoreFailures = true,
            ITapeFileNotifiable? fileNotify = null)
        {
            if (fileList.Count == 0)
            {
                m_logger.LogWarning("No files found to backup in {Method}", nameof(BackupFilesToCurrentSet));
                return TapeResult.OK; // no files found to back up -> treat as success
            }

            _stats.Reset();
            NotifyBatchStart(fileNotify, fileList.Count);

            m_logger.LogTrace("Starting backing up {Count} files to current set #{Set}", fileList.Count, TOC.CurrentSetIndex);
            if (TOC.CurrentSetTOC.Incremental)
                m_logger.LogTrace("Performing incremental backup to incremental set #{Set}", TOC.CurrentSetIndex);

            MultiVolumeContext = new(fileList, ignoreFailures, fileNotify, TOC.CurrentSetTOC.Incremental);

            return BackupFilesToCurrentSet(newSet)
                ? TapeResult.OK : TapeResult.Fail(this);
        } // BackupFilesToCurrentSet()

    } // class TapeFileBackupAgent


} // namespace TapeNET
