using System;
using System.Text;

namespace TapeLibNET;

/// <summary>Where the TOC lives on a medium, recorded in a <see cref="TapeMediaHeader"/>.</summary>
public enum TapeTocPlacement : byte
{
    /// <summary>Single-partition medium: the TOC follows the content in the same partition.</summary>
    InSet = 0,

    /// <summary>Initiator-partition medium: the TOC lives in its own partition.</summary>
    InPartition = 1,
}

/// <summary>
/// Media (volume) header written once at the beginning of medium (BOM) of the content partition,
///  positively identifying the cartridge as "ours" so a fresh load need not seek to end-of-data
///  merely to discover whether a TOC exists.
/// </summary>
/// <remarks>
/// <para>
/// An immutable identity projection of the <see cref="TapeTOC"/>: it carries only values that never
///  change once the medium is formatted — the series <see cref="MediaId"/>, the immutable per-tape
///  <see cref="Volume"/>, the creation time, where the TOC lives (<see cref="TocPlacement"/>), and a
///  creation-time snapshot of the name. Written once at format (and on a fresh continuation volume),
///  never rewritten. Placed on ALL formatted media, single- and multi-partition alike, so every
///  medium has one cheap, uniform identity/verification block.
/// </para>
/// <para>
/// The current, user-renameable media name is deliberately NOT stored here — renaming rewrites the
///  TOC, not this once-written header — so UIs must show the TOC's live description, never
///  <see cref="OriginalName"/>. Build one only via <see cref="TapeTOC.CreateHeader"/>.
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
    /// The TOC's on-tape block size — reinterprets the base <see cref="TapeHeader.BlockSize"/> slot.
    ///  Immutable at format; lets a reader adopt larger TOC blocks in future without probing.
    /// </summary>
    public uint TocBlockSize
    {
        get => BlockSize;
        init => BlockSize = value;
    }

    /// <summary>The volume number within a multi-volume series — immutable for the life of this tape.</summary>
    public int Volume { get; init; }

    /// <summary>
    /// <see cref="MediaPartition"/> where this header resides.
    /// </summary>
    public MediaPartition Partition { get; init; }

    /// <summary>Where the TOC lives, so a reader knows the layout without probing.</summary>
    public TapeTocPlacement TocPlacement { get; init; }

    /// <summary>
    /// Creation-time snapshot of the media name, kept only for recovery/diagnostics; may be
    ///  <see langword="null"/> when none was recorded (<see cref="DisplayName"/> then synthesizes one).
    /// </summary>
    /// <remarks>
    /// NOT the current media name: renaming rewrites the TOC, not this header. UIs should show the
    ///  TOC's live description instead of this value.
    /// </remarks>
    public string? OriginalName { get; init; }

    /// <summary>
    /// A never-empty, human-readable name: the recorded <see cref="OriginalName"/> when present,
    ///  otherwise one synthesized from <see cref="MediaId"/>, <see cref="Volume"/>, and
    ///  <see cref="TapeHeader.CreatedUtc"/>.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(OriginalName)
            ? OriginalName!
            : $"Media {MediaId:N} · vol {Volume} · {CreatedUtc:yyyy-MM-dd HH:mm}";

    /// <summary>
    /// Clamps a candidate name to the header's UTF-8 byte budget so the framed record always fits one
    ///  <see cref="TapeHeader.FixedHeaderBlockSize"/> block. A null or empty name maps to
    ///  <see langword="null"/> ("no name recorded").
    /// </summary>
    public static string? ClampName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        if (Encoding.UTF8.GetByteCount(name) <= c_maxOriginalNameBytes)
            return name;

        // Trim by whole characters until it fits — simple and safe; names this long never occur in practice.
        var span = name.AsSpan();
        while (span.Length > 0 && Encoding.UTF8.GetByteCount(span) > c_maxOriginalNameBytes)
            span = span[..^1];

        return span.ToString();
    }

    /// <inheritdoc/>
    public override void SerializeTo(TapeSerializer s)
    {
        SerializePreamble(s);

        s.Serialize(Volume);
        s.Serialize((byte)Partition);
        s.Serialize((byte)TocPlacement);
        s.Serialize(OriginalName ?? string.Empty);   // empty stands in for "no name"; normalized back to null on read
    }

    /// <summary>
    /// Reads the media-specific fields after the shared preamble has been decoded. Called only by
    ///  <see cref="TapeHeader.ConstructFrom"/> once the kind byte selected <see cref="TapeHeaderKind.Media"/>.
    /// </summary>
    internal static TapeMediaHeader ConstructBody(TapeDeserializer d, in TapeHeaderPreamble p)
    {
        int volume    = d.DeserializeInt32();
        var partition = (MediaPartition)(d.DeserializeBytes(1)?[0] ?? (byte)MediaPartition.Content);
        var placement = (TapeTocPlacement)(d.DeserializeBytes(1)?[0] ?? (byte)TapeTocPlacement.InSet);
        string name   = d.DeserializeString();

        return new TapeMediaHeader
        {
            MediaId      = p.Id,
            CreatedUtc   = p.CreatedUtc,
            BlockSize    = p.BlockSize,
            Volume       = volume,
            Partition    = partition,
            TocPlacement = placement,
            OriginalName = string.IsNullOrEmpty(name) ? null : name,
        };
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"Media header — id {MediaId:N}, volume {Volume} / partition {Partition}, created {CreatedUtc:u}, name \"{DisplayName}\"";
}
