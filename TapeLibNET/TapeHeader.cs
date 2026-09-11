using System;

namespace TapeLibNET;

/// <summary>
/// Discriminates the kind of a <see cref="TapeHeader"/> record, written as a single byte right
///  after the shared signature so one framed read can classify any block-boundary record.
/// </summary>
public enum TapeHeaderKind : byte
{
    /// <summary>Not one of our headers — legacy content, blank, or foreign media.</summary>
    Unknown = 0,

    /// <summary>Media (volume) header at BOM. See <see cref="TapeMediaHeader"/>.</summary>
    Media = 1,

    /// <summary>Calibration run header at BOM of a scratch cartridge. See <see cref="TapeCalibrationHeader"/>.</summary>
    Calibration = 2,

    /// <summary>Per-set header at the front of a backup set's data region (Phase B).</summary>
    Set = 3,
}

/// <summary>
/// The shared preamble decoded from any <see cref="TapeHeader"/> block before the concrete kind
///  reads its own fields. Produced inside <see cref="TapeHeader.ConstructFrom"/> and handed to the
///  matching <c>ConstructBody</c>.
/// </summary>
internal readonly record struct TapeHeaderPreamble(
    TapeHeaderKind Kind, Guid Id, DateTime CreatedUtc, uint BlockSize);

/// <summary>
/// Abstract base for every self-identifying block-boundary record in TapeLibNET: the media
///  (volume) header, the per-set header, and the calibration run header.
/// </summary>
/// <remarks>
/// <para>
/// Every concrete header shares a fixed preamble — signature, a one-byte <see cref="Kind"/>
///  discriminator, an identifying <see cref="Id"/> (re-exposed by each kind under a domain name
///  such as <c>MediaId</c> or <c>RunId</c>), a creation timestamp, and the block size used to read
///  it back — followed by kind-specific fields. A single <see cref="TapeFramer.Unpack{T}"/> over
///  <see cref="TapeHeader"/> therefore classifies a block as media, set, calibration, or
///  (on any signature/CRC failure) foreign/blank in one read.
/// </para>
/// <para>
/// The whole record is framed by <see cref="TapeFramer"/> with an external CRC and copied into the
///  front of one block; the block's remaining bytes are padding, ignored on read-back. Backup and
///  set headers use the fixed <see cref="FixedHeaderBlockSize"/> block; the calibration header rides
///  in the calibration run's own (larger) block. Only the record grammar is shared — never the
///  physical write path.
/// </para>
/// </remarks>
public abstract record TapeHeader : ITapeSerializable
{
    /// <summary>
    /// Fixed on-tape block size for the backup media/set headers, reusing the TOC's proven 16 KiB
    ///  block — large enough to read a header in one <c>ReadDirect</c>, and above any drive's minimum
    ///  block-size quirks. The calibration header is exempt: it uses the run's block size instead.
    /// </summary>
    public const uint FixedHeaderBlockSize = 16 * 1024;

    /// <summary>The concrete kind of this header, written as the preamble discriminator byte.</summary>
    public abstract TapeHeaderKind Kind { get; }

    /// <summary>
    /// The raw identity. Protected so only the hierarchy touches it directly; each concrete kind
    ///  re-exposes it under a domain-specific name (<c>MediaId</c>, <c>RunId</c>).
    /// </summary>
    protected Guid Id { get; init; }

    /// <summary>
    /// When this header was created — UTC for calibration (<c>StartedUtc</c>), the TOC's creation
    ///  time for media. Only the tick value is persisted.
    /// </summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>The header block size recorded at write time; usage defined by descendant classes.</summary>
    protected uint BlockSize { get; init; }

    /// <summary>
    /// Writes the shared preamble — signature, <see cref="Kind"/>, <see cref="Id"/>,
    ///  <see cref="CreatedUtc"/>, <see cref="BlockSize"/>. Concrete headers call this first from
    ///  <see cref="SerializeTo"/>, then append their own fields.
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/> is written as 16 raw bytes (<see cref="Guid.ToByteArray"/>) so the on-tape
    ///  layout stays wire-identical to the calibration header, letting the unified framer read every
    ///  kind byte-for-byte.
    /// </remarks>
    protected void SerializePreamble(TapeSerializer s)
    {
        s.SerializeSignature();
        s.Serialize((byte)Kind);
        s.Serialize(Id.ToByteArray());
        s.Serialize(CreatedUtc);
        s.Serialize(BlockSize);
    }

    /// <summary>
    /// Reads and validates the shared preamble. Returns <see langword="null"/> when the signature
    ///  does not match (⇒ not one of our records), so the caller classifies the block as foreign.
    /// </summary>
    private static TapeHeaderPreamble? ReadPreamble(TapeDeserializer d)
    {
        if (!d.ValidateSignature())
            return null;

        var kind    = (TapeHeaderKind)(d.DeserializeBytes(1)?[0] ?? (byte)TapeHeaderKind.Unknown);
        var id      = new Guid(d.DeserializeBytes(16) ?? throw new FormatException("TapeHeader: Id"));
        var created = d.DeserializeDateTime();
        var bs      = d.DeserializeUInt32();

        return new TapeHeaderPreamble(kind, id, created, bs);
    }

    /// <summary>Writes this header (preamble + kind-specific fields) to <paramref name="s"/>.</summary>
    public abstract void SerializeTo(TapeSerializer s);

    /// <summary>
    /// Polymorphic factory: reads the preamble, then dispatches on <see cref="TapeHeaderKind"/> to
    ///  the matching concrete body reader. Inherited by every concrete kind, so
    ///  <c>TapeFramer.Unpack&lt;TapeMediaHeader&gt;</c>, <c>&lt;TapeCalibrationHeader&gt;</c>, or the
    ///  polymorphic <c>&lt;TapeHeader&gt;</c> all route here and the caller's <c>as T</c> narrows the result.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the signature does not match, or for a kind not yet wired
    ///  in. <c>Set</c> is wired in Phase B; until then a set block reads back as <see langword="null"/>.
    /// </remarks>
    public static ITapeSerializable? ConstructFrom(TapeDeserializer d)
    {
        if (ReadPreamble(d) is not { } p)
            return null;

        return p.Kind switch
        {
            TapeHeaderKind.Media       => TapeMediaHeader.ConstructBody(d, p),
            TapeHeaderKind.Calibration => TapeCalibrationHeader.ConstructBody(d, p),

            // TapeHeaderKind.Set is wired in Phase B.
            _ => null,
        };
    }

    /// <summary>A short, human-readable description used in user prompts and logs.</summary>
    public abstract override string ToString();
}
