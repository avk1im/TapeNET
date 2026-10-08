using TapeLibNET.Agents;
using TapeLibNET.Headers;
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Helpers;


/// <summary>
/// TEST-ONLY emitter that makes the real agents write GENUINE legacy (pre-2.1) tapes: 12-byte file headers, no codec
///  prefix, legacy media / set header frames, legacy TOC copies (layout B, version 0x0102).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this produces faithful legacy media.</b> The record bytes come from the frozen legacy writers, each pinned
///  byte for byte against the Phase 0 goldens (<c>LegacyGoldenTests</c>, <c>LegacyTocWriterTests</c>). The LAYOUT — where
///  records sit, which marks separate them, how files pack into blocks — comes from the production navigator and packer,
///  which the format change did not touch. <c>LegacyEmitterTests.Emitter_MatchesPhase0Image</c> checks the result against
///  the images the old build wrote.
/// </para>
/// <para>
/// Software compression is refused by the agent with this emitter: a legacy body has no codec prefix, so a ZSTD body
///  would be unreadable — and no legacy tape carries one (Design-Format-v2 §1).
/// </para>
/// </remarks>
internal sealed class LegacyRecordEmitter : ITapeRecordEmitter
{
    public static readonly LegacyRecordEmitter Instance = new();

    private LegacyRecordEmitter() { }

    public TapeDataFormat DataFormat => TapeDataFormat.Legacy;

    public bool WritesCodecPrefix => false;

    public void WriteFileHeader(Stream stream, TapeSetTOC set, TapeFileInfo file)
        => stream.Write(LegacyFileHeaderWriter.Bytes(file.FileId));

    public void WriteToc(TapeTOC toc, Stream stream)
        => stream.Write(LegacyTocWriter.TocBytes(toc));

    public byte[]? FrameHeaderBlock(TapeHeader header) => header switch
    {
        TapeMediaHeader or TapeSetHeader => LegacyHeaderWriter.Block(header),
        _ => throw new NotSupportedException($"No legacy tape path writes a {header.GetType().Name}"),
    };
}
