using TapeLibNET.Agents;
using TapeLibNET.Calibration;
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Scan;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;
using Windows.Win32.Foundation;

namespace TapeLibNET.Tests.Scan;


/// <summary>
/// Scan Media Phase 1: <see cref="TapeScanner"/> walking real (virtual) cartridges — healthy, damaged,
///  legacy, foreign and blank.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test here runs with NO TOC in play.</b> The scanner is constructed from a drive alone, and no
///  assertion consults <c>fixture.TOC</c> for anything the tape should be telling us. That is the feature
///  under test (SM-2), not an incidental property of the setup.
/// </para>
/// <para>
/// The fixture is used only to LAY DOWN media — its backup agents write the headers, sets and TOC copies
///  the scanner must then rediscover from the bytes.
/// </para>
/// </remarks>
public class TapeScannerTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #endregion

    #region *** Helpers ***

    private static MediaScanMap Scan(
        VirtualTapeFixture fixture,
        ScanMediaOptions? options = null,
        IProgress<TapeScanProgress>? progress = null)
    {
        var scanner = new TapeScanner(fixture.Drive) { Options = options ?? ScanMediaOptions.Default };
        MediaScanMap? map = scanner.Scan(progress);

        Assert.True(map is not null, $"Scan returned no map: {scanner.LastResult}");
        return map!;
    }

    /// <summary>Lays down <paramref name="setCount"/> ordinary backup sets, each with a few small files.</summary>
    private static List<TempFileTree> SeedSets(VirtualTapeFixture fixture, int setCount, string prefix = "s")
    {
        List<TempFileTree> trees = [];

        for (int i = 1; i <= setCount; i++)
        {
            var tree = new TempFileTree();
            tree.AddFiles($"{prefix}{i}", count: 2, minSize: 256, maxSize: 4 * 1024);
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

    /// <summary>
    /// Kills the LAST set's closing separator, leaving the dominant real-world damage: a backup that died
    ///  mid-set.
    /// </summary>
    /// <remarks>
    /// Layout-aware by necessity. On the setmark layouts a set is closed by a real SETMARK, which nothing
    ///  else disturbs, so it must be erased deliberately — the fixture's <c>EraseLastSetmark</c> does
    ///  exactly that. On the filemark layouts the same helper still applies, since the navigator's
    ///  content-setmark verb resolves to whichever mark the layout uses.
    /// </remarks>
    private static void DamageTheTail(VirtualTapeFixture fixture) => fixture.EraseLastSetmark();

    #endregion

    #region *** (A) Healthy media — the structures v4 had to AVOID are now identified ***

    /// <summary>
    /// The minimal cartridge that exposes a mis-crossed media header: ONE set. The set header must be the
    ///  fragment immediately after the media header, at begin-of-content exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Regression.</b> The walk once advanced past the media header by the LAYOUT's separator. The
    ///  header is closed by a FILEMARK on every layout, so on the setmark layouts that first
    ///  <c>SpaceSetmarks(1)</c> sailed past the header's filemark, the whole set, and the set's closing
    ///  setmark — reporting ZERO sets. The filemark layouts hid it: there the separator is the same mark.
    /// </para>
    /// <para>
    /// A multi-set cartridge disguises the same bug as an off-by-one (4 sets found as 3), which reads like
    ///  a tail problem. One set makes it unmistakable, and the block assertion pins WHERE the walk landed
    ///  rather than merely what it counted.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SingleSet_IsFound_RightAfterTheMediaHeader(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 1);

        try
        {
            MediaScanMap map = Scan(fixture);

            ScanMapAssert.WellFormed(map);
            ScanMapAssert.Complete(map);
            ScanMapAssert.SetCount(map, 1);
            ScanMapAssert.SetHeaderAt(map, 0, description: "Set 1", volumeSetIndex: 0);

            // Positional, not just numerical: fragment #0 is the media header at block 0, and fragment #1
            //  is the set header at begin-of-content — the block just past the header's own filemark.
            Assert.True(map.Fragments.Count >= 2, ScanMapAssert.Describe(map));
            Assert.True(map.Fragments[0].Kind == FragmentKind.MediaHeader, ScanMapAssert.Describe(map));
            Assert.True(map.Fragments[1].Kind == FragmentKind.SetHeader, ScanMapAssert.Describe(map));

            Assert.True(map.Fragments[1].StartBlock == fixture.FirstContentBlock,
                $"set header at block {map.Fragments[1].StartBlock}, expected begin-of-content " +
                $"{fixture.FirstContentBlock}\n{ScanMapAssert.Describe(map)}");

            // The header is closed by its filemark and spans exactly [block, filemark].
            Assert.True(map.Fragments[0].ClosedBySeparator, ScanMapAssert.Describe(map));
            Assert.True(map.Fragments[0].BlockSpan == fixture.FirstContentBlock, ScanMapAssert.Describe(map));
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A freshly headed cartridge with no sets at all: the header is identified, the walk crosses its
    ///  filemark and ends cleanly — and no set is invented.
    /// </summary>
    /// <remarks>
    /// The other edge of the same one-shot header cross: with nothing behind the header, the walk must
    ///  terminate as a clean finding, never report a phantom set from whatever follows the mark.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HeaderOnly_FindsNoSets_AndEndsCleanly(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);

        MediaScanMap map = Scan(fixture);

        ScanMapAssert.WellFormed(map);
        ScanMapAssert.Complete(map);
        ScanMapAssert.SetCount(map, 0);

        Assert.NotNull(ScanMapAssert.MediaHeader(map));
        Assert.Single(map.Fragments);
        Assert.True(map.Fragments[0].Kind == FragmentKind.MediaHeader, ScanMapAssert.Describe(map));
        Assert.True(map.Fragments[0].ClosedBySeparator);
        Assert.True(ScannedMediaKind.Backup == map.Kind, ScanMapAssert.Describe(map));
    }

    /// <summary>
    /// Two filemarks with no data between them become ONE <see cref="FragmentKind.MarkRun"/>, detected by
    ///  the identification reads themselves — and the data block after the run is still identified.
    /// </summary>
    /// <remarks>
    /// One extra mark only: that is a double-filemark end-of-data convention, which must NOT fold into a
    ///  TOC mark even on the sequential-filemark layout (the fold needs at least two).
    /// </remarks>
    [Theory]
    [InlineData(DriveProfile.FilemarksOnly)]
    [InlineData(DriveProfile.SeqFilemarks)]
    public void AdjacentFilemarks_BecomeOneMarkRun_AndTheNextBlockIsIdentified(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        Assert.True(fixture.Drive.MoveToPartition(MediaPartition.Content));
        Assert.True(fixture.Drive.Rewind());

        int blk = (int)fixture.Drive.BlockSize;
        var junk = new byte[blk];
        new Random(7).NextBytes(junk);

        Assert.Equal(blk, fixture.Drive.WriteDirect(junk, 0, blk));
        Assert.True(fixture.Drive.WriteFilemark(1));    // closes the first block
        Assert.True(fixture.Drive.WriteFilemark(1));    // adjacent — the run
        Assert.Equal(blk, fixture.Drive.WriteDirect(junk, 0, blk));
        Assert.True(fixture.Drive.WriteFilemark(1));

        MediaScanMap map = Scan(fixture);

        ScanMapAssert.WellFormed(map);
        ScanMapAssert.Complete(map);
        ScanMapAssert.KindSequence(map, FragmentKind.Unknown, FragmentKind.MarkRun, FragmentKind.Unknown);

        Assert.True(ScanMapAssert.OfKind(map, FragmentKind.MarkRun)[0].MarkCount == 1,
            ScanMapAssert.Describe(map));
    }

    /// <summary>
    /// The central claim of the TOC-less design (§1): the trailing TOC copies are IDENTIFIED rather than
    ///  bounded away, and the set count is exact.
    /// </summary>
    /// <remarks>
    /// <b>The exact count carries this test.</b> On <c>SeqFilemarks</c> and <c>FilemarksOnly</c> the set
    ///  separator and the TOC delimiter are the same mark, so a walk that over-runs the content would read
    ///  a TOC copy and report a phantom fifth set. An "at least 4" assertion would pass on exactly the
    ///  cartridge the scanner must get right.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HealthyMedia_MapsEverySetAndTheTocCopies(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 4);

        try
        {
            MediaScanMap map = Scan(fixture);

            ScanMapAssert.WellFormed(map);
            ScanMapAssert.Complete(map);
            ScanMapAssert.SetCount(map, 4);
            ScanMapAssert.AllSetsClosed(map);

            Assert.True(ScannedMediaKind.Backup == map.Kind, ScanMapAssert.Describe(map));
            Assert.NotNull(ScanMapAssert.MediaHeader(map));
            ScanMapAssert.NothingUnknown(map);

            // The TOC lives in partition 0 on the partitioned layout, so the content walk rightly finds
            //  none there — every other layout trails its copies behind the content.
            if (!map.Layout.TocInPartition)
                ScanMapAssert.FoundTocCopies(map);

            // The TOC-mark layout names its marker rather than leaving an unidentified gap behind.
            if (map.Layout.HasTocMark)
                Assert.True(ScanMapAssert.OfKind(map, FragmentKind.TocMark).Count == 1,
                    ScanMapAssert.Describe(map));
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A user who lost their table of contents can still see what is on the tape — each set names and
    ///  dates itself. NO TOC is consulted anywhere in this test.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EverySetHeaderCarriesItsIdentity(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 3);

        try
        {
            MediaScanMap map = Scan(fixture);

            ScanMapAssert.SetCount(map, 3);
            ScanMapAssert.SetHeaderAt(map, 0, description: "Set 1", volumeSetIndex: 0);
            ScanMapAssert.SetHeaderAt(map, 1, description: "Set 2", volumeSetIndex: 1);
            ScanMapAssert.SetHeaderAt(map, 2, description: "Set 3", volumeSetIndex: 2);

            foreach (TapeMediaFragment set in ScanMapAssert.SetHeaders(map))
            {
                Assert.True(set.CreatedUtc is not null, ScanMapAssert.Describe(map));
                Assert.True(set.Id is not null && set.Id != Guid.Empty, ScanMapAssert.Describe(map));
            }

            // Every set agrees with the media header's identity — no mixed-identity observation.
            Assert.True(map.MixedIdentityFragments.Count == 0, ScanMapAssert.Describe(map));
            Assert.True(map.SetIndexGaps.Count == 0, ScanMapAssert.Describe(map));
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// SM-1, asserted at the byte level: a scan of a healthy cartridge changes nothing on the medium.
    /// </summary>
    /// <remarks>
    /// Compares the backing stream AND the virtual-block metadata. The second half matters: a stray mark
    ///  write carries no data, so a content-only comparison would miss it entirely.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Scan_NeverWrites(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 3);

        try
        {
            var before = ScanMapAssert.Snapshot(fixture);

            Scan(fixture, new ScanMediaOptions { HarvestTocCopies = true });

            before.AssertUnchanged(fixture);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>SM-9: the drive's block size survives a scan, whatever the walk set it to.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BlockSize_IsRestored(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 2);

        try
        {
            uint before = fixture.Drive.BlockSize;

            Scan(fixture, new ScanMediaOptions { HarvestTocCopies = true });

            Assert.True(fixture.Drive.BlockSize == before,
                $"Block size {fixture.Drive.BlockSize} after the scan, {before} before");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>A scan is repeatable: running it twice yields the same map and disturbs nothing.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Scan_IsIdempotent(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 3);

        try
        {
            MediaScanMap first = Scan(fixture);
            MediaScanMap second = Scan(fixture);

            Assert.True(first.Fragments.Count == second.Fragments.Count,
                $"FIRST:\n{ScanMapAssert.Describe(first)}\nSECOND:\n{ScanMapAssert.Describe(second)}");

            for (int i = 0; i < first.Fragments.Count; i++)
            {
                Assert.Equal(first.Fragments[i].Kind, second.Fragments[i].Kind);
                Assert.Equal(first.Fragments[i].StartBlock, second.Fragments[i].StartBlock);
                Assert.Equal(first.Fragments[i].Description, second.Fragments[i].Description);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A header-less 2.1 tape: every set start — block 0 included — is identified by its first file's header frame,
    ///  carrying the SetId the TOC records. The scan counts the sets with no TOC and no set headers.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Scan_HeaderlessV21Sets_IdentifiedBySetContent(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 1);
        tree1.AddFiles("a", count: 3, minSize: 512, maxSize: 4 * 1024);
        using var tree2 = new TempFileTree(seed: 2);
        tree2.AddFiles("b", count: 3, minSize: 512, maxSize: 4 * 1024);

        using var fx = new VirtualTapeFixture(profile);                       // no media header, no set headers
        fx.BackupFiles(tree1.Files, description: "One");
        fx.BackupFiles(tree2.Files, description: "Two");

        MediaScanMap map = new TapeScanner(fx.Drive).Scan()!;

        Assert.Equal(ScannedMediaKind.Backup, map.Kind);
        var sets = map.Fragments.Where(f => f.Kind == FragmentKind.SetContent).ToList();
        Assert.Equal(new[] { fx.TOC[1].SetId, fx.TOC[2].SetId }, sets.Select(f => f.Id!.Value).ToArray());
        Assert.Equal(2, map.SetCount);
    }

    /// <summary>A block that starts with the 2.1 magic by chance is never a set: the CRC decides.</summary>
    [Fact]
    public void SetContent_RequiresAValidFrame()
    {
        var block = new byte[TapeHeaderBlock.Size];
        "TpN#"u8.CopyTo(block);                                               // magic, then garbage
        new Random(3).NextBytes(block.AsSpan(4));

        Assert.Equal(HeaderBlockIdentity.Foreign, TapeHeaderBlock.IdentifyBlock(block, block.Length).Kind);
    }

    /// <summary>The live progress counter and the final map agree on header-less sets.</summary>
    [Fact]
    public void ScanProgress_CountsHeaderlessSets()
    {
        var f = new TapeMediaFragment
        {
            Ordinal = 0,
            StartBlock = 0,
            Kind = FragmentKind.SetContent,
            Id = Guid.NewGuid(),
            Description = @"C:\x.txt"
        };
        var line = ServiceScanProgressHandler.DescribeFragment(f);
        Assert.NotNull(line);
        Assert.Equal(ServiceReportLevel.Info, line!.Value.Level);
        Assert.Contains("no set header", line.Value.Text);
    }

    #endregion

    #region *** (B) Damaged media ***

    /// <summary>
    /// The dominant real-world fault — a backup that died mid-set — detected with NO TOC whatsoever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Note what the scanner cannot lean on: the half-written set's HEADER is perfectly healthy, since it
    ///  is written at the set's start before the first file. Only the missing closing separator proves the
    ///  set never completed, which is precisely why <see cref="TapeMediaFragment.ClosedBySeparator"/>
    ///  exists.
    /// </para>
    /// <para>
    /// And the scan must SUCCEED: end-of-data is the expected terminator, not a failure (§3.2).
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DamagedTail_LastSetUnclosed_AndTheScanStillSucceeds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 3);

        try
        {
            DamageTheTail(fixture);

            MediaScanMap map = Scan(fixture);

            ScanMapAssert.WellFormed(map);
            ScanMapAssert.Complete(map);          // damage is a FINDING, not a scan failure
            ScanMapAssert.OnlyLastSetUnclosed(map);

            // The surviving sets are still fully described — the damage cost the tail, not the inventory.
            ScanMapAssert.SetHeaderAt(map, 0, description: "Set 1");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A corrupt header must not stop the walk (SM-4): the fragments BEHIND a bad block are exactly the
    ///  ones most worth finding on a damaged cartridge.
    /// </summary>
    /// <remarks>
    /// Uses <c>Corrupt</c> rather than <c>Fail</c> deliberately — it reports full success with wrong
    ///  bytes, so only the framing CRC knows anything is amiss. That is the realistic shape of a damaged
    ///  header, and it is the one that proves identification (not the error channel) is doing the work.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CorruptSetHeader_IsUnknown_AndTheWalkContinues(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 4);

        try
        {
            // Skip the BOM read at block 0, then corrupt the NEXT block the scan reads — the first set's
            //  header. Offset 48 lands inside the framed record, past the length prefix.
            fixture.Backend.ContentReadFaults.SkipN = 1;
            fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48);

            MediaScanMap map = Scan(fixture);

            ScanMapAssert.WellFormed(map);
            Assert.True(fixture.Backend.ContentReadFaults.Occurrences == 1,
                $"the corruption never fired\n{ScanMapAssert.Describe(map)}");

            // One fragment lost its identity; the ones behind it did not.
            ScanMapAssert.UnknownCount(map, 1);
            ScanMapAssert.SetCount(map, 3);

            TapeMediaFragment unknown = ScanMapAssert.OfKind(map, FragmentKind.Unknown)[0];
            Assert.False(string.IsNullOrEmpty(unknown.Fingerprint),
                $"an unidentified fragment must carry a fingerprint\n{ScanMapAssert.Describe(map)}");

            // Not just "unidentified": our header, damaged. (Before the fix this block was a phantom TOC.)
            Assert.True(unknown.Diagnosis.ErrorWin32 == WIN32_ERROR.ERROR_CRC,
                $"expected a CRC diagnosis\n{ScanMapAssert.Describe(map)}");

            // And the real copies are recognized as what they are — carrying this cartridge's identity.
            if (!map.Layout.TocInPartition)
            {
                ScanMapAssert.FoundTocCopies(map, atLeast: 2);
                Assert.All(ScanMapAssert.OfKind(map, FragmentKind.TOC),
                    t => Assert.Equal(map.MediaId, t.Id));
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A read fault on one block is likewise survivable — the scan records the diagnosis and walks on.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ReadFaultOnOneBlock_IsRecorded_AndTheWalkContinues(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 4);

        try
        {
            fixture.Backend.ContentReadFaults.SkipN = 1;     // let block 0 through
            fixture.Backend.ContentReadFaults.FailOnce();

            MediaScanMap map = Scan(fixture);

            ScanMapAssert.WellFormed(map);
            Assert.True(fixture.Backend.ContentReadFaults.Occurrences == 1,
                $"the fault never fired\n{ScanMapAssert.Describe(map)}");

            // The scan carried on past the fault rather than abandoning the cartridge.
            Assert.True(map.Fragments.Count > 2, ScanMapAssert.Describe(map));
            ScanMapAssert.SetCount(map, 3);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A cartridge whose TOC was never written — a backup that died before its own TOC write. The content
    ///  is intact and fully mapped; only the index is missing.
    /// </summary>
    /// <remarks>
    /// The scenario that motivated the whole feature: the TOC-less scan is the ONLY thing that can tell
    ///  the user what survives here.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ContentWithoutAnyTocCopy_IsStillFullyMapped(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);

        using var tree = new TempFileTree();
        tree.AddFiles("notoc", count: 3, minSize: 256, maxSize: 4 * 1024);

        // Back up WITHOUT the fixture's TOC save — BackupFiles always saves, so drive the agent directly.
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Orphaned";
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using (var agent = fixture.CreateBackupAgent())
        {
            Assert.True(agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: true),
                "seeding backup failed");
            // Deliberately NO BackupTOC() — this is the cartridge with content but no index.
        }

        MediaScanMap map = Scan(fixture);

        ScanMapAssert.WellFormed(map);
        ScanMapAssert.SetCount(map, 1);
        ScanMapAssert.SetHeaderAt(map, 0, description: "Orphaned");
        Assert.True(map.TocCopyCount == 0, ScanMapAssert.Describe(map));
    }

    #endregion

    #region *** (C) Media that is not a backup ***

    /// <summary>
    /// SM-6: blank media yields an empty, UNTRUNCATED map. "Blank" and "broken" must never be the same
    ///  result — a user told their cartridge is broken will not then reuse it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BlankMedia_YieldsEmptyMap_WithoutFailing(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);   // nothing written at all

        MediaScanMap map = Scan(fixture);

        Assert.True(ScannedMediaKind.Blank == map.Kind, ScanMapAssert.Describe(map));
        Assert.True(map.Fragments.Count == 0, ScanMapAssert.Describe(map));
        Assert.False(map.Truncated, ScanMapAssert.Describe(map));
        Assert.Equal(0, map.SetCount);
        Assert.Null(map.MediaId);
        Assert.False(map.LastSetUnclosed);
    }

    /// <summary>
    /// Legacy media: content at block 0, no media header, no set headers. The block-0 read SUCCEEDS but
    ///  identifies nothing — which is a different finding from blank, and must stay different.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LegacyMedia_IsNotMistakenForBlank(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: false);
        var trees = SeedSets(fixture, setCount: 2, prefix: "lg");

        try
        {
            MediaScanMap map = Scan(fixture);

            ScanMapAssert.WellFormed(map);
            Assert.True(map.Fragments.Count > 0,
                $"legacy media must not read as blank\n{ScanMapAssert.Describe(map)}");
            Assert.True(ScannedMediaKind.Blank != map.Kind, ScanMapAssert.Describe(map));

            // No headers were ever written, so nothing identifies itself — yet the separators still map.
            Assert.Null(ScanMapAssert.MediaHeader(map));
            ScanMapAssert.SetCount(map, 0);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Foreign data — filemark-delimited blocks that are nobody's records. Every fragment is
    ///  <see cref="FragmentKind.Unknown"/>, the walk completes, and nothing throws.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ForeignData_MapsAsUnknownFragments(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        Assert.True(fixture.Drive.MoveToPartition(MediaPartition.Content));
        Assert.True(fixture.Drive.Rewind());

        int blk = (int)fixture.Drive.BlockSize;
        var junk = new byte[blk];
        new Random(99).NextBytes(junk);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(blk, fixture.Drive.WriteDirect(junk, 0, blk));
            Assert.True(fixture.Drive.WriteFilemark(1));
        }

        MediaScanMap map = Scan(fixture);

        ScanMapAssert.WellFormed(map);
        ScanMapAssert.SetCount(map, 0);
        Assert.Null(ScanMapAssert.MediaHeader(map));
        Assert.True(map.UnknownCount > 0, ScanMapAssert.Describe(map));
    }

    #endregion

    #region *** (D) Termination and abort ***

    /// <summary>
    /// A transport fault marks the map truncated — and still RETURNS what was gathered (SM-5).
    /// </summary>
    /// <remarks>
    /// The distinction that matters: a positional error means "the tape ends here" and is a finding, while
    ///  a transport fault means the drive went away. A map built on a dead drive's silence, presented as
    ///  complete, would let the comparison phase read that silence as ABSENCE.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TransportFault_MarksMapTruncated_ButStillReturnsIt(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 4);

        try
        {
            // Let the first few reads through, then take the drive away mid-walk.
            fixture.Backend.ContentReadFaults.SkipN = 3;
            fixture.Backend.ContentReadFaults.FailOnce((uint)WIN32_ERROR.ERROR_NOT_READY);

            var scanner = new TapeScanner(fixture.Drive);
            MediaScanMap? map = scanner.Scan();

            // Whatever the walk managed is still handed back — that is the whole of SM-5.
            Assert.True(map is not null, $"a fault mid-walk must not discard the map: {scanner.LastResult}");
            ScanMapAssert.WellFormed(map!);
            Assert.True(map!.Fragments.Count > 0, ScanMapAssert.Describe(map));
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// An abort stops the walk at a clean fragment boundary, flags the map truncated, and leaves the tape
    ///  byte-identical.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Abort_MidScan_YieldsTruncatedMap_AndTapeByteIdentical(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 5);

        try
        {
            var before = ScanMapAssert.Snapshot(fixture);

            var scanner = new TapeScanner(fixture.Drive);
            var sink = new AbortAfterFragments(scanner, afterCount: 2);

            MediaScanMap? map = scanner.Scan(sink);

            Assert.True(map is not null, $"an abort must still yield the partial map: {scanner.LastResult}");
            ScanMapAssert.Truncated(map);
            ScanMapAssert.WellFormed(map);

            Assert.True(map.TerminatorWin32 == (uint)WIN32_ERROR.ERROR_CANCELLED,
                ScanMapAssert.Describe(map));

            before.AssertUnchanged(fixture, "an aborted scan must leave the tape untouched");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// SM-10, and the deliberate divergence from <see cref="TapeCalibrator"/>: a progress sink that THROWS
    ///  <see cref="TapeAbortRequestedException"/> aborts the scan cleanly — the exception is converted to
    ///  the cooperative flag at the scanner and never escapes.
    /// </summary>
    /// <remarks>
    /// Real, not defensive: a WPF sink marshals to the UI thread and may well throw, and a service-side
    ///  handler may bridge a <c>CancellationToken</c> that way.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProgressSinkThrowingAbort_StopsCleanly(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 4);

        try
        {
            var scanner = new TapeScanner(fixture.Drive);
            var sink = new ThrowingSink(afterCount: 2, new TapeAbortRequestedException("test abort"));

            // The decisive assertion is that this call RETURNS rather than propagating.
            MediaScanMap? map = scanner.Scan(sink);

            Assert.True(map is not null, "an aborting sink must still yield the partial map");
            Assert.True(scanner.IsAbortRequested, "the exception must become the cooperative flag");
            ScanMapAssert.Truncated(map!);
            ScanMapAssert.WellFormed(map!);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A sink that throws anything ELSE is logged and swallowed: a reporting fault can abort a scan, never
    ///  fail one.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ProgressSinkThrowingOther_DoesNotFailTheScan(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 3);

        try
        {
            var scanner = new TapeScanner(fixture.Drive);
            var sink = new ThrowingSink(afterCount: 1, new InvalidOperationException("sink is broken"));

            MediaScanMap? map = scanner.Scan(sink);

            Assert.True(map is not null, "a broken sink must not fail the scan");
            Assert.False(scanner.IsAbortRequested, "a non-abort exception must not request an abort");
            ScanMapAssert.Complete(map!);
            ScanMapAssert.SetCount(map!, 3);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>The runaway guard stops the walk and truncates rather than spinning.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MaxFragments_StopsAndTruncates(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 4);

        try
        {
            MediaScanMap map = Scan(fixture, new ScanMediaOptions { MaxFragments = 2 });

            ScanMapAssert.Truncated(map);
            Assert.True(map.Fragments.Count <= 3,   // the guard trips between fragments
                ScanMapAssert.Describe(map));
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>Progress fires once per fragment, in order, and the final ordinal matches the map.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Progress_ReportsEveryFragmentInOrder(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 3);

        try
        {
            var sink = new RecordingSink();

            MediaScanMap map = Scan(fixture, progress: sink);

            Assert.True(sink.Fragments.Count > 0, ScanMapAssert.Describe(map));

            for (int i = 0; i < sink.Fragments.Count; i++)
                Assert.Equal(i, sink.Fragments[i].Ordinal);

            // Every reported fragment appears in the map, at the same block.
            foreach (TapeMediaFragment reported in sink.Fragments)
                Assert.True(ScanMapAssert.AtBlock(map, reported.StartBlock) is not null,
                    $"fragment reported at block {reported.StartBlock} is missing from the map\n" +
                    ScanMapAssert.Describe(map));
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (E) No media ***

    /// <summary>
    /// With no cartridge there is no map — but <see cref="TapeScanner.LastResult"/> explains why, which is
    ///  the whole reason the result latch exists on a verb that returns a nullable.
    /// </summary>
    [Fact]
    public void NoMedia_ReturnsNullWithADiagnosis()
    {
        var backend = VirtualTapeDriveBackend.CreateMemoryBacked(
            TestLoggerFactory.Default,
            VirtualTapeFixture.ProfileToCapabilities(DriveProfile.Setmarks),
            contentCapacity: VirtualTapeFixture.DefaultContentCapacity,
            initiatorPartitionCapacity: 0);

        using var drive = new TapeDrive(TestLoggerFactory.Default, backend);
        Assert.True(drive.ReopenDrive(0));
        Assert.False(drive.IsMediaLoaded);

        var scanner = new TapeScanner(drive);
        MediaScanMap? map = scanner.Scan();

        Assert.Null(map);
        Assert.False(scanner.LastResult.Success);
        Assert.Equal(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE, scanner.LastResult.ErrorWin32);
    }

    #endregion

    #region *** Progress sinks ***

    /// <summary>Records every fragment the scan reports, for order and completeness assertions.</summary>
    private sealed class RecordingSink : IProgress<TapeScanProgress>
    {
        public List<TapeMediaFragment> Fragments { get; } = [];
        public List<string> Phases { get; } = [];

        public void Report(TapeScanProgress p)
        {
            Phases.Add(p.Phase);

            if (p.Fragment is { } fragment)
                Fragments.Add(fragment);
        }
    }

    /// <summary>
    /// Flips the scanner's abort flag after N fragments — the COOPERATIVE path, mirroring
    ///  <c>CalibrationResumeTests.AbortAfterBytes</c>.
    /// </summary>
    private sealed class AbortAfterFragments(TapeScanner scanner, int afterCount)
        : IProgress<TapeScanProgress>
    {
        private int m_seen;

        public void Report(TapeScanProgress p)
        {
            if (p.Fragment is null)
                return;

            if (++m_seen >= afterCount)
                scanner.IsAbortRequested = true;
        }
    }

    /// <summary>Throws a supplied exception after N fragments — the EXCEPTION path (SM-10).</summary>
    private sealed class ThrowingSink(int afterCount, Exception toThrow) : IProgress<TapeScanProgress>
    {
        private int m_seen;

        public void Report(TapeScanProgress p)
        {
            if (p.Fragment is null)
                return;

            if (++m_seen >= afterCount)
                throw toThrow;
        }
    }

    #endregion
}
