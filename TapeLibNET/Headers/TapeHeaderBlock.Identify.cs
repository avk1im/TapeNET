using System.Diagnostics.CodeAnalysis;
using TapeLibNET.Calibration;
using TapeLibNET.Format;
using TapeLibNET.Legacy;
using TapeLibNET.Toc;

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
    /// Nothing we recognize: foreign data, legacy file content, an unverified 2.1 file header (a file body that merely
    ///  starts with the magic), or a record damaged in its magic / signature.
    /// </summary>
    Foreign = 0,

    /// <summary>A framed header that verified and parsed: media, set, or calibration.</summary>
    Header,

    /// <summary>
    /// A record of ours that did NOT verify — a torn frame, a CRC mismatch, or an intact record this build cannot read
    ///  (unknown kind, newer format, unknown critical field).
    /// </summary>
    DamagedRecord,

    /// <summary>The first block of a table-of-contents copy; <see cref="IdentifiedBlock.Toc"/> says what it describes.</summary>
    TocCopy,

    /// <summary>
    /// A 2.1 calibration checkpoint block, identified by its record kind (Design-FormatV21 §6). Legacy checkpoints carry
    ///  no kind of their own and still identify as <see cref="DamagedRecord"/>, as they always did.
    /// </summary>
    CalibrationCheckpoint,

    /// <summary>
    /// A CRC-verified 2.1 file header frame at the block start — the first file of a set (Design-TocLocation §3.2);
    ///  <see cref="IdentifiedBlock.File"/> names it. Content, positively identified.
    /// </summary>
    FileHeader,
}

/// <summary>The outcome of <see cref="TapeHeaderBlock.IdentifyBlock"/>, with whatever evidence the kind carries.</summary>
/// <param name="Kind">What the block is.</param>
/// <param name="Header">Set for <see cref="HeaderBlockIdentity.Header"/>.</param>
/// <param name="FrameStatus">Why the record failed for <see cref="HeaderBlockIdentity.DamagedRecord"/>; Ok for a verified record.</param>
/// <param name="Toc">Set for <see cref="HeaderBlockIdentity.TocCopy"/>.</param>
/// <param name="File">Set for <see cref="HeaderBlockIdentity.FileHeader"/>.</param>
/// <remarks>Read through the typed accessors: <see cref="TryGetHeader{T}"/>, <see cref="TryGetToc"/>, <see cref="TryGetFile"/>.</remarks>
public readonly record struct IdentifiedBlock(
    HeaderBlockIdentity Kind,
    TapeHeader? Header = null,
    TapeFrameStatus FrameStatus = TapeFrameStatus.NotFramed,
    TapeTocPeek? Toc = null,
    TapeFilePeek? File = null)
{
    /// <summary>The foreign outcome, spelled out — rather than trusting <c>default</c> for a struct.</summary>
    public static readonly IdentifiedBlock Foreign = new(HeaderBlockIdentity.Foreign);

    /// <summary>The TOC version for <see cref="HeaderBlockIdentity.TocCopy"/>; 0 otherwise.</summary>
    public ushort TocVersion => TryGetToc(out TapeTocPeek toc) ? toc.Version : (ushort)0;

    /// <summary>The TOC's series identity for <see cref="HeaderBlockIdentity.TocCopy"/>; empty otherwise.</summary>
    public Guid TocMediaId => TryGetToc(out TapeTocPeek toc) ? toc.MediaId : Guid.Empty;

    /// <summary>The intact header of kind <typeparamref name="T"/>, if that is what the block is.</summary>
    public bool TryGetHeader<T>([NotNullWhen(true)] out T? header) where T : TapeHeader
    {
        header = Kind == HeaderBlockIdentity.Header ? Header as T : null;
        return header is not null;
    }

    /// <summary>What the TOC copy says, if the block is one.</summary>
    public bool TryGetToc(out TapeTocPeek toc)
    {
        toc = Toc ?? default;
        return Kind == HeaderBlockIdentity.TocCopy && Toc is not null;
    }

    /// <summary>What the file header says, if the block is one.</summary>
    public bool TryGetFile(out TapeFilePeek file)
    {
        file = File ?? default;
        return Kind == HeaderBlockIdentity.FileHeader && File is not null;
    }
}

/// <summary>
/// TOC-less identification of a block already read from tape (SM-2) — THE dispatcher (Design-TocLocation §3). Split out
///  from the I/O half so a caller that holds bytes — and no TOC, no navigator, no agent — can still classify them.
/// </summary>
public static partial class TapeHeaderBlock
{
    #region *** The dispatcher ***

    /// <summary>
    /// Identifies one block positively: a TOC copy, an intact header, a calibration checkpoint, a file header, a damaged
    ///  record — or none of them. Pure, total, TOC-free. EVERY classification in the library ends here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Format first, by the first bytes.</b> <c>TpN#</c> at byte 0 is a 2.1 record; anything else goes to the frozen
    ///  legacy probe (<see cref="LegacyIdentify.IdentifyBlock"/>). No legacy record can begin with the magic.
    /// </para>
    /// <para>
    /// <b>2.1, by the record kind, to the type that owns it</b> (<see cref="ITapeBlockPeeker{TPeek}"/>):
    /// <list type="bullet">
    ///   <item><c>TocHeader</c> → <see cref="TapeTOC"/> → <see cref="HeaderBlockIdentity.TocCopy"/>, else damaged.</item>
    ///   <item><c>FileHeader</c> → <see cref="TapeFileHeader"/> → <see cref="HeaderBlockIdentity.FileHeader"/> when the frame
    ///    verifies, else <see cref="HeaderBlockIdentity.Foreign"/>: a set's first block may be a file BODY that starts with
    ///    the magic by chance, so an unverified frame is no evidence of damage.</item>
    ///   <item><c>CalibrationCheckpoint</c> → <see cref="TapeCalibrationCheckpoint"/>, else damaged.</item>
    ///   <item>Every other kind → <see cref="TapeHeader"/> → <see cref="HeaderBlockIdentity.Header"/>, else damaged. An unknown
    ///    kind or a newer major with an intact CRC is "ours, from a newer TapeNET" (<see cref="TapeFrameStatus.Unparseable"/>).</item>
    /// </list>
    /// </para>
    /// <para>
    /// A record whose MAGIC is damaged cannot be told from foreign data. A record damaged BEHIND its magic is ours,
    ///  damaged — never a phantom TOC (F23).
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
            {
                // The TOC stream carries no per-block CRC; a header record that does not parse is damaged or newer.
                TapeFrameStatus status = TapeBlockPeek.Run<TapeTOC, TapeTocPeek>(data, out TapeTocPeek toc);
                return status == TapeFrameStatus.Ok
                    ? new(HeaderBlockIdentity.TocCopy, FrameStatus: status, Toc: toc)
                    : new(HeaderBlockIdentity.DamagedRecord, FrameStatus: status);
            }

            case TapeRecordKind.FileHeader:
            {
                TapeFrameStatus status = TapeBlockPeek.Run<TapeFileHeader, TapeFilePeek>(data, out TapeFilePeek file);
                return status == TapeFrameStatus.Ok
                    ? new(HeaderBlockIdentity.FileHeader, FrameStatus: status, File: file)
                    : IdentifiedBlock.Foreign;      // unverified: maybe a body starting with the magic — no evidence
            }

            case TapeRecordKind.CalibrationCheckpoint:
            {
                TapeFrameStatus status = TapeBlockPeek.Run<TapeCalibrationCheckpoint, TapeCalibrationCheckpoint>(data, out _);
                return status == TapeFrameStatus.Ok
                    ? new(HeaderBlockIdentity.CalibrationCheckpoint, FrameStatus: status)
                    : new(HeaderBlockIdentity.DamagedRecord, FrameStatus: status);
            }

            default:
            {
                TapeFrameStatus status = TapeBlockPeek.Run<TapeHeader, TapeHeader>(data, out TapeHeader? header);
                return status == TapeFrameStatus.Ok && header is not null
                    ? new(HeaderBlockIdentity.Header, Header: header, FrameStatus: status)
                    : new(HeaderBlockIdentity.DamagedRecord,
                        FrameStatus: status == TapeFrameStatus.Ok ? TapeFrameStatus.Unparseable : status);
            }
        }
    }

    #endregion

    #region *** Projection — one-line view of the dispatcher ***

    /// <summary>
    /// The intact header a block holds — media, set, or calibration, in either format — or <see langword="null"/> for
    ///  everything else. A projection of <see cref="IdentifyBlock"/>.
    /// </summary>
    public static TapeHeader? Classify(byte[] buffer, int length) => IdentifyBlock(buffer, length).Header;

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
            || LegacyIdentify.HasSignatureAt(block, usable, 0, out _)
            || LegacyIdentify.HasSignatureAt(block, usable, FramedPayloadOffset, out _);
    }

    #endregion
}
