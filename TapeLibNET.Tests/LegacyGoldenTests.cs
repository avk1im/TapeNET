using TapeLibNET.Legacy;
using System.IO.Hashing;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Phase 0 legacy freeze (Design-Format-v2 §11.4): the checked-in goldens must (a) still be reproduced
/// byte for byte by <see cref="LegacyFormatWriter"/> and (b) be read correctly by the current readers.
/// </summary>
public class LegacyGoldenTests
{
    private static string GoldenPath(string name, [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden", "Legacy", name);

    private static byte[] Load(string name) => File.ReadAllBytes(GoldenPath(name));

    public static TheoryData<string> GoldenNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in GoldenData.All().Keys)
            data.Add(name);
        return data;
    }

    // Splits a TOC stream into body + CRC-64 trailer, verifying the trailer.
    private static byte[] TocBody(byte[] stream)
    {
        var body = stream[..^8];
        Assert.Equal(LegacyFormatWriter.Crc64Trailer(body), stream[^8..]);
        return body;
    }

    private static T? Read<T>(byte[] bytes) where T : class, ITapeSerializable
    {
        using var ms = new MemoryStream(bytes);
        return new LegacyDeserializer(ms).Deserialize<T>();
    }

    #region *** Byte-for-byte reproduction ***

    [Theory]
    [MemberData(nameof(GoldenNames))]
    public void Golden_MatchesLegacyFormatWriter(string name)
    {
        Assert.Equal(GoldenData.All()[name], Load(name));
    }

    [Theory]
    [MemberData(nameof(GoldenNames))]
    public void Golden_HasMatchingExpectedJson(string name)
    {
        var json = File.ReadAllText(GoldenPath(Path.ChangeExtension(name, ".expected.json")));
        Assert.Equal(GoldenData.Describe(name, Load(name)).ReplaceLineEndings(), json.ReplaceLineEndings());
    }

    #endregion

    #region *** TOC ***

    [Fact]
    public void TocLayoutB_ReadsWithCurrentReader()
    {
        var expected = GoldenData.RichToc();
        var toc = Read<TapeTOC>(TocBody(Load("toc-layoutB.bin")));

        Assert.NotNull(toc);
        Assert.Equal(expected.MediaId, toc!.MediaId);
        Assert.Equal(expected.Description, toc.Description);
        Assert.Equal(expected.Volume, toc.Volume);
        Assert.Equal(expected.ContinuedOnNextVolume, toc.ContinuedOnNextVolume);
        Assert.Equal(expected.CreationTime.Ticks, toc.CreationTime.Ticks);
        Assert.True(toc.LoadedFromLegacy);
        Assert.Equal(expected.Sets.Count, toc.Count);

        for (int i = 0; i < expected.Sets.Count; i++)
        {
            var es = expected.Sets[i];
            var set = toc[i + 1]; // public set index is 1-based, ascending from the oldest (0 = newest)
            Assert.Equal(es.Description, set.Description);
            Assert.Equal(es.Volume, set.Volume);
            Assert.Equal(es.Incremental, set.Incremental);
            Assert.Equal(es.ContinuedFromPrevVolume, set.ContinuedFromPrevVolume);
            Assert.Equal(es.BlockSize, set.BlockSize);
            Assert.Equal(es.HashAlgorithm, set.HashAlgorithm);
            Assert.Equal(es.Files.Count, set.Count);

            for (int j = 0; j < es.Files.Count; j++)
            {
                var ef = es.Files[j];
                var tfi = set[j];
                Assert.Equal(ef.Uid, (ulong)tfi.FileId);
                Assert.Equal(ef.Block, tfi.Address.Block);
                Assert.Equal(ef.Offset, tfi.Address.Offset);
                Assert.Equal(ef.FullName, tfi.FileDescr.FullName);
                Assert.Equal(ef.Length, tfi.FileDescr.Length);
                Assert.Equal(ef.LastWriteTime.Ticks, tfi.FileDescr.LastWriteTime.Ticks);
                Assert.Equal(ef.Hash, tfi.Hash);
                Assert.Equal(ef.SizeOnTape, tfi.SizeOnTape);
                Assert.Equal(ef.Codec, tfi.Codec);
            }
        }
    }

    [Fact]
    public void TocPreMediaId_ReadsWithEmptyMediaId()
    {
        var expected = GoldenData.SimpleToc();
        var toc = Read<TapeTOC>(TocBody(Load("toc-preMediaId.bin")));

        Assert.NotNull(toc);
        Assert.Equal(Guid.Empty, toc!.MediaId);
        Assert.Equal(expected.Description, toc.Description);
        Assert.Equal(expected.Sets.Count, toc.Count);
        Assert.Equal(expected.Sets[0].Files.Count, toc[0].Count);
        Assert.True(toc.LoadedFromLegacy);
    }

    [Fact]
    public void TocLayoutA_HasNoCompressionOrCodecFields()
    {
        // Layout A is shorter than B for the same content; the dedicated legacy reader (later phase) will detect it.
        var a = Load("toc-layoutA.bin");
        var b = LegacyFormatWriter.SerializeToc(GoldenData.SimpleToc(), LegacyTocLayout.B);
        int expectedDelta = 2 * 1 /* codec per file */ + 8 /* compression + level per set */;
        Assert.Equal(b.Length + 8 - expectedDelta, a.Length);
    }

    #endregion

    #region *** Headers and records ***

    [Fact]
    public void MediaHeaders_ReadWithCurrentReader()
    {
        var withFlag = TapeFramer.Unpack<TapeMediaHeader>(Load("media-header-withflag.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(withFlag);
        Assert.Equal(GoldenData.MediaId, withFlag!.MediaId);
        Assert.Equal(1, withFlag.Volume);
        Assert.Equal(TapeTocPlacement.InSet, withFlag.TocPlacement);
        Assert.Equal("Orig", withFlag.OriginalName);
        Assert.True(withFlag.HasSetHeaders);

        var noFlag = TapeFramer.Unpack<TapeMediaHeader>(Load("media-header-noflag.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(noFlag);
        Assert.Equal(TapeTocPlacement.InPartition, noFlag!.TocPlacement);
        Assert.Null(noFlag.OriginalName);
        Assert.False(noFlag.HasSetHeaders);
    }

    [Fact]
    public void SetHeaders_ReadWithCurrentReader()
    {
        var withDesc = TapeFramer.Unpack<TapeSetHeader>(Load("set-header-desc.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(withDesc);
        Assert.Equal(GoldenData.MediaId, withDesc!.MediaId);
        Assert.Equal(1, withDesc.Volume);
        Assert.Equal(2, withDesc.VolumeSetIndex);
        Assert.Equal(3, withDesc.GlobalSetIndex);
        Assert.Equal("Set \u00fc", withDesc.Description);

        var noDesc = TapeFramer.Unpack<TapeSetHeader>(Load("set-header-nodesc.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(noDesc);
        Assert.Null(noDesc!.Description);
    }

    [Theory]
    [InlineData("calibration-header-standard.bin", (int)GoldenData.BlockSize)]
    [InlineData("calibration-header-run.bin", 1 << 20)]
    public void CalibrationHeaders_ReadWithCurrentReader(string name, int blockSize)
    {
        var h = TapeFramer.Unpack<TapeCalibrationHeader>(Load(name), blockSize);
        Assert.NotNull(h);
        Assert.Equal(GoldenData.RunId, h!.RunId);
        Assert.Equal("LTO-9|test", h.ProfileKey);
        Assert.Equal(18_000_000_000_000L, h.CapacityReportedAtBom);

        var p = GoldenData.Plan();
        Assert.Equal(p.SampleCount, h.Plan.SampleCount);
        Assert.Equal(p.ChunkSize, h.Plan.ChunkSize);
        Assert.Equal(p.TailCapacityFraction, h.Plan.TailCapacityFraction);
        Assert.Equal(p.NumCheckpoints, h.Plan.NumCheckpoints);
    }

    [Fact]
    public void Checkpoints_ReadWithCurrentReader()
    {
        var noEw = TapeFramer.Unpack<TapeCalibrationCheckpoint>(Load("checkpoint-noew.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(noEw);
        Assert.Equal(GoldenData.RunId, noEw!.RunId);
        Assert.Equal(1, noEw.Index);
        Assert.Equal(123456, noEw.BytesWritten);
        Assert.Null(noEw.EarlyWarning);
        Assert.Equal(2, noEw.Samples.Count);

        var ew = TapeFramer.Unpack<TapeCalibrationCheckpoint>(Load("checkpoint-ew-manysamples.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(ew);
        Assert.Equal((17_000_000_000_000L, 1_000_000_000_000L), ew!.EarlyWarning);
        Assert.Equal(200, ew.Samples.Count);
        Assert.Equal((199_000_000L, 18_000_000_000_000L - 199_000_000L), ew.Samples[199]);
    }

    [Theory]
    [InlineData("media-header-withflag.bin")]
    [InlineData("set-header-desc.bin")]
    [InlineData("set-header-nodesc.bin")]
    [InlineData("calibration-header-standard.bin")]
    public void Headers_CurrentWriterReproducesGolden(string name)
    {
        // The current production writer must still emit the frozen legacy bytes for these records.
        var golden = Load(name);
        var record = TapeFramer.Unpack<TapeHeader>(golden, golden.Length);
        Assert.NotNull(record);

        var frame = TapeFramer.Pack(record!);
        Assert.Equal(frame, golden[..frame.Length]);
    }

    [Fact]
    public void Checkpoints_CurrentWriterReproducesGolden()
    {
        var golden = Load("checkpoint-ew-manysamples.bin");
        var record = TapeFramer.Unpack<TapeCalibrationCheckpoint>(golden, golden.Length);
        Assert.NotNull(record);

        var frame = TapeFramer.Pack(record!);
        Assert.Equal(frame, golden[..frame.Length]);
    }

    [Fact]
    public void FileHeader_Is12Bytes()
    {
        var bytes = Load("file-header.bin");
        Assert.Equal(12, bytes.Length);

        using var ms = new MemoryStream(bytes);
        var d = new LegacyDeserializer(ms);
        Assert.True(d.ValidateSignature());
        Assert.Equal(42UL, d.DeserializeUInt64());
    }

    [Fact]
    public void CorruptedFrame_FailsCrc()
    {
        var golden = Load("set-header-desc.bin");
        golden[20] ^= 0xFF;
        Assert.Equal(TapeFramer.FrameStatus.CrcMismatch,
            TapeFramer.TryUnpack<TapeSetHeader>(golden, golden.Length, out _));
    }

    #endregion
}
