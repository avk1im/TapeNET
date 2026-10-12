using TapeLibNET.Calibration;
using TapeLibNET.Compression;
using TapeLibNET.Drive;
using TapeLibNET.Format;
using TapeLibNET.Headers;
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Helpers;


/// <summary>
/// The format 2.1 golden cases: deterministic inputs, how today's writer turns them into bytes, and how today's reader
///  must read those bytes back. Shared by the (skipped) generator and by <c>FormatGoldenV21Tests</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is fixed</b> — GUIDs, UTC times, names, sample values. A golden that depends on the clock, the
///  machine or a random number is not a golden. The TOC is written through the deterministic
///  <see cref="TapeTOC.SaveTo(System.IO.Stream, System.DateTime, string)"/>, which stamps the given time and writer
///  instead of now / the running build.
/// </para>
/// <para>
/// <b>Frames, not blocks.</b> Header and checkpoint goldens hold the frame (<c>Record ‖ CRC-64</c>) only: the zero padding
///  of a header block adds nothing, and a calibration run block is padded with random bytes.
/// </para>
/// <para>
/// <b>Never edit a case's inputs once its golden is checked in.</b> To pin a new shape, add a new case with a new name.
/// </para>
/// </remarks>
internal static class GoldenV21
{
    /// <summary>One golden: its file name, what it pins, the writer, and the reader check.</summary>
    internal sealed record Case(string Name, string Describes, Func<byte[]> Write, Action<byte[]> AssertReads);

    #region *** Fixed inputs ***

    private static readonly Guid s_mediaId = new("6a2f1c00-0021-4e0b-9d5c-000000000001");
    private static readonly Guid s_setIdA = new("6a2f1c00-0021-4e0b-9d5c-0000000000a1");
    private static readonly Guid s_setIdB = new("6a2f1c00-0021-4e0b-9d5c-0000000000b2");
    private static readonly Guid s_setIdC = new("6a2f1c00-0021-4e0b-9d5c-0000000000c3");
    private static readonly Guid s_runId = new("6a2f1c00-0021-4e0b-9d5c-0000000000ca");

    private static readonly DateTime T0 = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    /// <summary>The save moment stamped into every TOC golden — the TOC's and every (new, hence modified) set's.</summary>
    private static readonly DateTime SaveTime = T2;

    private const string WrittenBy = "TapeNET golden generator / TapeLibNET 2.1";

    private static TapeFileDescriptor Descr(string path, long length, FileAttributes attributes = FileAttributes.Archive)
        => new(path)
        {
            Length = length,
            Attributes = attributes,
            CreationTime = T0,
            LastWriteTime = T1,
            LastAccessTime = T2,
        };

    private static TapeFileInfo File(TapeSetTOC set, string path, long length, long block, uint offset,
        TapeFileCodec codec = TapeFileCodec.Stored, byte[]? hash = null, FileAttributes attributes = FileAttributes.Archive)
        => new(set.GenerateFileId(), new TapeAddress(block, offset), Descr(path, length, attributes))
        {
            SizeOnTape = length + 120,
            Codec = codec,
            Hash = hash,
        };

    private static byte[] Hash(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);      // seeded: deterministic across runs and machines
        return bytes;
    }

    private static TapeCalibrationPlan Plan() => new(
        SampleCount: 1000, BodySampleCount: 600, TailSampleCount: 400,
        BlockSize: 1u << 20, BlocksPerChunk: 8, ChunkSize: 8 << 20,
        TailBlocksPerChunk: 1, TailChunkSize: 1 << 20,
        TailCapacityFraction: 0.05, NumCheckpoints: 128);

    private static List<(long ActualWritten, long ReportedRemaining)> Samples(int count, long step)
    {
        var list = new List<(long, long)>(count);
        long capacity = step * (count + 10);
        for (int i = 0; i < count; i++)
            list.Add((i * step, capacity - i * step - i * 37L));
        return list;
    }

    #endregion

    #region *** The TOC ***

    /// <summary>
    /// Four sets covering every TOC feature: a full V2 set (hashes, both codecs, Unicode and long names, combined
    ///  attributes), an incremental continuation set, a LEGACY set carried inside a 2.1 TOC, and an empty set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The empty set comes last</b> on purpose: <c>AddNewSetTOC</c> REPLACES a trailing empty set, so any set added
    ///  after it would overwrite it.
    /// </para>
    /// <para>
    /// <b>The continuation set is built through <c>AddContinuationSetTOC</c></b> — <c>ContinuedFromPrevVolume</c> is
    ///  init-only. It carries its own SetId here (a real continuation shares its parent's), which keeps all ids distinct
    ///  in the bytes.
    /// </para>
    /// <para>
    /// No set's <c>LastSaveTime</c> is set: every set here is new, hence modified, so the save stamps
    ///  <see cref="SaveTime"/> on all of them.
    /// </para>
    /// </remarks>
    internal static TapeTOC BuildToc()
    {
        var toc = new TapeTOC("Golden 2.1 — Ünïcødé media")
        {
            CreationTime = T0,
            Volume = 2,
            ContinuedOnNextVolume = true,
            MediaId = s_mediaId,
        };

        // Set 1 — full V2 set
        toc.AddNewSetTOC();
        TapeSetTOC full = toc.CurrentSetTOC;
        full.SetId = s_setIdA;
        full.Description = "Full";
        full.CreationTime = T0;
        full.BlockSize = 64 * 1024;
        full.HashAlgorithm = TapeHashAlgorithm.XxHash3;
        full.Compression = TapeCompression.Software;
        full.Append(File(full, @"C:\Data\Projects\Alpha\readme.txt", 1_234, 3, 0, TapeFileCodec.Zstd, Hash(1, 8)));
        full.Append(File(full, @"C:\Data\Projects\Alpha\src\main.cs", 98_765, 3, 1_354, TapeFileCodec.Stored, Hash(2, 8)));
        full.Append(File(full, @"C:\Data\Projects\Βήτα\日本語\ファイル.dat", 0, 5, 0, TapeFileCodec.Stored, Hash(3, 8),
            FileAttributes.Archive | FileAttributes.ReadOnly | FileAttributes.Hidden));
        full.Append(File(full, @"C:\Data\" + new string('x', 300) + @"\long.bin", 70_000, 5, 200,
            TapeFileCodec.Zstd, Hash(4, 8)));

        // Set 2 — incremental, continued from the previous volume
        toc.AddContinuationSetTOC(
            new TapeSetTOCParams("Incremental", TapeHashAlgorithm.Crc32, 64 * 1024, Incremental: true,
                SetId: s_setIdB, NextFileId: 1UL),
            contFromPrevVolume: true);
        TapeSetTOC inc = toc.CurrentSetTOC;
        inc.CreationTime = T1;
        inc.Append(File(inc, @"C:\Data\Projects\Alpha\src\main.cs", 99_000, 20, 0));

        // Set 3 — a legacy set carried in a 2.1 TOC: no SetId, FileIds are the old UIDs
        toc.AddNewSetTOC();
        TapeSetTOC legacy = toc.CurrentSetTOC;
        legacy.DataFormat = TapeDataFormat.Legacy;
        legacy.SetId = Guid.Empty;
        legacy.Description = "Legacy";
        legacy.CreationTime = T0;
        legacy.BlockSize = 32 * 1024;
        legacy.HashAlgorithm = TapeHashAlgorithm.Crc32;
        legacy.Append(File(legacy, @"C:\Old\a.txt", 500, 30, 0, hash: Hash(5, 4)));
        legacy.Append(File(legacy, @"C:\Old\b.txt", 600, 30, 512, hash: Hash(6, 4)));

        // Set 4 — empty, and LAST: writes a set record but no batch
        toc.AddNewSetTOC();
        TapeSetTOC empty = toc.CurrentSetTOC;
        empty.SetId = s_setIdC;
        empty.CreationTime = T2;
        empty.BlockSize = 64 * 1024;

        return toc;
    }

    /// <summary>One set of 4,097 files with deep shared prefixes — crosses the 4,096-file batch boundary.</summary>
    internal static TapeTOC BuildBatchToc()
    {
        var toc = new TapeTOC("Golden 2.1 — batches")
        {
            CreationTime = T0,
            MediaId = s_mediaId,
        };
        toc.AddNewSetTOC();
        TapeSetTOC set = toc.CurrentSetTOC;
        set.SetId = s_setIdA;
        set.Description = "Many";
        set.CreationTime = T0;
        set.BlockSize = 256 * 1024;
        for (int i = 0; i < 4_097; i++)
            set.Append(File(set, $@"C:\Tree\dir{i / 100:D3}\file{i:D5}.txt", 100 + i, 10 + i / 50, (uint)(i % 50 * 200)));
        return toc;
    }

    /// <summary>
    /// Writes <paramref name="toc"/> deterministically — and stamps it, exactly as a real save does. The stamped instance
    ///  is therefore the expected value for the reader check.
    /// </summary>
    private static byte[] WriteToc(TapeTOC toc)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms, SaveTime, WrittenBy);
        return ms.ToArray();
    }

    /// <summary>Builds a TOC, saves it the same way the golden was saved, and checks the bytes read back into it.</summary>
    private static void AssertTocReads(byte[] bytes, Func<TapeTOC> build)
    {
        TapeTOC expected = build();
        WriteToc(expected);                     // apply the same stamps the golden carries
        TapeTOC actual = TapeTOC.LoadFrom(new MemoryStream(bytes));

        Assert.False(actual.LoadedFromLegacy);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.MediaId, actual.MediaId);
        Assert.Equal(expected.CreationTime, actual.CreationTime);
        Assert.Equal(expected.LastSaveTime, actual.LastSaveTime);
        Assert.Equal(expected.Volume, actual.Volume);
        Assert.Equal(expected.ContinuedOnNextVolume, actual.ContinuedOnNextVolume);
        Assert.Equal(expected.WrittenBy, actual.WrittenBy);
        Assert.Equal(expected.Count, actual.Count);

        for (int s = 1; s <= expected.Count; s++)
        {
            TapeSetTOC e = expected[s], a = actual[s];
            Assert.Equal(e.SetId, a.SetId);
            Assert.Equal(e.DataFormat, a.DataFormat);
            Assert.Equal(e.Description, a.Description);
            Assert.Equal(e.CreationTime, a.CreationTime);
            Assert.Equal(e.LastSaveTime, a.LastSaveTime);
            Assert.Equal(e.BlockSize, a.BlockSize);
            Assert.Equal(e.Volume, a.Volume);
            Assert.Equal(e.NextFileId, a.NextFileId);
            Assert.Equal(e.HashAlgorithm, a.HashAlgorithm);
            Assert.Equal(e.Compression, a.Compression);
            Assert.Equal(e.CompressionLevel, a.CompressionLevel);
            Assert.Equal(e.Incremental, a.Incremental);
            Assert.Equal(e.ContinuedFromPrevVolume, a.ContinuedFromPrevVolume);
            Assert.Equal(e.Count, a.Count);
            for (int f = 0; f < e.Count; f++)
            {
                TapeFileInfo ef = e[f], af = a[f];
                Assert.Equal(ef.FileId, af.FileId);
                Assert.Equal(ef.Address, af.Address);
                Assert.Equal(ef.FileDescr.FullName, af.FileDescr.FullName);
                Assert.Equal(ef.FileDescr.Length, af.FileDescr.Length);
                Assert.Equal(ef.FileDescr.Attributes, af.FileDescr.Attributes);
                Assert.Equal(ef.FileDescr.CreationTime, af.FileDescr.CreationTime);
                Assert.Equal(ef.FileDescr.LastWriteTime, af.FileDescr.LastWriteTime);
                Assert.Equal(ef.FileDescr.LastAccessTime, af.FileDescr.LastAccessTime);
                Assert.Equal(ef.SizeOnTape, af.SizeOnTape);
                Assert.Equal(ef.Codec, af.Codec);
                Assert.Equal(ef.Hash ?? [], af.Hash ?? []);
            }
        }
    }

    #endregion

    #region *** Headers and calibration ***

    private static TapeMediaHeader MediaHeader(bool rich) => new()
    {
        MediaId = s_mediaId,
        CreatedUtc = T0,
        TocBlockSize = TapeHeader.FixedHeaderBlockSize,
        Volume = rich ? 3 : 1,
        Partition = MediaPartition.Content,
        TocPlacement = rich ? TapeTocPlacement.InPartition : TapeTocPlacement.InSet,
        OriginalName = rich ? "Weekly — Ünïcødé" : null,
        HasSetHeaders = rich,
    };

    private static TapeSetHeader SetHeader() => new()
    {
        MediaId = s_mediaId,
        SetId = s_setIdA,
        CreatedUtc = T1,
        SetBlockSize = 64 * 1024,
        Volume = 2,
        VolumeSetIndex = 1,
        GlobalSetIndex = 5,
        Description = "Full",
    };

    private static TapeCalibrationHeader CalibrationHeader()
        => TapeCalibrationHeader.CreateHeader(s_runId, "VENDOR|PRODUCT|REV|64MB", 64L << 20, 1u << 20, T0, Plan());

    private static TapeCalibrationCheckpoint Checkpoint(string shape) => shape switch
    {
        "ew" => new(s_runId, 7, 123_456_789L, (100_000_000L, 0L), Samples(40, 1L << 20)),   // LTO-3: 0 at EW
        "noew" => new(s_runId, 0, 0L, null, Samples(1, 1)),
        _ => new(s_runId, 99, 1_000L << 30, (990L << 30, 12_345L), Samples(1_000, 30L << 30)),  // LTO-9 cadence
    };

    private static void AssertHeaderReads<T>(byte[] bytes, T expected) where T : TapeHeader
    {
        Assert.True(TapeFormat.IsV2(bytes));
        T? actual = TapeFramer.UnpackHeader<T>(bytes, bytes.Length);
        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    private static void AssertCheckpointReads(byte[] bytes, TapeCalibrationCheckpoint expected)
    {
        TapeCalibrationCheckpoint? actual = TapeCalibrationFramer.Unpack<TapeCalibrationCheckpoint>(bytes, bytes.Length);
        Assert.NotNull(actual);
        Assert.Equal(expected.RunId, actual!.RunId);
        Assert.Equal(expected.Index, actual.Index);
        Assert.Equal(expected.BytesWritten, actual.BytesWritten);
        Assert.Equal(expected.EarlyWarning, actual.EarlyWarning);
        Assert.Equal(expected.Samples, actual.Samples);
    }

    #endregion

    #region *** File header ***

    private static (Guid SetId, TapeFileInfo File) FileHeaderInput()
    {
        var toc = BuildToc();
        return (s_setIdA, toc[1][2]);      // the Unicode path with combined attributes
    }

    private static byte[] WriteFileHeader()
    {
        var (setId, file) = FileHeaderInput();
        using var ms = new MemoryStream();
        TapeFileHeader.Write(ms, setId, file);
        return ms.ToArray();
    }

    private static void AssertFileHeaderReads(byte[] bytes)
    {
        var (setId, file) = FileHeaderInput();
        using var ms = new MemoryStream(bytes);
        TapeFileHeader header = TapeFileHeader.Read(ms);
        Assert.Equal(bytes.Length, ms.Position);             // no read-ahead: the body would follow here
        Assert.Equal(setId, header.SetId);
        Assert.Equal(file.FileId, header.FileId);
        Assert.Equal(file.FileDescr.FullName, header.Name);
    }

    #endregion

    #region *** The catalog ***

    /// <summary>Every 2.1 golden. Append only — never rename or edit a case whose golden is checked in.</summary>
    internal static IReadOnlyList<Case> All { get; } =
    [
        new("toc-sets.v21.bin",
            "TOC stream: full V2 set, incremental continuation, legacy set in a 2.1 TOC, empty set",
            () => WriteToc(BuildToc()), b => AssertTocReads(b, BuildToc)),
        new("toc-batches.v21.bin",
            "TOC stream: 4,097 files — two batches, front coding reset per batch",
            () => WriteToc(BuildBatchToc()), b => AssertTocReads(b, BuildBatchToc)),
        new("file-header.v21.bin",
            "inline file header frame: Unicode path, combined attributes",
            WriteFileHeader, AssertFileHeaderReads),
        new("media-header-rich.v21.bin",
            "media header frame: every optional field present",
            () => TapeFramer.PackHeader(MediaHeader(rich: true)), b => AssertHeaderReads(b, MediaHeader(rich: true))),
        new("media-header-plain.v21.bin",
            "media header frame: every optional field at its default (elided)",
            () => TapeFramer.PackHeader(MediaHeader(rich: false)), b => AssertHeaderReads(b, MediaHeader(rich: false))),
        new("set-header.v21.bin",
            "set header frame with SetId",
            () => TapeFramer.PackHeader(SetHeader()), b => AssertHeaderReads(b, SetHeader())),
        new("calibration-header.v21.bin",
            "calibration run header frame with the plan group",
            () => TapeCalibrationFramer.Pack(CalibrationHeader()), b => AssertHeaderReads(b, CalibrationHeader())),
        new("checkpoint-ew.v21.bin",
            "checkpoint: early-warning group with ReportedRemaining 0 (LTO-3)",
            () => TapeCalibrationFramer.Pack(Checkpoint("ew")), b => AssertCheckpointReads(b, Checkpoint("ew"))),
        new("checkpoint-noew.v21.bin",
            "checkpoint: no early-warning group",
            () => TapeCalibrationFramer.Pack(Checkpoint("noew")), b => AssertCheckpointReads(b, Checkpoint("noew"))),
        new("checkpoint-many.v21.bin",
            "checkpoint: 1,000 delta-coded samples at an LTO-9 cadence",
            () => TapeCalibrationFramer.Pack(Checkpoint("many")), b => AssertCheckpointReads(b, Checkpoint("many"))),
    ];

    #endregion
}
