using TapeLibNET.Format;
using TapeLibNET.Legacy;

namespace TapeLibNET;

/// <summary>
/// Outcome of verifying a set header against the TOC's expectation for the set just positioned at.
/// </summary>
/// <remarks>
/// <see cref="Match"/> and <see cref="NotExpected"/> are the normal outcomes; everything else describes a tape that
///  disagrees with what the library believes about it. New values are APPENDED — the numeric values may travel to the
///  remote host and into logs.
/// </remarks>
public enum TapeSetHeaderVerdict
{
    /// <summary>Media identity, volume, on-volume index and set identity all agree. The overwhelmingly common case.</summary>
    Match,
    /// <summary>Header-less volume — no set header was expected, so none was read (SH-1).</summary>
    NotExpected,
    /// <summary>
    /// A set header was expected but the block did not classify as one (torn write, host-path corruption, or a read
    ///  fault). Warn and proceed: an unverifiable record removes a safety net, not the tape's data.
    /// </summary>
    Unreadable,
    /// <summary>
    /// The media id differs — a cartridge swapped mid-operation. Every in-memory assumption is void, including the
    ///  TOC; nothing is correctable.
    /// </summary>
    WrongMedia,
    /// <summary>
    /// Right series, wrong cartridge. File addresses are physical-per-volume, so every address in the TOC would
    ///  resolve to garbage on this volume.
    /// </summary>
    WrongVolume,
    /// <summary>
    /// Identity confirmed, on-volume index differs — a recoverable navigation miscount. Repaired in place (SH-10).
    /// </summary>
    SetIndexDrift,
    /// <summary>
    /// Right media, right volume, right position — but a DIFFERENT set stands there: both <c>SetId</c>s are known and
    ///  differ (Design-Format-v2 §5.4). The TOC describes another set than the tape holds — a stale or foreign TOC.
    ///  Not repairable by repositioning; terminal on every path, like <see cref="WrongVolume"/>.
    /// </summary>
    SetIdMismatch,
}

/// <summary>Wire form of the 2.1 set header (record kind <c>SetHeader</c>, Design-Format-v2 §5.4).</summary>
internal sealed class TapeSetHeaderWire : TapeHeaderWire
{
    public int Volume;
    public int VolumeSetIndex;
    public int GlobalSetIndex;
    public Guid SetId;
    public string Description = "";

    public static readonly TapeSchema<TapeSetHeaderWire> Schema = new(TapeRecordKind.SetHeader, inherits: Shared)
    {
        // ── scalars 1–31 (1–3 inherited) ──
        { 4,  w => w.Volume,         (w, v) => w.Volume = v,         FieldFlags.Required },
        { 5,  w => w.VolumeSetIndex, (w, v) => w.VolumeSetIndex = v },
        { 6,  w => w.GlobalSetIndex, (w, v) => w.GlobalSetIndex = v, FieldFlags.Required },
        { 7,  w => w.SetId,          (w, v) => w.SetId = v },        // optional: empty for headers built without a set
        // ── strings 32–47 ──
        { 32, w => w.Description,    (w, v) => w.Description = v },
    };
}

/// <summary>
/// Per-set header written as the first block of a backup set's data region, positively identifying which set of which
///  medium the tape head is actually standing on.
/// </summary>
/// <remarks>
/// <para>
/// An immutable identity projection of one <see cref="TapeSetTOC"/> plus its position in the <see cref="TapeTOC"/>:
///  the series <see cref="MediaId"/>, the set's own <see cref="SetId"/>, the <see cref="Volume"/> carrying it, and the
///  set's two indices. It carries <b>a-priori facts only</b> (SH-5) — everything here is known before the set's first
///  file is written, so nothing post-hoc (file counts, totals, hashes) can ever appear in it.
/// </para>
/// <para>
/// Unlike the media header, the set header carries <b>no tapemark</b> (SH-2): it is always the first thing written at
///  an already-legal position, and the write itself truncates, so everything after it is a sequential append. It
///  therefore contributes no mark and never alters setmark/filemark arithmetic (SH-3).
/// </para>
/// <para>
/// Build one only via <see cref="TapeTOC.CreateSetHeader(int)"/> / <see cref="TapeTOC.CreateSetHeaderForCurrentSet"/> —
///  <see cref="TapeSetTOC"/> knows neither its own index nor the media identity, so the TOC is the sole factory.
/// </para>
/// </remarks>
public sealed record TapeSetHeader : TapeHeader
{
    /// <summary>UTF-8 byte budget for <see cref="Description"/>, leaving ample room inside the frame.</summary>
    private const int c_maxDescriptionBytes = 15 * 1024;

    /// <inheritdoc/>
    public override TapeHeaderKind Kind => TapeHeaderKind.Set;

    /// <summary>
    /// The series/cartridge identity, shared with (and copied from) the TOC's <c>MediaId</c> — the same value the media
    ///  header carries.
    /// </summary>
    /// <remarks>
    /// Not redundant with the media header's copy: the media header is read once per media LOAD, this one at every
    ///  content-set ACCESS. That is what catches a cartridge swapped mid-operation — and what makes a set-index mismatch
    ///  interpretable at all: with identity confirmed, a mismatch is a recoverable navigation drift rather than a wrong tape.
    /// </remarks>
    public Guid MediaId
    {
        get => Id;
        init => Id = value;
    }

    /// <summary>
    /// The set's own identity (<see cref="TapeSetTOC.SetId"/>) — the same value every 2.1 file header of the set carries.
    ///  <see cref="Guid.Empty"/> for legacy set headers, which predate set identities.
    /// </summary>
    public Guid SetId { get; init; }

    /// <summary>This set's on-tape block size — reinterprets the base <see cref="TapeHeader.BlockSize"/> slot.</summary>
    /// <remarks>
    /// <b>Advisory only (SH-11).</b> The TOC stays authoritative for every set parameter; a disagreement is logged, never
    ///  acted on.
    /// </remarks>
    public uint SetBlockSize
    {
        get => BlockSize;
        init => BlockSize = value;
    }

    /// <summary>The volume carrying THIS set, as recorded in its <see cref="TapeSetTOC"/>. Never negative.</summary>
    public required int Volume { get; init; }

    /// <summary>
    /// The set's 0-based index on its own volume — the <b>functional</b> index, since it is what drives (and verifies)
    ///  navigation. Resets to 0 on every continuation volume.
    /// </summary>
    public required int VolumeSetIndex { get; init; }

    /// <summary>The set's 1-based index within the whole series — <b>attribution</b>, and advisory (SH-11).</summary>
    public required int GlobalSetIndex { get; init; }

    /// <summary>
    /// Creation-time snapshot of the set's description, kept for diagnostics only; may be <see langword="null"/> when
    ///  none was recorded (<see cref="DisplayName"/> then synthesizes one). NOT the live value.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// A never-empty, human-readable name: the recorded <see cref="Description"/> when present, otherwise one
    ///  synthesized from the set's indices.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Description)
            ? Description!
            : $"Set #{GlobalSetIndex} · vol {Volume} · {CreatedUtc:yyyy-MM-dd HH:mm}";

    /// <summary>
    /// Clamps a candidate description to the header's UTF-8 byte budget so the framed record always fits one
    ///  <see cref="TapeHeader.FixedHeaderBlockSize"/> block. A null or empty name maps to <see langword="null"/>.
    /// </summary>
    public static string? ClampName(string? name) => ClampUtf8(name, c_maxDescriptionBytes);

    #region *** Format 2.1 ***

    /// <inheritdoc/>
    public override void WriteBody(TapeFieldWriter fields) => TapeSetHeaderWire.Schema.Write(fields, new TapeSetHeaderWire
    {
        Id = MediaId,
        CreatedUtc = CreatedUtc,
        BlockSize = SetBlockSize,
        Volume = Volume,
        VolumeSetIndex = VolumeSetIndex,
        GlobalSetIndex = GlobalSetIndex,
        SetId = SetId,
        Description = Description ?? "",          // "" is elided; read back as null
    });

    /// <summary>Reads a 2.1 set header body. Called by <see cref="TapeHeader.ReadBody"/>.</summary>
    internal static TapeSetHeader ReadWire(TapeFieldReader fields)
    {
        TapeSetHeaderWire w = TapeSetHeaderWire.Schema.Read(fields, new TapeSetHeaderWire());
        return new TapeSetHeader
        {
            MediaId = w.Id,
            CreatedUtc = w.CreatedUtc,
            SetBlockSize = w.BlockSize,
            Volume = w.Volume,
            VolumeSetIndex = w.VolumeSetIndex,
            GlobalSetIndex = w.GlobalSetIndex,
            SetId = w.SetId,
            Description = string.IsNullOrEmpty(w.Description) ? null : w.Description,
        };
    }

    #endregion

    #region *** Legacy read — removed in Phase 6 ***

    /// <summary>
    /// Reads the set-specific fields after the shared preamble has been decoded. Called only by
    ///  <see cref="LegacyHeaderReader.Read"/> once the kind byte selected <see cref="TapeHeaderKind.Set"/>.
    /// </summary>
    internal static TapeSetHeader ConstructBody(LegacyDeserializer d, in TapeHeaderPreamble p)
    {
        int volume = d.DeserializeInt32();
        int volIndex = d.DeserializeInt32();
        int gblIndex = d.DeserializeInt32();
        string descr = d.DeserializeString();

        return new TapeSetHeader
        {
            MediaId = p.Id,
            CreatedUtc = p.CreatedUtc,
            BlockSize = p.BlockSize,
            Volume = volume,
            VolumeSetIndex = volIndex,
            GlobalSetIndex = gblIndex,
            Description = string.IsNullOrEmpty(descr) ? null : descr,
        };
    }

    #endregion

    /// <inheritdoc/>
    public override string ToString() =>
        $"Set header — set #{GlobalSetIndex} (volume {Volume}, #{VolumeSetIndex} on volume), " +
        $"set id {(SetId == Guid.Empty ? "—" : SetId.ToString("N"))}, media id {MediaId:N}, " +
        $"created {CreatedUtc:u}, name \"{DisplayName}\"";
}
