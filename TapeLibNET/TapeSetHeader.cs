using System;

namespace TapeLibNET;

/// <summary>
/// Per-set header written as the first block of a backup set's data region, positively
///  identifying which set of which medium the tape head is actually standing on.
/// </summary>
/// <remarks>
/// <para>
/// An immutable identity projection of one <see cref="TapeSetTOC"/> plus its position in the
///  <see cref="TapeTOC"/>: the series <see cref="MediaId"/>, the <see cref="Volume"/> carrying this
///  set, and the set's two indices. It carries <b>a-priori facts only</b> (SH-5) — everything here is
///  known before the set's first file is written, so nothing post-hoc (file counts, totals, hashes)
///  can ever appear in it.
/// </para>
/// <para>
/// Unlike the media header, the set header carries <b>no tapemark</b> (SH-2): it is always the first
///  thing written at an already-legal position (post-mark, at begin-of-content, or at EOD), and the
///  write itself truncates, so everything after it is a sequential append. It therefore contributes
///  no mark and never alters setmark/filemark arithmetic (SH-3).
/// </para>
/// <para>
/// Build one only via <see cref="TapeTOC.CreateSetHeader(int)"/> /
///  <see cref="TapeTOC.CreateSetHeaderForCurrentSet"/> — <see cref="TapeSetTOC"/> knows neither its
///  own index nor the media identity, so the TOC is the sole factory.
/// </para>
/// </remarks>
public sealed record TapeSetHeader : TapeHeader
{
    /// <summary>UTF-8 byte budget for <see cref="Description"/>, leaving ample room inside the frame.</summary>
    private const int c_maxDescriptionBytes = 15 * 1024;

    /// <inheritdoc/>
    public override TapeHeaderKind Kind => TapeHeaderKind.Set;

    /// <summary>
    /// The series/cartridge identity, shared with (and copied from) the TOC's <c>MediaId</c> — the
    ///  same value the media header carries.
    /// </summary>
    /// <remarks>
    /// Not redundant with the media header's copy despite checking the same fact: the media header is
    ///  read once per media LOAD, this one at every content-set ACCESS. That is what catches a
    ///  cartridge swapped mid-operation — and, more importantly, what makes a set-index mismatch
    ///  interpretable at all: with identity confirmed, a mismatch is a recoverable navigation drift
    ///  rather than a wrong tape.
    /// </remarks>
    public Guid MediaId
    {
        get => Id;
        init => Id = value;
    }

    /// <summary>
    /// This set's on-tape block size — reinterprets the base <see cref="TapeHeader.BlockSize"/> slot.
    /// </summary>
    /// <remarks>
    /// <b>Advisory only (SH-11).</b> The TOC stays authoritative for every set parameter; a
    ///  disagreement is logged, never acted on. By the time this header is parsed the reader has
    ///  already committed to the TOC's block size, so honouring it here would demote the TOC to a
    ///  secondary authority that nothing else in the library recognizes.
    /// </remarks>
    public uint SetBlockSize
    {
        get => BlockSize;
        init => BlockSize = value;
    }

    /// <summary>The volume carrying THIS set, as recorded in its <see cref="TapeSetTOC"/>.</summary>
    public required int Volume { get; init; }

    /// <summary>
    /// The set's 0-based index on its own volume — the <b>functional</b> index, since it is what
    ///  drives (and verifies) navigation.
    /// </summary>
    /// <remarks>
    /// Equals <c>setIndex - TapeTOC.FirstSetOnVolume</c>, and therefore resets to 0 on every
    ///  continuation volume. This is the value a restore compares against
    ///  <see cref="TapeTOC.CurrentSetIndexOnVolume"/> before delivering a single file byte.
    /// </remarks>
    public required int VolumeSetIndex { get; init; }

    /// <summary>
    /// The set's 1-based index within the whole series — <b>attribution</b>, and advisory (SH-11).
    /// </summary>
    /// <remarks>
    /// Checked but never gating: attribution can legitimately shift after a TOC import or a
    ///  partial-series rebuild, so a mismatch logs a warning and nothing more. Gating on it would
    ///  fail otherwise-correct restores.
    /// </remarks>
    public required int GlobalSetIndex { get; init; }

    /// <summary>
    /// Creation-time snapshot of the set's description, kept for diagnostics only; may be
    ///  <see langword="null"/> when none was recorded (<see cref="DisplayName"/> then synthesizes one).
    /// </summary>
    /// <remarks>
    /// The single non-index field admitted to the record, for the same reason
    ///  <see cref="TapeMediaHeader.OriginalName"/> is admitted: a drift or mismatch message must name
    ///  WHICH set, not merely an integer. Like the media name, it is NOT the live value — renaming a
    ///  set rewrites the TOC, never this header.
    /// </remarks>
    public string? Description { get; init; }

    /// <summary>
    /// A never-empty, human-readable name: the recorded <see cref="Description"/> when present,
    ///  otherwise one synthesized from the set's indices.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Description)
            ? Description!
            : $"Set #{GlobalSetIndex} · vol {Volume} · {CreatedUtc:yyyy-MM-dd HH:mm}";

    /// <summary>
    /// Clamps a candidate description to the header's UTF-8 byte budget so the framed record always
    ///  fits one <see cref="TapeHeader.FixedHeaderBlockSize"/> block. A null or empty name maps to
    ///  <see langword="null"/> ("no description recorded").
    /// </summary>
    public static string? ClampName(string? name) => ClampUtf8(name, c_maxDescriptionBytes);

    /// <inheritdoc/>
    public override void SerializeTo(TapeSerializer s)
    {
        SerializePreamble(s);
        s.Serialize(Volume);
        s.Serialize(VolumeSetIndex);
        s.Serialize(GlobalSetIndex);
        s.Serialize(Description ?? string.Empty);   // empty stands in for "none"; normalized back to null on read
    }

    /// <summary>
    /// Reads the set-specific fields after the shared preamble has been decoded. Called only by
    ///  <see cref="TapeHeader.ConstructFrom"/> once the kind byte selected <see cref="TapeHeaderKind.Set"/>.
    /// </summary>
    internal static TapeSetHeader ConstructBody(TapeDeserializer d, in TapeHeaderPreamble p)
    {
        int volume   = d.DeserializeInt32();
        int volIndex = d.DeserializeInt32();
        int gblIndex = d.DeserializeInt32();
        string descr = d.DeserializeString();

        return new TapeSetHeader
        {
            MediaId        = p.Id,
            CreatedUtc     = p.CreatedUtc,
            BlockSize      = p.BlockSize,
            Volume         = volume,
            VolumeSetIndex = volIndex,
            GlobalSetIndex = gblIndex,
            Description    = string.IsNullOrEmpty(descr) ? null : descr,
        };
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"Set header — set #{GlobalSetIndex} (volume {Volume}, #{VolumeSetIndex} on volume), " +
        $"media id {MediaId:N}, created {CreatedUtc:u}, name \"{DisplayName}\"";
}
