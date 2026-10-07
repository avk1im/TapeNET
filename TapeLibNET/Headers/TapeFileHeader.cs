// Save as: TapeLibNET/TapeFileHeader.cs

using System.Text;

using TapeLibNET.Format;
using TapeLibNET.Compression;
using TapeLibNET.Toc;

namespace TapeLibNET.Headers;


/// <summary>
/// The 2.1 per-file header (record kind <c>FileHeader</c>, 0x0201) written in front of every file body of a
///  <see cref="TapeDataFormat.V2"/> set, as an inline frame <c>Record ‖ CRC-64</c> (Design-Format-v2 §4.5, §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-describing.</b> Carries the set identity, the file identity and everything needed to restore the file
///  without a TOC: full path, length, attributes and the three UTC times. A file's identity is
///  <c>SetId ‖ FileId</c> — globally unique (§5.2).
/// </para>
/// <para>
/// <b>Not front-coded</b>: one torn header must not break a chain of others. Names that are not well-formed UTF-16
///  (a lone surrogate — legal on NTFS) travel losslessly as raw UTF-16LE in field 33, with an empty field 32 —
///  the same rule as the TOC file entry.
/// </para>
/// <para>
/// <b>Not in the header</b>: <c>Hash</c>, <c>SizeOnTape</c> and the codec are unknown when it is written. The codec
///  follows as ONE byte at the start of the body (<see cref="WriteCodec"/>), written by
///  <see cref="ProbingCompressionStream"/> on its commit, or by the agent on the plain path.
/// </para>
/// <para>
/// <b>On-tape layout of one V2 file:</b> <c>[file header frame][codec u8][body]</c>. The hash covers the body only.
///  <see cref="TapeFileInfo.SizeOnTape"/> covers all three.
/// </para>
/// </remarks>
internal sealed class TapeFileHeader : ITapeRecord<TapeFileHeader>
{
    /// <summary>
    /// Upper bound of everything a V2 file adds in front of its body, EXCLUDING the name bytes: prologue, the seven
    ///  scalar fields at maximum varint width, both name field headers, the CRC-64 trailer and the codec byte.
    ///  Measured maximum 109 B; rounded up. Sizes capacity estimates only — never parsing.
    /// </summary>
    internal const int MaxFixedOverhead = 120;

    /// <summary>Name budget assumed by capacity estimates when the name is not known yet.</summary>
    internal const int AssumedNameBytes = 256;

    /// <summary>Length of the codec prefix at the start of a V2 file body.</summary>
    internal const int CodecPrefixLength = 1;

    public Guid SetId;
    public ulong FileId;
    public long Length;
    public FileAttributes Attributes;
    public DateTime CreationTime;
    public DateTime LastWriteTime;
    public DateTime LastAccessTime;
    public string FullName = "";
    public byte[] NameUtf16 = [];

    private static readonly TapeSchema<TapeFileHeader> s_schema = new(TapeRecordKind.FileHeader, validate: Validate)
    {
        // ── scalars 1–31 ──
        { 1,  h => h.SetId,          (h, v) => h.SetId = v,          FieldFlags.Required },
        { 2,  h => h.FileId,         (h, v) => h.FileId = v,         FieldFlags.Required },
        { 3,  h => h.Length,         (h, v) => h.Length = v },
        { 4,  h => h.Attributes,     (h, v) => h.Attributes = v,     FieldFlags.Bitmask },
        { 5,  h => h.CreationTime,   (h, v) => h.CreationTime = v,   FieldFlags.Required },
        { 6,  h => h.LastWriteTime,  (h, v) => h.LastWriteTime = v,  FieldFlags.Required },
        { 7,  h => h.LastAccessTime, (h, v) => h.LastAccessTime = v, FieldFlags.Required },
        // ── blobs 32–47 ──
        { 32, h => h.FullName,       (h, v) => h.FullName = v,       FieldFlags.Required },     // "" when field 33 is used
        { 33, h => h.NameUtf16,      (h, v) => h.NameUtf16 = v,      FieldFlags.Critical },     // ill-formed names only
    };

    // Cross-field rules - run on write (programming error) and on read (CrossCheck), G11
    private static string? Validate(TapeFileHeader h)
    {
        if (h.SetId == Guid.Empty)
            return "file header without SetId";
        if (h.FileId == 0)
            return "file header without FileId";
        if (h.NameUtf16.Length > 0 && h.FullName.Length > 0)
            return "file header carries both name forms";
        return null;
    }

    #region *** ITapeRecord ***

    public TapeRecordKind RecordKind => TapeRecordKind.FileHeader;
    public void WriteBody(TapeFieldWriter fields) => s_schema.Write(fields, this);
    public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.FileHeader;
    public static TapeFileHeader ReadBody(TapeFieldReader fields) => s_schema.Read(fields, new TapeFileHeader());

    #endregion

    /// <summary>The full path, whichever form it travelled in.</summary>
    public string Name => NameUtf16.Length > 0 ? TapeTextRules.DecodeUtf16LE(NameUtf16) : FullName;

    /// <summary>Builds the header for <paramref name="file"/> in the set <paramref name="setId"/>.</summary>
    public static TapeFileHeader From(Guid setId, TapeFileInfo file)
    {
        TapeFileDescriptor d = file.FileDescr;
        string name = d.FullName ?? "";
        bool wellFormed = TapeTextRules.IsWellFormedUtf16(name);
        return new TapeFileHeader
        {
            SetId = setId,
            FileId = file.FileId,
            Length = d.Length,
            Attributes = d.Attributes,
            CreationTime = d.CreationTime,          // UTC in memory (§8.5)
            LastWriteTime = d.LastWriteTime,
            LastAccessTime = d.LastAccessTime,
            FullName = wellFormed ? name : "",
            NameUtf16 = wellFormed ? [] : TapeTextRules.EncodeUtf16LE(name),
        };
    }

    #region *** Stream helpers ***

    /// <summary>Writes the header frame for <paramref name="file"/> to <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidOperationException">Empty <paramref name="setId"/> or <c>FileId</c> 0 (programming error).</exception>
    public static void Write(Stream stream, Guid setId, TapeFileInfo file)
        => TapeFrame.WriteInline(stream, From(setId, file));

    /// <summary>
    /// Reads one header frame from <paramref name="stream"/>, verifying its CRC-64; leaves the stream at the codec
    ///  byte. Never reads ahead.
    /// </summary>
    /// <exception cref="TapeFormatException">Truncated, damaged, foreign, or from a newer build.</exception>
    public static TapeFileHeader Read(Stream stream) => TapeFrame.ReadInline<TapeFileHeader>(stream);

    /// <summary>Exact on-tape size of the header frame for <paramref name="file"/> (without the codec byte).</summary>
    public static int Measure(Guid setId, TapeFileInfo file)
    {
        using var ms = new MemoryStream();
        TapeFrame.WriteInline(ms, From(setId, file));
        return (int)ms.Length;
    }

    /// <summary>
    /// Conservative estimate of what a V2 file adds in front of its body — header frame plus codec byte — for
    ///  capacity planning. <paramref name="fullName"/> <see langword="null"/> assumes <see cref="AssumedNameBytes"/>.
    /// </summary>
    public static int EstimateOverhead(string? fullName)
    {
        int nameBytes = fullName is null
            ? AssumedNameBytes
            : TapeTextRules.IsWellFormedUtf16(fullName) ? Encoding.UTF8.GetByteCount(fullName) : 2 * fullName.Length;
        return MaxFixedOverhead + nameBytes;
    }

    /// <summary>Writes the one-byte codec prefix of a V2 file body.</summary>
    public static void WriteCodec(Stream stream, TapeFileCodec codec) => stream.WriteByte((byte)codec);

    /// <summary>Reads the one-byte codec prefix of a V2 file body.</summary>
    /// <exception cref="TapeFormatException">End of stream, or a codec this build does not know.</exception>
    public static TapeFileCodec ReadCodec(Stream stream)
    {
        int b = stream.ReadByte();
        if (b < 0)
            throw new TapeFormatException(FormatErrorKind.Truncated, "file body ends before its codec byte");
        var codec = (TapeFileCodec)b;
        if (!Enum.IsDefined(codec))
            throw new TapeFormatException(FormatErrorKind.BadValue, $"unknown file codec {b}");
        return codec;
    }

    #endregion
}
