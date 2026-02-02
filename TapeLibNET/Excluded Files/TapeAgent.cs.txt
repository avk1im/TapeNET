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

    public interface ITapeFileNotifiable
    {
        public void BatchStartStatistics(int set, int filesFound);
        public void BatchEndStatistics(int set, int filesProcessed, int filesFailed, long bytesProcessed);
        
        // called for a chance to modify the fileDescr before restoring the file. If returns false, skip the file
        public bool PreProcessFile(ref TapeFileDescriptor fileDescr);
        
        // called for a chance to modify the fileDescr after restoring the file. If returns false, skip applying fileDescr
        public bool PostProcessFile(ref TapeFileDescriptor fileDescr);
        
        // called when an error occurs during file restore
        public void OnFileFailed(TapeFileDescriptor fileDescr, Exception ex);
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
                    Debug.Assert(navCurr > 0);

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


        private TapeWriteStream? OpenWriteTOCStream()
        {
            // If we were reading or writing, end it first - before setting the new parameters
            Manager.EndReadWrite();

            Drive.SetBlockSize(c_fixedTOCBlockSize);

            return Manager.ProduceWriteTOCStream();
        }

        private bool BackupTOCCore()
        {
            try
            {
                using var wstream = OpenWriteTOCStream();
                if (wstream == null)
                    return false;

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
                }

                BytesBackedup += wstream.Length;
                return true;
            }
            catch (Exception ex)
            {
                m_logger.LogWarning("Exception {Exception} in {Method}", ex, nameof(BackupTOC));
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

            return result1 && result2;
        }


        private TapeReadStream? OpenReadTOCStream()
        {
            // If we were reading or writing, end it first - before setting the new parameters
            Manager.EndReadWrite();

            Drive.SetBlockSize(c_fixedTOCBlockSize);

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
            bool result = RestoreTOCCore();

            if (result)
                m_logger.LogTrace("TOC restored from 1st copy");
            else
            {
                m_logger.LogWarning("TOC restore from 1st copy failed. Trying 2nd copy");
                result = RestoreTOCCore();
                if (result)
                    m_logger.LogTrace("TOC restored from 2nd copy");
                else
                    m_logger.LogError("TOC restore from 2nd copy failed");
            }

            return result;
        }


        // Safe calls to ITapeFileNotifiable
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
                catch (Exception ex2)
                {
                    m_logger.LogWarning("Exception {Exception} while notifying post-process file", ex2);
                }
            }
            return true;
        }
        protected void NotifyFileFailed(ITapeFileNotifiable? fileNotify, TapeFileDescriptor fileDescr, Exception ex)
        {
            if (fileNotify != null)
            {
                try
                {
                    fileNotify.OnFileFailed(fileDescr, ex);
                }
                catch (Exception ex2)
                {
                    m_logger.LogWarning("Exception {Exception} while notifying file failure", ex2);
                }
            }
        }
        protected void NotifyFileSkipped(ITapeFileNotifiable? fileNotify, TapeFileDescriptor fileDescr)
        {
            if (fileNotify != null)
            {
                try
                {
                    fileNotify.OnFileSkipped(fileDescr);
                }
                catch (Exception ex2)
                {
                    m_logger.LogWarning("Exception {Exception} while notifying file skipped", ex2);
                }
            }
        }

    } // class TapeFileAgent

} // namespace TapeNET
