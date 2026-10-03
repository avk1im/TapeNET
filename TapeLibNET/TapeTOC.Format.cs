using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using TapeLibNET.Format;
using TapeLibNET.Legacy;

namespace TapeLibNET;

// ============================================================================================================
//  Format 2.1 representation of the TOC (Design-Format-v2 §5.1, Appendix B §B.8).
//
//  TOC stream := TocHeader (TocSet TocFileBatch*)* TocEnd ‖ CRC-64
//
//  The domain types (TapeTOC, TapeSetTOC, TapeFileInfo) stay free of wire concerns: each record has a small
//   mutable wire DTO with a schema table (G4). This file holds the DTOs, the TOC's SaveTo / LoadFrom / TryPeek,
//   and the per-set "persisted state" that decides which sets get a fresh LastSaveTime.
// ============================================================================================================

#region *** TocHeader (0x0101) ***

/// <summary>Wire form of the TOC header record — the first record of every 2.1 TOC stream.</summary>
/// <remarks>
/// Kept small and first: the TOC's first 16 KiB block always holds it whole, so <see cref="TapeTOC.TryPeek"/>
///  identifies a TOC copy from that block alone (§5.9). Hence the clamps on the two strings.
/// </remarks>
internal sealed class TocHeaderWire : ITapeRecord<TocHeaderWire>
{
    /// <summary>Description clamp — keeps the header within the first TOC block (§5.1).</summary>
    internal const int MaxDescriptionBytes = 8 * 1024;

    /// <summary>WrittenBy clamp (§5.1).</summary>
    internal const int MaxWrittenByBytes = 256;

    public Guid MediaId;
    public DateTime CreationTime;
    public DateTime LastSaveTime;
    public int Volume = 1;
    public bool ContinuedOnNextVolume;
    public int SetCount;
    public string Description = "";
    public string WrittenBy = "";

    private static readonly TapeSchema<TocHeaderWire> s_schema = new(TapeRecordKind.TocHeader)
    {
        // ── scalars 1–31 ──
        { 1,  h => h.MediaId,               (h, v) => h.MediaId = v },
        { 2,  h => h.CreationTime,          (h, v) => h.CreationTime = v,          FieldFlags.Required },
        { 3,  h => h.LastSaveTime,          (h, v) => h.LastSaveTime = v,          FieldFlags.Required },
        { 4,  h => h.Volume,                (h, v) => h.Volume = v,                1 },
        { 5,  h => h.ContinuedOnNextVolume, (h, v) => h.ContinuedOnNextVolume = v },
        { 6,  h => h.SetCount,              (h, v) => h.SetCount = v,              FieldFlags.Required },
        // ── strings 32–47 ──
        { 32, h => h.Description,           (h, v) => h.Description = v },
        { 33, h => h.WrittenBy,             (h, v) => h.WrittenBy = v },
    };

    public TapeRecordKind RecordKind => TapeRecordKind.TocHeader;
    public void WriteBody(TapeFieldWriter fields) => s_schema.Write(fields, this);
    public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.TocHeader;
    public static TocHeaderWire ReadBody(TapeFieldReader fields) => s_schema.Read(fields, new TocHeaderWire());

    public static TocHeaderWire From(TapeTOC toc) => new()
    {
        MediaId = toc.MediaId,
        CreationTime = toc.CreationTime,
        LastSaveTime = toc.LastSaveTime,
        Volume = toc.Volume,
        ContinuedOnNextVolume = toc.ContinuedOnNextVolume,
        SetCount = toc.Count,
        Description = TapeTextRules.ClampWellFormed(toc.Description, MaxDescriptionBytes),
        WrittenBy = TapeTextRules.ClampWellFormed(toc.WrittenBy, MaxWrittenByBytes),
    };

    /// <summary>Builds the TOC around the sets read from the stream; the last set becomes current.</summary>
    public TapeTOC ToToc(List<TapeSetTOC> sets)
    {
        var toc = new TapeTOC(sets)
        {
            MediaId = MediaId,
            Description = Description,
            CreationTime = CreationTime,
            LastSaveTime = LastSaveTime,
            Volume = Volume,
            ContinuedOnNextVolume = ContinuedOnNextVolume,
            WrittenBy = WrittenBy,
            LoadedFromLegacy = false,
        };
        toc.MakeLastSetCurrent();
        return toc;
    }
}

#endregion

#region *** TocEnd (0x0104) ***

/// <summary>Wire form of the TOC end record: positively terminates the stream and carries the cross-check totals.</summary>
internal sealed class TocEndWire : ITapeRecord<TocEndWire>
{
    public int SetCount;
    public long TotalFileCount;

    private static readonly TapeSchema<TocEndWire> s_schema = new(TapeRecordKind.TocEnd)
    {
        { 1, e => e.SetCount,       (e, v) => e.SetCount = v,       FieldFlags.Required },
        { 2, e => e.TotalFileCount, (e, v) => e.TotalFileCount = v, FieldFlags.Required },
    };

    public TapeRecordKind RecordKind => TapeRecordKind.TocEnd;
    public void WriteBody(TapeFieldWriter fields) => s_schema.Write(fields, this);
    public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.TocEnd;
    public static TocEndWire ReadBody(TapeFieldReader fields) => s_schema.Read(fields, new TocEndWire());
}

#endregion

#region *** File entry (group, field 48 of TocFileBatch) ***

/// <summary>
/// Wire form of one TOC file entry — a nested group inside a <c>TocFileBatch</c> record (§5.1).
/// </summary>
/// <remarks>
/// <para>
/// ONE instance serves a whole TOC on write and on read (§B.8.4). Reuse on read is safe only because the schema
///  resets every absent optional field to its default (<see cref="ITapeField{T}.ApplyDefault"/>): a
///  <see cref="Hash"/> can never leak from one entry into the next.
/// </para>
/// <para>
/// <b>Names.</b> NTFS accepts names that are not well-formed UTF-16 (a lone surrogate). Strict UTF-8 cannot carry
///  them, and a lossy replacement would restore a different file name. Such a name travels losslessly as raw
///  UTF-16LE in field 34 (<see cref="NameUtf16"/>), with <see cref="NameShared"/> = 0 and an empty
///  <see cref="NameSuffix"/>. The front coder still remembers it, so the next entry shares its prefix as usual.
/// </para>
/// </remarks>
internal sealed class TocFileEntryWire
{
    public ulong FileId;
    public long AddressBlock;
    public uint AddressOffset;
    public long Length;
    public long SizeOnTape;
    public FileAttributes Attributes;
    public TapeFileCodec Codec = TapeFileCodec.Stored;
    public DateTime CreationTime;
    public DateTime LastWriteTime;
    public DateTime LastAccessTime;
    public int NameShared;
    public string NameSuffix = "";
    public byte[] Hash = [];
    public byte[] NameUtf16 = [];

    /// <summary>Group schema (no record kind).</summary>
    public static readonly TapeSchema<TocFileEntryWire> Schema = new()
    {
        // ── scalars 1–31 ──
        { 1,  e => e.FileId,         (e, v) => e.FileId = v,         FieldFlags.Required },
        { 2,  e => e.AddressBlock,   (e, v) => e.AddressBlock = v,   FieldFlags.Required },
        { 3,  e => e.AddressOffset,  (e, v) => e.AddressOffset = v },
        { 4,  e => e.Length,         (e, v) => e.Length = v },
        { 5,  e => e.SizeOnTape,     (e, v) => e.SizeOnTape = v },
        { 6,  e => e.Attributes,     (e, v) => e.Attributes = v,     FieldFlags.Bitmask },
        { 7,  e => e.Codec,          (e, v) => e.Codec = v,          TapeFileCodec.Stored, FieldFlags.Critical },
        { 8,  e => e.CreationTime,   (e, v) => e.CreationTime = v,   FieldFlags.Required },
        { 9,  e => e.LastWriteTime,  (e, v) => e.LastWriteTime = v,  FieldFlags.Required },
        { 10, e => e.LastAccessTime, (e, v) => e.LastAccessTime = v, FieldFlags.Required },
        { 11, e => e.NameShared,     (e, v) => e.NameShared = v },
        // ── blobs 32–47 ──
        { 32, e => e.NameSuffix,     (e, v) => e.NameSuffix = v,     FieldFlags.Required },     // "" is written
        { 33, e => e.Hash,           (e, v) => e.Hash = v },
        { 34, e => e.NameUtf16,      (e, v) => e.NameUtf16 = v,      FieldFlags.Critical },     // ill-formed names only
    };

    /// <summary>Fills this wire object from <paramref name="file"/>; advances <paramref name="coder"/>.</summary>
    public void From(TapeFileInfo file, TapeNameFrontCoder coder)
    {
        TapeFileDescriptor d = file.FileDescr;

        FileId = file.FileId;
        AddressBlock = file.Address.Block;
        AddressOffset = file.Address.Offset;
        Length = d.Length;
        SizeOnTape = file.SizeOnTape;
        Attributes = d.Attributes;
        Codec = file.Codec;
        CreationTime = d.CreationTime;          // UTC in memory (§8.5)
        LastWriteTime = d.LastWriteTime;
        LastAccessTime = d.LastAccessTime;
        Hash = file.Hash ?? [];

        string name = d.FullName ?? "";
        if (TapeTextRules.IsWellFormedUtf16(name))
        {
            NameSuffix = coder.Encode(name, out int shared).ToString();
            NameShared = shared;
            NameUtf16 = [];
        }
        else
        {
            coder.Remember(name);               // the next entry may still share this name's prefix
            NameShared = 0;
            NameSuffix = "";
            NameUtf16 = TapeTextRules.EncodeUtf16LE(name);
        }
    }

    /// <summary>Builds the domain entry; advances <paramref name="coder"/>.</summary>
    /// <exception cref="TapeFormatException">Inconsistent name fields or a front-coding violation.</exception>
    public TapeFileInfo ToRecord(TapeNameFrontCoder coder)
    {
        string name;
        if (NameUtf16.Length > 0)
        {
            if (NameShared != 0 || NameSuffix.Length != 0)
                throw TapeFormatException.Bad("a UTF-16 name must not be front-coded");
            name = TapeTextRules.DecodeUtf16LE(NameUtf16);
            coder.Remember(name);
        }
        else
            name = coder.Decode(NameShared, NameSuffix);

        var descr = new TapeFileDescriptor(name)
        {
            Length = Length,
            Attributes = Attributes,
            CreationTime = CreationTime,
            LastWriteTime = LastWriteTime,
            LastAccessTime = LastAccessTime,
        };

        return new TapeFileInfo(FileId, new TapeAddress(AddressBlock, AddressOffset), descr)
        {
            Hash = Hash.Length == 0 ? null : Hash,     // ReadBytes hands out a fresh array: no aliasing between entries
            SizeOnTape = SizeOnTape,
            Codec = Codec,
        };
    }
}

#endregion

#region *** TapeFileInfo — 2.1 size estimate ***

public partial class TapeFileInfo
{
    /// <summary>
    /// Upper bound of the fixed part of one 2.1 TOC entry: group header, the ten scalar fields at maximum varint
    ///  width with their headers, and the headers of the name, hash and UTF-16 name fields — plus the share of
    ///  a batch prologue. Deliberately conservative: it sizes the early-warning reserve for the TOC.
    /// </summary>
    private const int c_tocEntryFixedMax = 120;

    /// <summary>
    /// Conservative 2.1 TOC footprint of this entry (§9): assumes no shared name prefix and maximum varint widths.
    /// </summary>
    public int EstimateSerializedSize()
    {
        string name = FileDescr.FullName ?? "";
        int nameBytes = TapeTextRules.IsWellFormedUtf16(name)
            ? Encoding.UTF8.GetByteCount(name)
            : 2 * name.Length;
        return c_tocEntryFixedMax + nameBytes + (Hash?.Length ?? 0);
    }
}

#endregion

#region *** TapeSetTOC — persisted state, set record, batches ***

public partial class TapeSetTOC
{
    /// <summary>A batch closes at this many files (§5.1).</summary>
    internal const int MaxFilesPerBatch = 4096;

    /// <summary>A batch closes once its body reaches this size (§5.1); one entry may overshoot it.</summary>
    internal const int MaxBatchBodyBytes = 1024 * 1024;

    /// <summary>Repeated field of a <c>TocFileBatch</c> record holding one file entry group.</summary>
    internal const int BatchFileField = 48;

    /// <summary>
    /// Pre-allocation cap on load: a declared count never drives allocation beyond this (R5); the list grows
    ///  as entries actually arrive.
    /// </summary>
    private const int c_maxPreallocFiles = 64 * 1024;

    #region *** Persisted state — which sets changed since the last save ***

    /// <summary>
    /// Everything about a set that is persisted, except <see cref="LastSaveTime"/> itself. Files are append-only
    ///  and every attempt burns a <see cref="NextFileId"/>, so the file count plus the id counter capture any
    ///  change to the file list.
    /// </summary>
    private readonly record struct PersistedState(
        Guid SetId, TapeDataFormat DataFormat, ulong NextFileId, int FileCount,
        string Description, DateTime CreationTime, uint BlockSize, TapeHashAlgorithm HashAlgorithm,
        TapeCompression Compression, int CompressionLevel, bool Incremental, int Volume, bool ContinuedFromPrevVolume);

    // The state as last saved or loaded; null for a set never persisted (a new set is always "modified").
    //  Copied by CopyFrom (TapeTOC.cs), so a loaded set copied into the live TOC stays unmodified.
    private PersistedState? m_savedState;

    private PersistedState CaptureState() => new(
        SetId, DataFormat, NextFileId, Count, Description, CreationTime, BlockSize, HashAlgorithm,
        Compression, CompressionLevel, Incremental, Volume, ContinuedFromPrevVolume);

    /// <summary>
    /// Whether this set differs from what was last saved or loaded — a new set, appended files, a burned
    ///  <see cref="TapeFileInfo.FileId"/>, or any changed attribute. Only modified sets get a fresh
    ///  <see cref="LastSaveTime"/> on save.
    /// </summary>
    /// <remarks>
    /// Compares a snapshot rather than tracking setters: no property can be forgotten, and the domain class needs
    ///  no change notifications. The cost is one small struct comparison per set and save.
    /// </remarks>
    internal bool IsModified => m_savedState != CaptureState();

    /// <summary>Records the current state as persisted: after a successful save, and after a load.</summary>
    internal void MarkSaved() => m_savedState = CaptureState();

    #endregion

    /// <summary>Wire form of the set record (§5.1, §B.8.2).</summary>
    internal sealed class Wire : ITapeRecord<Wire>
    {
        public Guid SetId;
        public DateTime CreationTime;
        public DateTime LastSaveTime;
        public uint BlockSize;
        public int Volume;
        public TapeDataFormat DataFormat = TapeDataFormat.V2;
        public ulong NextFileId;
        public TapeHashAlgorithm HashAlgorithm = TapeHashAlgorithm.Crc32;
        public TapeCompression Compression = TapeCompression.None;
        public int CompressionLevel = ZstdLevel.Default;
        public bool Incremental;
        public bool ContinuedFromPrevVolume;
        public int FileCount;
        public string Description = "";

        private static readonly TapeSchema<Wire> s_schema = new(TapeRecordKind.TocSet, validate: Validate)
        {
            // ── scalars 1–31 ──
            { 1,  w => w.SetId,                   (w, v) => w.SetId = v },
            { 2,  w => w.CreationTime,            (w, v) => w.CreationTime = v,            FieldFlags.Required },
            { 3,  w => w.LastSaveTime,            (w, v) => w.LastSaveTime = v,            FieldFlags.Required },
            { 4,  w => w.BlockSize,               (w, v) => w.BlockSize = v,               FieldFlags.Required },
            { 5,  w => w.Volume,                  (w, v) => w.Volume = v,                  FieldFlags.Required },
            { 6,  w => w.DataFormat,              (w, v) => w.DataFormat = v,              TapeDataFormat.V2, FieldFlags.Critical },
            { 7,  w => w.NextFileId,              (w, v) => w.NextFileId = v,              FieldFlags.Required },
            { 8,  w => w.HashAlgorithm,           (w, v) => w.HashAlgorithm = v,           TapeHashAlgorithm.Crc32 },
            { 9,  w => w.Compression,             (w, v) => w.Compression = v,             TapeCompression.None },
            { 10, w => w.CompressionLevel,        (w, v) => w.CompressionLevel = v,        ZstdLevel.Default },
            { 11, w => w.Incremental,             (w, v) => w.Incremental = v },
            { 12, w => w.ContinuedFromPrevVolume, (w, v) => w.ContinuedFromPrevVolume = v },
            { 13, w => w.FileCount,               (w, v) => w.FileCount = v,               FieldFlags.Required },
            // ── strings 32–47 ──
            { 32, w => w.Description,             (w, v) => w.Description = v },
        };

        // Cross-field rules - run on write (programming error) and on read (CrossCheck), G11
        private static string? Validate(Wire w)
        {
            if (w.DataFormat == TapeDataFormat.V2 && w.SetId == Guid.Empty)
                return "V2 set without SetId";
            if (w.NextFileId == 0)
                return "NextFileId must be at least 1";
            return null;
        }

        public TapeRecordKind RecordKind => TapeRecordKind.TocSet;
        public void WriteBody(TapeFieldWriter fields) => s_schema.Write(fields, this);
        public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.TocSet;
        public static Wire ReadBody(TapeFieldReader fields) => s_schema.Read(fields, new Wire());

        public static Wire From(TapeSetTOC set) => new()
        {
            SetId = set.SetId,
            CreationTime = set.CreationTime,
            LastSaveTime = set.LastSaveTime,
            BlockSize = set.BlockSize,
            Volume = set.Volume,
            DataFormat = set.DataFormat,
            NextFileId = set.NextFileId,
            HashAlgorithm = set.HashAlgorithm,
            Compression = set.Compression,
            CompressionLevel = set.CompressionLevel,
            Incremental = set.Incremental,
            ContinuedFromPrevVolume = set.ContinuedFromPrevVolume,
            FileCount = set.Count,
            Description = TapeTextRules.ClampWellFormed(set.Description, TapeFormat.MaxStringBytes),
        };

        /// <summary>Builds an empty set; its files arrive with the batches that follow.</summary>
        public TapeSetTOC ToSet() => new(Volume, Math.Min(FileCount, c_maxPreallocFiles), Incremental)
        {
            SetId = SetId,
            DataFormat = DataFormat,
            NextFileId = NextFileId,
            Description = Description,
            CreationTime = CreationTime,
            LastSaveTime = LastSaveTime,
            BlockSize = BlockSize,
            HashAlgorithm = HashAlgorithm,
            Compression = Compression,
            CompressionLevel = CompressionLevel,
            ContinuedFromPrevVolume = ContinuedFromPrevVolume,
        };
    }

    /// <summary>
    /// Writes this set's files as <c>TocFileBatch</c> records: at most <see cref="MaxFilesPerBatch"/> files or about
    ///  <see cref="MaxBatchBodyBytes"/> per batch. An empty set writes no batch.
    /// </summary>
    /// <param name="entry">Reusable wire object (one per TOC).</param>
    /// <param name="coder">Reusable front coder; reset at every batch start, so batches decode on their own.</param>
    internal void WriteBatches(TapeRecordWriter writer, TocFileEntryWire entry, TapeNameFrontCoder coder)
    {
        int i = 0;
        while (i < m_tapeFileInfos.Count)
        {
            coder.Reset();
            TapeFieldWriter body = writer.BeginRecord(TapeRecordKind.TocFileBatch);
            try
            {
                int inBatch = 0;
                do
                {
                    entry.From(m_tapeFileInfos[i++], coder);
                    TapeFieldWriter group = body.BeginGroup(BatchFileField);
                    TocFileEntryWire.Schema.Write(group, entry);
                    body.EndGroup(group);
                }
                while (i < m_tapeFileInfos.Count && ++inBatch < MaxFilesPerBatch && body.BytesWritten < MaxBatchBodyBytes);
            }
            catch
            {
                writer.AbandonRecord();     // keep the writer usable (same rule as F1)
                throw;
            }
            writer.EndRecord();
        }
    }

    /// <summary>Appends the files of one <c>TocFileBatch</c> body to this (loading) set.</summary>
    /// <param name="declaredFileCount">The set record's <c>FileCount</c>: more entries than declared are refused
    ///  as they arrive, so a lying stream cannot grow the list without bound.</param>
    internal void ReadBatch(TapeFieldReader body, TocFileEntryWire entry, TapeNameFrontCoder coder, int declaredFileCount)
    {
        coder.Reset();
        while (body.MoveNext())
        {
            if (body.Number != BatchFileField)
            {
                body.SkipUnknown();
                continue;
            }

            if (Count >= declaredFileCount)
                throw body.Error(FormatErrorKind.CrossCheck, $"batch holds more files than the set declares ({declaredFileCount})");

            TocFileEntryWire.Schema.Read(body.ReadGroup(), entry);
            TapeFileInfo file;
            try
            {
                file = entry.ToRecord(coder);
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException)
            {
                // Domain conversion refused the data (G8): report it as a format error with context
                throw new TapeFormatException(FormatErrorKind.BadValue, $"file entry: {ex.Message}", ex)
                {
                    Record = TapeRecordKind.TocFileBatch,
                    Field = BatchFileField,
                };
            }
            Append(file);
        }
    }

    /// <summary>
    /// Completes a set after its last batch: checks the declared file count and guarantees that
    ///  <see cref="NextFileId"/> lies above every <see cref="TapeFileInfo.FileId"/> on record.
    /// </summary>
    /// <remarks>
    /// A <see cref="NextFileId"/> at or below an id already in the set would let the next backup re-issue that id —
    ///  exactly what per-set ids exist to prevent (§2 #12). Raising it is always safe: ids only need to be unused.
    /// </remarks>
    internal void FinishLoading(int declaredFileCount)
    {
        if (Count != declaredFileCount)
            throw new TapeFormatException(FormatErrorKind.CrossCheck,
                $"set declares {declaredFileCount} files, its batches hold {Count}") { Record = TapeRecordKind.TocSet };

        ulong maxFileId = 0;
        foreach (TapeFileInfo file in m_tapeFileInfos)
            maxFileId = Math.Max(maxFileId, file.FileId);
        if (maxFileId == ulong.MaxValue)
            throw new TapeFormatException(FormatErrorKind.BadValue, "FileId space of the set is exhausted") { Record = TapeRecordKind.TocSet };
        if (maxFileId >= NextFileId)
            NextFileId = maxFileId + 1;
    }
}

#endregion

#region *** TapeTOC — SaveTo / LoadFrom / TryPeek ***

public partial class TapeTOC
{
    /// <summary>
    /// TOC version reported for 2.1 TOC copies (<c>Major &lt;&lt; 8 | Minor</c>), e.g. by Scan Media. Legacy TOCs
    ///  report <see cref="TocVersionInitial"/> or <see cref="TocVersionWithMediaId"/>.
    /// </summary>
    public const ushort TocVersion = (TapeFormat.Major << 8) | TapeFormat.Minor;

    /// <summary>The newest legacy TOC version; legacy readers and probes accept nothing above it.</summary>
    public const ushort LegacyTocVersionMax = TocVersionWithMediaId;

    /// <summary>Pre-allocation cap for the set list on load (R5).</summary>
    private const int c_maxPreallocSets = 1024;

    /// <summary>
    /// Identity of the running build, stamped into <see cref="WrittenBy"/> on save:
    ///  <c>"&lt;entry app&gt; &lt;version&gt; / TapeLibNET &lt;version&gt;"</c>.
    /// </summary>
    internal static string DefaultWrittenBy { get; } = BuildWrittenBy();

    private static string BuildWrittenBy()
    {
        AssemblyName lib = typeof(TapeTOC).Assembly.GetName();
        AssemblyName? app = Assembly.GetEntryAssembly()?.GetName();
        string libPart = $"TapeLibNET {lib.Version?.ToString(3)}";
        return (app is null || app.Name == lib.Name) ? libPart : $"{app.Name} {app.Version?.ToString(3)} / {libPart}";
    }

    /// <summary>
    /// Writes this TOC as a 2.1 TOC stream: <c>TocHeader (TocSet TocFileBatch*)* TocEnd ‖ CRC-64</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stamps the TOC's <see cref="LastSaveTime"/> and <see cref="WrittenBy"/>, and the
    ///  <see cref="TapeSetTOC.LastSaveTime"/> of every set that changed since it was last saved or loaded
    ///  (<see cref="TapeSetTOC.IsModified"/>). Unchanged sets keep their time.
    /// </para>
    /// <para>
    /// Does NOT clear <see cref="LoadedFromLegacy"/>: a <c>.tapetoc</c> export upgrades nothing on tape. The agent
    ///  clears it once a TOC copy has reached the tape (<see cref="TapeAgentBase.BackupTOC"/>).
    /// </para>
    /// </remarks>
    /// <param name="stream">Destination; written forward only, never disposed.</param>
    public void SaveTo(Stream stream) => SaveTo(stream, DateTime.UtcNow, DefaultWrittenBy);

    /// <summary>Deterministic form of <see cref="SaveTo(Stream)"/> — tests and goldens pin time and writer.</summary>
    internal void SaveTo(Stream stream, DateTime saveTimeUtc, string writtenBy)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(writtenBy);

        LastSaveTime = saveTimeUtc;
        WrittenBy = writtenBy;
        foreach (TapeSetTOC set in m_setTOCs)
        {
            if (set.IsModified)
                set.LastSaveTime = saveTimeUtc;
        }

        TapeCrc64Envelope.Write(stream, s =>
        {
            using var writer = new TapeRecordWriter(s);
            WriteRecords(writer);
        });

        // Only after the whole stream went out: a failed save leaves the sets "modified" for the next attempt
        foreach (TapeSetTOC set in m_setTOCs)
            set.MarkSaved();
    }

    private void WriteRecords(TapeRecordWriter writer)
    {
        writer.Write(TocHeaderWire.From(this));

        var entry = new TocFileEntryWire();
        var coder = new TapeNameFrontCoder();
        long totalFiles = 0;
        foreach (TapeSetTOC set in m_setTOCs)
        {
            writer.Write(TapeSetTOC.Wire.From(set));
            set.WriteBatches(writer, entry, coder);
            totalFiles += set.Count;
        }

        writer.Write(new TocEndWire { SetCount = m_setTOCs.Count, TotalFileCount = totalFiles });
    }

    /// <summary>
    /// Reads a TOC in either format: a 2.1 TOC stream (CRC-64 verified), or a legacy TOC through
    ///  <see cref="LegacyTocReader"/> (which sets <see cref="LoadedFromLegacy"/>). Every loaded set counts as
    ///  saved: it keeps its <see cref="TapeSetTOC.LastSaveTime"/> until it changes.
    /// </summary>
    /// <param name="stream">Source; read forward only. A 2.1 TOC is never read past its CRC-64 trailer.</param>
    /// <exception cref="TapeFormatException">
    /// Any damage, truncation, cross-check failure or unsupported content, in either format. Callers treat EVERY
    ///  such exception as "this copy is bad" and try the other TOC copy — damage usually surfaces before the CRC
    ///  is compared.
    /// </exception>
    public static TapeTOC LoadFrom(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        TapePeekStream peek = TapePeekStream.Wrap(stream);     // replays the magic; never disposes the stream
        TapeTOC toc = TapeFormat.IsV2(peek.Peeked)
            ? TapeCrc64Envelope.Read(peek, ReadRecords)
            : LegacyTocReader.Load(peek);                     // never null: throws TapeFormatException

        foreach (TapeSetTOC set in toc)
            set.MarkSaved();
        return toc;
    }

    private static TapeTOC ReadRecords(Stream stream)
    {
        var reader = new TapeRecordReader(stream);

        // The header must come first: Read<T> refuses any other kind (UnexpectedKind)
        TocHeaderWire header = reader.Read<TocHeaderWire>();

        var sets = new List<TapeSetTOC>(Math.Min(header.SetCount, c_maxPreallocSets));
        var entry = new TocFileEntryWire();
        var coder = new TapeNameFrontCoder();
        TapeSetTOC? set = null;
        int declaredFiles = 0;

        while (true)
        {
            // null is a clean end of stream - never "end of TOC"; only TocEnd is
            TapeRecord record = reader.ReadRecord()
                ?? throw new TapeFormatException(FormatErrorKind.Truncated, "TOC ends without a TocEnd record");

            switch (record.Kind)
            {
                case TapeRecordKind.TocSet:
                    set?.FinishLoading(declaredFiles);
                    if (sets.Count == header.SetCount)
                        throw CrossCheck(TapeRecordKind.TocSet, $"more sets than the header declares ({header.SetCount})");
                    TapeSetTOC.Wire wire = record.Read<TapeSetTOC.Wire>();
                    set = wire.ToSet();
                    declaredFiles = wire.FileCount;
                    sets.Add(set);
                    break;

                case TapeRecordKind.TocFileBatch:
                    if (set is null)
                        throw record.Fields.Error(FormatErrorKind.UnexpectedKind, "file batch before any set");
                    set.ReadBatch(record.Fields, entry, coder, declaredFiles);
                    break;

                case TapeRecordKind.TocEnd:
                    set?.FinishLoading(declaredFiles);
                    TocEndWire end = record.Read<TocEndWire>();
                    long totalFiles = 0;
                    foreach (TapeSetTOC s in sets)
                        totalFiles += s.Count;
                    if (end.SetCount != sets.Count || header.SetCount != sets.Count)
                        throw CrossCheck(TapeRecordKind.TocEnd,
                            $"set count mismatch: header {header.SetCount}, end {end.SetCount}, read {sets.Count}");
                    if (end.TotalFileCount != totalFiles)
                        throw CrossCheck(TapeRecordKind.TocEnd,
                            $"file count mismatch: end {end.TotalFileCount}, read {totalFiles}");
                    return header.ToToc(sets);

                default:
                    throw record.Fields.Error(FormatErrorKind.UnexpectedKind, $"record {record.Kind} inside a TOC stream");
            }
        }

        static TapeFormatException CrossCheck(TapeRecordKind kind, string message)
            => new(FormatErrorKind.CrossCheck, message) { Record = kind };
    }

    /// <summary>
    /// Whether <paramref name="block"/> — the FIRST block of a stream — opens a TOC copy, in either format.
    /// </summary>
    /// <param name="version">
    /// <c>Major &lt;&lt; 8 | Minor</c> as written for a 2.1 copy (<see cref="TocVersion"/> for this build's own), or the
    ///  legacy TOC version.
    /// </param>
    /// <param name="mediaId">The series identity; <see cref="Guid.Empty"/> when the TOC carries none.</param>
    /// <remarks>
    /// A 2.1 copy is identified positively: magic, kind <c>TocHeader</c> as the FIRST record, supported major, and a
    ///  <c>TocHeader</c> body that parses. A copy from a newer major is not recognized here (Phase 5 reports it as
    ///  "ours, from a newer TapeNET"). Anything without the magic goes to the legacy probe, unchanged.
    /// </remarks>
    public static bool TryPeek(byte[] block, int length, out ushort version, out Guid mediaId)
    {
        ArgumentNullException.ThrowIfNull(block);
        ReadOnlySpan<byte> data = block.AsSpan(0, Math.Clamp(length, 0, block.Length));

        if (!TapeFormat.IsV2(data))
            return LegacyIdentify.TryPeekToc(block, length, out version, out mediaId);

        version = 0;
        mediaId = Guid.Empty;

        if (TapeRecordReader.ParsePrologue(data, out TapeRecordReader.Prologue p) != TapeRecordReader.PrologueStatus.Ok
            || p.RawKind != (ushort)TapeRecordKind.TocHeader
            || p.Major != TapeFormat.Major
            || p.PrologueLength + p.BodyLength > data.Length)
            return false;

        try
        {
            var record = new TapeRecord(p.RawKind, p.Major, p.Minor, data.Slice(p.PrologueLength, p.BodyLength).ToArray());
            mediaId = record.Read<TocHeaderWire>().MediaId;
            version = (ushort)((p.Major << 8) | p.Minor);
            return true;
        }
        catch (TapeFormatException)
        {
            return false;
        }
    }
}

#endregion

#region *** Text rules ***

/// <summary>
/// UTF-16 well-formedness and the lossless UTF-16LE fallback for names strict UTF-8 cannot carry.
/// </summary>
internal static class TapeTextRules
{
    /// <summary>Whether every surrogate in <paramref name="text"/> is part of a proper pair.</summary>
    public static bool IsWellFormedUtf16(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }
                return false;
            }
            if (char.IsLowSurrogate(c))
                return false;
        }
        return true;
    }

    /// <summary>Raw UTF-16 code units, little-endian - lossless for any .NET string.</summary>
    public static byte[] EncodeUtf16LE(string text)
    {
        var bytes = new byte[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), text[i]);
        return bytes;
    }

    /// <summary>Inverse of <see cref="EncodeUtf16LE"/>.</summary>
    /// <exception cref="TapeFormatException">Odd byte count.</exception>
    public static string DecodeUtf16LE(byte[] bytes)
    {
        if ((bytes.Length & 1) != 0)
            throw TapeFormatException.Bad("UTF-16 name has an odd byte count");
        return string.Create(bytes.Length / 2, bytes, static (chars, b) =>
        {
            for (int i = 0; i < chars.Length; i++)
                chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2 * i));
        });
    }

    /// <summary>
    /// For free text (descriptions, WrittenBy): replaces lone surrogates with U+FFFD, then clamps to
    ///  <paramref name="maxBytes"/> UTF-8 bytes. Free text has no lossless requirement - names do (see
    ///  <see cref="TocFileEntryWire"/>).
    /// </summary>
    public static string ClampWellFormed(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (!IsWellFormedUtf16(text))
            text = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));     // non-throwing UTF8: lone surrogate → U+FFFD
        return TapePrimitives.ClampToUtf8Bytes(text, maxBytes);
    }
}

#endregion
