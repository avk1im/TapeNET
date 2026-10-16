using Windows.Win32.Foundation;

using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Scan;
using TapeLibNET.Services;
using TapeLibNET.Toc;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Scan;


/// <summary>
/// Scan Media across partitions (SM-14) and across mark types: every partition walked, nothing presumed about what any of
///  them holds, the same cartridge content yielding the same findings on every layout — and fragments no convention
///  covers closed by the nearest mark.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite exists.</b> The earlier suites encoded the limitation as expected behaviour: TOC assertions were
///  skipped with <c>if (!map.Layout.TocInPartition)</c>, and the harvest tests ran on in-set profiles only. A partitioned
///  cartridge therefore scanned as "no TOC" — and a legacy one as Foreign — without a single red test. The parity theory
///  below is the test that would have caught it.
/// </para>
/// <para>
/// The odd cartridges are laid down with raw writes: a media header in the TOC partition, a TOC copy in the content
///  partition, debris, a damaged TOC copy ahead of a good one. Raw records use the fixed 16 KiB header/TOC block, as the
///  agents write them.
/// </para>
/// </remarks>
public class TapeScannerPartitionTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    /// <summary>The two layouts that separate sets with SETMARKS — where the nearest-mark rule matters.</summary>
    public static TheoryData<DriveProfile> SetmarkProfiles => [DriveProfile.Setmarks, DriveProfile.Partitions];

    #endregion

    #region *** Helpers ***

    private static MediaScanMap Scan(VirtualTapeFixture fixture, bool harvest = true,
        IProgress<TapeScanProgress>? progress = null)
    {
        var scanner = new TapeScanner(fixture.Drive) { Options = new ScanMediaOptions { HarvestTocCopies = harvest } };
        MediaScanMap? map = scanner.Scan(progress);
        Assert.True(map is not null, $"Scan returned no map: {scanner.LastResult}");
        ScanMapAssert.WellFormed(map!);
        Assert.Equal((int)fixture.Drive.PartitionCount, map!.Partitions.Count);
        return map;
    }

    private static List<TempFileTree> SeedSets(VirtualTapeFixture fixture, int setCount)
    {
        List<TempFileTree> trees = [];
        for (int i = 1; i <= setCount; i++)
        {
            var tree = new TempFileTree(seed: 40 + i);
            tree.AddFiles($"p{i}", count: 2, minSize: 256, maxSize: 4 * 1024);
            trees.Add(tree);
            fixture.BackupFiles(tree.Files, description: $"Set {i}");
        }
        return trees;
    }

    private static void DisposeAll(List<TempFileTree> trees)
    {
        foreach (var t in trees)
            t.Dispose();
    }

    /// <summary>Writes <paramref name="bytes"/> as one record in 16 KiB blocks at the head's position, plus a filemark.</summary>
    private static void WriteRecord(VirtualTapeFixture fx, byte[] bytes)
    {
        uint saved = fx.Drive.BlockSize;
        uint bs = (uint)TapeHeaderBlock.Size;
        Assert.True(fx.Drive.SetBlockSize(bs));
        int len = (int)((bytes.Length + bs - 1) / bs * bs);
        var buffer = new byte[len];
        bytes.CopyTo(buffer, 0);
        Assert.Equal(len, fx.Drive.WriteDirect(buffer, 0, len));
        Assert.True(fx.Drive.WriteFilemark(1));
        Assert.True(fx.Drive.SetBlockSize(saved));
    }

    /// <summary>Appends a record at the end of data of <paramref name="partition"/>.</summary>
    private static void AppendRecord(VirtualTapeFixture fx, MediaPartition partition, byte[] bytes)
    {
        Assert.True(fx.Drive.FastforwardToEnd(partition), $"cannot reach the end of the {partition} partition");
        WriteRecord(fx, bytes);
    }

    /// <summary>A TOC copy's bytes, exactly as the agent writes one.</summary>
    private static byte[] TocBytes(TapeTOC toc)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms);
        return ms.ToArray();
    }

    /// <summary>A TOC copy damaged behind its magic: ours by the magic, unreadable by its record.</summary>
    private static byte[] DamagedTocBytes(TapeTOC toc)
    {
        byte[] bytes = TocBytes(toc);
        bytes[20] ^= 0x5A;
        return bytes;
    }

    private static TapeTOC NewToc(string name)
    {
        var toc = new TapeTOC(name);
        toc.EnsureMediaId();
        return toc;
    }

    /// <summary>Random debris, one 16 KiB block.</summary>
    private static byte[] Debris(int seed)
    {
        var junk = new byte[TapeHeaderBlock.Size];
        new Random(seed).NextBytes(junk);
        return junk;
    }

    #endregion

    #region *** (A) Parity — the same cartridge content, the same findings, on every layout ***

    /// <summary>
    /// THE regression test for partitioned media: three sets with headers, both TOC copies, every copy recovered, nothing
    ///  unidentified, nothing out of place — identically on all four layouts. Only WHERE the copies stand may differ.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SameContent_SameFindings_OnEveryLayout(DriveProfile profile)
    {
        using var fx = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 3);
        try
        {
            MediaScanMap map = Scan(fx);
            string d = ScanMapAssert.Describe(map);

            Assert.True(map.Kind == ScannedMediaKind.Backup, d);
            ScanMapAssert.Complete(map);
            ScanMapAssert.SetCount(map, 3);
            ScanMapAssert.NothingUnknown(map);
            Assert.True(map.OutOfPlaceFragments.Count == 0, d);
            Assert.True(map.MediaId == fx.TOC.MediaId, d);

            var copies = ScanMapAssert.OfKind(map, FragmentKind.TOC);
            Assert.True(copies.Count == 2, $"expected both TOC copies\n{d}");
            Assert.All(copies, c =>
            {
                Assert.NotNull(c.HarvestedToc);
                Assert.Equal(fx.TOC.Count, c.HarvestedToc!.Count);
                Assert.Equal(fx.TOC.MediaId, c.HarvestedToc.MediaId);
            });
            ScanMapAssert.TocCopiesWhereTheLayoutPutsThem(map);
            Assert.All(ScanMapAssert.SetHeaders(map), s => Assert.Equal(MediaPartition.Content, s.Partition));
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>Harvesting leaves the map identical on every layout — the initiator walk included (SM-12).</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Harvest_LeavesTheMapUnchanged_OnEveryLayout(DriveProfile profile)
    {
        using var fx = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 2);
        try
        {
            MediaScanMap plain = Scan(fx, harvest: false);
            MediaScanMap harvested = Scan(fx, harvest: true);
            string both = $"PLAIN:\n{ScanMapAssert.Describe(plain)}\nHARVESTED:\n{ScanMapAssert.Describe(harvested)}";
            Assert.True(plain.Fragments.Count == harvested.Fragments.Count, both);
            for (int i = 0; i < plain.Fragments.Count; i++)
            {
                TapeMediaFragment p = plain.Fragments[i], h = harvested.Fragments[i];
                Assert.True(p.Kind == h.Kind && p.Partition == h.Partition && p.StartBlock == h.StartBlock
                            && p.ClosedBySeparator == h.ClosedBySeparator && p.BlockSpan == h.BlockSpan,
                    $"fragment #{i} differs\n{both}");
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (B) The partitioned layout ***

    /// <summary>The TOC copies are found where the layout writes them: the initiator partition, the first at block 0.</summary>
    [Fact]
    public void Partitioned_TocCopiesAreInTheInitiatorPartition_FromBlock0()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 2);
        try
        {
            MediaScanMap map = Scan(fx);
            string d = ScanMapAssert.Describe(map);

            Assert.DoesNotContain(map.FragmentsIn(MediaPartition.Content), f => f.Kind == FragmentKind.TOC);
            var copies = map.FragmentsIn(MediaPartition.Initiator).Where(f => f.Kind == FragmentKind.TOC).ToList();
            Assert.True(copies.Count == 2, d);
            Assert.True(copies[0].StartBlock == 0, d);
            Assert.NotNull(ScanMapAssert.AtBlock(map, 0, MediaPartition.Initiator));
            Assert.All(copies, c => Assert.True(c.ClosedBySeparator, d));
            Assert.False(map.Partitions[1].Blank, d);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>A scan leaves the drive in the content partition, where every other verb starts.</summary>
    [Fact]
    public void Partitioned_Scan_ReturnsToTheContentPartition()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 1);
        try
        {
            Scan(fx);
            Assert.Equal(MediaPartition.Content, fx.Drive.GetCurrentPartition());
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Content written, TOC partition empty: the partition is reported BLANK — walked, nothing there — which is a finding
    ///  of its own, distinct from "not scanned".
    /// </summary>
    [Fact]
    public void Partitioned_EmptyTocPartition_IsReportedBlank()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions);
        Assert.True(fx.Drive.MoveToPartition(MediaPartition.Content, 0));
        WriteRecord(fx, Debris(3));

        MediaScanMap map = Scan(fx);
        string d = ScanMapAssert.Describe(map);
        Assert.True(map.Kind == ScannedMediaKind.Foreign, d);
        ScanMapAssert.Complete(map);
        Assert.True(map.Partitions[1].Blank, d);
        Assert.Empty(map.FragmentsIn(MediaPartition.Initiator));
    }

    /// <summary>
    /// A blank content partition no longer ends the scan: a TOC in the initiator partition makes the cartridge a backup
    ///  cartridge — and it is recovered.
    /// </summary>
    [Fact]
    public void Partitioned_BlankContent_TocInInitiator_IsBackup()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions);
        TapeTOC toc = NewToc("Only an index");
        Assert.True(fx.Drive.MoveToPartition(MediaPartition.Initiator, 0));
        WriteRecord(fx, TocBytes(toc));

        MediaScanMap map = Scan(fx);
        string d = ScanMapAssert.Describe(map);
        Assert.True(map.Kind == ScannedMediaKind.Backup, d);
        Assert.True(map.Partitions[0].Blank, d);
        var copy = Assert.Single(map.Fragments);
        Assert.Equal(FragmentKind.TOC, copy.Kind);
        Assert.Equal(MediaPartition.Initiator, copy.Partition);
        Assert.NotNull(copy.HarvestedToc);
        Assert.Equal(toc.MediaId, copy.HarvestedToc!.MediaId);
    }

    /// <summary>
    /// Debris in the TOC partition — after the copies — is mapped there as unidentified data, not damage; the copies are
    ///  still recovered, and the cartridge is still a backup cartridge.
    /// </summary>
    [Fact]
    public void Partitioned_DebrisInTheTocPartition_IsMappedThere()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 2);
        try
        {
            AppendRecord(fx, MediaPartition.Initiator, Debris(11));
            AppendRecord(fx, MediaPartition.Initiator, Debris(12));

            MediaScanMap map = Scan(fx);
            string d = ScanMapAssert.Describe(map);

            var init = map.FragmentsIn(MediaPartition.Initiator).ToList();
            Assert.True(init.Count(f => f.Kind == FragmentKind.TOC) == 2, d);
            Assert.True(init.Count(f => f.Kind == FragmentKind.Unknown && f.Diagnosis.Success) == 2, d);
            Assert.All(init.Where(f => f.Kind == FragmentKind.TOC), c => Assert.NotNull(c.HarvestedToc));
            Assert.True(map.Kind == ScannedMediaKind.Backup, d);
            ScanMapAssert.Complete(map);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Partitions mixed up, direction 1: a media header in the TOC partition is identified there and flagged out of place.
    /// </summary>
    [Fact]
    public void Partitioned_MediaHeaderInTheTocPartition_IsIdentified_AndOutOfPlace()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 1);
        try
        {
            byte[] block = TapeHeaderBlock.Frame(fx.TOC.CreateHeader(TapeHeader.FixedHeaderBlockSize,
                TapeTocPlacement.InPartition))!;
            AppendRecord(fx, MediaPartition.Initiator, block);

            MediaScanMap map = Scan(fx);
            string d = ScanMapAssert.Describe(map);
            var stray = Assert.Single(map.FragmentsIn(MediaPartition.Initiator),
                f => f.Kind == FragmentKind.MediaHeader);
            Assert.Contains(stray, map.OutOfPlaceFragments);
            Assert.Equal(MediaPartition.Content, map.PrimaryMediaHeader!.Partition);   // content speaks for the cartridge
            Assert.True(map.MixedIdentityFragments.Count == 0, d);                     // same series

            var (level, _, details) = TapeServiceBase.VerbalizeScan(map);
            Assert.Equal(ServiceReportLevel.Warning, level);
            Assert.Contains(details, l => l.Contains("partition"));
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Partitions mixed up, direction 2: a TOC copy in the CONTENT partition of a partitioned cartridge is identified,
    ///  flagged out of place — and recovered from where it stands, not from where the layout says a TOC belongs.
    /// </summary>
    [Fact]
    public void Partitioned_TocCopyInTheContentPartition_IsRecoveredFromThere()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 1);
        try
        {
            TapeTOC stray = NewToc("Stray index");
            AppendRecord(fx, MediaPartition.Content, TocBytes(stray));

            MediaScanMap map = Scan(fx);
            string d = ScanMapAssert.Describe(map);
            var copy = Assert.Single(map.FragmentsIn(MediaPartition.Content), f => f.Kind == FragmentKind.TOC);
            Assert.Contains(copy, map.OutOfPlaceFragments);
            Assert.True(copy.HarvestedToc is not null, d);
            Assert.Equal(stray.MediaId, copy.HarvestedToc!.MediaId);

            // The real copies in the TOC partition are unaffected.
            Assert.True(map.FragmentsIn(MediaPartition.Initiator).Count(f => f.HarvestedToc is not null) == 2, d);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A damaged FIRST TOC copy in the TOC partition: reported as damage, crossed by the filemark that closes it — and the
    ///  second copy behind it is recovered.
    /// </summary>
    [Fact]
    public void Partitioned_DamagedFirstTocCopy_SecondIsRecovered()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions);
        TapeTOC toc = NewToc("Two copies, one bad");
        Assert.True(fx.Drive.MoveToPartition(MediaPartition.Initiator, 0));
        WriteRecord(fx, DamagedTocBytes(toc));
        WriteRecord(fx, TocBytes(toc));

        MediaScanMap map = Scan(fx, harvest: true);
        string d = ScanMapAssert.Describe(map);
        var init = map.FragmentsIn(MediaPartition.Initiator).ToList();

        Assert.True(init.Count == 2, d);
        Assert.True(init[0].Kind == FragmentKind.TOC && !init[0].Diagnosis.Success && init[0].ClosedBySeparator, d);
        Assert.True(init[1].Kind == FragmentKind.TOC && init[1].Diagnosis.Success && init[1].HarvestedToc is not null, d);
        Assert.Equal(toc.MediaId, init[1].HarvestedToc!.MediaId);
    }

    #endregion

    #region *** (C) The nearest-mark rule — setmark layouts ***

    /// <summary>
    /// THE regression test for the setmark gap: an unidentifiable block in the CONTENT partition of a setmark layout,
    ///  closed by a FILEMARK, used to be crossed by a setmark hop — which ran to end of data and lost everything behind it.
    ///  Here a damaged TOC copy is followed by a good one: the nearest mark (the filemark) closes the damaged one, and the
    ///  good one is found and recovered.
    /// </summary>
    [Theory]
    [MemberData(nameof(SetmarkProfiles))]
    public void DamagedTocCopy_ClosedByAFilemark_DoesNotHideTheNextCopy(DriveProfile profile)
    {
        using var fx = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 2);
        try
        {
            TapeTOC stray = NewToc("Behind the damage");
            MediaPartition tocPart = profile is DriveProfile.Partitions ? MediaPartition.Initiator : MediaPartition.Content;
            AppendRecord(fx, tocPart, DamagedTocBytes(stray));
            AppendRecord(fx, tocPart, TocBytes(stray));

            MediaScanMap map = Scan(fx, harvest: true);
            string d = ScanMapAssert.Describe(map);

            var damaged = Assert.Single(map.FragmentsIn(tocPart),
                f => f.Kind == FragmentKind.TOC && !f.Diagnosis.Success);
            Assert.True(damaged.ClosedBySeparator, $"the damaged copy must be closed by the filemark behind it\n{d}");

            var recovered = map.FragmentsIn(tocPart)
                .Where(f => f.Kind == FragmentKind.TOC && f.HarvestedToc?.MediaId == stray.MediaId).ToList();
            Assert.True(recovered.Count == 1, $"the good copy behind the damaged one must be found\n{d}");
            Assert.True(recovered[0].Ordinal > damaged.Ordinal, d);
            ScanMapAssert.Complete(map);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The other direction of the same rule: debris followed only by a SETMARK is closed by that setmark — the nearest
    ///  mark when no filemark lies ahead — and not left open.
    /// </summary>
    [Fact]
    public void Debris_ClosedByASetmark_IsClosedByIt()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 1);
        try
        {
            // … [TOC copies] [debris][SM] — no filemark behind the debris, only a setmark.
            Assert.True(fx.Drive.FastforwardToEnd(MediaPartition.Content));
            uint saved = fx.Drive.BlockSize;
            Assert.True(fx.Drive.SetBlockSize((uint)TapeHeaderBlock.Size));
            byte[] junk = Debris(21);
            Assert.Equal(junk.Length, fx.Drive.WriteDirect(junk, 0, junk.Length));
            Assert.True(fx.Drive.WriteSetmark(1));
            Assert.True(fx.Drive.SetBlockSize(saved));

            MediaScanMap map = Scan(fx);
            string d = ScanMapAssert.Describe(map);
            var debris = Assert.Single(map.FragmentsIn(MediaPartition.Content),
                f => f.Kind == FragmentKind.Unknown && f.Diagnosis.Success);
            Assert.True(debris.ClosedBySeparator, d);
            ScanMapAssert.Complete(map);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (D) Stopping ***

    /// <summary>
    /// An abort during the content walk does not move the tape into the TOC partition: it is reported NOT SCANNED —
    ///  truncated, never blank.
    /// </summary>
    [Fact]
    public void Partitioned_AbortInContent_TocPartitionNotScanned()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 4);
        try
        {
            var scanner = new TapeScanner(fx.Drive);
            MediaScanMap map = scanner.Scan(new AbortAfter(scanner, fragments: 2))!;
            string d = ScanMapAssert.Describe(map);

            ScanMapAssert.Truncated(map);
            Assert.Equal((uint)WIN32_ERROR.ERROR_CANCELLED, map.TerminatorWin32);
            Assert.Empty(map.FragmentsIn(MediaPartition.Initiator));
            Assert.True(map.Partitions[1].Truncated && !map.Partitions[1].Blank, d);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>A scan of a partitioned cartridge writes nothing, in either partition (SM-1).</summary>
    [Fact]
    public void Partitioned_Scan_NeverWrites()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 2);
        try
        {
            var before = ScanMapAssert.Snapshot(fx);    // covers BOTH partitions
            Scan(fx, harvest: true);
            before.AssertUnchanged(fx, "a scan must write nothing in either partition");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (E) Map, wording, persistence ***

    /// <summary>The partition survives a JSON round trip, fragment by fragment and partition by partition.</summary>
    [Fact]
    public void Map_RoundTripsPartitions_ThroughJson()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fx, setCount: 1);
        try
        {
            MediaScanMap map = Scan(fx);
            MediaScanMap back = MediaScanMap.FromJson(map.ToJson())!;
            Assert.Equal(map.Fragments.Select(f => f.Partition), back.Fragments.Select(f => f.Partition));
            Assert.Equal(map.Partitions, back.Partitions);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>A fragment in the TOC partition says so in its log line — "block 0" alone would be ambiguous.</summary>
    [Fact]
    public void DescribeFragment_NamesTheTocPartition()
    {
        var f = new TapeMediaFragment
        {
            Ordinal = 5, StartBlock = 0, Partition = MediaPartition.Initiator, Kind = FragmentKind.TOC,
            HarvestedToc = new TapeTOC("x"),
        };
        var line = ServiceScanProgressHandler.DescribeFragment(f, tocInPartition: true);
        Assert.NotNull(line);
        Assert.Contains("TOC partition", line!.Value.Text);
    }

    #endregion

    #region *** Sinks ***

    private sealed class AbortAfter(TapeScanner scanner, int fragments) : IProgress<TapeScanProgress>
    {
        private int m_seen;
        public void Report(TapeScanProgress p)
        {
            if (p.Fragment is not null && ++m_seen >= fragments)
                scanner.IsAbortRequested = true;
        }
    }

    #endregion
}
