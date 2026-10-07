using System.IO.Hashing;
using TapeLibNET.Format;
using TapeLibNET.Legacy;
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

    /*
    // Not used past V2.1 Phase 6
    private static T? Read<T>(byte[] bytes) where T : class, ITapeSerializable
    {
        using var ms = new MemoryStream(bytes);
        return new LegacyDeserializer(ms).Deserialize<T>();
    }
    */

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
        var toc = LoadToc(Load("toc-layoutB.bin"));

        Assert.NotNull(toc);
        Assert.Equal(expected.MediaId, toc!.MediaId);
        Assert.Equal(expected.Description, toc.Description);
        Assert.Equal(expected.Volume, toc.Volume);
        Assert.Equal(expected.ContinuedOnNextVolume, toc.ContinuedOnNextVolume);
        // Legacy times were LOCAL ticks; the reader converts them to UTC (§7.1)
        LegacyTestTime.AsLegacyLocal(expected.CreationTime);
        Assert.True(toc.LoadedFromLegacy);
        Assert.Equal(expected.Sets.Count, toc.Count);

        for (int i = 0; i < expected.Sets.Count; i++)
        {
            var es = expected.Sets[i];
            var set = toc[i + 1]; // public set index is 1-based, ascending from the oldest (0 = newest)
            Assert.Equal(TapeDataFormat.Legacy, set.DataFormat);
            Assert.Equal(Guid.Empty, set.SetId);
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
                LegacyTestTime.AsLegacyLocal(ef.LastWriteTime);
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
        var toc = LoadToc(Load("toc-preMediaId.bin"));

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
        // Layout A is shorter than B for the same content; LegacyTocReader.Load detects it.
        var a = Load("toc-layoutA.bin");
        var b = LegacyFormatWriter.SerializeToc(GoldenData.SimpleToc(), LegacyTocLayout.B);
        int expectedDelta = 2 * 1 /* codec per file */ + 8 /* compression + level per set */;
        Assert.Equal(b.Length + 8 - expectedDelta, a.Length);
    }

    [Fact]
    public void TocLayoutA_DetectedAndRead()
    {
        var expected = GoldenData.SimpleToc();
        var toc = LoadToc(Load("toc-layoutA.bin"));

        Assert.NotNull(toc);
        Assert.True(toc!.LoadedFromLegacy);
        Assert.Equal(expected.Description, toc.Description);
        Assert.Equal(expected.Sets.Count, toc.Count);
        for (int i = 0; i < expected.Sets.Count; i++)
        {
            var set = toc[i + 1];
            Assert.Equal(TapeDataFormat.Legacy, set.DataFormat);
            Assert.Equal(TapeCompression.None, set.Compression);
            Assert.Equal(expected.Sets[i].Files.Count, set.Count);
            for (int j = 0; j < set.Count; j++)
            {
                Assert.Equal(TapeFileCodec.Stored, set[j].Codec);
                Assert.Equal(expected.Sets[i].Files[j].Uid, (ulong)set[j].FileId);
            }
        }
    }

    [Theory]
    [InlineData("toc-layoutA.bin")]
    [InlineData("toc-layoutB.bin")]
    [InlineData("toc-preMediaId.bin")]
    public void Toc_CorruptedCrc_Throws(string name)
    {
        var bytes = Load(name);
        bytes[^1] ^= 0xFF; // damage the CRC-64 trailer: body still parses
        var ex = Assert.Throws<TapeFormatException>(() => LoadToc(bytes));
        Assert.Equal(FormatErrorKind.CrcMismatch, ex.Kind);
        Assert.Equal((int)Windows.Win32.Foundation.WIN32_ERROR.ERROR_CRC, ex.HResult & 0xFFFF);
    }

    [Fact]
    public void Toc_NextFileId_ContinuesAfterLargestLegacyId()
    {
        var toc = LoadToc(Load("toc-layoutB.bin"))!;
        foreach (var set in toc)
        {
            ulong max = 0;
            foreach (var tfi in set)
                max = Math.Max(max, tfi.FileId);
            Assert.Equal(max + 1, set.GenerateFileId());
        }
    }

    [Fact]
    public void Toc_GarbageOrTruncated_Throws()
    {
        // The reader never returns null: not-a-TOC is BadMagic, unreadable (e.g. truncated) is BadValue.
        static void AssertRefused(byte[] bytes)
        {
            var ex = Assert.Throws<TapeFormatException>(() => LoadToc(bytes));
            Assert.True(ex.Kind is FormatErrorKind.BadMagic or FormatErrorKind.BadValue, $"Unexpected kind {ex.Kind}");
        }

        AssertRefused(new byte[64]);
        AssertRefused([]);
        AssertRefused(Load("toc-layoutB.bin")[..40]);
    }

    private static TapeTOC? LoadToc(byte[] stream)
    {
        using var ms = new MemoryStream(stream);
        return LegacyTocReader.Load(ms);
    }

    #endregion

    #region *** Headers and records ***

    [Fact]
    public void MediaHeaders_ReadWithCurrentReader()
    {
        var withFlag = TapeFramer.UnpackHeader<TapeMediaHeader>(Load("media-header-withflag.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(withFlag);
        Assert.Equal(GoldenData.MediaId, withFlag!.MediaId);
        Assert.Equal(1, withFlag.Volume);
        Assert.Equal(TapeTocPlacement.InSet, withFlag.TocPlacement);
        Assert.Equal("Orig", withFlag.OriginalName);
        Assert.True(withFlag.HasSetHeaders);

        var noFlag = TapeFramer.UnpackHeader<TapeMediaHeader>(Load("media-header-noflag.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(noFlag);
        Assert.Equal(TapeTocPlacement.InPartition, noFlag!.TocPlacement);
        Assert.Null(noFlag.OriginalName);
        Assert.False(noFlag.HasSetHeaders);
    }

    [Fact]
    public void SetHeaders_ReadWithCurrentReader()
    {
        var withDesc = TapeFramer.UnpackHeader<TapeSetHeader>(Load("set-header-desc.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(withDesc);
        Assert.Equal(GoldenData.MediaId, withDesc!.MediaId);
        Assert.Equal(1, withDesc.Volume);
        Assert.Equal(2, withDesc.VolumeSetIndex);
        Assert.Equal(3, withDesc.GlobalSetIndex);
        Assert.Equal("Set \u00fc", withDesc.Description);

        var noDesc = TapeFramer.UnpackHeader<TapeSetHeader>(Load("set-header-nodesc.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(noDesc);
        Assert.Null(noDesc!.Description);
    }

    [Theory]
    [InlineData("calibration-header-standard.bin", (int)GoldenData.BlockSize)]
    [InlineData("calibration-header-run.bin", 1 << 20)]
    public void CalibrationHeaders_ReadWithCurrentReader(string name, int blockSize)
    {
        var h = TapeFramer.UnpackHeader<TapeCalibrationHeader>(Load(name), blockSize);
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
        var noEw = TapeCalibrationFramer.Unpack<TapeCalibrationCheckpoint>(Load("checkpoint-noew.bin"), (int)GoldenData.BlockSize);
        Assert.NotNull(noEw);
        Assert.Equal(GoldenData.RunId, noEw!.RunId);
        Assert.Equal(1, noEw.Index);
        Assert.Equal(123456, noEw.BytesWritten);
        Assert.Null(noEw.EarlyWarning);
        Assert.Equal(2, noEw.Samples.Count);

        var ew = TapeCalibrationFramer.Unpack<TapeCalibrationCheckpoint>(Load("checkpoint-ew-manysamples.bin"), (int)GoldenData.BlockSize);
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
    public void Headers_LegacyWriterReproducesGolden(string name)
    {
        // Reader converts legacy local ticks to UTC; LegacyHeaderWriter converts back — the bytes must match exactly.
        //  The calibration header is still product-legacy until Phase 6; LegacyHeaderWriter forwards it to TapeFramer.Pack.
        byte[] golden = Load(name);
        TapeHeader header = TapeHeaderBlock.Classify(golden, golden.Length)
            ?? throw new InvalidOperationException($"{name} did not read");
        byte[] frame = LegacyHeaderWriter.Frame(header);
        Assert.Equal(golden[..frame.Length], frame);
    }

    /// <summary>
    /// Pins the legacy checkpoint layout both ways: the shipping reader parses the frozen golden, and the test-only
    ///  legacy writer reproduces it byte for byte from what was read. Checkpoints carry no timestamps, so — unlike the
    ///  media / set headers — there is no local ↔ UTC conversion to undo.
    /// </summary>
    [Fact]
    public void Checkpoints_LegacyWriterReproducesGolden()
    {
        byte[] golden = Load("checkpoint-ew-manysamples.bin");

        // The shipping reader: no 2.1 magic, so it falls back to the legacy frame.
        var record = TapeCalibrationFramer.Unpack<TapeCalibrationCheckpoint>(golden, golden.Length);
        Assert.NotNull(record);

        // The frozen legacy writer (test-only) — NOT TapeCalibrationFramer.Pack, which writes format 2.1.
        byte[] frame = LegacyFormatWriter.Frame(LegacyFormatWriter.CheckpointPayload(
            record.RunId, record.Index, record.BytesWritten, record.EarlyWarning, record.Samples));

        Assert.Equal(golden[..frame.Length], frame);
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
        Assert.Equal(TapeFrameStatus.CrcMismatch,
            TapeFramer.TryUnpackHeader<TapeSetHeader>(golden, golden.Length, out _));
    }

    #endregion
}
