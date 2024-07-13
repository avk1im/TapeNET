using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Win32.Foundation;


namespace TapeNET
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

        public readonly bool SameFileName(TapeFileDescriptor other) => FullName.Equals(other.FullName, StringComparison.OrdinalIgnoreCase);

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
    }

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
        public bool SameFileName(FileInfo fileInfo) => FileDescr.FullName.Equals(fileInfo.FullName, StringComparison.OrdinalIgnoreCase);

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

        // serailize only minimal information necessary to check file match on tape 
        public void SerializeHeaderTo(TapeSerializer serializer)
        {
            serializer.SerializeSignature();
            serializer.Serialize((ulong)UID);
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


    // While the class is accessible externally, new instances only created via class TapeTOC.AddNewSetTOC()
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
        public DateTime CreationTime { get; init; } = DateTime.Now;
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

        // ITapeSerializable {
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
        // } ITapeSerializable


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
            // CreationTime = toc.CreationTime; // CreationTime is init only
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

        private static bool FileMatchesRegexPattern(string fullFileName, string pattern)
        {
            return Regex.IsMatch(fullFileName, pattern, RegexOptions.IgnoreCase);
        }

        private static bool FileMatchesRegexPatterns(string fullFileName, IEnumerable<string> patterns)
        {
            return patterns.Any(pattern => FileMatchesRegexPattern(fullFileName, pattern));
        }

        private static string FromFilePatternToRegexPattern(string pattern)
        {
            // Convert the pattern to a regular expression
            //  Consider that if the pattern ends with \, automatically treat it as
            //  "select all files from that directory", that is as "*.*"
            if (pattern.EndsWith('\\'))
                pattern += "*.*";

            // Replace directory separators with the regex equivalent
            pattern = pattern.Replace("\\", "\\\\");

            // Escape special regex characters, then replace wildcard characters with their regex equivalents
            pattern = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");

            // No ^ or $ to match the start or end of the string,
            //  since we want to match the specified file pattern anywhere in the string
            return pattern;
        }

        private static IEnumerable<string> FromFilePatternsToRegexPatterns(List<string> patterns)
        {
            // Convert the list of patterns to a list of regular expressions
            return patterns.Select(FromFilePatternToRegexPattern);
        }

        // Returns a list of TapeFileInfo objects that match the given file patterns
        //  Null patterns means select all files
        //  Empty patterns means select no files
        //  If returns null, it means all files from the set
        public List<TapeFileInfo>? SelectFiles(List<string>? filePatterns = null)
        {
            if (filePatterns == null)
                return null; // null means all files in set
            if (filePatterns.Count == 0)
                return []; // empty list means no files

            // Convert the list of patterns to a list of regular expressions
            //  Consider that if the patern ends with '', automatically treat it as
            //  "select all files from that directory", that is as "*.*"
            var patterns = FromFilePatternsToRegexPatterns(filePatterns).ToList(); // use ToList() to cache the results!

            // Iterate over all files in the current set and add those that match at least one of the patterns to the list
            List<TapeFileInfo> filesSelected = [];

            foreach (var tfi in this)
            {
                if (FileMatchesRegexPatterns(tfi.FileDescr.FullName, patterns))
                    filesSelected.Add(tfi);
            }

            return (filesSelected.Count == Count) ? null /* null means all files in set */ : filesSelected;
        }

        // Returns a linked list of TapeFileInfo objects that match the given file patterns
        //  Null patterns means select all files
        //  Empty patterns means select no files
        //  Doesn't return "null means all files" shortcut since the list might need editing
        internal LinkedList<TapeFileInfo> SelectFilesAsLinkedList(List<string>? filePatterns = null)
        {
            if (filePatterns == null)
                return new(m_tapeFileInfos); // list all files -- don't use "null means all files" shortcut
            if (filePatterns.Count == 0)
                return []; // empty list means no files

            // Convert the list of patterns to a list of regular expressions
            //  Consider that if the patern ends with '', automatically treat it as
            //  "select all files from that directory", that is as "*.*"
            var patterns = FromFilePatternsToRegexPatterns(filePatterns).ToList(); // use ToList() to cache the results!

            // Iterate over all files in the current set and add those that match at least one of the patterns to the list
            LinkedList<TapeFileInfo> filesSelected = [];

            foreach (var tfi in this)
            {
                if (FileMatchesRegexPatterns(tfi.FileDescr.FullName, patterns))
                    filesSelected.AddLast(tfi);
            }

            return filesSelected; // don't use "null means all files" shortcut since the list might need editing
        }

    } // class TapeSetTOC

    // manages the list of SetTOCs
    public class TapeTOC : ITapeSerializable, IEnumerable<TapeSetTOC>
    {
        private readonly List<TapeSetTOC> m_setTOCs;
        private TypeUID m_nextUID;

        public TapeTOC()
        {
            m_setTOCs = [];
            m_nextUID = 1UL; // 0UL is an invalid or "not set" value for UID
        }
        public TapeTOC(TapeTOC toc) : this()
        {
            CopyFrom(toc);
        }

        internal TypeUID GenerateUID() => m_nextUID++;
        public string Description { get; set; } = string.Empty;
        public DateTime CreationTime { get; init; } = DateTime.Now;
        public DateTime LastSaveTime { get; internal set; } = DateTime.Now;

        public int Volume { get; internal set; } = 1; // volume indexing starts from 1
        public bool ContinuedOnNextVolume { get; internal set; } = false;

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
        public bool IsCurrentSetContOnNextVolume => ContinuedOnNextVolume && IsCurrentSetLast;
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
                    ContinuedFromPrevVolume = contFromPrevVolume && (m_setTOCs.Count > 0), // the very first set may not be continued
                }
            );

            MakeLastSetCurrent();
        }
        public void CloneCurrentSetTOC(bool contFromPrevVolume = false) => CloneSetTOC(CurrentSetIndex, contFromPrevVolume);

        public void CopyFrom(TapeTOC toc) // Replaces the whole content -> use with CAUTION!
        {
            m_setTOCs.Clear();
            foreach (var setTOC in toc) // deep copy the sets, so that modifications to the original toc don't affect this toc
                m_setTOCs.Add(new TapeSetTOC(setTOC));

            m_nextUID = toc.m_nextUID;
            Description = toc.Description;
            // CreationTime = toc.CreationTime; // CreationTime is init only
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
            Debug.Assert(m_currSetInternal == Count -1); // the current set is now the last set
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

        // ITapeSerializable {
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
            if (setTOCs.Count == 0)
                return null;
            return new TapeTOC(nextUID, setTOCs)
            {
                Description = deserializer.DeserializeString(),
                CreationTime = deserializer.DeserializeDateTime(),
                LastSaveTime = deserializer.DeserializeDateTime(),
                Volume = deserializer.DeserializeInt32(),
                ContinuedOnNextVolume = deserializer.DeserializeBoolean(),
            };
        }
        // } ITapeSerializable


        // IEnumerable<TapeSetTOC> {
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
        // } IEnumerable<TapeSetTOC>


        // File selection methods

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

       // Considering incremental sets, select files from the current and previous set(s) that match the given patterns
        //  null patterns means consider all files
        //  Reurn an array of lists of files from the latest set, 2nd latest, 3rd latest, etc.
        //  A null list means all files from the corresponding set
        public List<TapeFileInfo>?[] SelectFiles(bool incremental, List<string>? filePatterns = null)
        {
            if (!incremental || !CurrentSetTOC.Incremental) // non-incremental case
            {
                return CurrentSetTOC.ContinuedFromPrevVolume ?
                    [ CurrentSetTOC.SelectFiles(filePatterns), m_setTOCs[m_currSetInternal - 1].SelectFiles(filePatterns) ] :
                    [ CurrentSetTOC.SelectFiles(filePatterns) ];
            }
            else
            {
                int firstNonInc = LastNonIncSetInternal; // find the latest non-incremental set

                Debug.Assert(firstNonInc <= m_currSetInternal);

                var filesSelected = new List<TapeFileInfo>?[m_currSetInternal - firstNonInc + 1];
                HashSet<string> alreadySelected = new(StringComparer.OrdinalIgnoreCase); // to look up in already selected files

                for (int i = m_currSetInternal; i >= firstNonInc; i--)
                {
                    // use LinkedList since we will be removing elements from the middle
                    var filesToCheck = m_setTOCs[i].SelectFilesAsLinkedList(filePatterns); // never returns null

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

    } // class TapeTOC

} // namespace TapeNET