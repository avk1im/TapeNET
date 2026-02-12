using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Diagnostics;
using Windows.Win32.Foundation;
using Microsoft.Extensions.Logging;
using System.Buffers.Text;
using TapeLibNET;


namespace TapeLibNET
{

    /// <summary>
    /// Exception thrown when user requests to abort a tape operation.
    /// </summary>
    public class TapeAbortRequestedException(string? message = null) :
        OperationCanceledException(message ?? "Operation aborted by user request.")
    {
    }

    public enum FileFailedAction { Skip, Retry, Abort }

    public interface ITapeFileNotifiable
    {
        public void BatchStartStatistics(int set, int filesFound);
        public void BatchEndStatistics(int set, int filesProcessed, int filesFailed, long bytesProcessed);

        // The following methos may throw TapeOperationAbortException to abort the entire operation (not just the file)

        // called for a chance to modify the fileDescr before restoring the file. If returns false, skip the file
        public bool PreProcessFile(ref TapeFileDescriptor fileDescr);
        
        // called for a chance to modify the fileDescr after restoring the file. If returns false, skip applying fileDescr
        public bool PostProcessFile(ref TapeFileDescriptor fileDescr);
        
        // called when an error occurs during file restore
        //  returns how the caller wishes to proceed: skip file, retry the same file, abort entire operation
        public FileFailedAction OnFileFailed(TapeFileDescriptor fileDescr, Exception ex);
        public void OnFileSkipped(TapeFileDescriptor fileDescr);
    }


    // The base class handles TOC backup and restore
    public class TapeFileAgent : TapeDriveHolder<TapeFileAgent>, IDisposable
    {
        private const uint c_fixedTOCBlockSize = 16 * 1024; // 16K

        // Hashing for TOC is fixed since it needs to be known upfront for each tape
        private readonly TapeHashAlgorithm c_hashForTOC = TapeHashAlgorithm.Crc64;

        public TapeTOC TOC { get; init; } // TOC reference is guranteed immutable, so user may store
        public TapeStreamManager Manager { get; init; }
        public TapeNavigator Navigator => Manager.Navigator;

        public long BytesBackedup { get; protected set; } = 0L;
        public long BytesRestored { get; protected set; } = 0L;

        // checked periodically if the entire operation should be aborted
        public bool IsAbortRequested { get; set; }


        protected int CurrentSetAsNavigatorContentSet // used by both backup and restore agents
        {
            get
            {
                int toBegin = TOC.CurrentSetIndexOnVolume; // same as TOC.CurrentSetIndex - TOC.FirstSetOnVolume
                if (toBegin == 0)
                    return 0; // the first content set on volume should always be accessed from the beginning

                // Optimization: consider Navigator's current position when chosing how to specify the content set for Navigator
                int toCurr; // use to determine if current set is closer to Navigator's current position than to begin or end
                if (Navigator.CurrentContentSet != TapeNavigator.UnknownSet && Navigator.CurrentContentSet != TapeNavigator.InTOCSet)
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


        public TapeFileAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : base(drive)
        {
            Manager = new (drive);
            AddErrorSource(Manager);
            TOC = legacyTOC ?? [];
        }


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

        #region *** TOC Backup ***

        private bool BeginWriteTOC()
        {
            // If we were reading or writing, end it first - before setting the parameters for TOC writing
            Manager.EndReadWrite();

            Drive.SetBlockSize(c_fixedTOCBlockSize);

            return Manager.BeginWriteTOC();
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

                ThrowIfAbortRequested($"after openning TOC in {nameof(BackupTOCCore)}");

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
                    using var hashingStream = new HashingStream(wstream, hasher, disposeInnerToo: false);
                    var serializer = new TapeSerializer(hashingStream);
                    serializer.Serialize(TOC);
                    serializer.Serialize(hasher.GetCurrentHash()); // notice the hash bytes themselves aren't added to the hash!

/*#if DEBUG
                    // TEST FIXME: serialize a 55 MB dummy array
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
                m_logger.LogWarning("Exception {Exception} in {Method}", ex, nameof(BackupTOCCore));
                return false;
            }
        }

        public bool BackupTOC(bool enforce = false)
        {
            m_logger.LogTrace("Backing up TOC, 1st copy");

            if (enforce)
            {
                Manager.EndReadWrite();
                Navigator.ResetContentSet();
                
                m_logger.LogTrace("Enforcing TOC backup by resetting content set");
            }

            if (!BeginWriteTOC())
            {
                m_logger.LogError("Failed to begin TOC write in {Method}", nameof(BackupTOC));
                return false;
            }

            // To ensure TOC integrity, backup TOC twice
            bool result1 = BackupTOCCore();
            if (result1)
                m_logger.LogTrace("TOC 1st copy backed up ok");
            else
                m_logger.LogWarning("TOC 1st copy backup failed");

            m_logger.LogTrace("Backing up TOC, 2nd copy");
            bool result2 = BackupTOCCore();
            if (result2)
                m_logger.LogTrace("TOC 2nd copy backed up ok");
            else
                m_logger.LogWarning("TOC 2nd copy backup failed");

            return result1 || result2;
        }

#endregion // *** TOC Backup ***

        #region *** TOC Restore ***

        private bool BeginReadTOC()
        {
            // If we were reading or writing, end it first - before setting the parameters for TOC reading
            Manager.EndReadWrite();

            // set the filemarks and block size from the set to the manager
            Drive.SetBlockSize(c_fixedTOCBlockSize);

            return Manager.BeginReadTOC();
        }
        private TapeReadStream? OpenReadTOCStream()
        {
            return Manager.ProduceReadTOCStream(textFileMode: false, lengthLimit: -1);
        }

        private bool RestoreTOCCore()
        {
            try
            {
                using var rstream = OpenReadTOCStream();
                if (rstream == null)
                {
                    m_logger.LogWarning("Failed to open TOC read stream in {Method}", nameof(RestoreTOCCore));
                    return false;
                }

                ThrowIfAbortRequested($"after openning TOC in {nameof(RestoreTOCCore)}");

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
                        return false;
                    }
                }
                else
                {
                    using var hashingStream = new HashingStream(rstream, hasher, disposeInnerToo: false);
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
                        return false;
                    }

                }
            }
            catch (Exception ex)
            {
                m_logger.LogWarning("Exception {Exception} while restoring TOC", ex);
                return false;
            }
        }

        public bool RestoreTOC()
        {
            // Since TOC is stored twice, if the first attempt fails, try again
            m_logger.LogTrace("Restoring TOC from 1st copy");

            if (!BeginReadTOC())
            {
                m_logger.LogError("Failed to begin TOC read in {Method}", nameof(RestoreTOC));
                return false;
            }

            bool result = RestoreTOCCore();

            if (result)
                m_logger.LogTrace("TOC restored from 1st copy");
            else
            {
                m_logger.LogWarning("TOC restore from 1st copy failed. Trying 2nd copy");
                // Notice we now must be at the beginning of the 2nd copy, as Manager calls Navigator.MoveToNextTOCFilemark()
                //  from Manager.EndReadFile() when disposing the 1st read TOC sytream
                result = RestoreTOCCore();
                if (result)
                    m_logger.LogTrace("TOC restored from 2nd copy");
                else
                    m_logger.LogError("TOC restore from 2nd copy failed");

                if (!result)
                {
                    // Last try: BeginReadTOC() again, then immediately move to the filemark for the 2nd copy
                    m_logger.LogTrace("Attempting to skip to 2nd TOC copy directly");
                    result = BeginReadTOC() && Navigator.MoveToNextTOCFilemark() && RestoreTOCCore();

                    if (result)
                        m_logger.LogTrace("TOC restored directly from 2nd copy");
                    else
                        m_logger.LogError("TOC restore directly from 2nd copy failed");
                }
            }

            if (result)
            {
                // Optimization for the case of TOC in a partition:
                //  Now that we've restored TOC, we're likely to start restoring content ->
                //  move to the beginning of content. Besides, in case of TOC in a partition,
                //  this will ensure the drive reports the media capacity for Content partition
                if (Drive.HasInitiatorPartition)
                    Navigator.MoveToBeginOfContent();
            }

            return result;
        }

        #endregion // *** TOC Restore ***

        #region *** Notification wrappers ***

        // Safe calls to ITapeFileNotifiable
        //  All exceptions are caught and logged as warnings -- except for TapeAbortRequestedException, which is rethrown

        protected void NotifyBatchStartStatistics(ITapeFileNotifiable? fileNotify, int filesFound)
        {
            if (fileNotify != null)
            {
                try
                {
                    fileNotify.BatchStartStatistics(TOC.CurrentSetIndex, filesFound);
                }
                catch (Exception ex2)
                {
                    // in statistics notification, we don't rethrow TapeAbortRequestedException
                    m_logger.LogWarning("Exception {Exception} while notifying batch start statistics", ex2);
                }
            }
        }
        protected void NotifyBatchEndStatistics(ITapeFileNotifiable? fileNotify, int filesProcessed, int filesFailed, long bytesProcessed)
        {
            if (fileNotify != null)
            {
                try
                {
                    fileNotify.BatchEndStatistics(TOC.CurrentSetIndex, filesProcessed, filesFailed, bytesProcessed);
                }
                catch (Exception ex2)
                {
                    // in statistics notification, we don't rethrow TapeAbortRequestedException
                    m_logger.LogWarning("Exception {Exception} while notifying batch end statistics", ex2);
                }
            }
        }

        protected bool NotifyPreProcessFile(ITapeFileNotifiable? fileNotify, ref TapeFileDescriptor fileDescr)
        {
            if (fileNotify != null)
            {
                try
                {
                    return fileNotify.PreProcessFile(ref fileDescr);
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
        protected bool NotifyPostProcessFile(ITapeFileNotifiable? fileNotify, ref TapeFileDescriptor fileDescr)
        {
            if (fileNotify != null)
            {
                try
                {
                    return fileNotify.PostProcessFile(ref fileDescr);
                }
                catch (TapeAbortRequestedException ex1)
                {
                    m_logger.LogInformation("Abort requested while notifying pre-process file: {Exception}", ex1);
                    throw; // rethrow to abort the entire operation
                }
                catch (Exception ex2)
                {
                    m_logger.LogWarning("Exception {Exception} while notifying post-process file", ex2);
                }
            }
            return true;
        }

        // Returns true to signal abort of entire operation
        //  Since we may call this method outside of try block, do not rethrow TapeAbortRequestedException here --
        //  return the action request instead
        protected FileFailedAction NotifyFileFailed(ITapeFileNotifiable? fileNotify, TapeFileDescriptor fileDescr, Exception ex)
        {
            FileFailedAction result = FileFailedAction.Skip;

            if (fileNotify != null)
            {
                try
                {
                    result = fileNotify.OnFileFailed(fileDescr, ex);
                }
                catch (TapeAbortRequestedException ex1)
                {
                    m_logger.LogInformation("Abort requested while notifying pre-process file: {Exception}", ex1);
                    return FileFailedAction.Abort; // signal to abort the entire operation
                }
                catch (Exception ex2)
                {
                    m_logger.LogWarning("Exception {Exception} while notifying file failure", ex2);
                    return FileFailedAction.Skip; // do not abort the operation
                }
            }

            return result;
        }
        protected void NotifyFileSkipped(ITapeFileNotifiable? fileNotify, TapeFileDescriptor fileDescr)
        {
            if (fileNotify != null)
            {
                try
                {
                    fileNotify.OnFileSkipped(fileDescr);
                }
                catch (TapeAbortRequestedException ex1)
                {
                    m_logger.LogInformation("Abort requested while notifying pre-process file: {Exception}", ex1);
                    throw; // rethrow to abort the entire operation
                }
                catch (Exception ex2)
                {
                    m_logger.LogWarning("Exception {Exception} while notifying file skipped", ex2);
                }
            }
        }

        #endregion // *** Notification wrappers ***

        protected void ThrowIfAbortRequested(string where)
        {
            if (IsAbortRequested)
                throw new TapeAbortRequestedException($"Abort requested in {where}");
        }

    } // class TapeFileAgent

} // namespace TapeNET
