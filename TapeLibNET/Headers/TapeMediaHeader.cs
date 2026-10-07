// Save as: TapeLibNET/TapeMediaHeader.cs
using TapeLibNET.Drive;
using TapeLibNET.Toc;
using TapeLibNET.Format;
using TapeLibNET.Legacy;

namespace TapeLibNET.Headers;

/// <summary>Where the TOC lives on a medium, recorded in a <see cref="TapeMediaHeader"/>.</summary>
public enum TapeTocPlacement : byte
{
    /// <summary>Single-partition medium: the TOC follows the content in the same partition.</summary>
    InSet = 0,
    /// <summary>Initiator-partition medium: the TOC lives in its own partition.</summary>
    InPartition = 1,
}

/// <summary>Wire form of the 2.1 media header (record kind <c>MediaHeader</c>, Design-Format-v2 §5.4).</summary>
internal sealed class TapeMediaHeaderWire : TapeHeaderWire
{
    public int Volume = 1;
    public MediaPartition Partition = MediaPartition.Content;
    public TapeTocPlacement TocPlacement = TapeTocPlacement.InSet;
    public bool HasSetHeaders;
    public string OriginalName = "";

    public static readonly TapeSchema<TapeMediaHeaderWire> Schema = new(TapeRecordKind.MediaHeader, inherits: Shared)
    {
        // ── scalars 1–31 (1–3 inherited) ──
        { 4,  w => w.Volume,        (w, v) => w.Volume = v,        1 },
        { 5,  w => w.Partition,     (w, v) => w.Partition = v,     MediaPartition.Content },
        { 6,  w => w.TocPlacement,  (w, v) => w.TocPlacement = v,  TapeTocPlacement.InSet },
        { 7,  w => w.HasSetHeaders, (w, v) => w.HasSetHeaders = v },
        // ── strings 32–47 ──
        { 32, w => w.OriginalName,  (w, v) => w.OriginalName = v },
    };
}

/// <summary>
/// Media (volume) header written once at the beginning of medium (BOM) of the content partition, positively identifying
///  the cartridge as "ours" so a fresh load need not seek to end-of-data merely to discover whether a TOC exists.
/// </summary>
/// <remarks>
/// <para>
/// An immutable identity projection of the <see cref="TapeTOC"/>: it carries only values that never change once the
///  medium is formatted — the series <see cref="MediaId"/>, the immutable per-tape <see cref="Volume"/>, the creation
///  time, where the TOC lives (<see cref="TocPlacement"/>), and a creation-time snapshot of the name. Written once at
///  format (and on a fresh continuation volume), never rewritten — a legacy media header stays legacy (§3.3).
/// </para>
/// <para>
/// The current, user-renameable media name is deliberately NOT stored here — renaming rewrites the TOC, not this
///  once-written header — so UIs must show the TOC's live description, never <see cref="OriginalName"/>. Build one only
///  via <see cref="TapeTOC.CreateHeader"/>.
/// </para>
/// </remarks>
public sealed record TapeMediaHeader : TapeHeader
{
    /// <summary>UTF-8 byte budget for <see cref="OriginalName"/>, leaving ample room inside the frame.</summary>
    private const int c_maxOriginalNameBytes = 15 * 1024;

    /// <inheritdoc/>
    public override TapeHeaderKind Kind => TapeHeaderKind.Media;

    /// <summary>The series/cartridge identity, shared with (and copied from) the TOC's <c>MediaId</c>.</summary>
    public Guid MediaId
    {
        get => Id;
        init => Id = value;
    }

    /// <summary>
    /// The TOC's on-tape block size — reinterprets the base <see cref="TapeHeader.BlockSize"/> slot. Immutable at
    ///  format; lets a reader adopt larger TOC blocks in future without probing.
    /// </summary>
    public uint TocBlockSize
    {
        get => BlockSize;
        init => BlockSize = value;
    }

    /// <summary>The volume number within a multi-volume series — immutable for the life of this tape.</summary>
    public int Volume { get; init; }

    /// <summary><see cref="MediaPartition"/> where this header resides.</summary>
    public MediaPartition Partition { get; init; }

    /// <summary>Where the TOC lives, so a reader knows the layout without probing.</summary>
    public TapeTocPlacement TocPlacement { get; init; }

    /// <summary>
    /// Creation-time snapshot of the media name, kept only for recovery/diagnostics; may be <see langword="null"/> when
    ///  none was recorded (<see cref="DisplayName"/> then synthesizes one).
    /// </summary>
    /// <remarks>
    /// NOT the current media name: renaming rewrites the TOC, not this header. UIs should show the TOC's live
    ///  description instead of this value.
    /// </remarks>
    public string? OriginalName { get; init; }

    /// <summary>Whether the sets on this volume carry their own <see cref="TapeSetHeader"/> (SH-1).</summary>
    /// <remarks>
    /// <para>
    /// <b>Presence is declared, never probed.</b> The media header is already read once per volume at every content
    ///  choke-point and at every load, so this flag rides along at zero I/O cost — and re-resolves per volume for free,
    ///  which is what makes a mixed series (legacy volume 1, headed volume 2+) answer correctly on each cartridge.
    /// </para>
    /// <para>
    /// A volume is headed-with-sets, headed-without-sets, or legacy — never mixed within itself. In format 2.1 the field
    ///  is an ordinary optional tag; legacy headers written before it existed read <see langword="false"/>.
    /// </para>
    /// </remarks>
    public bool HasSetHeaders { get; init; }

    /// <summary>
    /// A never-empty, human-readable name: the recorded <see cref="OriginalName"/> when present, otherwise one
    ///  synthesized from <see cref="MediaId"/>, <see cref="Volume"/>, and <see cref="TapeHeader.CreatedUtc"/>.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(OriginalName)
            ? OriginalName!
            : $"Media {MediaId:N} · vol {Volume} · {CreatedUtc:yyyy-MM-dd HH:mm}";

    /// <summary>
    /// Clamps a candidate name to the header's UTF-8 byte budget so the framed record always fits one
    ///  <see cref="TapeHeader.FixedHeaderBlockSize"/> block. A null or empty name maps to <see langword="null"/>.
    /// </summary>
    public static string? ClampName(string? name) => ClampUtf8(name, c_maxOriginalNameBytes);

    #region *** Format 2.1 ***

    /// <inheritdoc/>
    public override void WriteBody(TapeFieldWriter fields) => TapeMediaHeaderWire.Schema.Write(fields, new TapeMediaHeaderWire
    {
        Id = MediaId,
        CreatedUtc = CreatedUtc,
        BlockSize = TocBlockSize,
        Volume = Volume,
        Partition = Partition,
        TocPlacement = TocPlacement,
        HasSetHeaders = HasSetHeaders,
        OriginalName = OriginalName ?? "",       // "" is elided; read back as null
    });

    /// <summary>Reads a 2.1 media header body. Called by <see cref="TapeHeader.ReadBody"/>.</summary>
    internal static TapeMediaHeader ReadWire(TapeFieldReader fields)
    {
        TapeMediaHeaderWire w = TapeMediaHeaderWire.Schema.Read(fields, new TapeMediaHeaderWire());
        return new TapeMediaHeader
        {
            MediaId = w.Id,
            CreatedUtc = w.CreatedUtc,
            TocBlockSize = w.BlockSize,
            Volume = w.Volume,
            Partition = w.Partition,
            TocPlacement = w.TocPlacement,
            OriginalName = string.IsNullOrEmpty(w.OriginalName) ? null : w.OriginalName,
            HasSetHeaders = w.HasSetHeaders,
        };
    }

    #endregion

    #region *** Legacy read — removed in Phase 6 ***

    // Reads the trailing set-header flag, tolerating its absence on media written before the field
    //  existed. Such a header's frame simply ends here, so the read may return null OR throw,
    //  depending on how the framer bounds the record — both mean "no flag recorded" = false.
    private static bool ReadSetHeadersFlag(LegacyDeserializer d)
        => LegacyHeaderReader.ReadSetHeadersFlag(d);

    /// <summary>
    /// Reads the media-specific fields after the shared preamble has been decoded. Called only by
    ///  <see cref="LegacyHeaderReader.Read"/> once the kind byte selected <see cref="TapeHeaderKind.Media"/>.
    /// </summary>
    internal static TapeMediaHeader ConstructBody(LegacyDeserializer d, in TapeHeaderPreamble p)
    {
        int volume = d.DeserializeInt32();
        var partition = (MediaPartition)(d.DeserializeBytes(1)?[0] ?? (byte)MediaPartition.Content);
        var placement = (TapeTocPlacement)(d.DeserializeBytes(1)?[0] ?? (byte)TapeTocPlacement.InSet);
        string name = d.DeserializeString();
        bool hasSetHeaders = ReadSetHeadersFlag(d);

        return new TapeMediaHeader
        {
            MediaId = p.Id,
            CreatedUtc = p.CreatedUtc,
            BlockSize = p.BlockSize,
            Volume = volume,
            Partition = partition,
            TocPlacement = placement,
            OriginalName = string.IsNullOrEmpty(name) ? null : name,
            HasSetHeaders = hasSetHeaders,
        };
    }

    #endregion

    /// <inheritdoc/>
    public override string ToString() =>
        $"Media header — id {MediaId:N}, volume {Volume} / partition {Partition}, " +
        $"created {CreatedUtc:u}, set headers {(HasSetHeaders ? "yes" : "no")}, name \"{DisplayName}\"";
}
