using TapeLibNET.Legacy;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;


namespace TapeLibNET;

/// <summary>
/// How a backup set's data is laid out on tape: governs the per-file header and set-header formats.
///  Persisted per set in the TOC, so one tape may mix both.
/// </summary>
public enum TapeDataFormat
{
    /// <summary>Set written by a pre-2.1 build: 12-byte <c>TF</c> file headers, legacy-framed set header.</summary>
    Legacy = 1,

    /// <summary>Set written in the 2.1 format: self-describing inline-framed file headers, block-framed set header.</summary>
    V2 = 2,
}

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
/// <para><b>All times are UTC</b> (Design-Format-v2 §8.5): captured with the <c>…Utc</c> getters and applied with
///  the <c>…Utc</c> setters, so a backup restored in another time zone or across a DST change keeps its times.
///  Displays convert with <c>ToLocalTime()</c>.</para>
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
        FullName = fsi.FullName;
        if (fsi.Exists)
        {
            Attributes = fsi.Attributes;
            CreationTime = fsi.CreationTimeUtc;
            LastWriteTime = fsi.LastWriteTimeUtc;
            LastAccessTime = fsi.LastAccessTimeUtc;
        }
    }

    // Safe access to FileInfo fields -- only if the file exists
    public TapeFileDescriptor(FileInfo fsi)
    {
        FullName = fsi.FullName;
        if (fsi.Exists)
        {
            Attributes = fsi.Attributes;
            CreationTime = fsi.CreationTimeUtc;
            LastWriteTime = fsi.LastWriteTimeUtc;
            LastAccessTime = fsi.LastAccessTimeUtc;
            Length = fsi.Length;
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
            fileInfo.CreationTimeUtc = CreationTime;
            fileInfo.LastWriteTimeUtc = LastWriteTime;
            fileInfo.LastAccessTimeUtc = LastAccessTime;
            return true;
        }
        else
            return false;
    }

    public void FillFrom(FileSystemInfo fsi)
    {
        FullName = fsi.FullName;
        Attributes = fsi.Attributes;
        CreationTime = fsi.CreationTimeUtc;
        LastWriteTime = fsi.LastWriteTimeUtc;
        LastAccessTime = fsi.LastAccessTimeUtc;
    }

    public void FillFrom(FileInfo fsi)
    {
        FillFrom(fsi as FileSystemInfo);
        Length = fsi.Length;
    }
}

/// <summary>
/// On-tape file record — serves as both a TOC entry and an on-tape file header.
/// <para>Each instance carries a <see cref="FileId"/> unique within its set, the tape
///  a <see cref="TapeFileDescriptor"/>, and an optional integrity <see cref="Hash"/>.
///  Serialized in the TOC by TocFileEntryWire (format 2.1).</para>
/// </summary>
public partial class TapeFileInfo(ulong fileId, TapeAddress address, TapeFileDescriptor fileDescr)
{
    /// <summary>
    /// Identity of this file within its set; 0 means "not set". Allocated per attempt by
    ///  <see cref="TapeSetTOC.GenerateFileId"/> and never reused, so an orphaned attempt left on tape
    ///  can never collide with the committed file. For legacy sets this is the former per-TOC UID.
    /// </summary>
    public ulong FileId { get; } = fileId;
    /// <summary>Media address (block + offset) where this file's data begins.</summary>
    public TapeAddress Address { get; } = address;
    /// <summary>Block number where this file's data begins. Convenience accessor for <see cref="Address"/>.Block.</summary>
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

    public TapeFileInfo(ulong fileId, TapeAddress address, FileInfo fileInfo)
        : this(fileId, address, new TapeFileDescriptor(fileInfo))
    { }

    public bool SameFileName(TapeFileInfo other) => FileDescr.SameFileName(other.FileDescr);
    public bool SameFileName(FileInfo fileInfo) => FileDescr.SameFileName(fileInfo.FullName);

    /// <summary>Whether this entry carries a real <see cref="FileId"/> and a file name.</summary>
    public bool IsValid => FileId != 0 && !string.IsNullOrEmpty(FileDescr.FullName);

    // serailize only minimal information necessary to check file match on tape 
    public void SerializeHeaderTo(TapeSerializer serializer)
    {
        serializer.SerializeSignature();
        serializer.Serialize(FileId);
    }

    public static int EstimateSerializedHeaderSize()
    {
        // Signature: 2 bytes + Version: 2 bytes
        int size = LegacyFormat.Signature.Length + sizeof(ushort);
        // UID: 8 bytes (ulong)
        size += sizeof(ulong);
        return size;
    }

    // deserailize header and check if it matches this file info
    public bool DeserializeAndCheckHeaderFrom(LegacyDeserializer deserializer)
    => LegacyFileHeader.Matches(deserializer, FileId);

} // struct TapeFileInfo


/// <summary>
/// Lightweight bundle of <see cref="TapeSetTOC"/> creation metadata, used to seed a new
///  set on a continuation volume without cloning the previous-volume set instance.
/// <para><paramref name="SetId"/> and <paramref name="NextFileId"/> are carried over because a
///  multi-volume continuation is ONE logical set: it keeps the identity and the file-id sequence.
///  A default (empty) <paramref name="SetId"/> means "mint a fresh one".</para>
/// </summary>
public record TapeSetTOCParams(
    string Description,
    TapeHashAlgorithm HashAlgorithm,
    uint BlockSize,
    bool Incremental,
    int Capacity = 0,
    TapeCompression Compression = TapeCompression.None,
    int CompressionLevel = ZstdLevel.Default,
    Guid SetId = default,
    ulong NextFileId = 1UL);

/// <summary>
/// Table of contents for a single backup set — an <see cref="IReadOnlyList{TapeFileInfo}"/>
///  with per-set metadata (description, hash algorithm, block size, incremental flag, volume).
/// <para>New instances are created only via <see cref="TapeTOC.AddNewSetTOC"/>.</para>
/// </summary>
public partial class TapeSetTOC : IReadOnlyList<TapeFileInfo>
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
    /// <summary>Deserialization constructor -- used by the legacy readers.</summary>
    /// <param name="fileInfos"></param>
    internal TapeSetTOC(List<TapeFileInfo> fileInfos) => m_tapeFileInfos = fileInfos;

    /// <summary>
    /// Globally unique identity of this set; <c>SetId ‖ FileId</c> identifies any file on any tape.
    ///  Freshly minted for every new set; shared by all volumes of a multi-volume continuation;
    ///  <see cref="Guid.Empty"/> for sets loaded from a legacy TOC (see <see cref="DataFormat"/>).
    /// </summary>
    public Guid SetId { get; internal set; } = Guid.NewGuid();

    /// <summary>On-tape data layout of this set; <see cref="TapeDataFormat.Legacy"/> for sets read from a pre-2.1 TOC.</summary>
    public TapeDataFormat DataFormat { get; internal set; } = TapeDataFormat.V2;

    /// <summary>The next <see cref="TapeFileInfo.FileId"/> to be handed out; starts at 1 (0 means "not set").</summary>
    public ulong NextFileId { get; internal set; } = 1UL;

    /// <summary>
    /// Allocates a <see cref="TapeFileInfo.FileId"/> for a new file attempt in this set. Ids are never
    ///  reused: retries, skips and EOM rollbacks burn numbers, so an orphaned attempt still on tape can
    ///  never collide with the committed file. Gaps are harmless.
    /// </summary>
    internal ulong GenerateFileId() => NextFileId++;

    public string Description { get; set; } = string.Empty;
    public DateTime CreationTime { get; internal set; } = DateTime.UtcNow;
    public uint BlockSize { get; set; } = 0;
    public DateTime LastSaveTime { get; internal set; } = DateTime.UtcNow;
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
        new(Description, HashAlgorithm, BlockSize, Incremental, Capacity, Compression, CompressionLevel,
            SetId, NextFileId);

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
        SetId = toc.SetId;
        NextFileId = toc.NextFileId;
        DataFormat = toc.DataFormat;
        // ContinuedFromPrevVolume is init-only: the copy constructor carries it
        m_isPackedLayout = toc.m_isPackedLayout;

        // A copy is exactly as persisted as its source: a set loaded from tape and copied into the live TOC
        //  (TapeTOC.CopyFrom) must not count as modified, or its LastSaveTime would be restamped on the next save.
        m_savedState = toc.m_savedState;
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

    /// <summary>
    /// Computes the total size of all files in the set on tape, considering the block size
    /// and file block alignment or packing, plus the set header block when present (SH-12).
    /// <para>Works properly for both packed and aligned (deprecated) layouts.</para>
    /// </summary>
    /// <param name="defaultBlockSize">The default block size to use if the set's block size is not specified.</param>
    /// <returns>The total size of all files on tape, rounded up to the nearest block boundary.</returns>
    // We detect packed-layout sets dynamically: if any file has a non-zero intra-block
    //  offset (Address.Offset != 0), the set is packed and files share blocks, so we
    //  only round the *total* of (file + header) sizes up to one block boundary
    //  rather than rounding each file individually. The detection result is cached.
    /// <param name="blockSize">
    /// Block size to assume; 0 uses the set's own <see cref="BlockSize"/>.
    /// </param>
    /// <param name="withSetHeader">
    /// Whether this set carries a <see cref="TapeSetHeader"/>. The set TOC cannot know this itself — the
    ///  media header declares it per volume (SH-1) — so the caller supplies it.
    /// </param>
    public long ComputeTotalFileSizeOnTape(uint defaultBlockSize = 0, bool withSetHeader = false)
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
        long total = packed ? RoundUpToBlock(rawTotal, blockSize) : alignedTotal;

        // The set header is one fixed block at the head of the set's data, written BEFORE the first
        //  file and at the header block size, independent of the set's own block size (SH-12). It is
        //  content: it draws on the same capacity the files do, and the early-warning reserve must see it.
        if (withSetHeader)
            total += TapeHeaderBlock.Size;

        return total;

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
///  per-set FileId allocation (<see cref="TapeSetTOC.GenerateFileId"/>), multi-volume tracking,
///  and file selection across incremental chains.
/// <para><b>Three flavors of set indexation:</b></para>
///  <code>
///  Standard: 1, 2, 3, …, N   (1 = oldest, N = newest)
///  Alternative: −(N−1), …, −1, 0  (0 = newest, −1 = second newest, etc.)
///  Internal: 0, 1, …, N−1    (0 = oldest, used only inside this class)
/// </code>
/// </summary>
public partial class TapeTOC : IEnumerable<TapeSetTOC>
{
    private readonly List<TapeSetTOC> m_setTOCs;

    /// <summary>
    /// On-tape format version for the <see cref="TapeTOC"/> record specifically, kept
    ///  independent of the library-wide <see cref="LegacyFormat.Version"/> so the TOC
    ///  layout can evolve without invalidating every other serialized type's signature.
    /// <para>Legacy TOCs — written before per-record versioning — carry the then-current
    ///  library version (<c>0x0101</c>) in their signature; that value doubles as our
    ///  "initial, pre-MediaId" TOC version.</para>
    /// </summary>
    public const ushort TocVersionInitial = 0x0101;
    /// <summary>TOC format version that introduced the <see cref="MediaId"/> field.</summary>
    public const ushort TocVersionWithMediaId = 0x0102;

    public TapeTOC()
    {
        m_setTOCs = [];
    }
    public TapeTOC(string description) : this()
    {
        Description = description;
    }
    /// <summary>
    /// Copy constructor: clones the given <see cref="TapeTOC"/> into a new instance.
    /// <remark>Calls <see cref="CopyFrom(TapeTOC)"/>.</remark>
    /// </summary>
    /// <param name="toc">The <see cref="TapeTOC"/> instance to copy.</param>
    public TapeTOC(TapeTOC toc) : this()
    {
        CopyFrom(toc);
    }

    /// <summary>
    /// <c>true</c> when this TOC was loaded from a pre-2.1 (legacy) tape or file; every set then has
    ///  <see cref="TapeSetTOC.DataFormat"/> = <see cref="TapeDataFormat.Legacy"/>. The next durable write
    ///  upgrades the TOC copies on tape to the 2.1 format (legacy builds can no longer read them).
    /// </summary>
    public bool LoadedFromLegacy { get; internal set; } = false;

    /// <summary>
    /// Identifies the build that last wrote this TOC (e.g. <c>TapeWinNET 3.4.0 / TapeLibNET 3.4.0</c>);
    ///  informational only, empty for legacy TOCs.
    /// </summary>
    public string WrittenBy { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    /// <summary>
    /// Stable identity of the media — or, for a multi-volume backup, of the whole volume
    ///  series, since every volume of one series shares this id. Minted once on the first
    ///  durable TOC write (see <see cref="EnsureMediaId"/>) and never altered afterwards.
    /// <para><see cref="Guid.Empty"/> means "not yet assigned": a legacy Guid-less TOC just
    ///  read from tape, or a brand-new in-memory TOC not yet written. Read by apps for
    ///  catalog keys, log correlation, .tapetoc↔cartridge matching, and wrong-media guards;
    ///  writable only inside TapeLibNET.</para>
    /// </summary>
    public Guid MediaId { get; internal set; } = Guid.Empty;
    /// <summary>
    /// Guarantees a non-empty <see cref="MediaId"/>: mints a fresh <see cref="Guid"/> when
    ///  none exists, otherwise leaves the current identity intact. Called on the first durable
    ///  TOC write, so freshly formatted media and legacy Guid-less media both acquire a
    ///  permanent id — one that then rides unchanged across every rewrite and across all
    ///  volumes of a multi-volume series.
    /// </summary>
    /// <returns>The effective (possibly newly minted) <see cref="MediaId"/>.</returns>
    internal Guid EnsureMediaId()
    {
        if (MediaId == Guid.Empty)
            MediaId = Guid.NewGuid();

        return MediaId;
    }
    /// <summary>
    /// Clears the media identity so the next durable write (header / TOC) mints a fresh one. Used on
    ///  OVERWRITE, where the tape becomes new-content media of a new series (§10.5) — a fresh MediaId
    ///  avoids collisions with surviving volumes of the overwritten series.
    /// </summary>
    public void ResetMediaId() => MediaId = Guid.Empty;   // EnsureMediaId() re-mints on next write

    public DateTime CreationTime { get; internal set; } = DateTime.UtcNow;
    public DateTime LastSaveTime { get; internal set; } = DateTime.UtcNow;

    /// <summary>Current volume number (1-based).</summary>
    public int Volume { get; internal set; } = 1; // volume indexing starts from 1
    /// <summary>Whether the last set continues on a subsequent volume.</summary>
    public bool ContinuedOnNextVolume { get; set; } = false;

    /// <summary>
    /// Returns the number of <see cref="TapeSetTOC"/> instances in this TOC. Always ≥ 0.
    /// </summary>
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
    /// <summary>
    /// Returns true if the TOC is empty (no sets) OR contains a single set with NO files.
    /// </summary>
    public bool IsEmpty => Count == 0 || Count == 1 && CurrentSetTOC.Count == 0;

    /// <summary>
    /// Builds the immutable <see cref="TapeMediaHeader"/> for this medium — the sole authority for
    ///  producing a media header, guaranteeing it always matches this TOC's identity.
    /// </summary>
    /// <remarks>
    /// Mints the <see cref="MediaId"/> if still unset (so header and TOC always share it), then
    ///  snapshots the current identity: id, creation time, volume, and a length-clamped name. Written
    ///  once at format / on a fresh volume and never rewritten. Only ever produced for single-headerPartition
    ///  (TOC-in-set) media, hence <see cref="TapeTocPlacement.InSet"/>.
    /// </remarks>
    /// <param name="tocBlockSize">The block size to use for the TOC on tape (NOT the header itself!
    ///  This is always <see cref="TapeHeader.FixedHeaderBlockSize"/>).</param>
    /// <param name="tapeTocPlacement">The placement of the TOC on tape (in-set or in-partition).</param>
    /// <param name="headerPartition">The partition in which this header resides. By default <see cref="MediaPartition.Content"/>.</param>
    /// <param name="hasSetHeaders">
    /// Whether this volume's sets carry their own set headers (SH-1). Sourced from the writing
    ///  agent's <see cref="TapeAgentBase.WritesSetHeaders"/> — the TOC cannot know it, exactly as it
    ///  cannot know its own <paramref name="tapeTocPlacement"/>.
    /// </param>

    public TapeMediaHeader CreateHeader(uint tocBlockSize,
            TapeTocPlacement tapeTocPlacement,
            MediaPartition headerPartition = MediaPartition.Content,
            bool hasSetHeaders = false) =>
        new()
        {
            MediaId = EnsureMediaId(),
            CreatedUtc = CreationTime,
            TocBlockSize = tocBlockSize,
            Volume = Volume,
            Partition = headerPartition,
            TocPlacement = tapeTocPlacement,
            OriginalName = TapeMediaHeader.ClampName(Description),
            HasSetHeaders = hasSetHeaders,
        };

    /// <summary>
    /// Builds the <see cref="TapeSetHeader"/> for the set at <paramref name="setIndex"/> — the sole
    ///  authority for producing one, guaranteeing it always matches this TOC's identity and indexing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The TOC is the sole factory for the same reason it is for the media header: a
    ///  <see cref="TapeSetTOC"/> knows neither its own index nor the media identity. Set indexing and
    ///  series identity (<see cref="EnsureMediaId"/>) both live here.
    /// </para>
    /// <para>
    /// Carries <b>a-priori facts only</b> (SH-5): every value is known before the set's first file is
    ///  written. Nothing post-hoc — no file count, no totals — may ever be added.
    /// </para>
    /// </remarks>
    /// <param name="setIndex">
    /// Index of the set to describe, in standard (1..N) or alternative (≤ 0) form — normalized
    ///  internally, so <see cref="TapeSetHeader.GlobalSetIndex"/> always records the STANDARD index.
    /// </param>
    public TapeSetHeader CreateSetHeader(int setIndex)
    {
        if (Count == 0)
            throw new InvalidOperationException($"{nameof(CreateSetHeader)}: the TOC holds no sets");

        int setInternal = SetIndexToInternal(setIndex);
        if (setInternal < 0 || setInternal >= Count)
            throw new ArgumentOutOfRangeException(nameof(setIndex),
                $"Set index {setIndex} is outside {MinSetIndex}..{MaxSetIndex}");

        var setTOC = m_setTOCs[setInternal];

        return new TapeSetHeader
        {
            MediaId = EnsureMediaId(),         // idempotent — the same id the media header carries
            CreatedUtc = setTOC.CreationTime,     // the SET's own creation moment (cf. remark below)
            SetBlockSize = setTOC.BlockSize,        // advisory (SH-11); the TOC stays authoritative
            Volume = setTOC.Volume,
            VolumeSetIndex = setInternal - FirstSetInternalOfVolume(setTOC.Volume),
            GlobalSetIndex = InternalToSetIndex(setInternal),   // ALWAYS standard, whatever form came in
            Description = TapeSetHeader.ClampName(setTOC.Description),
        };
    }

    /// <summary>Convenience overload for <see cref="CurrentSetIndex"/> — the write path's entry point.</summary>
    public TapeSetHeader CreateSetHeaderForCurrentSet() => CreateSetHeader(CurrentSetIndex);

    /// <summary>
    /// Internal index of the oldest set residing on <paramref name="volume"/>, independent of the
    ///  current position and of the mounted <see cref="Volume"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the <see cref="FirstSetInternalOnVolume"/> property, which is anchored to BOTH
    ///  the mounted <see cref="Volume"/> and <see cref="m_currSetInternal"/> — it scans backward from
    ///  the current set. <see cref="CreateSetHeader"/> accepts ANY set index, so using the property
    ///  would make <see cref="TapeSetHeader.VolumeSetIndex"/> depend on where the TOC happens to point,
    ///  and would go NEGATIVE for a set on an earlier volume. Sets per tape number in the handful, so
    ///  the forward scan costs nothing. Falls back to the property when no set carries that volume.
    /// </remarks>
    private int FirstSetInternalOfVolume(int volume)
    {
        for (int i = 0; i < m_setTOCs.Count; i++)
            if (m_setTOCs[i].Volume == volume)
                return i;

        return FirstSetInternalOnVolume;   // no set on that volume — fall back to the mounted one
    }


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
    /// <summary>Convert standard index to alternative one, or vice versa.</summary>
    /// <param name="setIndex">Index in standard (1..N) or alternative (−(N−1)..0) format.</param>
    /// <returns>The converted index.</returns>
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
    /// Appends a new <see cref="TapeSetTOC"/> — or replaces a trailing EMPTY one — and makes it current.
    /// <para>The first set in a TOC is never marked incremental, regardless of the parameter.</para>
    /// </summary>
    /// <remarks>
    /// A trailing empty set (e.g. left by an aborted backup) is REPLACED by a fresh instance, not recycled: the new
    ///  set starts with every attribute at its default — a fresh <see cref="TapeSetTOC.SetId"/>, FileIds from 1,
    ///  the current <see cref="TapeDataFormat"/>, and no description, hash, compression or block size inherited from
    ///  the abandoned attempt. Replacing rather than resetting field by field means a future attribute can never be
    ///  forgotten. The new object identity also lets consumers detect the replacement, as with
    ///  <see cref="ReplaceCurrentSetTOC"/>.
    /// </remarks>
    public void AddNewSetTOC(int capacity = 0, bool incremental = false)
    {
        // Do NOT use CurrentSetTOC here: it calls AddNewSetTOC() itself when the TOC is empty.
        if (m_setTOCs.Count > 0 && m_setTOCs[^1].Count == 0)
        {
            // Count > 1 means "there's at least one other set before this empty one."
            //  If Count == 1, the empty set IS the first set → not incremental.
            m_setTOCs[^1] = new TapeSetTOC(Volume, capacity, incremental && m_setTOCs.Count > 1);
        }
        else
        {
            // Count > 0 means "there's at least one set already present before the one we're about to add."
            //  If Count == 0, we're adding the very first set → not incremental.
            m_setTOCs.Add(new TapeSetTOC(Volume, capacity, incremental && m_setTOCs.Count > 0));
        }

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
                // one logical set across volumes: same SetId and FileId sequence (empty SetId -> fresh one)
                SetId = (setParams.SetId != Guid.Empty) ? setParams.SetId : Guid.NewGuid(),
                NextFileId = Math.Max(1UL, setParams.NextFileId),
                ContinuedFromPrevVolume = contFromPrevVolume
            }
        );

        MakeLastSetCurrent();
    }

    /// <summary>
    /// Deep-copies all content from <paramref name="toc"/>, replacing everything in this instance.
    /// <remarks>Used by the copy constructor <see cref="TapeTOC(TapeTOC)"/></remarks>
    /// </summary>
    public void CopyFrom(TapeTOC toc) // Replaces the whole content -> use with CAUTION!
    {
        m_setTOCs.Clear();
        foreach (var setTOC in toc) // deep copy the sets, so that modifications to the original toc don't affect this toc
            m_setTOCs.Add(new TapeSetTOC(setTOC));

        LoadedFromLegacy = toc.LoadedFromLegacy;
        WrittenBy = toc.WrittenBy;

        // Preserve
        // freshly deserialized TOC into the live one via CopyFrom, so dropping this line would
        // silently strip the id from every loaded TOC.
        MediaId = toc.MediaId;

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
    internal TapeTOC(List<TapeSetTOC> setTOCs) => m_setTOCs = setTOCs;

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

    /// <summary>
    /// Checks if the given file is already backed up in the current set or any previous incremental sets.
    /// </summary>
    /// <param name="fileInfo">The file to check.</param>
    /// <returns>
    /// <see langword="true"/> if the file is already backed up; otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Legacy sets: their times are converted to UTC by the legacy reader (<see cref="LegacyTime"/>),
    ///  so the comparison holds across formats.
    /// </remarks>
    public bool IsFileUptodateInc(FileInfo fileInfo)
    {
        // check if the same or newer version of the file is already backed up in the current set
        //  or in any of the previous incremental sets.
        //  Times are UTC on both sides: TapeFileDescriptor stores UTC, so compare with LastWriteTimeUtc.
        DateTime lastWriteUtc = fileInfo.LastWriteTimeUtc;
        int firstNonInc = LastNonIncSetInternal; // find the latest non-incremental set
        for (int i = m_currSetInternal; i >= firstNonInc; i--)
        {
            if (m_setTOCs[i].Any(tfi => tfi.SameFileName(fileInfo) && tfi.FileDescr.LastWriteTime >= lastWriteUtc))
                return true;
        } // for i
        return false;
    }

    /// <summary>
    /// Considering incremental sets and multi-volume continuation, select files from the current and previous set(s)
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
    /// Flattens the array of lists of <see cref="TapeFileInfo"/> objects returned by <see cref="SelectFiles(bool, ITapeFileFilter?)"/>
    /// <para>Assumes the same current set remains as in the call to <see cref="SelectFiles(bool, ITapeFileFilter?)"/></para>
    /// </summary>
    /// <param name="filesBySets">The array of lists of <see cref="TapeFileInfo"/> objects, where each list corresponds to a set of files.
    /// <para>A <c>null</c> list entry means all files from the corresponding set.</para>
    /// </param>
    /// <returns>A flattened list of <see cref="TapeFileInfo"/> objects.</returns>
    public List<TapeFileInfo> SelectedFilesToList(List<TapeFileInfo>?[] filesBySets)
    {
        var allFiles = new List<TapeFileInfo>();
        int setIndex = CurrentSetIndex;
        foreach (var setFiles in filesBySets)
        {
            if (setFiles != null)
                allFiles.AddRange(setFiles);
            else // null entries = "all files from that set"
                allFiles.AddRange(this[setIndex]);
            setIndex--; // move to the next older set
        }
        return allFiles;
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
    /// Computes the total file size on tape for all sets considering block sizes per set (incl. per-set headers SH-12),
    ///  optionally restricted to the current volume.
    /// </summary>
    /// <param name="defaultBlockSize">The default block size to use if a set does not specify one.</param>
    /// <param name="onVolumeOnly">If true, only compute for sets on the current volume; otherwise, compute for all sets.</param>
    /// <param name="withSetHeaders">
    /// Whether the sets carry set headers — from <see cref="TapeMediaHeader.HasSetHeaders"/> via the
    ///  service, or from <see cref="TapeNavigator.SetHeadersExpected"/> inside the library.
    /// </param>
    /// <returns>The total file size on tape.</returns>
    public long ComputeTotalFileSizeOnTape(uint defaultBlockSize = 0, bool onVolumeOnly = true, bool withSetHeaders = false)
    {
        if (Count == 0)
            return 0L;

        long totalSize = 0L;
        if (onVolumeOnly)
        {
            for (int i = FirstSetInternalOnVolume; i <= LastSetInternalOnVolume; i++)
            {
                totalSize += m_setTOCs[i].ComputeTotalFileSizeOnTape(defaultBlockSize, withSetHeader: withSetHeaders);
            }
        }
        else
        {
            foreach (var setTOC in m_setTOCs)
            {
                totalSize += setTOC.ComputeTotalFileSizeOnTape(defaultBlockSize, withSetHeader: withSetHeaders);
            }
        }

        // Media header overhead: one media header block at content BOM per volume.
        //  Counted for write-planning / remaining-capacity; freshly formatted media always carries it.
        if (onVolumeOnly)
            totalSize += TapeHeader.FixedHeaderBlockSize;                 // this volume's media header
        else
            totalSize += Volume /* == volume count incl. this TOC */ * (long)TapeHeader.FixedHeaderBlockSize;

        return totalSize;
    }

    /// <summary>
    /// Cumulative on-tape size of the sets on the current volume that PRECEDE the current set — i.e.
    ///  the approximate byte offset from the start of the volume's content to where the current set
    ///  begins. Used to anchor <see cref="TapeDrive.NotifyNextContentWritePosition"/> when overwriting
    ///  an existing set. Per-set block-size and software-compression aware, but oblivious to hardware
    ///  compression and setmark/gap overhead — sufficient as a rough, temporary anchor.
    /// </summary>
    /// <remarks>
    /// Feeds <see cref="TapeDrive.NotifyNextContentWritePosition"/> on the OVERWRITE path, where the
    ///  drive's own <c>capacity − EOD</c> figure is stale after a backward seek. Including the preceding
    ///  sets' headers (SH-12) keeps that anchor honest — under-reporting would tell the drive more room
    ///  remains than actually does, and the early warning would fire too late to reserve the TOC.
    /// </remarks>
    /// <param name="withSetHeaders">
    /// Whether the sets carry set headers — from <see cref="TapeMediaHeader.HasSetHeaders"/> via the
    ///  service, or from <see cref="TapeNavigator.SetHeadersExpected"/> inside the library.
    /// </param>

    public long ComputeContentSizeOnTapeBeforeCurrentSet(uint defaultBlockSize = 0, bool withSetHeaders = false)
    {
        long totalSize = 0L;
        for (int i = FirstSetInternalOnVolume; i < m_currSetInternal; i++)
            totalSize += m_setTOCs[i].ComputeTotalFileSizeOnTape(defaultBlockSize, withSetHeader: withSetHeaders);

        // Media header overhead: one media header block at content BOM per volume.
        //  Counted for write-planning / remaining-capacity; freshly formatted media always carries it.
        totalSize += TapeHeader.FixedHeaderBlockSize;                 // this volume's media header

        return totalSize;
    }

    #endregion // File selection methods

} // class TapeTOC

// namespace TapeNET