namespace TapeLibNET;


// TOC-less identification of a block already read from tape (SM-2). Split out from the I/O half so
//  a caller that holds bytes — and no TOC, no navigator, no agent — can still classify them.
/// <include file='docs/TapeHeaderBlock.xml' path='docs/TapeHeaderBlock/TryIdentifyHeaderBlock/*' />
public static partial class TapeHeaderBlock
{
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
    /// Added for Scan Media, which surveys a cartridge from BOM forward with nothing to compare against.
    ///  The pre-existing <see cref="Classify"/> already did the parse; what was missing was a TOTAL,
    ///  span-friendly entry point that states its own failure instead of returning a bare null.
    /// </para>
    /// <para>
    /// <b>Deliberately NOT a change to <see cref="TapeAgentBase.ClassifySetHeader"/>.</b> That method takes
    ///  an already-parsed <see cref="TapeSetHeader"/> and compares it with the TOC's expectation — it never
    ///  parsed anything. Parse and compare were already separate; this file only names the parse half and makes
    ///  it public.
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
    /// Span overload for callers holding a slice rather than the whole block. Copies into a pooled-free
    ///  temporary, since the framer's deserializer works over a <see cref="Stream"/>.
    /// </summary>
    /// <remarks>
    /// Provided for call-site convenience only. The scanner uses the array overload, which allocates
    ///  nothing beyond the buffer it already owns — worth preferring on the per-fragment path.
    /// </remarks>
    public static bool TryIdentifyHeaderBlock(ReadOnlySpan<byte> block, out TapeHeader? header)
        => TryIdentifyHeaderBlock(block.ToArray(), block.Length, out header);

    /// <summary>Bytes <see cref="TapeFramer"/> prepends to a framed payload: the int32 length.</summary>
    private const int FramedPayloadOffset = sizeof(int);

    /// <summary>
    /// True when <paramref name="block"/> carries the library's own record signature — i.e. it is "ours",
    ///  whether or not it parses as a header. The cheap first test of the TOC-copy probe (§4.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two layouts, both legitimate.</b> A FRAMED record (any <see cref="TapeHeader"/>) is
    ///  <c>[len][payload][crc]</c>, so its signature begins at <see cref="FramedPayloadOffset"/>. A raw
    ///  serialized stream — notably a TOC copy — carries its signature at byte 0. The scanner meets both
    ///  while walking an unknown cartridge, so the probe accepts either rather than assuming one.
    /// </para>
    /// <para>
    /// A header and a TOC stream share the signature but diverge after it: a header follows with its kind
    ///  byte, a TOC with its own version word. So "signature present AND
    ///  <see cref="TryIdentifyHeaderBlock"/> fails" is the signature-only evidence for a TOC copy — enough
    ///  to report <i>"a table of contents survives at block N"</i> without deserializing it.
    /// </para>
    /// <para>
    /// Kept beside the header probe rather than in the scanner, so both halves of "is this one of ours?"
    ///  live at the single point that already owns the on-tape record grammar.
    /// </para>
    /// </remarks>
    public static bool CarriesRecordSignature(byte[] block, int length)
    {
        if (block is null || length <= 0)
            return false;

        int usable = Math.Min(length, block.Length);

        // Raw stream first — a TOC copy is the case this probe exists to serve, and the framed shape is
        //  already covered by TryIdentifyHeaderBlock for everything that actually parses.
        return HasSignatureAt(block, usable, 0)
            || HasSignatureAt(block, usable, FramedPayloadOffset);
    }

    /// <summary>Validates the record signature at one offset, treating every malformed input as "not ours".</summary>
    private static bool HasSignatureAt(byte[] block, int usable, int offset)
    {
        if (offset >= usable)
            return false;

        try
        {
            using var ms = new MemoryStream(block, offset, usable - offset, writable: false);
            return new TapeDeserializer(ms).ValidateSignature();
        }
        catch (Exception)
        {
            // A block too short to even hold a signature is simply not ours — never an error.
            return false;
        }
    }
}
