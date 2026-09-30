namespace TapeLibNET;

/// <summary>What one block turned out to be. Every value but <see cref="Foreign"/> is a POSITIVE finding.</summary>
/// <remarks>
/// Each outcome rests on evidence in the block itself, never on another test having failed. Concluding
///  "TOC" merely because a block carried our signature and no header parsed turned every CRC-damaged
///  header into a phantom TOC copy.
/// </remarks>
public enum HeaderBlockIdentity
{
    /// <summary>Nothing we recognize: foreign data, legacy file content, or a record damaged in its signature.</summary>
    Foreign = 0,

    /// <summary>A framed header that verified and parsed: media, set, or calibration.</summary>
    Header,

    /// <summary>A framed record of ours that did NOT verify — a torn frame, a CRC mismatch, or an unparseable payload.</summary>
    DamagedRecord,

    /// <summary>The first block of a table-of-contents copy, verified structurally by <see cref="TapeTOC.TryPeek"/>.</summary>
    TocCopy,
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
    TapeFramer.FrameStatus FrameStatus = TapeFramer.FrameStatus.NotFramed,
    ushort TocVersion = 0,
    Guid TocMediaId = default)
{
    /// <summary>The foreign outcome, spelled out — rather than trusting <c>default</c> for a struct.</summary>
    public static readonly IdentifiedBlock Foreign = new(HeaderBlockIdentity.Foreign);
}

// TOC-less identification of a block already read from tape (SM-2). Split out from the I/O half so
//  a caller that holds bytes — and no TOC, no navigator, no agent — can still classify them.
/// <include file='docs/TapeHeaderBlock.xml' path='docs/TapeHeaderBlock/TryIdentifyHeaderBlock/*' />
public static partial class TapeHeaderBlock
{
    #region *** Header parse ***

    /// <summary>
    /// Parses one header block into its concrete kind. Pure, total, and TOC-free: any input that is not a
    ///  valid framed header yields <see langword="false"/> rather than throwing.
    /// </summary>
    /// <param name="block">The block bytes as read from tape; only the frame at the front is examined.</param>
    /// <param name="length">Bytes actually read (may be less than <paramref name="block"/>'s length).</param>
    /// <param name="header">The media, set, or calibration header; null when unidentifiable.</param>
    /// <returns><see langword="true"/> when a header of any kind was identified.</returns>
    /// <remarks>
    /// Costs ONE parse for all three kinds: <see cref="TapeHeader.ConstructFrom"/> dispatches on the kind
    ///  byte and returns the right subtype, so a caller need not know the kinds apart before parsing —
    ///  only after. Padding beyond the frame is ignored, as on every other read path.
    /// <para>
    /// Answers "is this an intact header?" only. A caller that must tell a DAMAGED header from foreign data,
    ///  or recognize a TOC copy, uses <see cref="IdentifyBlock"/>.
    /// </para>
    /// <para>
    /// <b>Deliberately NOT a change to <see cref="TapeAgentBase.ClassifySetHeader"/>.</b> That method takes
    ///  an already-parsed <see cref="TapeSetHeader"/> and compares it with the TOC's expectation — it never
    ///  parsed anything. Parse and compare were already separate; this file only names the parse half and
    ///  makes it public.
    /// </para>
    /// </remarks>
    public static bool TryIdentifyHeaderBlock(byte[] block, int length, out TapeHeader? header)
    {
        header = null;

        if (block is null || length <= 0 || length > block.Length)
            return false;

        // Classify() is already total — TapeFramer.Unpack swallows every framing/CRC/format fault and
        //  returns null — so no try/catch is warranted here. Wrapping it in one would only hide a future
        //  regression in that guarantee.
        header = Classify(block, length);
        return header is not null;
    }

    /// <summary>
    /// Span overload for callers holding a slice rather than the whole block. Copies into a temporary,
    ///  since the framer's deserializer works over a <see cref="Stream"/>.
    /// </summary>
    /// <remarks>
    /// Provided for call-site convenience only. The scanner uses the array overload, which allocates
    ///  nothing beyond the buffer it already owns — worth preferring on the per-fragment path.
    /// </remarks>
    public static bool TryIdentifyHeaderBlock(ReadOnlySpan<byte> block, out TapeHeader? header)
        => TryIdentifyHeaderBlock(block.ToArray(), block.Length, out header);

    #endregion

    #region *** Positive identification ***

    /// <summary>
    /// Identifies one block positively: a TOC copy, an intact header, a damaged header — or none of them.
    ///  Pure, total, TOC-free.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement first, content second.</b> A framed header carries our signature at
    ///  <see cref="FramedPayloadOffset"/>, behind the length prefix; a TOC stream carries it at byte 0. A
    ///  header damaged BEHIND its signature keeps the signature where it was — so it is recognized as
    ///  ours, and damaged, instead of falling through to "must be a TOC".
    /// </para>
    /// <para>
    /// <b>The TOC is tested first, and structurally.</b> Offset 4 of a TOC stream holds the low bytes of its
    ///  UID seed, which can coincide with a signature. Offset 0 of a plausible frame holds a length of at
    ///  most <see cref="Size"/>, whose high bytes are zero — it can never read as <c>"TF"</c> plus a
    ///  version. So a TOC match cannot swallow an intact frame, while the reverse order could.
    /// </para>
    /// <para>
    /// <b>The signature alone never identifies a TOC.</b> A legacy aligned file record
    ///  (<see cref="TapeFileInfo.SerializeHeaderTo"/>) opens a block with the very bytes a v0x0101 TOC does.
    ///  <see cref="TapeTOC.TryPeek"/> checks what only a TOC has: the fields that follow the signature.
    /// </para>
    /// <para>
    /// A record whose SIGNATURE is damaged cannot be told from foreign data, and is reported as
    ///  <see cref="HeaderBlockIdentity.Foreign"/>. Claiming more would be guessing.
    /// </para>
    /// </remarks>
    public static IdentifiedBlock IdentifyBlock(byte[] block, int length)
    {
        if (block is null || length <= 0 || length > block.Length)
            return IdentifiedBlock.Foreign;

        // 1. A TOC copy: a raw stream, verified by its structure — not by a header failing to parse.
        if (TapeTOC.TryPeek(block, length, out ushort tocVersion, out Guid tocMediaId))
            return new(HeaderBlockIdentity.TocCopy, TocVersion: tocVersion, TocMediaId: tocMediaId);

        // 2. A framed record: ours whether or not it verifies.
        if (HasSignatureAt(block, length, FramedPayloadOffset, out _))
        {
            var status = TapeFramer.TryUnpack(block, length, out TapeHeader? header);

            return status == TapeFramer.FrameStatus.Ok && header is not null
                ? new(HeaderBlockIdentity.Header, Header: header, FrameStatus: status)
                : new(HeaderBlockIdentity.DamagedRecord, FrameStatus: status);
        }

        return IdentifiedBlock.Foreign;
    }

    #endregion

    #region *** Signature probe ***

    /// <summary>Bytes <see cref="TapeFramer"/> prepends to a framed payload: the int32 length.</summary>
    private const int FramedPayloadOffset = sizeof(int);

    /// <summary>Lowest and highest version a record of ours can plausibly carry.</summary>
    /// <remarks>Every record written so far is 0x01xx: 0x0100 and 0x0101 library-wide, 0x0102 for the TOC.</remarks>
    private const ushort MinPlausibleVersion = 0x0100, MaxPlausibleVersion = 0x01FF;

    /// <summary>
    /// True when <paramref name="block"/> carries the library's own record signature — i.e. it is "ours",
    ///  whether or not it parses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two layouts, both legitimate.</b> A FRAMED record (any <see cref="TapeHeader"/>) is
    ///  <c>[len][payload][crc]</c>, so its signature begins at <see cref="FramedPayloadOffset"/>. A raw
    ///  serialized stream carries its signature at byte 0. This probe accepts either.
    /// </para>
    /// <para>
    /// <b>Only answers "is this ours?", never "which record?".</b> Use <see cref="IdentifyBlock"/> for that.
    ///  Reading "signature present, no header parses" as a TOC copy turned every CRC-damaged header into a
    ///  phantom TOC.
    /// </para>
    /// </remarks>
    public static bool CarriesRecordSignature(byte[] block, int length)
    {
        if (block is null || length <= 0)
            return false;

        int usable = Math.Min(length, block.Length);

        return HasSignatureAt(block, usable, 0, out _)
            || HasSignatureAt(block, usable, FramedPayloadOffset, out _);
    }

    /// <summary>
    /// Validates our signature at one offset, accepting any PLAUSIBLE version. Every malformed input is
    ///  "not ours".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Tolerant of the version, deliberately.</b> <see cref="TapeDeserializer.ValidateSignature()"/>
    ///  demands exactly <see cref="TapeSerializer.Version"/>, which made every current TOC — written as
    ///  0x0102 — read as foreign. It would do the same to a header of a newer format, which is our record,
    ///  merely unreadable to this build: exactly what <see cref="TapeFramer.FrameStatus.Unparseable"/>
    ///  reports once the record gets past this check.
    /// </para>
    /// <para>
    /// Bounded rather than open: the two signature bytes alone match random data once in 65 536 blocks;
    ///  requiring a 0x01xx version as well brings that down to once in 16 million.
    /// </para>
    /// </remarks>
    private static bool HasSignatureAt(byte[] block, int usable, int offset, out ushort version)
    {
        version = 0;

        if (offset >= usable)
            return false;

        try
        {
            using var ms = new MemoryStream(block, offset, usable - offset, writable: false);

            return new TapeDeserializer(ms).ValidateSignature(out version)
                && version is >= MinPlausibleVersion and <= MaxPlausibleVersion;
        }
        catch (Exception)
        {
            // A block too short to even hold a signature is simply not ours — never an error.
            return false;
        }
    }

    #endregion
}
