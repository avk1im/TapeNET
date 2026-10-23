using TapeLibNET.Calibration;
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Scan;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Headers;

/// <summary>
/// Design-TocLocation Phase 1: the shared classifier — every peeker total over arbitrary bytes, the dispatcher's
///  verdicts with their typed evidence, the projections agreeing with it, the shared read primitive, and the scanner
///  identifying block 0 like any other block.
/// </summary>
public class BlockIdentificationTests
{
    #region *** Sample blocks ***

    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid MediaId = new("0a0b0c0d-1111-2222-3333-444455556666");
    private static readonly Guid SetId = new("9f8e7d6c-aaaa-bbbb-cccc-ddddeeeeffff");

    private static byte[] Pad(byte[] bytes)
    {
        var block = new byte[Math.Max(TapeHeaderBlock.Size, bytes.Length)];
        bytes.CopyTo(block, 0);
        return block;
    }

    private static TapeTOC SampleToc()
    {
        var toc = new TapeTOC("Sample") { Volume = 3 };
        toc.EnsureMediaId();
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Description = "One";
        toc.CurrentSetTOC.Append(new TapeFileInfo(toc.CurrentSetTOC.GenerateFileId(), new TapeAddress(5, 0),
            new TapeFileDescriptor(@"C:\x.bin") { CreationTime = T0, LastWriteTime = T0, LastAccessTime = T0 }));
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Append(new TapeFileInfo(toc.CurrentSetTOC.GenerateFileId(), new TapeAddress(7, 0),
            new TapeFileDescriptor(@"C:\y.dat") { CreationTime = T0, LastWriteTime = T0, LastAccessTime = T0 }));
        return toc;
    }

    private static byte[] TocBlock(TapeTOC toc)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms, T0, "BlockIdentificationTests");
        return Pad(ms.ToArray());
    }

    private static byte[] FileHeaderBlock(string name = @"C:\data\first.txt")
    {
        var file = new TapeFileInfo(7, TapeAddress.Zero, new TapeFileDescriptor(name)
        {
            Length = 42, CreationTime = T0, LastWriteTime = T0, LastAccessTime = T0,
        });
        using var ms = new MemoryStream();
        TapeFileHeader.Write(ms, SetId, file);
        return Pad(ms.ToArray());
    }

    private static byte[] SetHeaderBlock() => TapeHeaderBlock.Frame(new TapeSetHeader
    {
        MediaId = MediaId, SetId = SetId, CreatedUtc = T0, SetBlockSize = 65536,
        Volume = 1, VolumeSetIndex = 0, GlobalSetIndex = 1, Description = "Set",
    })!;

    private static byte[] MediaHeaderBlock() => TapeHeaderBlock.Frame(new TapeMediaHeader
    {
        MediaId = MediaId, CreatedUtc = T0, TocBlockSize = TapeHeader.FixedHeaderBlockSize, Volume = 1,
        Partition = MediaPartition.Content, TocPlacement = TapeTocPlacement.InSet, OriginalName = "Media",
    })!;

    private static byte[] CheckpointBlock()
        => Pad(TapeCalibrationFramer.Pack(new TapeCalibrationCheckpoint(Guid.NewGuid(), 2, 4096, (10, 20), [(1, 2), (3, 4)])));

    /// <summary>Every kind of block the dispatcher knows, intact.</summary>
    private static IEnumerable<(string Name, byte[] Block)> ValidBlocks()
    {
        yield return ("toc", TocBlock(SampleToc()));
        yield return ("legacy toc", Pad(LegacyTocWriter.TocBytes(SampleToc())));
        yield return ("file header", FileHeaderBlock());
        yield return ("set header", SetHeaderBlock());
        yield return ("media header", MediaHeaderBlock());
        yield return ("legacy media header", LegacyHeaderWriter.Block(new TapeMediaHeader
        {
            MediaId = MediaId, CreatedUtc = T0, TocBlockSize = TapeHeader.FixedHeaderBlockSize, Volume = 1,
            Partition = MediaPartition.Content, TocPlacement = TapeTocPlacement.InSet,
        }));
        yield return ("checkpoint", CheckpointBlock());
    }

    /// <summary>Damaged variants: every truncation of the record's head, bit flips throughout, magic + noise.</summary>
    private static IEnumerable<byte[]> Mutations(byte[] valid)
    {
        int recordEnd = valid.Length;
        while (recordEnd > 0 && valid[recordEnd - 1] == 0)
            recordEnd--;

        for (int cut = 0; cut <= Math.Min(recordEnd + 8, valid.Length); cut += Math.Max(1, recordEnd / 64))
            yield return valid[..cut];
        for (int at = 0; at < recordEnd; at += 5)
        {
            byte[] flipped = [.. valid];
            flipped[at] ^= 0x5A;
            yield return flipped;
        }
        var noise = new byte[TapeHeaderBlock.Size];
        new Random(recordEnd).NextBytes(noise);
        valid.AsSpan(0, Math.Min(8, valid.Length)).CopyTo(noise);       // magic + kind + version, then noise
        yield return noise;
    }

    #endregion

    #region *** Totality — no peeker throws, whatever the bytes ***

    /// <summary>
    /// The contract every peeker signs (§3.2): never throw. Each peeker is fed EVERY block — its own, the other kinds',
    ///  and all their mutations — because the bytes on a damaged cartridge choose themselves.
    /// </summary>
    [Fact]
    public void EveryPeeker_IsTotal_OverEveryBlockAndMutation()
    {
        var inputs = new List<byte[]> { Array.Empty<byte>(), new byte[TapeHeaderBlock.Size] };
        foreach (var (_, block) in ValidBlocks())
        {
            inputs.Add(block);
            inputs.AddRange(Mutations(block));
        }

        foreach (byte[] input in inputs)
        {
            ReadOnlySpan<byte> span = input;
            TapeBlockPeek.Run<TapeTOC, TapeTocPeek>(span, out _);
            TapeBlockPeek.Run<TapeFileHeader, TapeFilePeek>(span, out _);
            TapeBlockPeek.Run<TapeHeader, TapeHeader>(span, out _);
            TapeBlockPeek.Run<TapeCalibrationCheckpoint, TapeCalibrationCheckpoint>(span, out _);
            TapeHeaderBlock.IdentifyBlock(input, input.Length);
        }
    }

    /// <summary>A mutated block is never identified as something it is not: it keeps its kind, or loses it.</summary>
    [Fact]
    public void Mutations_NeverChangeTheKindToAnotherPositiveOne()
    {
        foreach (var (name, block) in ValidBlocks())
        {
            HeaderBlockIdentity original = TapeHeaderBlock.IdentifyBlock(block, block.Length).Kind;
            foreach (byte[] mutated in Mutations(block))
            {
                HeaderBlockIdentity kind = TapeHeaderBlock.IdentifyBlock(mutated, mutated.Length).Kind;
                Assert.True(kind == original || kind is HeaderBlockIdentity.Foreign or HeaderBlockIdentity.DamagedRecord,
                    $"{name}: a mutation turned {original} into {kind}");
            }
        }
    }

    #endregion

    #region *** The dispatcher's verdicts and their evidence ***

    [Fact]
    public void TocCopy_CarriesTheRicherPeek()
    {
        TapeTOC toc = SampleToc();
        byte[] block = TocBlock(toc);

        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, block.Length);

        Assert.Equal(HeaderBlockIdentity.TocCopy, id.Kind);
        Assert.True(id.TryGetToc(out TapeTocPeek peek));
        Assert.False(peek.IsLegacy);
        Assert.Equal(TapeTOC.TocVersion, peek.Version);
        Assert.Equal(toc.MediaId, peek.MediaId);
        Assert.Equal(3, peek.Volume);
        Assert.Equal(T0, peek.LastSaveTime);
        Assert.Equal(2, peek.SetCount);
        Assert.Equal(peek.Version, id.TocVersion);                 // the compatibility properties agree
        Assert.Equal(peek.MediaId, id.TocMediaId);
        Assert.False(id.TryGetFile(out _));
        Assert.False(id.TryGetHeader<TapeHeader>(out _));
    }

    [Fact]
    public void LegacyTocCopy_IsLegacy_WithWhatTheFirstBlockCannotTellLeftNull()
    {
        TapeTOC toc = SampleToc();
        byte[] block = Pad(LegacyTocWriter.TocBytes(toc));

        Assert.True(TapeHeaderBlock.IdentifyBlock(block, block.Length).TryGetToc(out TapeTocPeek peek));
        Assert.True(peek.IsLegacy);
        Assert.Equal(toc.MediaId, peek.MediaId);
        Assert.Null(peek.Volume);
        Assert.Null(peek.LastSaveTime);
        Assert.Null(peek.SetCount);
    }

    [Fact]
    public void FileHeader_IsIdentifiedPositively_WithItsSetAndName()
    {
        byte[] block = FileHeaderBlock(@"C:\data\ünïcödé.txt");

        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, block.Length);

        Assert.Equal(HeaderBlockIdentity.FileHeader, id.Kind);
        Assert.True(id.TryGetFile(out TapeFilePeek file));
        Assert.Equal(SetId, file.SetId);
        Assert.Equal(7UL, file.FileId);
        Assert.Equal(@"C:\data\ünïcödé.txt", file.Name);
    }

    /// <summary>
    /// An unverified file header is no evidence of damage: a set's first block may be a file BODY that starts with the
    ///  magic by chance. Foreign — exactly what the scanner always made of it.
    /// </summary>
    [Fact]
    public void FileHeader_Damaged_IsForeign_NeverDamagedRecord()
    {
        byte[] block = FileHeaderBlock();
        block[20] ^= 0x01;
        Assert.Equal(HeaderBlockIdentity.Foreign, TapeHeaderBlock.IdentifyBlock(block, block.Length).Kind);
    }

    [Fact]
    public void Headers_ReachTypedAccessor()
    {
        byte[] block = SetHeaderBlock();
        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, block.Length);

        Assert.True(id.TryGetHeader(out TapeSetHeader? set));
        Assert.Equal(SetId, set!.SetId);
        Assert.False(id.TryGetHeader<TapeMediaHeader>(out _));      // narrow: another kind is not a misparse
        Assert.False(id.TryGetToc(out _));
    }

    #endregion

    #region *** Projections agree with the dispatcher ***

    [Fact]
    public void Projections_AgreeWithTheDispatcher_OnEveryBlock()
    {
        var blocks = ValidBlocks().Select(v => v.Block).ToList();
        blocks.AddRange(ValidBlocks().SelectMany(v => Mutations(v.Block)));

        foreach (byte[] block in blocks)
        {
            IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, block.Length);

            Assert.Equal(id.Header, TapeHeaderBlock.Classify(block, block.Length));
            Assert.Equal(id.Header is not null, TapeHeaderBlock.TryIdentifyHeaderBlock(block, block.Length, out _));

            bool isToc = TapeTOC.TryPeek(block, block.Length, out ushort version, out Guid media);
            Assert.Equal(id.Kind == HeaderBlockIdentity.TocCopy, isToc);
            if (isToc)
            {
                Assert.Equal(id.TocVersion, version);
                Assert.Equal(id.TocMediaId, media);
            }
        }
    }

    #endregion

    #region *** The shared read primitive ***

    /// <summary>One primitive reports blocks, marks and end-of-data — with the block identified.</summary>
    [Fact]
    public void ReadAndIdentify_WalksHeaderMarkHeaderAndEod()
    {
        using var tree = new TempFileTree();
        tree.AddFiles("rai", count: 2, minSize: 256, maxSize: 1024);
        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: true, withSetHeaders: true);
        fixture.BackupFiles(tree.Files, description: "Set");
        Assert.True(fixture.Drive.Rewind());

        IdentifiedBlock media = TapeHeaderBlock.ReadAndIdentify(fixture.Drive, out _, out int read, out bool mark, out bool eod);
        Assert.True(read > 0 && !mark && !eod);
        Assert.True(media.TryGetHeader<TapeMediaHeader>(out _));

        TapeHeaderBlock.ReadAndIdentify(fixture.Drive, out _, out read, out mark, out eod);
        Assert.True(read <= 0 && mark && !eod, "the media header's filemark");

        IdentifiedBlock set = TapeHeaderBlock.ReadAndIdentify(fixture.Drive, out _, out read, out _, out _);
        Assert.True(set.TryGetHeader<TapeSetHeader>(out TapeSetHeader? header));
        Assert.Equal(fixture.TOC[1].SetId, header!.SetId);

        using var blank = new VirtualTapeFixture(DriveProfile.Setmarks);
        Assert.True(blank.Drive.Rewind());
        IdentifiedBlock none = TapeHeaderBlock.ReadAndIdentify(blank.Drive, out _, out read, out mark, out eod);
        Assert.True(read <= 0 && eod && !mark);
        Assert.Equal(HeaderBlockIdentity.Foreign, none.Kind);
    }

    #endregion

    #region *** The scanner identifies block 0 like any other block ***

    public static TheoryData<DriveProfile> BomTocProfiles => [DriveProfile.Setmarks, DriveProfile.FilemarksOnly];

    /// <summary>
    /// A header-less volume holding only a TOC: block 0 IS the first copy. Through the one dispatcher the scan now says
    ///  so — and recovers it — where it used to report an unidentified block.
    /// </summary>
    [Theory]
    [MemberData(nameof(BomTocProfiles))]
    public void Scan_TocOnlyHeaderlessVolume_IdentifiesAndHarvestsTheTocAtBlock0(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        fixture.SaveTOC();

        var scanner = new TapeScanner(fixture.Drive) { Options = new ScanMediaOptions { HarvestTocCopies = true } };
        MediaScanMap map = scanner.Scan()!;
        string d = ScanMapAssert.Describe(map);

        Assert.True(map.Kind == ScannedMediaKind.Backup, d);
        Assert.True(map.Fragments[0].Kind == FragmentKind.TOC && map.Fragments[0].StartBlock == 0, d);
        Assert.True(map.Fragments[0].HarvestedToc is not null, d);
        Assert.Equal(2, map.TocCopyCount);
        ScanMapAssert.Complete(map);
    }

    #endregion
}
