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

    /// <summary>Hash algorithms supported for per-file integrity verification on tape.</summary>
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

    /// <summary>
    /// Lightweight mirror of <see cref="FileSystemInfo"/> properties, avoiding access to the actual file.
    /// <para>Used instead of <see cref="FileInfo"/> because setting properties on a <c>FileInfo</c>
    ///  instance would attempt to modify the real file on disk.</para>
    /// </summary>
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

    /// <summary>
    /// On-tape file record — serves as both a TOC entry and an on-tape file header.
    /// <para>Each instance carries a unique <see cref="UID"/>, the tape <see cref="Address"/>,
    ///  a <see cref="TapeFileDescriptor"/>, and an optional integrity <see cref="Hash"/>.
    ///  Implements <see cref="ITapeSerializable"/> for full and header-only serialization.</para>
    /// </summary>
    public class TapeFileInfo(TypeUID UID, TapeAddress address, TapeFileDescriptor fileDescr) : ITapeSerializable
    {
        public TypeUID UID { get; } = UID;
        /// <summary>Tape address (block + offset) where this file's data begins.</summary>
        public TapeAddress Address { get; } = address;
        /// <summary>Block number where this file's data begins. Convenience accessor for <see cref="Address"/>.Block.</summary>
        [Obsolete("Use Address")]
        public long Block => Address.Block;
        public TapeFileDescriptor FileDescr { get; } = fileDescr;
        internal byte[]? Hash { get; set; } = null;
        /// <summary>
        /// Actual on-tape size (header + blob body) in bytes, excluding block-alignment padding.
        ///  Set post-construction on packed-path commit; zero for legacy aligned files or when
        ///  not yet committed. Required for packed-set restore read-window sizing since files
        ///  are packed back-to-back with no per-file delimiters.
        /// </summary>
        internal long SizeOnTape { get; set; } = 0L;

        /// <summary>
        /// Per-file codec used to compress this file's body on tape.
        ///  <see cref="TapeFileCodec.Stored"/> means the body is uncompressed (passthrough or
        ///  auto-store fallback); <see cref="TapeFileCodec.Zstd"/> means ZSTD-compressed.
        ///  Restore reads this flag to decide whether to wrap the read stream with a decompressor.
        /// </summary>
        internal TapeFileCodec Codec { get; set; } = TapeFileCodec.Stored;

        /// <summary>Convenience constructor for transition: wraps a bare block number in a <see cref="TapeAddress"/> with zero offset.</summary>
        [Obsolete("Use TapeAddress address instead of long block")]
        public TapeFileInfo(TypeUID UID, long block, TapeFileDescriptor fileDescr)
            : this(UID, new TapeAddress(block, 0), fileDescr)
        { }

        public TapeFileInfo(TypeUID UID, TapeAddress address, FileInfo fileInfo)
            : this(UID, address, new TapeFileDescriptor(fileInfo))
        { }
        [Obsolete("Use TapeAddress address instead of long block")]
        public TapeFileInfo(TypeUID UID, long block, FileInfo fileInfo)
            : this(UID, new TapeAddress(block, 0), new TapeFileDescriptor(fileInfo))
        { }

        public bool SameFileName(TapeFileInfo other) => FileDescr.SameFileName(other.FileDescr);
        public bool SameFileName(FileInfo fileInfo) => FileDescr.SameFileName(fileInfo.FullName);

        public bool IsValid => UID != 0 && !string.IsNullOrEmpty(FileDescr.FullName);

        // ITapeSerializable {
        public void SerializeTo(TapeSerializer serializer)
        {
            serializer.SerializeSignature();
            serializer.Serialize((ulong)UID);
            serializer.Serialize(Address);
            serializer.Serialize(FileDescr);
            serializer.SerializeNullableWithLength(Hash);
            serializer.Serialize(SizeOnTape);
            serializer.Serialize((byte)Codec);
        }
        public static ITapeSerializable? ConstructFrom(TapeDeserializer deserializer)
        {
            if (!deserializer.ValidateSignature())
                return null; // version mismatch

            var UID = (TypeUID)deserializer.DeserializeUInt64();
            var address = deserializer.DeserializeTapeAddress();
            var fileDescr = deserializer.DeserializeFileDescriptor();

            var hash       = deserializer.DeserializeNullableBytesWithLength();
            var sizeOnTape = deserializer.DeserializeInt64();
            // Codec byte was added in v2 (compression support); default to Stored for older tapes.
            var codecBytes = deserializer.DeserializeBytes(1);
            var codec      = (codecBytes != null) ? (TapeFileCodec)codecBytes[0] : TapeFileCodec.Stored;

            return new TapeFileInfo(UID, address, fileDescr)
            {
                Hash       = hash,
                SizeOnTape = sizeOnTape,
                Codec      = codec,
            };
        }
        // } ITapeSerializable

        public int EstimateSerializedSize()
        {
            // Signature: 2 bytes + Version: 2 bytes
            int size = TapeSerializer.Signature.Length + sizeof(ushort);
            // UID: 8 bytes (ulong)
            size += sizeof(TypeUID);
            // Address: Block (8 bytes long) + Offset (4 bytes uint)
            size += sizeof(long) + sizeof(uint);
            // FileDescr
            size += FileDescr.EstimateSerializedSize();
            // Hash: length prefix (4 bytes) + optional bytes
            size += sizeof(int) + (Hash?.Length ?? 0);
            // SizeOnTape: 8 bytes (long)
            size += sizeof(long);
            // Codec: 1 byte
            size += sizeof(byte);

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

    
    /// <summary>
    /// Lightweight bundle of <see cref="TapeSetTOC"/> creation metadata, used to seed a new
    ///  set on a continuation volume without cloning the previous-volume set instance.
    /// </summary>
    public record TapeSetTOCParams(
        string Description,
        TapeHashAlgorithm HashAlgorithm,
        uint BlockSize,
        bool Incremental,
        int Capacity = 0,
        TapeCompression Compression = TapeCompression.None,
        int CompressionLevel = ZstdLevel.Default);

    /// <summary>
    /// Table of contents for a single backup set — an <see cref="IReadOnlyList{TapeFileInfo}"/>
    ///  with per-set metadata (description, hash algorithm, block size, incremental flag, volume).
    /// <para>New instances are created only via <see cref="TapeTOC.AddNewSetTOC"/>.</para>
    /// </summary>
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
        public uint BlockSize { get; set; } = 0;
        public DateTime LastSaveTime { get; internal set; } = DateTime.Now;
        public TapeHashAlgorithm HashAlgorithm { get; set; } = TapeHashAlgorithm.Crc32;
        /// <summary>Per-set compression mode. Mirrors <see cref="HashAlgorithm"/> in lifecycle and UI placement.</summary>
        public TapeCompression Compression { get; set; } = TapeCompression.None;
        /// <summary>ZSTD compression level (1–19). Only used when <see cref="Compression"/> is <see cref="TapeCompression.Software"/>.</summary>
        public int CompressionLevel { get; set; } = ZstdLevel.Default;
        public bool Incremental { get; internal set; } = false;
        internal bool MarkIncremental(bool incremental = true) // can only change Incremental if the set is empty
        {
            if (Count == 0)
                Incremental = incremental;
            return Incremental;
        }
        public int Volume { get; internal set; } = 0;
        public bool ContinuedFromPrevVolume { get; init; } = false;

        /// <summary>
        /// Snapshots this set's metadata (description, hash, block size, incremental, capacity)
        ///  into a <see cref="TapeSetTOCParams"/> bundle suitable for seeding a continuation
        ///  set on the next volume via <see cref="TapeTOC.AddContinuationSetTOC"/>.
        /// </summary>
        public TapeSetTOCParams ToParams() =>
            new(Description, HashAlgorithm, BlockSize, Incremental, Capacity, Compression, CompressionLevel);

        // deserialization constructor
        private TapeSetTOC(List<TapeFileInfo> fileInfos) => m_tapeFileInfos = fileInfos;

        #region ITapeSerializable
        public void SerializeTo(TapeSerializer serializer)
        {
            serializer.SerializeSignature();

            serializer.Serialize<List<TapeFileInfo>, TapeFileInfo>(m_tapeFileInfos);
            serializer.Serialize(Description);
            serializer.Serialize(CreationTime);
            serializer.Serialize(BlockSize);
            serializer.Serialize(LastSaveTime = DateTime.Now);
            serializer.Serialize((int)HashAlgorithm);
            serializer.Serialize(Incremental);
            serializer.Serialize(Volume);
            serializer.Serialize(ContinuedFromPrevVolume);
            serializer.Serialize((int)Compression);
            serializer.Serialize(CompressionLevel);
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
                BlockSize = deserializer.DeserializeUInt32(),
                LastSaveTime = deserializer.DeserializeDateTime(),
                HashAlgorithm = (TapeHashAlgorithm)deserializer.DeserializeInt32(),
                Incremental = deserializer.DeserializeBoolean(),
                Volume = deserializer.DeserializeInt32(),
                ContinuedFromPrevVolume = deserializer.DeserializeBoolean(),
                Compression = (TapeCompression)deserializer.DeserializeInt32(),
                CompressionLevel = deserializer.DeserializeInt32(),
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
            // A newly appended file may flip the layout from aligned to packed
            //  (or remain aligned), so the cached detection must be re-evaluated.
            if (m_isPackedLayout == false && tfi.Address.Offset != 0)
                m_isPackedLayout = true;
        }
        internal void CopyFrom(TapeSetTOC toc)
        {
            m_tapeFileInfos.Clear();
            m_tapeFileInfos.AddRange(toc.m_tapeFileInfos);
            Description = toc.Description;
            CreationTime = toc.CreationTime;
            BlockSize = toc.BlockSize;
            LastSaveTime = toc.LastSaveTime;
            HashAlgorithm = toc.HashAlgorithm;
            Incremental = toc.Incremental;
            Volume = toc.Volume;
            Compression = toc.Compression;
            CompressionLevel = toc.CompressionLevel;
            // ContinuedFromPrevVolume = toc.ContinuedFromPrevVolume; // set only during construction
            m_isPackedLayout = toc.m_isPackedLayout;
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

        /// <summary>
        /// Returns the total source size of all files in the set -- NOT the size on tape.
        /// </summary>
        public long TotalFileSize => m_tapeFileInfos.Sum(tfi => tfi.FileDescr.Length);

        /// <summary>
        /// Estimates the tape footprint of a single file under the legacy block-aligned
        ///  layout: file data + serialized header, rounded up to the next block boundary.
        /// <para>For packed sets, files share blocks, so per-file rounding is wrong;
        ///  use <see cref="ComputeTotalFileSizeOnTape(uint)"/> on the whole set instead.</para>
        /// </summary>
        public static long EstimateFileSizeOnTape(long fileLength, uint blockSize)
        {
            long rawSize = fileLength + TapeFileInfo.EstimateSerializedHeaderSize();
            if (blockSize <= 1)
                return rawSize;
            return (rawSize + blockSize - 1) / blockSize * blockSize;
        }

        // Cached dynamic detection of packed vs aligned layout, derived from file addresses.
        //  null = not yet determined; true = at least one file has Address.Offset != 0;
        //  false = all known files are block-aligned (legacy layout).
        private bool? m_isPackedLayout;

        // Invalidate cached layout flag when the file set is mutated.
        internal void InvalidateLayoutCache() => m_isPackedLayout = null;

        // Compute the size of all files in the set on tape, considering the block size.
        //  Detects packed-layout sets dynamically: if any file has a non-zero intra-block
        //  offset (Address.Offset != 0), the set is packed and files share blocks, so we
        //  only round the *total* of (file + header) sizes up to one block boundary
        //  rather than rounding each file individually. The detection result is cached.
        public long ComputeTotalFileSizeOnTape(uint defaultBlockSize = 0)
        {
            if (Count == 0)
                return 0L;
            uint blockSize = (BlockSize > 0) ? BlockSize : defaultBlockSize;

            // If we already know the set is packed, sum raw and round once.
            if (m_isPackedLayout == true)
                return RoundUpToBlock(SumRawFileSizes(), blockSize);

            // Otherwise walk once: detect packed-ness while computing the aligned total.
            //  As soon as we discover a non-zero offset, switch to packed accounting.
            long alignedTotal = 0L;
            long rawTotal     = 0L;
            bool packed       = false;
            foreach (var tfi in this)
            {
                // Use SizeOnTape when available (packed files with committed size);
                //  fall back to estimated size for legacy/aligned or uncommitted files.
                long raw = (tfi.SizeOnTape > 0)
                    ? tfi.SizeOnTape
                    : tfi.FileDescr.Length + TapeFileInfo.EstimateSerializedHeaderSize();
                rawTotal += raw;
                if (!packed && tfi.Address.Offset != 0)
                    packed = true;
                if (!packed)
                    alignedTotal += RoundUpToBlock(raw, blockSize);
            }

            m_isPackedLayout = packed;
            return packed ? RoundUpToBlock(rawTotal, blockSize) : alignedTotal;

            long SumRawFileSizes()
            {
                long sum = 0L;
                foreach (var tfi in this)
                {
                    // Use SizeOnTape when available (packed files with committed size);
                    //  fall back to estimated size for legacy/aligned or uncommitted files.
                    sum += (tfi.SizeOnTape > 0)
                        ? tfi.SizeOnTape
                        : tfi.FileDescr.Length + TapeFileInfo.EstimateSerializedHeaderSize();
                }
                return sum;
            }

            static long RoundUpToBlock(long size, uint blockSize)
                => (blockSize <= 1) ? size : (size + blockSize - 1) / blockSize * blockSize;
        }

    } // class TapeSetTOC


    /// <summary>
    /// Master table of contents — ordered list of <see cref="TapeSetTOC"/> instances with
    ///  UID generation, multi-volume tracking, and file selection across incremental chains.
    /// <para><b>Dual set indexation:</b></para>
    /// <code>
    /// Standard: 1, 2, 3, …, N   (1 = oldest, N = newest)
    /// Alternative: −(N−1), …, −1, 0  (0 = newest, −1 = second newest)
    /// Internal: 0, 1, …, N−1    (0 = oldest, used only inside this class)
    /// </code>
    /// </summary>
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

        /// <summary>Current volume number (1-based).</summary>
        public int Volume { get; internal set; } = 1; // volume indexing starts from 1
        /// <summary>Whether the last set continues on a subsequent volume.</summary>
        public bool ContinuedOnNextVolume { get; set; } = false;

        public int Count => m_setTOCs.Count;
        /// <summary>Returns the <see cref="TapeSetTOC"/> at <see cref="CurrentSetIndex"/>; lazily creates a first set if none exist.</summary>
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

        // Set index conversion helpers (see class-level doc for indexation scheme).
        //  Standard: 1..N,  Alternative: −(N−1)..0,  Internal: 0..N−1
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
        /// <summary>
        /// Current backup set index (standard form: 1 = oldest, <see cref="MaxSetIndex"/> = newest).
        /// <para>Accepts both standard (positive) and alternative (≤ 0) index forms on set.</para>
        /// </summary>
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
                if (Count == 0)
                    return 0;

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
                if (Count == 0)
                    return 0;

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

        /// <summary>
        /// Appends a new (or reuses the last empty) <see cref="TapeSetTOC"/> and makes it current.
        /// <para>The first set in a TOC is never marked incremental, regardless of the parameter.</para>
        /// </summary>
        public void AddNewSetTOC(int capacity = 0, bool incremental = false)
        {
            // if the last set is empty, use it and set its capacity to 'size'
            //  Do not use CurrentSetTOC since it can call AddNewSetTOC() recursively!
            if (m_setTOCs.Count > 0 && m_setTOCs.Last() is var last && last.Count == 0)
            {
                last.Volume = Volume;
                last.Capacity = capacity;
                last.Incremental = incremental && m_setTOCs.Count > 1; // the very first set shouldn't be incremental
                    // Count > 1 means "there's at least one other set before this empty one."
                    //  If Count == 1, the empty set IS the first set → not incremental.
            }
            else
                m_setTOCs.Add(new TapeSetTOC(Volume, capacity, incremental && m_setTOCs.Count > 0));
                    // Count > 0 means "there's at least one set already present before the one we're about to add."
                    //  If Count == 0, we're adding the very first set → not incremental.

            Debug.Assert(m_setTOCs.Count > 0);

            MakeLastSetCurrent();
        }

        /// <summary>
        /// Appends a fresh continuation <see cref="TapeSetTOC"/> seeded from
        ///  <paramref name="setParams"/> (no file cloning) and makes it current.
        /// <para>Used by multi-volume continuation: the previous-volume set instance is not
        ///  referenced — only its metadata is carried over via <see cref="TapeSetTOCParams"/>.
        ///  This is robust against the policy of removing the trailing empty set from the
        ///  full volume's TOC before saving (which would leave nothing valid to clone).</para>
        /// </summary>
        public void AddContinuationSetTOC(TapeSetTOCParams setParams, bool contFromPrevVolume = false)
        {
            m_setTOCs.Add(
                new TapeSetTOC(Volume, setParams.Capacity, setParams.Incremental) // notice: use *current* volume
                {
                    HashAlgorithm = setParams.HashAlgorithm,
                    Description = setParams.Description,
                    BlockSize = setParams.BlockSize,
                    Compression = setParams.Compression,
                    CompressionLevel = setParams.CompressionLevel,
                    ContinuedFromPrevVolume = contFromPrevVolume && (m_setTOCs.Count > 0), // the very first set may not be continued
                }
            );

            MakeLastSetCurrent();
        }

        /// <summary>Deep-copies all content from <paramref name="toc"/>, replacing everything in this instance.</summary>
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

        /// <summary>
        /// Replaces the current set with a fresh empty <see cref="TapeSetTOC"/>,
        ///  preserving the volume assignment. Guarantees a new object identity
        ///  so that consumers can detect the replacement via reference equality.
        /// </summary>
        public void ReplaceCurrentSetTOC(int capacity = 0, bool incremental = false)
        {
            m_setTOCs[m_currSetInternal] = new TapeSetTOC(Volume, capacity,
                incremental && m_setTOCs.Count > 1); // the very first set shouldn't be incremental
        }

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
            ContinuedOnNextVolume = false; // multi-volume continuation no longer valid

            Debug.Assert(m_currSetInternal == Count - 1); // the current set is now the last set
        }

        public void RemoveAllSets() // CAUTION!
        {
            m_setTOCs.Clear();
            ContinuedOnNextVolume = false; // multi-volume continuation no longer valid

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

        // Find the latest non-incremental set -- returns internal index.
        //  Walks back through the entire ContinuedFromPrevVolume chain so that
        //  a full backup spanning 3+ volumes is fully included.
        private int LastNonIncSetInternal
        {
            get
            {
                // check starting from current down to oldest
                for (int i = m_currSetInternal; i >= 0; i--)
                {
                    if (!m_setTOCs[i].Incremental)
                    {
                        // Walk back through the entire continuation chain (may span multiple volumes)
                        while (i > 0 && m_setTOCs[i].ContinuedFromPrevVolume)
                            i--;
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
        /// </summary>
        /// <returns>
        /// An array of lists of <see cref="TapeFileInfo"/> objects, from current (newest) to oldest set in the incremental chain.
        /// <para>A <c>null</c> list entry means all files from the corresponding set.</para>
        /// </returns>
        public List<TapeFileInfo>?[] SelectFiles(bool incremental, ITapeFileFilter? filter)
        {
            if (!incremental || !CurrentSetTOC.Incremental) // non-incremental case
            {
                // Walk back through the full multi-volume continuation chain
                List<List<TapeFileInfo>?> chain = [CurrentSetTOC.SelectFiles(filter)];
                int idx = m_currSetInternal;
                while (idx > 0 && m_setTOCs[idx].ContinuedFromPrevVolume)
                {
                    idx--;
                    chain.Add(m_setTOCs[idx].SelectFiles(filter));
                }
                return [.. chain];
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
        /// Returns per-set file counts from a pre-assembled <paramref name="combined"/> selection
        ///  array (as produced by <see cref="SelectFilesFromSets"/>), plus the overall total.
        ///  A <c>null</c> entry in <paramref name="combined"/> means "all files in set", so the
        ///  file count and size come from the corresponding <see cref="SetTOC"/>.
        /// </summary>
        /// <param name="combined">Selection array (index 0 = newest set, running down to oldest).</param>
        /// <param name="newestSetIndex">Standard 1-based index of the newest set (slot 0).</param>
        /// <returns>
        /// A tuple of (<c>totalFiles</c>, <c>totalBytes</c>, <c>perSet</c>) where <c>perSet</c> maps standard
        ///  set index → (number of files, total bytes) targeted for that set. Sets with zero files are omitted.
        /// </returns>
        public (int totalFiles, long totalBytes, Dictionary<int, (int, long)> perSet) GetFileCounts(
            List<TapeFileInfo>?[] combined, int newestSetIndex)
        {
            var perSet = new Dictionary<int, (int, long)>();
            int total = 0;
            long totalBytes = 0;

            for (int i = 0; i < combined.Length; i++)
            {
                int setIndex = newestSetIndex - i;
                int count = combined[i]?.Count ?? this[setIndex].Count;
                long bytes = combined[i]?.Sum(f => f.FileDescr.Length) ?? this[setIndex].TotalFileSize;
                if (count > 0)
                    perSet[setIndex] = (count, bytes);
                total += count;

                totalBytes += bytes;
            }

            return (total, totalBytes, perSet);
        }

        /// <summary>
        /// Returns the total file size in bytes from a pre-assembled <paramref name="combined"/> selection
        ///  array (as produced by <see cref="SelectFilesFromSets"/>), plus the overall total.
        ///  A <c>null</c> entry in <paramref name="combined"/> means "all files in set", so the
        ///  file size comes from the corresponding <see cref="SetTOC"/>.
        /// </summary>
        /// <param name="combined">Selection array (index 0 = newest set, running down to oldest).</param>
        /// <param name="newestSetIndex">Standard 1-based index of the newest set (slot 0).</param>
        /// <returns>Total file size in bytes.</returns>
        public long GetTotalFileSize(List<TapeFileInfo>?[] combined, int newestSetIndex)
        {
            long totalBytes = 0;
            for (int i = 0; i < combined.Length; i++)
            {
                int setIndex = newestSetIndex - i;
                long bytes = combined[i]?.Sum(f => f.FileDescr.Length) ?? this[setIndex].TotalFileSize;
                totalBytes += bytes;
            }
            return totalBytes;
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

        /// <summary>
        /// Computes the total file size on tape for all sets considering block sizes per set, optionally restricted to the current volume.
        /// </summary>
        /// <param name="defaultBlockSize">The default block size to use if a set does not specify one.</param>
        /// <param name="onVolumeOnly">If true, only compute for sets on the current volume; otherwise, compute for all sets.</param>
        /// <returns>The total file size on tape.</returns>
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

        #endregion // File selection methods

    } // class TapeTOC

} // namespace TapeNET