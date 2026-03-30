using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Win32.Foundation;


namespace TapeLibNET
{
    using TypeUID = ulong;

    public enum TapeHashAlgorithm
    {
        None,
        Crc32,
        Crc64,
        XxHash32,
        XxHash3,
        XxHash64,
        XxHash128,
    }

    // TapeFileDescriptor members match the public properties of FileSystemInfo
    //  We use TapeFileDescriptor instead of FileInfo to avoid accessing the actual file!
    //  E.g. setting Attributes on a FileInfo instance causes it to attempt to set them also for the actual file
    public struct TapeFileDescriptor
    {
        public string FullName;
        public long Length;
        public FileAttributes Attributes;
        public DateTime CreationTime;
        public DateTime LastWriteTime;
        public DateTime LastAccessTime;

        public TapeFileDescriptor(string fullName)
        {
            FullName = fullName;
        }

        // Safe access to FileSystemInfo fields -- only if the file/directory exists
        public TapeFileDescriptor(FileSystemInfo fsi)
        {
            if (fsi.Exists)
            {
                FullName = fsi.FullName;
                Attributes = fsi.Attributes;
                CreationTime = fsi.CreationTime;
                LastWriteTime = fsi.LastWriteTime;
                LastAccessTime = fsi.LastAccessTime;
            }
            else
            {
                FullName = fsi.FullName;
            }
        }

        // Safe access to FileInfo fields -- only if the file exists
        public TapeFileDescriptor(FileInfo fsi)
        {
            if (fsi.Exists)
            {
                FullName = fsi.FullName;
                Attributes = fsi.Attributes;
                CreationTime = fsi.CreationTime;
                LastWriteTime = fsi.LastWriteTime;
                LastAccessTime = fsi.LastAccessTime;
                Length = fsi.Length;
            }
            else
            {
                FullName = fsi.FullName;
            }
        }

        public readonly bool SameFileName(string fileName) => FullName.Equals(fileName, StringComparison.OrdinalIgnoreCase);
        public readonly bool SameFileName(TapeFileDescriptor other) => SameFileName(other.FullName);

        public readonly FileInfo CreateFileInfo() => new(FullName);
        public readonly bool ApplyToFileInfo(FileInfo fileInfo)
        {
            if (fileInfo.Exists)
            {
                fileInfo.Attributes = Attributes;
                fileInfo.CreationTime = CreationTime;
                fileInfo.LastWriteTime = LastWriteTime;
                fileInfo.LastAccessTime = LastAccessTime;
                return true;
            }
            else
                return false;
        }
        public void FillFrom(FileSystemInfo fsi)
        {
            FullName = fsi.FullName;
            Attributes = fsi.Attributes;
            CreationTime = fsi.CreationTime;
            LastWriteTime = fsi.LastWriteTime;
            LastAccessTime = fsi.LastAccessTime;
        }
        public void FillFrom(FileInfo fsi)
        {
            FillFrom(fsi as FileSystemInfo);
            Length = fsi.Length;
        }

        // Estimate serialized size
        public readonly int EstimateSerializedSize()
        {
            // string length (4 bytes) + UTF8 bytes
            int size = sizeof(int) + Encoding.UTF8.GetByteCount(FullName);
            unsafe // for size(DateTime)
            {
                // Length (8) + Attributes (4) + 3x DateTime (8 each)
                size += sizeof(long) + sizeof(FileAttributes) + 3 * sizeof(DateTime);
            }
            return size;
        }
    }

    // The on-tape information about a file. Used as both TOC entry and on-tape file header
    public class TapeFileInfo(TypeUID UID, long block, TapeFileDescriptor fileDescr) : ITapeSerializable
    {
        public TypeUID UID { get; } = UID;
        public long Block { get; } = block;
        public TapeFileDescriptor FileDescr { get; } = fileDescr;
        internal byte[]? Hash { get; set; } = null;

        public TapeFileInfo(TypeUID UID, long block, FileInfo fileInfo)
            : this(UID, block, new TapeFileDescriptor(fileInfo))
        { }

        public bool SameFileName(TapeFileInfo other) => FileDescr.SameFileName(other.FileDescr);
        public bool SameFileName(FileInfo fileInfo) => FileDescr.SameFileName(fileInfo.FullName);

        public bool IsValid => UID != 0 && !string.IsNullOrEmpty(FileDescr.FullName);

        // ITapeSerializable {
        public void SerializeTo(TapeSerializer serializer)
        {
            serializer.SerializeSignature();
            serializer.Serialize((ulong)UID);
            serializer.Serialize(Block);
            serializer.Serialize(FileDescr);
            serializer.SerializeNullableWithLength(Hash);
        }
        public static ITapeSerializable? ConstructFrom(TapeDeserializer deserializer)
        {
            if (!deserializer.ValidateSignature())
                return null; // version mismatch

            var UID = (TypeUID)deserializer.DeserializeUInt64();
            var block = deserializer.DeserializeInt64();
            var fileDescr = deserializer.DeserializeFileDescriptor();

            return new TapeFileInfo(UID, block, fileDescr)
            {
                Hash = deserializer.DeserializeNullableBytesWithLength()
            };
        }
        // } ITapeSerializable

        public int EstimateSerializedSize()
        {
            // Signature: 2 bytes + Version: 2 bytes
            int size = TapeSerializer.Signature.Length + sizeof(ushort);
            // UID: 8 bytes (ulong)
            size += sizeof(TypeUID);
            // Block: 8 bytes (long)
            size += sizeof(long);
            // FileDescr
            size += FileDescr.EstimateSerializedSize();
            // Hash: length prefix (4 bytes) + optional bytes
            size += sizeof(int) + (Hash?.Length ?? 0);

            return size;
        }

        // serailize only minimal information necessary to check file match on tape 
        public void SerializeHeaderTo(TapeSerializer serializer)
        {
            serializer.SerializeSignature();
            serializer.Serialize((ulong)UID);
        }

        public static int EstimateSerializedHeaderSize()
        {
            // Signature: 2 bytes + Version: 2 bytes
            int size = TapeSerializer.Signature.Length + sizeof(ushort);
            // UID: 8 bytes (ulong)
            size += sizeof(TypeUID);
            return size;
        }

        // deserailize header and check if it matches this file info
        public bool DeserializeAndCheckHeaderFrom(TapeDeserializer deserializer)
        {
            if (!deserializer.ValidateSignature())
                return false; // version mismatch

            var UID = (TypeUID)deserializer.DeserializeUInt64();
            return UID == this.UID;
        }

    } // struct TapeFileInfo

    
    // Manages a list of TapeFileInfo
    //  While the class is accessible externally, new instances only created via TapeTOC.AddNewSetTOC()
    public class TapeSetTOC : ITapeSerializable, IReadOnlyList<TapeFileInfo>
    {
        private readonly List<TapeFileInfo> m_tapeFileInfos;

        // for public access, new instances only created via class TapeTOC.AddNewSetTOC()
        internal TapeSetTOC(int volume, int capacity = 0, bool incremental = false)
        {
            Volume = volume;
            m_tapeFileInfos = new(capacity);
            Incremental = incremental;
        }
        internal TapeSetTOC(TapeSetTOC setTOC) : this(setTOC.Volume, setTOC.Capacity, setTOC.Incremental)
        {
            CopyFrom(setTOC);
            ContinuedFromPrevVolume = setTOC.ContinuedFromPrevVolume;
        }
        public string Description { get; set; } = string.Empty;
        public DateTime CreationTime { get; internal set; } = DateTime.Now;
        public bool FmksMode { get; set; } = false;
        public uint BlockSize { get; set; } = 0;
        public DateTime LastSaveTime { get; internal set; } = DateTime.Now;
        public TapeHashAlgorithm HashAlgorithm { get; set; } = TapeHashAlgorithm.Crc32;
        public bool Incremental { get; internal set; } = false;
        internal bool MarkIncremental(bool incremental = true) // can only change Incremental if the set is empty
        {
            if (Count == 0)
                Incremental = incremental;
            return Incremental;
        }
        public int Volume { get; internal set; } = 0;
        public bool ContinuedFromPrevVolume { get; init; } = false;

        // deserialization constructor
        private TapeSetTOC(List<TapeFileInfo> fileInfos) => m_tapeFileInfos = fileInfos;

        #region ITapeSerializable
        public void SerializeTo(TapeSerializer serializer)
        {
            serializer.SerializeSignature();

            serializer.Serialize<List<TapeFileInfo>, TapeFileInfo>(m_tapeFileInfos);
            serializer.Serialize(Description);
            serializer.Serialize(CreationTime);
            serializer.Serialize(FmksMode);
            serializer.Serialize(BlockSize);
            serializer.Serialize(LastSaveTime = DateTime.Now);
            serializer.Serialize((int)HashAlgorithm);
            serializer.Serialize(Incremental);
            serializer.Serialize(Volume);
            serializer.Serialize(ContinuedFromPrevVolume);
        }

        public static ITapeSerializable? ConstructFrom(TapeDeserializer deserializer)
        {
            if (!deserializer.ValidateSignature())
                return null;

            var fileInfos = deserializer.Deserialize<List<TapeFileInfo>, TapeFileInfo>();
            if (fileInfos == null)
                return null;

            return new TapeSetTOC(fileInfos)
            {
                Description = deserializer.DeserializeString(),
                CreationTime = deserializer.DeserializeDateTime(),
                FmksMode = deserializer.DeserializeBoolean(),
                BlockSize = deserializer.DeserializeUInt32(),
                LastSaveTime = deserializer.DeserializeDateTime(),
                HashAlgorithm = (TapeHashAlgorithm)deserializer.DeserializeInt32(),
                Incremental = deserializer.DeserializeBoolean(),
                Volume = deserializer.DeserializeInt32(),
                ContinuedFromPrevVolume = deserializer.DeserializeBoolean(),
            };
        }
        #endregion // } ITapeSerializable


        public int Count => m_tapeFileInfos.Count;
        public int IndexOf(TapeFileInfo item) => m_tapeFileInfos.IndexOf(item);
        public int IndexOf(TapeFileInfo item, int fromIndex) => m_tapeFileInfos.IndexOf(item, fromIndex);
        internal int Capacity
        {
            get => m_tapeFileInfos.Capacity;
            set
            {
                // only set if new capacity is greater than current count
                if (value > m_tapeFileInfos.Count)
                    m_tapeFileInfos.Capacity = value;
            }
        }
        internal void Append(TapeFileInfo tfi)
        {
            m_tapeFileInfos.Add(tfi);
        }
        internal void Clear() => m_tapeFileInfos.Clear(); // removes all file entries -> use with CAUTION!
        internal void CopyFrom(TapeSetTOC toc)
        {
            m_tapeFileInfos.Clear();
            m_tapeFileInfos.AddRange(toc.m_tapeFileInfos);
            Description = toc.Description;
            CreationTime = toc.CreationTime;
            FmksMode = toc.FmksMode;
            BlockSize = toc.BlockSize;
            LastSaveTime = toc.LastSaveTime;
            HashAlgorithm = toc.HashAlgorithm;
            Incremental = toc.Incremental;
            Volume = toc.Volume;
            // ContinuedFromPrevVolume = toc.ContinuedFromPrevVolume; // set only during construction
        }


        // IReadOnlyList<TapeFileInfo> {
        public TapeFileInfo this[int index] => m_tapeFileInfos[index];
        // } IReadOnlyList<TapeFileInfo>

        // IEnumerable<TapeFileInfo> {
        public IEnumerator<TapeFileInfo> GetEnumerator()
        {
            return m_tapeFileInfos.GetEnumerator();
            /*
            foreach (var entry in m_fileInfos)
            {
                yield return entry;
            }
            */
        }

        // Explicit implementation of the non-generic IEnumerable interface
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator(); // Just call the generic version
        }
        // } IEnumerable<TapeFileInfo>

        
        public List<int> RefsToIndexes(IEnumerable<TapeFileInfo> tfis)
        {
            List<int> indexes = [];
            int last = 0;
            bool sorted = true;
            foreach (var tfi in tfis)
            {
                // first, try to find from last index -- assuming tfis are sorted
                int index = IndexOf(tfi, last);
                if (index < 0) // otherwise, try to find from the beginning
                {
                    index = IndexOf(tfi);
                    if (index > 0)
                        sorted = false;
                }

                if (index >= 0)
                {
                    indexes.Add(index);
                    last = index + 1;
                    if (last >= Count)
                        break;
                }
            }

            if (!sorted)
                indexes.Sort();

            return indexes;
        }

        public static bool FileMatchesRegexPattern(string fullFileName, string pattern)
        {
            return Regex.IsMatch(fullFileName, pattern, RegexOptions.IgnoreCase);
        }

        public static bool FileMatchesRegexPatterns(string fullFileName, IEnumerable<string> patterns)
        {
            return patterns.Any(pattern => FileMatchesRegexPattern(fullFileName, pattern));
        }

        public static string FromFilePatternToRegexPattern(string pattern)
        {
            // Convert the pattern to a regular expression
            //  Consider that if the pattern ends with \, automatically treat it as
            //  "select all files from that directory", that is as "*.*"
            if (pattern.EndsWith('\\'))
                pattern += "*.*";

            // Escape special regex characters, then replace wildcard characters with their regex equivalents
            pattern = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");

            // No ^ or $ to match the start or end of the string,
            //  since we want to match the specified file pattern anywhere in the string
            return pattern;
        }

        public static IEnumerable<string> FromFilePatternsToRegexPatterns(List<string> patterns)
        {
            // Convert the list of patterns to a list of regular expressions
            return patterns.Select(FromFilePatternToRegexPattern);
        }

        public static bool PatternsHaveWildcards(List<string> patterns) =>
            patterns.Exists(p => p.Contains('*') || p.Contains('?') || p.EndsWith('\\'));

        /// <summary>
        /// Returns a list of <see cref="TapeFileInfo"/> objects that pass the given filter.
        /// A <c>null</c> filter means select all files.
        /// Returns <c>null</c> when all files in the set match ("null means all" convention).
        /// </summary>
        public List<TapeFileInfo>? SelectFiles(ITapeFileFilter? filter)
        {
            if (filter == null)
                return null; // null means all files in set

            List<TapeFileInfo> filesSelected = [];
            foreach (var tfi in this)
            {
                if (filter.Matches(tfi.FileDescr))
                    filesSelected.Add(tfi);
            }

            return (filesSelected.Count == Count) ? null /* null means all files in set */ : filesSelected;
        }

        /// <summary>
        /// Returns a linked list of <see cref="TapeFileInfo"/> objects that pass the given filter.
        /// A <c>null</c> filter means select all files.
        /// Doesn't return "null means all files" shortcut since the list might need editing.
        /// </summary>
        internal LinkedList<TapeFileInfo> SelectFilesAsLinkedList(ITapeFileFilter? filter)
        {
            if (filter == null)
                return new(m_tapeFileInfos); // list all files -- don't use "null means all files" shortcut

            LinkedList<TapeFileInfo> filesSelected = [];
            foreach (var tfi in this)
            {
                if (filter.Matches(tfi.FileDescr))
                    filesSelected.AddLast(tfi);
            }

            return filesSelected; // don't use "null means all files" shortcut since the list might need editing
        }
        // Compute the size of all files in the set on tape, consideing the block size
        public long ComputeTotalFileSizeOnTape(uint defaultBlockSize = 0)
        {
            if (Count == 0)
                return 0L;
            long totalSize = 0L;
            uint blockSize = (BlockSize > 0) ? BlockSize : defaultBlockSize;
            if (blockSize == 0)
                blockSize = 1; // don't round up if block size is not specified
            foreach (var tfi in this)
            {
                long fileSize = tfi.FileDescr.Length + TapeFileInfo.EstimateSerializedHeaderSize();
                long blocks = (fileSize + blockSize - 1) / blockSize; // round up to next block
                totalSize += blocks * blockSize;
            }
            return totalSize;
        }

    } // class TapeSetTOC


    // Manages a list of SetTOCs
    public class TapeTOC : ITapeSerializable, IEnumerable<TapeSetTOC>
    {
        private readonly List<TapeSetTOC> m_setTOCs;
        private TypeUID m_nextUID;

        public TapeTOC()
        {
            m_setTOCs = [];
            m_nextUID = 1UL; // 0UL is an invalid or "not set" value for UID
        }
        public TapeTOC(string description) : this()
        {
            Description = description;
        }
        public TapeTOC(TapeTOC toc) : this()
        {
            CopyFrom(toc);
        }

        internal TypeUID GenerateUID() => m_nextUID++;
        public string Description { get; set; } = string.Empty;
        public DateTime CreationTime { get; internal set; } = DateTime.Now;
        public DateTime LastSaveTime { get; internal set; } = DateTime.Now;

        public int Volume { get; internal set; } = 1; // volume indexing starts from 1
        public bool ContinuedOnNextVolume { get; set; } = false;

        public int Count => m_setTOCs.Count;
        public TapeSetTOC CurrentSetTOC
        {
            get
            {
                if (m_setTOCs.Count == 0)
                    AddNewSetTOC();

                Debug.Assert(m_setTOCs.Count > 0);
                Debug.Assert(m_currSetInternal >= 0 && m_currSetInternal < Count);

                return m_setTOCs[m_currSetInternal];
            }
        }
        public bool IsEmpty => Count == 0 || Count == 1 && CurrentSetTOC.Count == 0;

        // Notice backup set index assignment:
        //  positive number means counting from the oldest up: 1 is the oldest set, 2 the second oldest, etc.
        //  0 or negative number means counting from the latest down: 0 is the latest, -1 is the second latest, -2 is the 3rd latest, etc.
        //  0 means the latest (last backed up) set -- this is the default value
        //  Illustration for 3 sets (Count = 3):
        //  index:      1      2      3    <- main index (what we return via CurrentSetIndex)
        //  oldest -> [set0] [set1] [set2] <- latest
        //  alt index: -2     -1      0    <- alternative index (what we also understand)
        //  internal:   0      1      2    <- this is what we use internally
        private int SetIndexToInternal(int setIndex)
        {
            if (Count == 0)
                return 0; // no sets, so return 0
            if (setIndex <= 0)
                return Count - 1 + setIndex;
            else // setIndex > 0
                return setIndex - 1;
        }
        private static int InternalToSetIndex(int setInternal) => setInternal + 1;
        // convert standard index to alternative one, or vice versa
        public int SetIndexToAlt(int setIndex)
        {
            if (setIndex <= 0)
                return MaxSetIndex + setIndex;
            else // setIndex > 0
                return setIndex - MaxSetIndex;
        }
        // the standard index form is 1..MaxSetIndex
        public int SetIndexToStd(int setIndex) => (setIndex <= 0) ? SetIndexToAlt(setIndex) : setIndex;
        public int CapSetIndex(int setIndex) => Math.Max(MinSetIndex, Math.Min(MaxSetIndex, setIndex));
        public int MaxSetIndex => Count; // for the standard index form 1..MaxSetIndex
        public int MinSetIndex => -(Count - 1); // for the alternative index form -MinSetIndex..0
        public int CurrentSetIndex
        {
            set // Notice: does NOT check if the set is on volume!
            {
                value = SetIndexToInternal(value);

                if (value == m_currSetInternal)
                    return; // nothing to do

                SetCurrentSetInternal(value);
            }

            get => InternalToSetIndex(m_currSetInternal);
        }

        public void MakeLastSetCurrent() => CurrentSetIndex = 0;
        private void SetCurrentSetInternal(int setInternal)
        {
            if (Count == 0)
                return;
            Debug.Assert(setInternal >= 0 && setInternal < Count);
            Debug.Assert(Count > 0);
            m_currSetInternal = setInternal;
        }
        private int m_currSetInternal = 0;

        private int FirstSetInternalOnVolume // internal index of the first set on the current volume
        {
            get
            {
                // shortcut for Volume #1
                if (Volume == 1)
                    return 0;

                int firstOnVolume = m_currSetInternal;
                for (int i = m_currSetInternal; i >= 0; i--)
                {
                    if (m_setTOCs[i].Volume < Volume)
                        return firstOnVolume;
                    firstOnVolume = i;
                }
                return 0;
            }
        }
        private int LastSetInternalOnVolume // internal index of the last set on the current volume
        {
            get
            {
                // shortcut if the last set is on current Volume
                if (this[Count - 1].Volume == Volume)
                    return Count - 1;

                int lastOnVolume = m_currSetInternal;
                for (int i = m_currSetInternal; i < Count; i++)
                {
                    if (m_setTOCs[i].Volume > Volume)
                        return lastOnVolume;
                    lastOnVolume = i;
                }
                return Count - 1;
            }
        }
        public int FirstSetOnVolume => InternalToSetIndex(FirstSetInternalOnVolume);
        public int LastSetOnVolume => InternalToSetIndex(LastSetInternalOnVolume);
        public bool IsCurrentSetOnVolume => CurrentSetTOC.Volume == Volume;
        public bool IsCurrentSetLast => CurrentSetIndex == MaxSetIndex;
        public bool IsCurrentSetContOnNextVolume => IsCurrentSetLast ? ContinuedOnNextVolume :
            this[CurrentSetIndex + 1].ContinuedFromPrevVolume;
        public bool IsCurrentSetContFromPrevVolume => CurrentSetTOC.ContinuedFromPrevVolume;
        public bool IsCurrentSetContFromPrevVolumeInc => LastNonIncSetInternal < FirstSetInternalOnVolume;

        internal int CurrentSetIndexOnVolume => m_currSetInternal - FirstSetInternalOnVolume;

        public TapeSetTOC this[int setIndex]
        {
            get
            {
                if (m_setTOCs.Count == 0)
                    AddNewSetTOC();

                int setInternal = SetIndexToInternal(setIndex);
                return m_setTOCs[setInternal]; // will throw if setIndex is out of range
            }
        }

        public void AddNewSetTOC(int capacity = 0, bool incremental = false)
        {
            // if the last set is empty, use it and set its capacity to 'size'
            //  Do not use CurrentSetTOC since it can call AddNewSetTOC() recursively!
            if (m_setTOCs.Count > 0 && m_setTOCs.Last() is var last && last.Count == 0)
            {
                last.Volume = Volume;
                last.Capacity = capacity;
                last.Incremental = incremental && m_setTOCs.Count > 1; // the very first set shouldn't be incremental
            }
            else
                m_setTOCs.Add(new TapeSetTOC(Volume, capacity, incremental && m_setTOCs.Count > 1));

            Debug.Assert(m_setTOCs.Count > 0);

            MakeLastSetCurrent();
        }

        public void CloneSetTOC(int setIndex, bool contFromPrevVolume = false)
        {
            int setInternal = SetIndexToInternal(setIndex);
            var setTOC = m_setTOCs[setInternal];
            m_setTOCs.Add(
                new TapeSetTOC(Volume, setTOC.Capacity, setTOC.Incremental) // notice: use *current* volume
                {
                    HashAlgorithm = setTOC.HashAlgorithm,
                    Description = setTOC.Description,
                    BlockSize = setTOC.BlockSize,
                    ContinuedFromPrevVolume = contFromPrevVolume && (m_setTOCs.Count > 0), // the very first set may not be continued
                }
            );

            MakeLastSetCurrent();
        }
        public void CloneCurrentSetTOC(bool contFromPrevVolume = false) => CloneSetTOC(CurrentSetIndex, contFromPrevVolume);

        // Deep copy the content of the given TOC to this TOC, replacing the whole content of this TOC
        public void CopyFrom(TapeTOC toc) // Replaces the whole content -> use with CAUTION!
        {
            m_setTOCs.Clear();
            foreach (var setTOC in toc) // deep copy the sets, so that modifications to the original toc don't affect this toc
                m_setTOCs.Add(new TapeSetTOC(setTOC));

            m_nextUID = toc.m_nextUID;
            Description = toc.Description;
            CreationTime = toc.CreationTime;
            LastSaveTime = toc.LastSaveTime;
            Volume = toc.Volume;
            ContinuedOnNextVolume = toc.ContinuedOnNextVolume;

            MakeLastSetCurrent();
        }

        public void EmptyCurrentSet() => CurrentSetTOC.Clear(); // removes all file entries from the current set -> use with CAUTION!

        public bool RemoveLastEmptySet() // can only remove the last set, and only if it's empty
        {
            if (m_setTOCs.Count > 0 && m_setTOCs.Last().Count == 0)
            {
                m_setTOCs.RemoveAt(m_setTOCs.Count - 1);
                MakeLastSetCurrent();
                return true;
            }
            return false;
        }

        public void RemoveSetsAfterCurrent() // Removes all sets after the current -> use with CAUTION!
        {
            if (m_currSetInternal == Count - 1)
                return; // current is the last set; nothing to do

            m_setTOCs.RemoveRange(m_currSetInternal + 1, Count - m_currSetInternal - 1);
            Debug.Assert(m_currSetInternal == Count - 1); // the current set is now the last set
        }

        public void RemoveAllSets() // CAUTION!
        {
            m_setTOCs.Clear();
            m_currSetInternal = 0;
        }

        public bool MarkCurrentSetIncremental(bool incremental = true) =>
            CurrentSetTOC.MarkIncremental(incremental && m_setTOCs.Count > 1); // the very first set shouldn't be incremental

        // serialization constructor
        private TapeTOC(TypeUID nextUID, List<TapeSetTOC> setTOCs)
        {
            m_nextUID = nextUID;
            m_setTOCs = setTOCs;
        }

        #region ITapeSerializable

        public void SerializeTo(TapeSerializer serializer)
        {
            serializer.SerializeSignature();

            serializer.Serialize((ulong)m_nextUID);
            serializer.Serialize<List<TapeSetTOC>, TapeSetTOC>(m_setTOCs);
            serializer.Serialize(Description);
            serializer.Serialize(CreationTime);
            serializer.Serialize(LastSaveTime = DateTime.Now);
            serializer.Serialize(Volume);
            serializer.Serialize(ContinuedOnNextVolume);
        }

        public static ITapeSerializable? ConstructFrom(TapeDeserializer deserializer)
        {
            if (!deserializer.ValidateSignature())
                return null;

            TypeUID nextUID = (TypeUID)deserializer.DeserializeUInt64();
            if (nextUID == 0UL) // invalid UID
                return null;

            var setTOCs = deserializer.Deserialize<List<TapeSetTOC>, TapeSetTOC>();

            return new TapeTOC(nextUID, setTOCs)
            {
                Description = deserializer.DeserializeString(),
                CreationTime = deserializer.DeserializeDateTime(),
                LastSaveTime = deserializer.DeserializeDateTime(),
                Volume = deserializer.DeserializeInt32(),
                ContinuedOnNextVolume = deserializer.DeserializeBoolean(),
            };
        }
        
        #endregion // ITapeSerializable


        #region IEnumerable<TapeSetTOC> 

        // Implementation of the generic IEnumerable<T> interface
        public IEnumerator<TapeSetTOC> GetEnumerator()
        {
            return m_setTOCs.GetEnumerator();
            /*
            foreach (var entry in m_setTOCs)
            {
                yield return entry;
            }
            */
        }

        // Explicit implementation of the non-generic IEnumerable interface
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator(); // Just call the generic version
        }
        
        #endregion IEnumerable<TapeSetTOC>


        #region *** File selection methods ***

        // Find the latest non-incremental set -- returns internal index
        private int LastNonIncSetInternal
        {
            get
            {
                // check starting from current down to oldest
                for (int i = m_currSetInternal; i >= 0; i--)
                {
                    if (!m_setTOCs[i].Incremental)
                    {
                        if (m_setTOCs[i].ContinuedFromPrevVolume)
                        {
                            // if a continued set, then also include the previous set, from the previous volume
                            Debug.Assert(i > 0); // the very first set cannot be continued
                            i--;
                        }
                        return i;
                    }
                }

                return 0; // if all sets are marked incremental, use the oldest set
            }
        }

        public int LastNonIncSet => InternalToSetIndex(LastNonIncSetInternal);

        public bool IsFileUptodateInc(FileInfo fileInfo)
        {
            // check if the same or newer version of the file is already backed up in the current set
            //  or in any of the previous incremental sets

            int firstNonInc = LastNonIncSetInternal; // find the latest non-incremental set

            for (int i = m_currSetInternal; i >= firstNonInc; i--)
            {
                if (m_setTOCs[i].Any(tfi => tfi.SameFileName(fileInfo) && tfi.FileDescr.LastWriteTime >= fileInfo.LastWriteTime))
                    return true;
            } // for i
            return false;
        }

        /// <summary>
        /// Considering incremental sets, select files from the current and previous set(s)
        /// that pass the given filter. A <c>null</c> filter means consider all files.
        /// Returns an array of lists from newest to oldest set in the incremental chain.
        /// A <c>null</c> list entry means all files from the corresponding set.
        /// </summary>
        public List<TapeFileInfo>?[] SelectFiles(bool incremental, ITapeFileFilter? filter)
        {
            if (!incremental || !CurrentSetTOC.Incremental) // non-incremental case
            {
                return CurrentSetTOC.ContinuedFromPrevVolume && m_currSetInternal > 0 ?
                    [ CurrentSetTOC.SelectFiles(filter),
                        m_setTOCs[m_currSetInternal - 1].SelectFiles(filter) ] :
                    [ CurrentSetTOC.SelectFiles(filter) ];
            }
            else // go down the incremental chain
            {
                int firstNonInc = LastNonIncSetInternal; // find the latest non-incremental set

                Debug.Assert(firstNonInc <= m_currSetInternal);

                var filesSelected = new List<TapeFileInfo>?[m_currSetInternal - firstNonInc + 1];
                HashSet<string> alreadySelected = new(StringComparer.OrdinalIgnoreCase); // to look up in already selected files

                for (int i = m_currSetInternal; i >= firstNonInc; i--)
                {
                    // use LinkedList since we will be removing elements from the middle
                    var filesToCheck = m_setTOCs[i].SelectFilesAsLinkedList(filter); // never returns null

                    // now remove all files that are already selected in the newer sets
                    //  iterate over filesToCheck backwards since we will be removing elements
                    for (var nodeToCheck = filesToCheck.Last; nodeToCheck != null;)
                    {
                        var tfiToCheck = nodeToCheck.Value;
                        var nodeToCheckNext = nodeToCheck.Previous; // save the next node before possibly removing

                        if (!alreadySelected.Add(tfiToCheck.FileDescr.FullName))
                        {
                            // Add() returns false if file is in alreadySelected -> remove it
                            filesToCheck.Remove(nodeToCheck);
                        }
                        nodeToCheck = nodeToCheckNext;
                    }
                    // the files finally selected from m_setTOCs[i]:
                    filesSelected[m_currSetInternal - i] = (filesToCheck.Count == m_setTOCs[i].Count) ?
                        null /* null means all files in set */ : [.. filesToCheck];
                } // for i

                return filesSelected;
            }
        }

        /// <summary>
        /// Combines two arrays of tape file selections returned by SelectFiles()
        /// </summary>
        /// <param name="selA">This selection was peformed for the set index idxB.</param>
        /// <param name="idxA">The set index for which selA was selected</param>
        /// <param name="selB">This selection was performed for the set index idxB</param>
        /// <param name="idxB">The set index for which selB was selected</param>
        /// <returns>An array containing the combined tape file selections from both input arrays.</returns>
        /// <remarks>selA and selB are not modeified</remarks>
        // Notice selection arrays run the index from newer down to older -- the revers of TOC indexation
        // idxA:                                       v
        // selA:             oldest -> [set2] [set1] [set0] <- latest
        // selB:      oldest -> [set2] [set1] [set0] <- latest
        // idxB:                                ^
        public List<TapeFileInfo>?[] CombineSelectedFiles(List<TapeFileInfo>?[] selA, int idxA,
            List<TapeFileInfo>?[] selB, int idxB)
        {
            // ensure the indexes are of the same indexation system
            int intA = SetIndexToInternal(idxA);
            int intB = SetIndexToInternal(idxB);

            // pick as A the selection that starts with the newer set
            if (intA < intB)
            {
                (intB, intA) = (intA, intB);
                //(idxB, idxA) = (idxA, idxB); // not needed since we don't use idxA and idxB anymore
                (selB, selA) = (selA, selB);
            }
            Debug.Assert(intA >= intB); // now selA is the selection starting with the earlier set
            
            int bBegin = intA - intB; // the begin index of selB in selA's indexation
            int bEnd = bBegin + selB.Length - 1; // the end index of selB in selA's indexation
            int abEnd = int.Max(selA.Length - 1, bEnd); // the end index of the combined selection in selA's indexation

            var selAandB = new List<TapeFileInfo>?[abEnd + 1];

            // First, copy the sets from A's overhang over B (if any) since they are not affected by the combination
            //  selAandB follows selA's indexation
            //  i is in selA's (and selAandB's) indexation, and i - bBegin is in selB's indexation
            for (int i = 0; i < int.Min(selA.Length, bBegin); i++)
                selAandB[i] = selA[i];

            // Next, feel the gap between the end of selA and the begin of selB (if any) with []
            for (int i = selA.Length; i < bBegin; i++)
                selAandB[i] = []; // empty selection since selA doesn't cover this part, and selB starts later

            // Next, combine the overlapping part of A and B (if any)
            if (bBegin < selA.Length) // if there is an overlapping part between selA and selB
            {
                //  i is in selA's (and selAandB's) indexation, and i - bBegin is in selB's indexation
                for (int i = bBegin; i <= int.Min(bEnd, selA.Length - 1); i++)
                {
                    if (selA[i] == null || selB[i - bBegin] == null)
                    {
                        // if either selection is "all files", then the combined selection is also "all files"
                        selAandB[i] = null;
                    }
                    else
                    {
                        Debug.Assert(selA[i] != null && selB[i - bBegin] != null);
                        // combine the two selections by taking the union of the two lists of files
                        //  Notice: the compiler cannot null-check indexed array elements, hence '!'
                        var combined = new List<TapeFileInfo>(selA[i]!);
                        var combinedNames = new HashSet<string>(selA[i]!.Select(t => t.FileDescr.FullName), StringComparer.OrdinalIgnoreCase);
                        foreach (var tfi in selB[i - bBegin]!)
                        {
                            if (combinedNames.Add(tfi.FileDescr.FullName))
                                combined.Add(tfi);
                        }
                        selAandB[i] = combined;
                    }
                }

                // Finally, determine if A or B has an overhang over the other in the older direction
                //  and copy it since there's no combination
                if (bEnd >= selA.Length) // B has an overhang over A in the older direction
                {
                    for (int i = selA.Length; i <= bEnd; i++)
                        selAandB[i] = selB[i - bBegin]; // copy from B
                }
                else // A has an overhang over B in the older direction (or both end)
                {
                    for (int i = bEnd + 1; i <= selA.Length - 1; i++)
                        selAandB[i] = selA[i]; // copy from A
                }
            }
            else // if there is no overlap between selA and selB...
            {
                // ...then just copy from B
                for (int i = bBegin; i <= bEnd; i++)
                    selAandB[i] = selB[i - bBegin];
            }

            return selAandB;
        }

        /// <summary>
        /// Selects files from multiple backup sets, resolving incremental chains as needed,
        ///  and returns a combined array ready for
        ///  <see cref="TapeFileRestoreBaseAgent.RestoreFilesFromCurrentSetDown"/>.
        /// </summary>
        /// <param name="incremental">Whether to traverse each set's incremental chain.</param>
        /// <param name="checkedFilesBySet">
        /// Dictionary mapping 1-based set indexes to the files selected by the user.
        ///  A <c>null</c> value means "all files in set"; a non-null list means only those files
        ///  (matched by <see cref="TapeFileDescriptor.FullName"/>, case-insensitive).
        ///  Absent keys are not included in the result.
        /// </param>
        /// <returns>
        /// An array of file lists from newest to oldest set, compatible with
        ///  <see cref="TapeFileRestoreBaseAgent.RestoreFilesFromCurrentSetDown"/>.
        ///  A <c>null</c> entry means all files from the corresponding set.
        /// </returns>
        public List<TapeFileInfo>?[] SelectFilesFromSets(
            bool incremental,
            Dictionary<int, IReadOnlyList<TapeFileInfo>?> checkedFilesBySet)
        {
            if (checkedFilesBySet.Count == 0)
                return [];

            // Normalize to standard indexes, sort newest-first
            var stdIndexes = checkedFilesBySet.Keys
                .Select(SetIndexToStd)
                .Distinct()
                .OrderByDescending(i => i)
                .ToList();

            int newestIdx = stdIndexes[0];
            int savedCurrSet = m_currSetInternal; // save — SelectFiles modifies CurrentSetIndex

            try
            {
                // Select files for the newest set
                CurrentSetIndex = newestIdx;
                var combined = SelectFilesForOneSet(newestIdx, incremental, checkedFilesBySet[newestIdx]);

                // Combine with selections from remaining (older) sets
                for (int i = 1; i < stdIndexes.Count; i++)
                {
                    int idx = stdIndexes[i];
                    CurrentSetIndex = idx;
                    var selected = SelectFilesForOneSet(idx, incremental, checkedFilesBySet[idx]);
                    combined = CombineSelectedFiles(combined, newestIdx, selected, idx);
                }

                return combined;
            }
            finally
            {
                m_currSetInternal = savedCurrSet; // restore — this method is side-effect-free
            }
        }

        /// <summary>
        /// Selects files from a single set, optionally resolving its incremental chain.
        ///  <paramref name="checkedFiles"/> == null means "all files" (delegates to
        ///  <see cref="SelectFiles(bool, ITapeFileFilter?)"/>).
        ///  Otherwise, only files whose <see cref="TapeFileDescriptor.FullName"/> matches
        ///  one of the checked entries are kept.
        /// </summary>
        /// <remarks>Assumes <see cref="CurrentSetIndex"/> is already set to
        ///  <paramref name="setIndex"/>.</remarks>
        private List<TapeFileInfo>?[] SelectFilesForOneSet(
            int setIndex, bool incremental, IReadOnlyList<TapeFileInfo>? checkedFiles)
        {
            // "All files" — delegate to the filter-based path (null filter = all files)
            if (checkedFiles is null)
                return SelectFiles(incremental, filter: null);

            Debug.Assert(CurrentSetIndex == setIndex);
            var setTOC = this[setIndex];

            // Build a HashSet of wanted filenames for fast lookup
            var wantedNames = new HashSet<string>(
                checkedFiles.Select(f => f.FileDescr.FullName),
                StringComparer.OrdinalIgnoreCase);

            if (!incremental || !setTOC.Incremental) // non-incremental case
            {
                // Directly pick matching TapeFileInfo entries from the set(s)
                var picked = PickFilesByName(setTOC, wantedNames);

                if (setTOC.ContinuedFromPrevVolume && SetIndexToInternal(setIndex) > 0)
                {
                    // Also pick from the continuation set on the previous volume
                    var prevSet = m_setTOCs[SetIndexToInternal(setIndex) - 1];
                    return [picked, PickFilesByName(prevSet, wantedNames)];
                }
                return [picked];
            }
            else // incremental chain — integrate wanted-name filtering into chain traversal
            {
                int firstNonInc = LastNonIncSetInternal;
                Debug.Assert(firstNonInc <= m_currSetInternal);

                var filesSelected = new List<TapeFileInfo>?[m_currSetInternal - firstNonInc + 1];
                var alreadySelected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = m_currSetInternal; i >= firstNonInc; i--)
                {
                    var setFiles = m_setTOCs[i];
                    List<TapeFileInfo>? picked = null;

                    foreach (var tfi in setFiles)
                    {
                        string name = tfi.FileDescr.FullName;
                        // Only keep files the user wants AND that haven't been selected
                        //  from a newer set already (incremental dedup)
                        if (wantedNames.Contains(name) && alreadySelected.Add(name))
                        {
                            picked ??= [];
                            picked.Add(tfi);
                        }
                    }

                    // "null means all files in set" when every file in the set was picked
                    filesSelected[m_currSetInternal - i] =
                        (picked is not null && picked.Count == setFiles.Count) ? null : picked;
                }

                return filesSelected;
            }
        }

        /// <summary>
        /// Picks <see cref="TapeFileInfo"/> entries from a <see cref="TapeSetTOC"/> whose
        ///  <see cref="TapeFileDescriptor.FullName"/> is in <paramref name="wantedNames"/>.
        ///  Returns <c>null</c> when all files match ("null means all" convention).
        /// </summary>
        private static List<TapeFileInfo>? PickFilesByName(TapeSetTOC setTOC,
            HashSet<string> wantedNames)
        {
            List<TapeFileInfo>? picked = null;
            foreach (var tfi in setTOC)
            {
                if (wantedNames.Contains(tfi.FileDescr.FullName))
                {
                    picked ??= [];
                    picked.Add(tfi);
                }
            }
            // null means all files in set
            return (picked is not null && picked.Count == setTOC.Count) ? null : picked;
        }

        // Compute total file size on tape -- considering block sizes per set
        // onVolumeOnly : if true, only compute for sets on the current volume; otherwise, compute for all sets
        public long ComputeTotalFileSizeOnTape(uint defaultBlockSize = 0, bool onVolumeOnly = true)
        {
            if (Count == 0)
                return 0L;

            long totalSize = 0L;
            if (onVolumeOnly)
            {
                for (int i = FirstSetInternalOnVolume; i <= LastSetInternalOnVolume; i++)
                {
                    totalSize += m_setTOCs[i].ComputeTotalFileSizeOnTape(defaultBlockSize);
                }
            }
            else
            {
                foreach (var setTOC in m_setTOCs)
                {
                    totalSize += setTOC.ComputeTotalFileSizeOnTape(defaultBlockSize);
                }
            }

            return totalSize;
        }

#if OLD
        // Considering incremental sets, select files from the current and previous set(s)
        //  Reurn an array of lists of files from the latest set, 2nd latest, 3rd latest, etc.
        public List<TapeFileInfo>[] SelectFilesInc()
        {
            int firstNonInc = LastNonIncSetInternal; // find the latest non-incremental set

            if (firstNonInc == m_currSetInternal) // the current is either non-incremental or only one set
                return [ CurrentSetTOC.SelectFiles() ];
            else
            {
                Debug.Assert(firstNonInc < m_currSetInternal);

                var filesSelected = new List<TapeFileInfo>[m_currSetInternal - firstNonInc + 1];

                for (int i = m_currSetInternal; i >= firstNonInc; i--)
                {
                    // use LinkedList since we will be removing elements from the middle
                    var filesToCheck = m_setTOCs[i].SelectFilesAsLinkedList();
                    // now remove all files that are already selected in the newer sets
                    for (int j = i + 1; j <= m_currSetInternal; j++) // iterate over newer sets
                    {
                        // iterate over filesToCheck backwards since we will be removing elements
                        for (var nodeToCheck = filesToCheck.Last; nodeToCheck != null;)
                        {
                            var tfiToCheck = nodeToCheck.Value;
                            var nodeNextToCheck = nodeToCheck.Previous; // save the next node before possibly removing
                            // find if tfiCheck.FileDescr.FullName is in filesSelected[j]
                            foreach (var tfi in filesSelected[j])
                            {
                                // find out if tfiCheck and tfi refer to the same file -- by comparing full file names
                                if (tfiToCheck.SameFileName(tfi))
                                {
                                    filesToCheck.Remove(nodeToCheck);
                                    break;
                                }
                            }
                            nodeToCheck = nodeNextToCheck;
                        }
                    } // for j
                    filesSelected[m_currSetInternal - i] = [.. filesToCheck]; // same as filesToCheck.ToList();
                } // for i

                return filesSelected;
            }
        }
#endif
        #endregion // File selection methods

    } // class TapeTOC

} // namespace TapeNET