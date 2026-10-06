using System.IO.Hashing;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// TEST-ONLY writer of legacy (pre-2.1) media and set header frames — the mirror of <c>LegacyHeaderReader</c> and
///  <c>LegacyFramer</c>. The product writes these headers in format 2.1 only (Design-Format-v2 §7.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Field order mirrors <c>LegacyHeaderReader</c> and the <c>ConstructBody</c> readers line by line.</b>
///  <c>LegacyGoldenTests.Headers_LegacyWriterReproducesGolden</c> pins it against the frozen golden bytes.
/// </para>
/// <para>
/// <b>Times.</b> Legacy media and set headers held LOCAL ticks (copied from the TOC's local creation time); the reader
///  converts them with <c>LegacyTime.FromLocal</c>. This writer converts back with <see cref="DateTime.ToLocalTime"/>,
///  so <c>read → Frame</c> reproduces the original bytes (away from DST transitions).
/// </para>
/// </remarks>
internal static class LegacyHeaderWriter
{
    private const byte KindMedia = 1, KindSet = 3;

    /// <summary>The legacy frame <c>[int32 len][payload][crc32]</c> of <paramref name="header"/>.</summary>
    /// <param name="includeSetHeadersFlag">
    /// <see langword="false"/> writes the media header as it was BEFORE <c>HasSetHeaders</c> was appended — the shape the
    ///  reader must still read as "no set headers". Ignored for other kinds.
    /// </param>
    public static byte[] Frame(TapeHeader header, bool includeSetHeadersFlag = true) => header switch
    {
        TapeMediaHeader media => FramePayload(MediaPayload(media, includeSetHeadersFlag)),
        TapeSetHeader set => FramePayload(SetPayload(set)),
        TapeCalibrationHeader calibration => CalibrationFrame(calibration),
        _ => throw new ArgumentException($"no legacy writer for {header.GetType().Name}", nameof(header)),
    };

    // Calibration headers held UTC ticks (DateTime.UtcNow): written as they are, unlike media / set headers.
    private static byte[] CalibrationFrame(TapeCalibrationHeader h)
    {
        TapeCalibrationPlan p = h.Plan;
        return LegacyFormatWriter.Frame(LegacyFormatWriter.CalibrationHeaderPayload(
            h.RunId, h.StartedUtc, h.RunBlockSize, h.ProfileKey, h.CapacityReportedAtBom,
            new LegacyCalibrationPlan(p.SampleCount, p.BodySampleCount, p.TailSampleCount, p.BlockSize,
                p.BlocksPerChunk, p.ChunkSize, p.TailBlocksPerChunk, p.TailChunkSize,
                p.TailCapacityFraction, p.NumCheckpoints)));
    }

    /// <summary>The legacy frame padded into one standard header block.</summary>
    public static byte[] Block(TapeHeader header, bool includeSetHeadersFlag = true)
    {
        byte[] frame = Frame(header, includeSetHeadersFlag);
        var block = new byte[TapeHeaderBlock.Size];
        frame.CopyTo(block, 0);
        return block;
    }

    private static byte[] MediaPayload(TapeMediaHeader h, bool includeSetHeadersFlag)
    {
        using var ms = new MemoryStream();
        var s = new TapeSerializer(ms);
        Preamble(s, KindMedia, h.MediaId, h.CreatedUtc, h.TocBlockSize);
        s.Serialize(h.Volume);                                  // int32
        s.Serialize((byte)h.Partition);
        s.Serialize((byte)h.TocPlacement);
        s.Serialize(h.OriginalName ?? string.Empty);            // length-prefixed UTF-8
        if (includeSetHeadersFlag)
            s.Serialize(h.HasSetHeaders);                       // trailing flag, appended later in the legacy format
        return ms.ToArray();
    }

    private static byte[] SetPayload(TapeSetHeader h)
    {
        using var ms = new MemoryStream();
        var s = new TapeSerializer(ms);
        Preamble(s, KindSet, h.MediaId, h.CreatedUtc, h.SetBlockSize);
        s.Serialize(h.Volume);
        s.Serialize(h.VolumeSetIndex);
        s.Serialize(h.GlobalSetIndex);
        s.Serialize(h.Description ?? string.Empty);
        return ms.ToArray();
    }

    // signature + kind byte + 16 raw id bytes + LOCAL creation ticks + uint32 block size
    private static void Preamble(TapeSerializer s, byte kind, Guid id, DateTime createdUtc, uint blockSize)
    {
        s.SerializeSignature();
        s.Serialize(kind);
        s.Serialize(id.ToByteArray());                          // raw, no length prefix
        s.Serialize(Local(createdUtc));
        s.Serialize(blockSize);
    }

    private static byte[] FramePayload(byte[] payload)
    {
        using var ms = new MemoryStream();
        var s = new TapeSerializer(ms);
        s.Serialize(payload.Length);                            // int32 length prefix
        s.Serialize(payload);                                   // raw payload
        s.Serialize(Crc32.Hash(payload));                       // raw 4-byte CRC-32 over the payload
        return ms.ToArray();
    }

    // What a legacy build held in memory: local wall-clock time. "Not set" stays "not set".
    private static DateTime Local(DateTime utc)
        => utc.Ticks == 0 ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
}
