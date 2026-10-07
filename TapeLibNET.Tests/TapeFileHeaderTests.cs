// Save as: TapeLibNET.Tests/TapeFileHeaderTests.cs

using TapeLibNET.Format;
using TapeLibNET.Compression;
using TapeLibNET.Headers;
using TapeLibNET.Toc;
using TapeLibNET.Legacy;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;


/// <summary>
/// Phase 4 (Design-Format-v2 §5.3, §5.8): the 2.1 per-file header, the codec prefix, the legacy header check, and the
///  capacity estimates built on them. Pure — no tape.
/// </summary>
public class TapeFileHeaderTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
    private static readonly Guid SetId = new("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    private static TapeFileInfo File(string name, ulong fileId = 7, long length = 12345,
        FileAttributes attributes = FileAttributes.Archive | FileAttributes.ReadOnly)
        => new(fileId, TapeAddress.Zero, new TapeFileDescriptor(name)
        {
            Length = length,
            Attributes = attributes,
            CreationTime = T0.AddDays(-3),
            LastWriteTime = T0.AddDays(-2),
            LastAccessTime = T0.AddDays(-1),
        });

    private static byte[] HeaderBytes(TapeFileInfo file)
    {
        using var ms = new MemoryStream();
        TapeFileHeader.Write(ms, SetId, file);
        return ms.ToArray();
    }

    private static TapeFormatException AssertRefused(FormatErrorKind kind, Action act)
    {
        var ex = Assert.Throws<TapeFormatException>(act);
        Assert.Equal(kind, ex.Kind);
        return ex;
    }

    #region *** Header ***

    [Fact]
    public void Header_RoundTrip_LeavesStreamAtBody()
    {
        TapeFileInfo file = File(@"C:\data\ümläut 😀\report.xlsx");
        byte[] body = [0xCA, 0xFE];
        using var ms = new MemoryStream([.. HeaderBytes(file), .. body]);

        TapeFileHeader h = TapeFileHeader.Read(ms);

        Assert.Equal(SetId, h.SetId);
        Assert.Equal(7UL, h.FileId);
        Assert.Equal(file.FileDescr.FullName, h.Name);
        Assert.Equal(12345L, h.Length);
        Assert.Equal(FileAttributes.Archive | FileAttributes.ReadOnly, h.Attributes);
        Assert.Equal(T0.AddDays(-3), h.CreationTime);
        Assert.Equal(T0.AddDays(-2), h.LastWriteTime);
        Assert.Equal(T0.AddDays(-1), h.LastAccessTime);
        Assert.Equal(DateTimeKind.Utc, h.LastWriteTime.Kind);
        Assert.Equal(body.Length, ms.Length - ms.Position);       // no read-ahead into the body
    }

    [Fact]
    public void Header_StartsWithMagicAndFileHeaderKind()
    {
        byte[] bytes = HeaderBytes(File(@"C:\x"));
        Assert.True(TapeFormat.IsV2(bytes));
        Assert.Equal((byte)((ushort)TapeRecordKind.FileHeader & 0xFF), bytes[4]);
        Assert.Equal((byte)((ushort)TapeRecordKind.FileHeader >> 8), bytes[5]);
    }

    [Fact]
    public void Header_LoneSurrogateName_RoundTripsLosslessly()
    {
        string bad = "C:\\data\\odd\uD800name.txt";
        using var ms = new MemoryStream(HeaderBytes(File(bad)));
        TapeFileHeader h = TapeFileHeader.Read(ms);
        Assert.Equal(bad, h.Name);
        Assert.Equal("", h.FullName);
        Assert.NotEmpty(h.NameUtf16);
    }

    [Fact]
    public void Header_LongPath_RoundTrips()
    {
        string name = @"\\?\C:\" + new string('p', 32_000);
        using var ms = new MemoryStream(HeaderBytes(File(name)));
        Assert.Equal(name, TapeFileHeader.Read(ms).Name);
    }

    [Fact]
    public void Header_EmptySetId_ThrowsOnWrite()
        => Assert.Throws<InvalidOperationException>(() => TapeFileHeader.Write(Stream.Null, Guid.Empty, File(@"C:\x")));

    [Fact]
    public void Header_ZeroFileId_ThrowsOnWrite()
        => Assert.Throws<InvalidOperationException>(() => TapeFileHeader.Write(Stream.Null, SetId, File(@"C:\x", fileId: 0)));

    [Fact]
    public void Header_FlippedByte_IsRefused()
    {
        byte[] bytes = HeaderBytes(File(@"C:\data\a.txt"));
        for (int pos = TapeFormat.MagicLength; pos < bytes.Length; pos += 5)
        {
            byte[] bad = (byte[])bytes.Clone();
            bad[pos] ^= 0x5A;
            using var ms = new MemoryStream(bad);
            Assert.Throws<TapeFormatException>(() => TapeFileHeader.Read(ms));
        }
    }

    [Fact]
    public void Header_CrcFlipped_CrcMismatch()
    {
        byte[] bytes = HeaderBytes(File(@"C:\data\a.txt"));
        bytes[^1] ^= 1;
        using var ms = new MemoryStream(bytes);
        AssertRefused(FormatErrorKind.CrcMismatch, () => TapeFileHeader.Read(ms));
    }

    [Fact]
    public void Header_Truncated_Truncated()
    {
        byte[] bytes = HeaderBytes(File(@"C:\data\a.txt"));
        using var ms = new MemoryStream(bytes, 0, bytes.Length - 3);
        AssertRefused(FormatErrorKind.Truncated, () => TapeFileHeader.Read(ms));
    }

    [Fact]
    public void Header_UnknownCriticalField_Refused()
    {
        using var rec = new MemoryStream();
        using (var w = new TapeRecordWriter(rec))
        {
            w.Write(TapeRecordKind.FileHeader, f =>
            {
                f.WriteGuid(1, SetId);
                f.WriteUInt(2, 1);
                f.WriteTimestamp(5, T0);
                f.WriteTimestamp(6, T0);
                f.WriteTimestamp(7, T0);
                f.WriteUInt(20, 1, critical: true);     // e.g. a future "Sealed" flag (encryption)
                f.WriteString(32, @"C:\x");
            });
        }
        byte[] record = rec.ToArray();
        using var ms = new MemoryStream([.. record, .. TapeFrame.ComputeCrc(record)]);
        AssertRefused(FormatErrorKind.UnknownCritical, () => TapeFileHeader.Read(ms));
    }

    [Fact]
    public void Header_LegacyBytes_AreNotAV2Header()
    {
        using var ms = new MemoryStream([.. LegacyFileHeaderWriter.Bytes(42), .. new byte[64]]);
        Assert.Throws<TapeFormatException>(() => TapeFileHeader.Read(ms));
    }

    #endregion

    #region *** Codec prefix ***

    [Theory]
    [InlineData(TapeFileCodec.Stored)]
    [InlineData(TapeFileCodec.Zstd)]
    public void Codec_RoundTrip(TapeFileCodec codec)
    {
        using var ms = new MemoryStream();
        TapeFileHeader.WriteCodec(ms, codec);
        ms.Position = 0;
        Assert.Equal(codec, TapeFileHeader.ReadCodec(ms));
        Assert.Equal(TapeFileHeader.CodecPrefixLength, ms.Position);
    }

    [Fact]
    public void Codec_Unknown_BadValue()
    {
        using var ms = new MemoryStream([7]);
        AssertRefused(FormatErrorKind.BadValue, () => TapeFileHeader.ReadCodec(ms));
    }

    [Fact]
    public void Codec_EmptyBody_Truncated()
    {
        using var ms = new MemoryStream();
        AssertRefused(FormatErrorKind.Truncated, () => TapeFileHeader.ReadCodec(ms));
    }

    #endregion

    #region *** ProbingCompressionStream prefix ***

    private static byte[] Probe(byte[] data, bool prefix, out TapeFileCodec codec)
    {
        using var session = new ProbingCompressionStream.Session();
        using var output = new MemoryStream();
        var probing = new ProbingCompressionStream(output, session, ZstdLevel.Default) { EmitsCodecPrefix = prefix };
        probing.Write(data, 0, data.Length);
        probing.Dispose();
        codec = probing.FinalCodec;
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var dec = new DecompressionFilterStream(input);
        using var output = new MemoryStream();
        dec.CopyTo(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(1_000)]                                         // inside the probe window
    [InlineData(ProbingCompressionStream.ProbeLength)]          // exactly the window
    [InlineData(3 * ProbingCompressionStream.ProbeLength + 17)] // probe + live stream
    public void Probing_Compressible_PrefixZstd_ThenDecompressibleBody(int size)
    {
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++)
            data[i] = (byte)(i % 13);

        byte[] output = Probe(data, prefix: true, out TapeFileCodec codec);

        Assert.Equal(TapeFileCodec.Zstd, codec);
        Assert.Equal((byte)TapeFileCodec.Zstd, output[0]);
        Assert.Equal(data, Decompress(output[1..]));
    }

    [Fact]
    public void Probing_Incompressible_PrefixStored_ThenRawBody()
    {
        byte[] data = new byte[2 * ProbingCompressionStream.ProbeLength + 5];
        new Random(42).NextBytes(data);

        byte[] output = Probe(data, prefix: true, out TapeFileCodec codec);

        Assert.Equal(TapeFileCodec.Stored, codec);
        Assert.Equal((byte)TapeFileCodec.Stored, output[0]);
        Assert.Equal(data, output[1..]);
    }

    [Fact]
    public void Probing_EmptyFile_OnlyThePrefix()
    {
        byte[] output = Probe([], prefix: true, out TapeFileCodec codec);
        Assert.Equal(TapeFileCodec.Stored, codec);
        Assert.Equal([ (byte)TapeFileCodec.Stored ], output);
    }

    [Fact]
    public void Probing_WithoutPrefix_OutputUnchanged()
    {
        byte[] data = new byte[1_000];
        byte[] output = Probe(data, prefix: false, out TapeFileCodec codec);
        Assert.Equal(TapeFileCodec.Zstd, codec);
        Assert.Equal(data, Decompress(output));                 // no prefix byte in front
    }

    #endregion

    #region *** Legacy header and estimates ***

    [Fact]
    public void LegacyHeader_MatchesItsUid_Only()
    {
        using (var ms = new MemoryStream(LegacyFileHeaderWriter.Bytes(42)))
            Assert.True(LegacyFileHeader.Matches(new LegacyDeserializer(ms), 42));
        using (var ms = new MemoryStream(LegacyFileHeaderWriter.Bytes(42)))
            Assert.False(LegacyFileHeader.Matches(new LegacyDeserializer(ms), 43));
        Assert.Equal(LegacyFileHeader.Size, LegacyFileHeaderWriter.Bytes(42).Length);
    }

    [Theory]
    [InlineData(@"C:\x")]
    [InlineData(@"C:\data\ümläut 😀\report.xlsx")]
    [InlineData("C:\\data\\odd\uD800name.txt")]
    public void EstimateOverhead_IsAnUpperBound(string name)
    {
        TapeFileInfo file = File(name, fileId: ulong.MaxValue, length: long.MaxValue,
            attributes: (FileAttributes)0x7FFFFFFF);
        int exact = TapeFileHeader.Measure(SetId, file) + TapeFileHeader.CodecPrefixLength;
        Assert.InRange(exact, 1, TapeFileHeader.EstimateOverhead(name));
    }

    [Fact]
    public void EstimateOverhead_LongPath_IsAnUpperBound()
    {
        string name = @"C:\" + new string('ä', 30_000);
        int exact = TapeFileHeader.Measure(SetId, File(name)) + TapeFileHeader.CodecPrefixLength;
        Assert.InRange(exact, 1, TapeFileHeader.EstimateOverhead(name));
    }

    [Fact]
    public void SetOverhead_PerDataFormat()
    {
        var toc = new TapeTOC();
        toc.AddNewSetTOC();
        TapeSetTOC set = toc.CurrentSetTOC;
        TapeFileInfo file = File(@"C:\data\a.txt");

        Assert.Equal(TapeFileHeader.EstimateOverhead(@"C:\data\a.txt"), set.EstimateFileOverhead(file));
        set.DataFormat = TapeDataFormat.Legacy;
        Assert.Equal(LegacyFileHeader.Size, set.EstimateFileOverhead(file));
    }

    #endregion
}
