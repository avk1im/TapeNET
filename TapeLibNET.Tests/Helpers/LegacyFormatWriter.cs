using System.IO.Hashing;
using System.Text;

namespace TapeLibNET.Tests.Helpers;

/// <summary>Which legacy TOC set / file-entry layout to emit (Design-Format-v2 §7.2).</summary>
public enum LegacyTocLayout
{
    /// <summary>Pre-compression: set ends after ContinuedFromPrevVolume; file entry ends after SizeOnTape.</summary>
    A,
    /// <summary>Current legacy: + Compression, CompressionLevel per set; + Codec byte per file.</summary>
    B,
}

/// <summary>A file entry as the legacy TOC stores it. Plain data, deliberately decoupled from production types.</summary>
public sealed record LegacyFileEntry(
    ulong Uid,
    long Block,
    uint Offset,
    string FullName,
    long Length,
    FileAttributes Attributes,
    DateTime CreationTime,
    DateTime LastWriteTime,
    DateTime LastAccessTime,
    byte[]? Hash = null,
    long SizeOnTape = 0,
    TapeFileCodec Codec = TapeFileCodec.Stored);

/// <summary>A set as the legacy TOC stores it.</summary>
public sealed record LegacySetEntry(
    IReadOnlyList<LegacyFileEntry> Files,
    string Description,
    DateTime CreationTime,
    uint BlockSize,
    DateTime LastSaveTime,
    TapeHashAlgorithm HashAlgorithm,
    bool Incremental,
    int Volume,
    bool ContinuedFromPrevVolume,
    TapeCompression Compression = TapeCompression.None,
    int CompressionLevel = 0);

/// <summary>A whole legacy TOC.</summary>
public sealed record LegacyTocEntry(
    ulong NextUid,
    Guid MediaId,
    IReadOnlyList<LegacySetEntry> Sets,
    string Description,
    DateTime CreationTime,
    DateTime LastSaveTime,
    int Volume,
    bool ContinuedOnNextVolume);

/// <summary>Legacy calibration plan, as the legacy header stores it.</summary>
public sealed record LegacyCalibrationPlan(
    int SampleCount, int BodySampleCount, int TailSampleCount, uint BlockSize,
    int BlocksPerChunk, int ChunkSize, int TailBlocksPerChunk, int TailChunkSize,
    double TailCapacityFraction, int NumCheckpoints);

/// <summary>
/// Test-project-only writer of the LEGACY on-tape format (<c>TF</c> + <c>0x01xx</c> family).
/// Frozen copy of the pre-2.1 serialization code, so the shipping readers can be tested against
/// legacy media after the product stops writing it (Design-Format-v2 §7.3). Self-contained:
/// uses nothing but primitives and public enums, so later production refactoring cannot change it.
/// </summary>
public static class LegacyFormatWriter
{
    /// <summary>Library-wide legacy signature.</summary>
    private static readonly byte[] Signature = [(byte)'T', (byte)'F'];

    /// <summary>Version of every nested legacy record and of the headers.</summary>
    public const ushort RecordVersion = 0x0101;
    /// <summary>TOC version without MediaId.</summary>
    public const ushort TocVersionPreMediaId = 0x0101;
    /// <summary>TOC version with MediaId.</summary>
    public const ushort TocVersionWithMediaId = 0x0102;

    #region *** Primitives (little-endian, as the legacy machine-endian writer produced on x86/x64) ***

    private sealed class Writer(Stream stream)
    {
        public void Bytes(ReadOnlySpan<byte> bytes) => stream.Write(bytes);
        public void U8(byte v) => stream.WriteByte(v);
        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b, v); Bytes(b); }
        public void I32(int v) { Span<byte> b = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, v); Bytes(b); }
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, v); Bytes(b); }
        public void I64(long v) { Span<byte> b = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b, v); Bytes(b); }
        public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(b, v); Bytes(b); }
        public void F64(double v) => I64(BitConverter.DoubleToInt64Bits(v));
        public void DateTimeTicks(DateTime dt) => I64(dt.Ticks);
        public void GuidBytes(Guid g) => Bytes(g.ToByteArray());
        public void String(string s)
        {
            var utf8 = Encoding.UTF8.GetBytes(s);
            I32(utf8.Length);
            Bytes(utf8);
        }
        public void NullableBytesWithLength(byte[]? bytes)
        {
            if (bytes == null) { I32(-1); return; }
            I32(bytes.Length);
            Bytes(bytes);
        }
        public void Signature(ushort version = RecordVersion)
        {
            Bytes(LegacyFormatWriter.Signature);
            U16(version);
        }
    }

    private static byte[] Build(Action<Writer> body)
    {
        using var ms = new MemoryStream();
        body(new Writer(ms));
        return ms.ToArray();
    }

    #endregion

    #region *** Framing ***

    /// <summary>Legacy frame: <c>[int32 payloadLen][payload][CRC-32 of payload, 4 bytes]</c>.</summary>
    public static byte[] Frame(byte[] payload)
    {
        var crc = new Crc32();
        crc.Append(payload);
        return Build(w =>
        {
            w.I32(payload.Length);
            w.Bytes(payload);
            w.Bytes(crc.GetCurrentHash());
        });
    }

    /// <summary>Copies <paramref name="frame"/> to the front of a block of <paramref name="blockSize"/> bytes; the rest is zero padding.</summary>
    public static byte[] PadToBlock(byte[] frame, int blockSize)
    {
        if (frame.Length > blockSize)
            throw new ArgumentException("Frame does not fit the block", nameof(frame));
        var block = new byte[blockSize];
        frame.CopyTo(block, 0);
        return block;
    }

    #endregion

    #region *** TOC ***

    private static void WriteFile(Writer w, LegacyFileEntry f, LegacyTocLayout layout)
    {
        w.Signature();
        w.U64(f.Uid);
        w.I64(f.Block);
        w.U32(f.Offset);
        w.String(f.FullName);
        w.I64(f.Length);
        w.U32((uint)f.Attributes);
        w.DateTimeTicks(f.CreationTime);
        w.DateTimeTicks(f.LastWriteTime);
        w.DateTimeTicks(f.LastAccessTime);
        w.NullableBytesWithLength(f.Hash);
        w.I64(f.SizeOnTape);
        if (layout == LegacyTocLayout.B)
            w.U8((byte)f.Codec);
    }

    private static void WriteSet(Writer w, LegacySetEntry s, LegacyTocLayout layout)
    {
        w.Signature();
        w.I32(s.Files.Count);
        foreach (var f in s.Files)
            WriteFile(w, f, layout);
        w.String(s.Description);
        w.DateTimeTicks(s.CreationTime);
        w.U32(s.BlockSize);
        w.DateTimeTicks(s.LastSaveTime);
        w.I32((int)s.HashAlgorithm);
        w.Bool(s.Incremental);
        w.I32(s.Volume);
        w.Bool(s.ContinuedFromPrevVolume);
        if (layout == LegacyTocLayout.B)
        {
            w.I32((int)s.Compression);
            w.I32(s.CompressionLevel);
        }
    }

    /// <summary>
    /// Serializes a legacy TOC body (no trailing CRC). <paramref name="preMediaId"/> emits TOC version 0x0101
    /// without the MediaId field.
    /// </summary>
    public static byte[] SerializeToc(LegacyTocEntry toc, LegacyTocLayout layout = LegacyTocLayout.B, bool preMediaId = false) =>
        Build(w =>
        {
            w.Signature(preMediaId ? TocVersionPreMediaId : TocVersionWithMediaId);
            w.U64(toc.NextUid);
            if (!preMediaId)
                w.GuidBytes(toc.MediaId);
            w.I32(toc.Sets.Count);
            foreach (var s in toc.Sets)
                WriteSet(w, s, layout);
            w.String(toc.Description);
            w.DateTimeTicks(toc.CreationTime);
            w.DateTimeTicks(toc.LastSaveTime);
            w.I32(toc.Volume);
            w.Bool(toc.ContinuedOnNextVolume);
        });

    /// <summary>The legacy TOC stream trailer: CRC-64 of the TOC body.</summary>
    public static byte[] Crc64Trailer(byte[] tocBody)
    {
        var crc = new Crc64();
        crc.Append(tocBody);
        return crc.GetCurrentHash();
    }

    #endregion

    #region *** Block-framed headers ***

    private static void WritePreamble(Writer w, TapeHeaderKind kind, Guid id, DateTime createdUtc, uint blockSize)
    {
        w.Signature();
        w.U8((byte)kind);
        w.GuidBytes(id);
        w.DateTimeTicks(createdUtc);
        w.U32(blockSize);
    }

    /// <summary>Media header payload (unframed). <paramref name="withSetHeadersFlag"/> false emulates headers written before <c>HasSetHeaders</c> existed.</summary>
    public static byte[] MediaHeaderPayload(
        Guid mediaId, DateTime createdUtc, uint tocBlockSize, int volume, MediaPartition partition,
        TapeTocPlacement placement, string? originalName, bool hasSetHeaders, bool withSetHeadersFlag = true) =>
        Build(w =>
        {
            WritePreamble(w, TapeHeaderKind.Media, mediaId, createdUtc, tocBlockSize);
            w.I32(volume);
            w.U8((byte)partition);
            w.U8((byte)placement);
            w.String(originalName ?? string.Empty);
            if (withSetHeadersFlag)
                w.U8(hasSetHeaders ? (byte)1 : (byte)0);
        });

    /// <summary>Set header payload (unframed).</summary>
    public static byte[] SetHeaderPayload(
        Guid mediaId, DateTime createdUtc, uint blockSize, int volume, int volumeSetIndex, int globalSetIndex, string? description) =>
        Build(w =>
        {
            WritePreamble(w, TapeHeaderKind.Set, mediaId, createdUtc, blockSize);
            w.I32(volume);
            w.I32(volumeSetIndex);
            w.I32(globalSetIndex);
            w.String(description ?? string.Empty);
        });

    /// <summary>Calibration run header payload (unframed).</summary>
    public static byte[] CalibrationHeaderPayload(
        Guid runId, DateTime startedUtc, uint runBlockSize, string profileKey, long capacityReportedAtBom, LegacyCalibrationPlan plan) =>
        Build(w =>
        {
            WritePreamble(w, TapeHeaderKind.Calibration, runId, startedUtc, runBlockSize);
            w.String(profileKey);
            w.I64(capacityReportedAtBom);
            w.I32(plan.SampleCount);
            w.I32(plan.BodySampleCount);
            w.I32(plan.TailSampleCount);
            w.U32(plan.BlockSize);
            w.I32(plan.BlocksPerChunk);
            w.I32(plan.ChunkSize);
            w.I32(plan.TailBlocksPerChunk);
            w.I32(plan.TailChunkSize);
            w.F64(plan.TailCapacityFraction);
            w.I32(plan.NumCheckpoints);
        });

    /// <summary>Calibration checkpoint payload (unframed).</summary>
    public static byte[] CheckpointPayload(
        Guid runId, int index, long bytesWritten, (long ActualWritten, long ReportedRemaining)? earlyWarning,
        IReadOnlyList<(long ActualWritten, long ReportedRemaining)> samples) =>
        Build(w =>
        {
            w.Signature();
            w.GuidBytes(runId);
            w.I32(index);
            w.I64(bytesWritten);
            w.Bool(earlyWarning.HasValue);
            if (earlyWarning is { } ew)
            {
                w.I64(ew.ActualWritten);
                w.I64(ew.ReportedRemaining);
            }
            w.I32(samples.Count);
            foreach (var (aw, rr) in samples)
            {
                w.I64(aw);
                w.I64(rr);
            }
        });

    #endregion

    #region *** Legacy file header ***

    /// <summary>The 12-byte legacy file header: <c>"TF"</c>, <c>0x0101</c>, <c>u64 UID</c>.</summary>
    public static byte[] FileHeader(ulong uid) =>
        Build(w =>
        {
            w.Signature();
            w.U64(uid);
        });

    #endregion
}
