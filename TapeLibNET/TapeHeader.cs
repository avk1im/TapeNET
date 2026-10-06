using System.Text;
using TapeLibNET.Format;
using TapeLibNET.Legacy;

namespace TapeLibNET;

/// <summary>
/// The kind of a <see cref="TapeHeader"/>. In format 2.1 the record kind carries it (Design-Format-v2 §5.4); legacy
///  records carry it as a byte after the signature. The in-memory enum serves both.
/// </summary>
public enum TapeHeaderKind : byte
{
    /// <summary>Not one of our headers — legacy content, blank, or foreign media.</summary>
    Unknown = 0,
    /// <summary>Media (volume) header at BOM. See <see cref="TapeMediaHeader"/>.</summary>
    Media = 1,
    /// <summary>Calibration run header at BOM of a scratch cartridge. See <see cref="TapeCalibrationHeader"/>.</summary>
    Calibration = 2,
    /// <summary>Per-set header at the front of a backup set's data region. See <see cref="TapeSetHeader"/>.</summary>
    Set = 3,
}

/// <summary>
/// Wire base of every 2.1 header record: the shared tags 1–3 (Design-Format-v2 §5.4), which mean the same in every
///  header kind, so identity reads without knowing the kind.
/// </summary>
internal abstract class TapeHeaderWire
{
    public Guid Id;
    public DateTime CreatedUtc;
    public uint BlockSize;

    /// <summary>Shared fields, inherited by each concrete header schema.</summary>
    public static readonly TapeSchema<TapeHeaderWire> Shared = new()
    {
        { 1, h => h.Id,         (h, v) => h.Id = v,         FieldFlags.Required },
        { 2, h => h.CreatedUtc, (h, v) => h.CreatedUtc = v, FieldFlags.Required },
        { 3, h => h.BlockSize,  (h, v) => h.BlockSize = v,  FieldFlags.Required },
    };
}

/// <summary>
/// Abstract base for every self-identifying block-boundary record in TapeLibNET: the media (volume) header, the per-set
///  header, and the calibration run header.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format 2.1</b> (Design-Format-v2 §5.4): one block frame <c>Record ‖ CRC-64</c> at block offset 0. The record kind
///  names the header kind; the shared tags 1–3 carry <see cref="Id"/>, <see cref="CreatedUtc"/> and
///  <see cref="BlockSize"/>. One read through <see cref="TapeFramer.TryUnpackHeader(byte[], int, out TapeHeader?)"/>
///  classifies any block as media, set, or calibration — in either format. The product writes 2.1 only.
/// </para>
/// <para>
/// <b>Legacy</b> headers keep reading through <see cref="LegacyHeaderReader"/> (<see cref="TryReadLegacy"/>), which
///  decodes the shared <see cref="TapeHeaderPreamble"/> and hands it to the concrete kind's <c>ConstructBody</c>.
/// </para>
/// <para>
/// The whole record sits in the front of one block; the remaining bytes are padding, ignored on read-back. Backup and
///  set headers use the fixed <see cref="FixedHeaderBlockSize"/> block; the calibration header rides in the standard
///  block too, or in the run's own block on drives that cannot carry one. Only the record grammar is shared — never the
///  physical write path.
/// </para>
/// </remarks>
public abstract record TapeHeader : ITapeFramedRecord<TapeHeader>
{
    /// <summary>
    /// Fixed on-tape block size for the backup media/set headers, reusing the TOC's proven 16 KiB block — large enough to
    ///  read a header in one <c>ReadDirect</c>, and above any drive's minimum block-size quirks.
    /// </summary>
    public const uint FixedHeaderBlockSize = 16 * 1024;

    /// <summary>The concrete kind of this header.</summary>
    public abstract TapeHeaderKind Kind { get; }

    /// <summary>
    /// The raw identity. Protected so only the hierarchy touches it directly; each concrete kind re-exposes it under a
    ///  domain-specific name (<c>MediaId</c>, <c>RunId</c>).
    /// </summary>
    protected Guid Id { get; init; }

    /// <summary>When this header was created, UTC (§8.5).</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>The header block size recorded at write time; usage defined by descendant classes.</summary>
    protected uint BlockSize { get; init; }

    #region *** Format 2.1 (ITapeFramedRecord) ***

    /// <inheritdoc/>
    public TapeRecordKind RecordKind => Kind switch
    {
        TapeHeaderKind.Media => TapeRecordKind.MediaHeader,
        TapeHeaderKind.Set => TapeRecordKind.SetHeader,
        TapeHeaderKind.Calibration => TapeRecordKind.CalibrationRunHeader,
        _ => throw new InvalidOperationException($"no record kind for header kind {Kind}"),
    };

    /// <inheritdoc/>
    public abstract void WriteBody(TapeFieldWriter fields);

    /// <summary>The header kinds this build reads in format 2.1.</summary>
    public static bool Accepts(TapeRecordKind kind)
        => kind is TapeRecordKind.MediaHeader or TapeRecordKind.SetHeader or TapeRecordKind.CalibrationRunHeader;

    /// <summary>Reads the body of a 2.1 header record, dispatching on the record kind.</summary>
    public static TapeHeader ReadBody(TapeFieldReader fields) => fields.Record.Kind switch
    {
        TapeRecordKind.MediaHeader => TapeMediaHeader.ReadWire(fields),
        TapeRecordKind.SetHeader => TapeSetHeader.ReadWire(fields),
        TapeRecordKind.CalibrationRunHeader => TapeCalibrationHeader.ReadWire(fields),
        _ => throw fields.Error(FormatErrorKind.UnexpectedKind,
            $"record kind {fields.Record.Kind} is not a header this build reads"),
    };

    /// <summary>Parses a LEGACY header frame (<c>[int32 len][payload][crc32]</c>) at the start of the block. Never throws.</summary>
    public static TapeFramer.FrameStatus TryReadLegacy(ReadOnlySpan<byte> block, out TapeHeader? record)
        => LegacyFramer.TryUnpack<TapeHeader>(block.ToArray(), block.Length, LegacyHeaderReader.Read, out record);

    #endregion

    /// <summary>A short, human-readable description used in user prompts and logs.</summary>
    public abstract override string ToString();

    /// <summary>
    /// Trims <paramref name="name"/> to at most <paramref name="maxBytes"/> UTF-8 bytes so the framed record always fits
    ///  its block. A null or empty name maps to <see langword="null"/> ("nothing recorded"). Shared by every kind that
    ///  snapshots a name; each supplies its own budget.
    /// </summary>
    protected static string? ClampUtf8(string? name, int maxBytes)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (Encoding.UTF8.GetByteCount(name) <= maxBytes)
            return name;
        // Trim by whole characters until it fits — simple and safe; names this long never occur in practice.
        var span = name.AsSpan();
        while (span.Length > 0 && Encoding.UTF8.GetByteCount(span) > maxBytes)
            span = span[..^1];
        return span.ToString();
    }
}
