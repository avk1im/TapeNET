using TapeLibNET.Toc;
using TapeLibNET.Agents;
using TapeLibNET.Calibration;
using TapeLibNET.Format;
using TapeLibNET.Legacy;

namespace TapeLibNET.Headers;

/// <summary>What one block turned out to be. Every value but <see cref="Foreign"/> is a POSITIVE finding.</summary>
/// <remarks>
/// Each outcome rests on evidence in the block itself, never on another test having failed. Concluding "TOC" merely
///  because a block carried our signature and no header parsed turned every CRC-damaged header into a phantom TOC copy.
///  New values are APPENDED.
/// </remarks>
public enum HeaderBlockIdentity
{
    /// <summary>
    /// Nothing we recognize as a header or TOC: foreign data, file content (legacy, or a 2.1 file header at a block
    ///  boundary), or a record damaged in its magic / signature.
    /// </summary>
    Foreign = 0,
    /// <summary>A framed header that verified and parsed: media, set, or calibration.</summary>
    Header,
    /// <summary>
    /// A record of ours that did NOT verify — a torn frame, a CRC mismatch, or an intact record this build cannot read
    ///  (unknown kind, newer format, unknown critical field).
    /// </summary>
    DamagedRecord,
    /// <summary>The first block of a table-of-contents copy, verified structurally by <see cref="TapeTOC.TryPeek"/>.</summary>
    TocCopy,
    /// <summary>
    /// A 2.1 calibration checkpoint block, identified by its record kind (Design-Format-v2 §5.9). Legacy checkpoints carry
    ///  no kind of their own and still identify as <see cref="DamagedRecord"/>, as they always did.
    /// </summary>
    CalibrationCheckpoint,
}

/// <summary>The outcome of <see cref="TapeHeaderBlock.IdentifyBlock"/>, with whatever evidence the kind carries.</summary>
/// <param name="Kind">What the block is.</param>
/// <param name="Header">Set for <see cref="HeaderBlockIdentity.Header"/>.</param>
/// <param name="FrameStatus">Why the record failed; meaningful ONLY for <see cref="HeaderBlockIdentity.DamagedRecord"/>.</param>
/// <param name="TocVersion">Set for <see cref="HeaderBlockIdentity.TocCopy"/>.</param>
/// <param name="TocMediaId">Set for <see cref="HeaderBlockIdentity.TocCopy"/>; empty for a pre-MediaId TOC.</param>
public readonly record struct IdentifiedBlock(
    HeaderBlockIdentity Kind,
    TapeHeader? Header = null,
    TapeFrameStatus FrameStatus = TapeFrameStatus.NotFramed,
    ushort TocVersion = 0,
    Guid TocMediaId = default)
{
    /// <summary>The foreign outcome, spelled out — rather than trusting <c>default</c> for a struct.</summary>
    public static readonly IdentifiedBlock Foreign = new(HeaderBlockIdentity.Foreign);
}

/// <summary>
/// TOC-less identification of a block already read from tape (SM-2). Split out from the I/O half so a caller that holds
///  bytes — and no TOC, no navigator, no agent — can still classify them.
/// </summary>
public static partial class TapeHeaderBlock
{
    #region *** Header parse ***

    /// <summary>
    /// Parses one header block into its concrete kind. Pure, total, and TOC-free: any input that is not a valid framed
    ///  header yields <see langword="false"/> rather than throwing.
    /// </summary>
    /// <param name="block">The block bytes as read from tape; only the frame at the front is examined.</param>
    /// <param name="length">Bytes actually read (may be less than <paramref name="block"/>'s length).</param>
    /// <param name="header">The media, set, or calibration header; null when unidentifiable.</param>
    /// <returns><see langword="true"/> when a header of any kind was identified.</returns>
    /// <remarks>
    /// Costs ONE parse for all kinds and both formats: <see cref="TapeFramer.TryUnpackHeader(byte[], int, out TapeHeader?)"/>
    ///  dispatches on the first bytes and on the record kind, and returns the right subtype. Padding beyond the frame is
    ///  ignored.
    /// <para>
    /// Answers "is this an intact header?" only. A caller that must tell a DAMAGED header from foreign data, or
    ///  recognize a TOC copy, uses <see cref="IdentifyBlock"/>.
    /// </para>
    /// <para>
    /// <b>Deliberately NOT a change to <see cref="TapeAgentBase.ClassifySetHeader"/>.</b> That method takes an
    ///  already-parsed <see cref="TapeSetHeader"/> and compares it with the TOC's expectation — it never parses.
    /// </para>
    /// </remarks>
    public static bool TryIdentifyHeaderBlock(byte[] block, int length, out TapeHeader? header)
    {
        header = null;
        if (block is null || length <= 0 || length > block.Length)
            return false;

        // Classify() is already total, so no try/catch is warranted here. Wrapping it in one would only hide a future
        //  regression in that guarantee.
        header = Classify(block, length);
        return header is not null;
    }

    /// <summary>Span overload for callers holding a slice rather than the whole block. Copies into a temporary.</summary>
    /// <remarks>
    /// Provided for call-site convenience only. The scanner uses the array overload, which allocates nothing beyond the
    ///  buffer it already owns — worth preferring on the per-fragment path.
    /// </remarks>
    public static bool TryIdentifyHeaderBlock(ReadOnlySpan<byte> block, out TapeHeader? header)
        => TryIdentifyHeaderBlock(block.ToArray(), block.Length, out header);

    #endregion

    #region *** Positive identification ***

    /// <summary>
    /// Identifies one block positively: a TOC copy, an intact header, a calibration checkpoint, a damaged record — or
    ///  none of them. Pure, total, TOC-free (Design-Format-v2 §5.9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Format first, by the first bytes.</b> <c>TpN#</c> at byte 0 is a 2.1 record; anything else goes to the frozen
    ///  legacy probe (<see cref="LegacyIdentify.IdentifyBlock"/>). No legacy record can begin with the magic: a legacy
    ///  TOC or file header starts with <c>"TF"</c>, a legacy frame with an int32 length whose byte 3 is zero.
    /// </para>
    /// <para>
    /// <b>2.1, by the record kind:</b>
    /// <list type="bullet">
    ///   <item><c>TocHeader</c> → <see cref="HeaderBlockIdentity.TocCopy"/> when its header record parses
    ///    (<see cref="TapeTOC.TryPeek"/>), else <see cref="HeaderBlockIdentity.DamagedRecord"/>.</item>
    ///   <item><c>FileHeader</c> → <see cref="HeaderBlockIdentity.Foreign"/>: content, the first file of a set standing at
    ///    a block boundary — not a damaged or newer record.</item>
    ///   <item><c>CalibrationCheckpoint</c> → <see cref="HeaderBlockIdentity.CalibrationCheckpoint"/> when it verifies,
    ///    else <see cref="HeaderBlockIdentity.DamagedRecord"/> with the reason.</item>
    ///   <item>Every other kind → the header frame: <see cref="HeaderBlockIdentity.Header"/> when it verifies and parses,
    ///    else <see cref="HeaderBlockIdentity.DamagedRecord"/> with the reason. An unknown kind or a newer major with an
    ///    intact CRC is "ours, from a newer TapeNET" (<see cref="TapeFrameStatus.Unparseable"/>).</item>
    /// </list>
    /// </para>
    /// <para>
    /// A record whose MAGIC is damaged cannot be told from foreign data, and is reported as
    ///  <see cref="HeaderBlockIdentity.Foreign"/>. A record damaged BEHIND its magic is ours, damaged — never a phantom TOC.
    /// </para>
    /// </remarks>
    public static IdentifiedBlock IdentifyBlock(byte[] block, int length)
    {
        if (block is null || length <= 0 || length > block.Length)
            return IdentifiedBlock.Foreign;

        ReadOnlySpan<byte> data = block.AsSpan(0, length);
        if (!TapeFormat.IsV2(data))
            return LegacyIdentify.IdentifyBlock(block, length);

        // Ours by the magic. A prologue that does not even parse is a torn record.
        if (TapeRecordReader.ParsePrologue(data, out TapeRecordReader.Prologue prologue) != TapeRecordReader.PrologueStatus.Ok)
            return new(HeaderBlockIdentity.DamagedRecord, FrameStatus: TapeFrameStatus.NotFramed);

        switch ((TapeRecordKind)prologue.RawKind)
        {
            case TapeRecordKind.TocHeader:
                // The TOC stream carries no per-block CRC; a header record that does not parse is damaged or newer.
                return TapeTOC.TryPeek(block, length, out ushort tocVersion, out Guid tocMediaId)
                    ? new(HeaderBlockIdentity.TocCopy, TocVersion: tocVersion, TocMediaId: tocMediaId)
                    : new(HeaderBlockIdentity.DamagedRecord, FrameStatus: TapeFrameStatus.Unparseable);

            case TapeRecordKind.FileHeader:
                return IdentifiedBlock.Foreign;    // content: the first file of a set at a block boundary

            case TapeRecordKind.CalibrationCheckpoint:
            {
                var status = TapeFrame.TryUnpack(data, out TapeCalibrationCheckpoint? _, out _, out _);
                return status == TapeFrameStatus.Ok
                    ? new(HeaderBlockIdentity.CalibrationCheckpoint, FrameStatus: status)
                    : new(HeaderBlockIdentity.DamagedRecord, FrameStatus: status);
            }

            default:
            {
                var status = TapeFramer.TryUnpackHeader(block, length, out TapeHeader? header);
                return status == TapeFrameStatus.Ok && header is not null
                    ? new(HeaderBlockIdentity.Header, Header: header, FrameStatus: status)
                    : new(HeaderBlockIdentity.DamagedRecord, FrameStatus: status);
            }
        }
    }

    #endregion

    #region *** Signature probe ***

    /// <summary>Bytes a LEGACY frame prepends to its payload: the int32 length.</summary>
    private const int FramedPayloadOffset = sizeof(int);

    /// <summary>
    /// True when <paramref name="block"/> carries one of our record markers — i.e. it is "ours", whether or not it parses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three layouts, all legitimate.</b> A 2.1 record carries the <c>TpN#</c> magic at byte 0. A legacy FRAMED
    ///  record (<c>[len][payload][crc]</c>) carries the <c>"TF"</c> signature at <see cref="FramedPayloadOffset"/>; a
    ///  legacy raw stream (TOC) at byte 0.
    /// </para>
    /// <para>
    /// <b>Only answers "is this ours?", never "which record?".</b> Use <see cref="IdentifyBlock"/> for that.
    /// </para>
    /// </remarks>
    public static bool CarriesRecordSignature(byte[] block, int length)
    {
        if (block is null || length <= 0)
            return false;
        int usable = Math.Min(length, block.Length);
        return TapeFormat.IsV2(block.AsSpan(0, usable))
            || HasSignatureAt(block, usable, 0, out _)
            || HasSignatureAt(block, usable, FramedPayloadOffset, out _);
    }

    /// <summary>
    /// Validates the LEGACY signature at one offset, accepting any plausible 0x01xx version. Every malformed input is
    ///  "not ours". Frozen in <see cref="LegacyIdentify.HasSignatureAt"/>.
    /// </summary>
    private static bool HasSignatureAt(byte[] block, int usable, int offset, out ushort version)
        => LegacyIdentify.HasSignatureAt(block, usable, offset, out version);

    #endregion
}
