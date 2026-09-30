using TapeLibNET.Scan;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Scan Media Phase 3: TOC recovery — <see cref="TapeAgentBase.RestoreTOCAt"/> on its own, and the
///  scanner's harvest step built on it.
/// </summary>
/// <remarks>
/// <para>
/// The TOC is never taken from the fixture's in-memory copy: every recovered TOC comes off the tape, and
///  the fixture's <c>TOC</c> serves only as the EXPECTED value.
/// </para>
/// <para>
/// In-set profiles only, unless stated: on the partitioned layout the TOC sits in the initiator
///  partition, which the content walk never visits.
/// </para>
/// </remarks>
public class TapeScannerHarvestTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> InSetProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #endregion

    #region *** Helpers ***

    private static List<TempFileTree> SeedSets(VirtualTapeFixture fixture, int setCount)
    {
        List<TempFileTree> trees = [];

        for (int i = 1; i <= setCount; i++)
        {
            var tree = new TempFileTree();
            tree.AddFiles($"h{i}", count: 3, minSize: 256, maxSize: 4 * 1024);
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

    private static MediaScanMap Scan(VirtualTapeFixture fixture, bool harvest,
        IProgress<TapeScanProgress>? progress = null)
    {
        var scanner = new TapeScanner(fixture.Drive) { Options = new ScanMediaOptions { HarvestTocCopies = harvest } };
        MediaScanMap? map = scanner.Scan(progress);

        Assert.True(map is not null, $"Scan returned no map: {scanner.LastResult}");
        return map!;
    }

    /// <summary>The TOC copies' first blocks, found by a plain scan.</summary>
    private static IReadOnlyList<long> TocBlocks(VirtualTapeFixture fixture)
    {
        MediaScanMap map = Scan(fixture, harvest: false);
        var blocks = ScanMapAssert.OfKind(map, FragmentKind.TOC).Select(f => f.StartBlock).ToList();

        Assert.True(blocks.Count >= 2, $"expected two TOC copies\n{ScanMapAssert.Describe(map)}");
        return blocks;
    }

    /// <summary>
    /// Compares the fields that survive a tape round trip. <c>LastSaveTime</c> is deliberately excluded:
    ///  every serialization restamps it.
    /// </summary>
    private static void AssertSameToc(TapeTOC expected, TapeTOC? actual, string because)
    {
        Assert.True(actual is not null, $"no TOC recovered — {because}");

        Assert.Equal(expected.MediaId, actual!.MediaId);
        Assert.Equal(expected.Volume, actual.Volume);
        Assert.Equal(expected.Count, actual.Count);

        for (int s = 1; s <= expected.Count; s++)
        {
            TapeSetTOC e = expected[s], a = actual[s];

            Assert.Equal(e.Description, a.Description);
            Assert.Equal(e.Count, a.Count);

            for (int f = 0; f < e.Count; f++)
            {
                Assert.Equal(e[f].FileDescr.FullName, a[f].FileDescr.FullName);
                Assert.Equal(e[f].Address, a[f].Address);
            }
        }
    }

    /// <summary>Arms a one-shot action on the first "harvesting-toc" phase tick.</summary>
    /// <remarks>
    /// Progress is synchronous and the phase tick precedes the harvest's first read, so a fault armed here
    ///  lands on exactly that read — deterministic, with no read counting.
    /// </remarks>
    private sealed class ArmOnFirstHarvest(Action arm) : IProgress<TapeScanProgress>
    {
        private bool m_armed;

        public void Report(TapeScanProgress p)
        {
            if (!m_armed && p.Phase == TapeScanProgress.PhaseHarvestingToc)
            {
                m_armed = true;
                arm();
            }
        }
    }

    #endregion

    #region *** (A) RestoreTOCAt — the agent verb on its own ***

    /// <summary>Each copy, read by its block, is the TOC that was written.</summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void RestoreTOCAt_EachCopy_MatchesTheTocThatWasWritten(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 3);

        try
        {
            foreach (long block in TocBlocks(fixture))
            {
                var toc = new TapeTOC();
                using var agent = new TapeAgentBase(fixture.Drive, toc);

                TapeResult result = agent.RestoreTOCAt(block);

                Assert.True(result.Success, $"copy at block {block}: {result}");
                AssertSameToc(fixture.TOC, toc, $"copy at block {block}");
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A block that holds no TOC fails cleanly: no throw, the agent's TOC untouched. The caller's evidence
    ///  can be wrong — a cartridge swapped since the scan — and the verb must say so, not misread.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void RestoreTOCAt_BlockWithoutAToc_FailsCleanly(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 2);

        try
        {
            long setHeaderBlock = ScanMapAssert.SetHeaders(Scan(fixture, harvest: false))[0].StartBlock;

            var toc = new TapeTOC("untouched");
            using var agent = new TapeAgentBase(fixture.Drive, toc);

            TapeResult result = agent.RestoreTOCAt(setHeaderBlock);

            Assert.False(result.Success);
            Assert.NotEqual(0u, result.ErrorCode);
            Assert.Equal("untouched", toc.Description);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    [Fact]
    public void RestoreTOCAt_NegativeBlock_FailsWithoutMoving()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, withMediaHeader: true, withSetHeaders: true);
        using var agent = new TapeAgentBase(fixture.Drive, new TapeTOC());

        Assert.False(agent.RestoreTOCAt(-1).Success);
    }

    /// <summary>
    /// Afterwards the navigator believes nothing and the manager is idle — the position rested on
    ///  evidence the navigator cannot verify.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void RestoreTOCAt_LeavesNoBelievedPosition_AndAnIdleManager(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 2);

        try
        {
            long block = TocBlocks(fixture)[0];
            using var agent = new TapeAgentBase(fixture.Drive, new TapeTOC());

            Assert.True(agent.RestoreTOCAt(block).Success);

            Assert.Equal(TapeNavigator.UnknownSet, agent.Navigator.CurrentContentSet);
            Assert.True(agent.Manager.State == TapeState.MediaPrepared, $"state {agent.Manager.State}");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The load-bearing check: on the SAME agent, the ordinary <see cref="TapeAgentBase.RestoreTOC"/> still
    ///  locates and reads the TOC after a positional read — the new path left the old one intact.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void RestoreTOC_AfterRestoreTOCAt_StillWorks(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 2);

        try
        {
            IReadOnlyList<long> blocks = TocBlocks(fixture);

            var toc = new TapeTOC();
            using var agent = new TapeAgentBase(fixture.Drive, toc);

            Assert.True(agent.RestoreTOCAt(blocks[^1]).Success);   // the SECOND copy, deliberately

            TapeResult ordinary = agent.RestoreTOC();

            Assert.True(ordinary.Success, $"ordinary restore after a positional one: {ordinary}");
            AssertSameToc(fixture.TOC, toc, "ordinary restore after RestoreTOCAt");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The partitioned layout's override: the block is addressed in the INITIATOR partition, where the TOC
    ///  lives — first copy at block 0.
    /// </summary>
    [Fact]
    public void RestoreTOCAt_OnPartitionedMedia_ReadsFromTheInitiatorPartition()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 2);

        try
        {
            var toc = new TapeTOC();
            using var agent = new TapeAgentBase(fixture.Drive, toc);

            TapeResult result = agent.RestoreTOCAt(0);

            Assert.True(result.Success, $"{result}");
            AssertSameToc(fixture.TOC, toc, "initiator partition, block 0");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (B) The scanner's harvest ***

    /// <summary>
    /// Every copy is recovered — and the map is otherwise IDENTICAL to a scan without recovery. Pins SM-12:
    ///  a harvest that disturbed the walk would shift or lose fragments.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void Harvest_OnHealthyMedia_RecoversEveryCopy_AndTheMapIsUnchanged(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 3);

        try
        {
            MediaScanMap plain = Scan(fixture, harvest: false);
            MediaScanMap harvested = Scan(fixture, harvest: true);

            string both = $"PLAIN:\n{ScanMapAssert.Describe(plain)}\nHARVESTED:\n{ScanMapAssert.Describe(harvested)}";

            Assert.True(plain.Fragments.Count == harvested.Fragments.Count, both);

            for (int i = 0; i < plain.Fragments.Count; i++)
            {
                TapeMediaFragment p = plain.Fragments[i], h = harvested.Fragments[i];

                Assert.True(p.Kind == h.Kind && p.StartBlock == h.StartBlock
                            && p.ClosedBySeparator == h.ClosedBySeparator && p.BlockSpan == h.BlockSpan,
                    $"fragment #{i} differs\n{both}");
            }

            ScanMapAssert.Complete(harvested);

            foreach (TapeMediaFragment copy in ScanMapAssert.OfKind(harvested, FragmentKind.TOC))
                AssertSameToc(fixture.TOC, copy.HarvestedToc, $"copy at block {copy.StartBlock}");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The redundancy the format has always had and nothing exploited: the first copy damaged, the second
    ///  still recovered. The damaged copy stays a TOC fragment, with the reason in its diagnosis (SM-8).
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void FirstTocCopyDamaged_SecondIsStillRecovered(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 2);

        try
        {
            // Offset 16 lands inside the TOC's MediaId (signature 4 + nextUID 8 = 12, Guid 12..27): the
            //  stream still deserializes, and only the CRC can tell — the realistic shape of the fault.
            var sink = new ArmOnFirstHarvest(() =>
            {
                fixture.Backend.ContentReadFaults.SkipN = 0;
                fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 16);
            });

            var scanner = new TapeScanner(fixture.Drive) { Options = new ScanMediaOptions { HarvestTocCopies = true } };
            MediaScanMap map = scanner.Scan(sink)!;
            string describe = ScanMapAssert.Describe(map);

            Assert.True(fixture.Backend.ContentReadFaults.Occurrences == 1, $"the fault never fired\n{describe}");

            var copies = ScanMapAssert.OfKind(map, FragmentKind.TOC);
            Assert.True(copies.Count >= 2, describe);

            Assert.True(copies[0].HarvestedToc is null, describe);
            Assert.False(copies[0].Diagnosis.Success, describe);

            AssertSameToc(fixture.TOC, copies[1].HarvestedToc, "second copy");

            // A failed recovery is a finding, never the scan's failure.
            ScanMapAssert.Complete(map);
            Assert.True(scanner.LastResult.Success, $"{scanner.LastResult}");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The scenario this phase exists for: a TOC the navigator cannot locate on its own, recovered from the
    ///  scan's evidence.
    /// </summary>
    /// <remarks>
    /// Two junk files appended after <c>toc2</c> on the filemark layout. The navigator locates the TOC by
    ///  "EOD, back three filemarks" — which now lands on the junk — so the ordinary restore FAILS, and the
    ///  test asserts that first. Only the setmark-free, TOC-mark-free layout searches this way; the others
    ///  anchor on a setmark or the TOC mark and would find the TOC anyway.
    /// </remarks>
    [Fact]
    public void Harvest_WhereTheNavigatorCannotFindTheToc_StillRecoversIt()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 2);

        try
        {
            Assert.True(fixture.Drive.FastforwardToEnd(MediaPartition.Content));

            int blk = (int)fixture.Drive.BlockSize;
            var junk = new byte[blk];
            new Random(5).NextBytes(junk);

            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(blk, fixture.Drive.WriteDirect(junk, 0, blk));
                Assert.True(fixture.Drive.WriteFilemark(1));
            }

            // The navigator's own search now fails...
            using (var ordinary = new TapeAgentBase(fixture.Drive, new TapeTOC()))
                Assert.False(ordinary.RestoreTOC().Success, "the navigator should no longer find the TOC");

            // ...while the scan finds both copies and recovers them.
            MediaScanMap map = Scan(fixture, harvest: true);
            var copies = ScanMapAssert.OfKind(map, FragmentKind.TOC);

            Assert.True(copies.Count == 2, ScanMapAssert.Describe(map));

            foreach (TapeMediaFragment copy in copies)
                AssertSameToc(fixture.TOC, copy.HarvestedToc, $"copy at block {copy.StartBlock}");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>A harvesting scan writes nothing — content and virtual-block structure alike (SM-1).</summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void Harvest_NeverWrites(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 2);

        try
        {
            var before = ScanMapAssert.Snapshot(fixture);

            Scan(fixture, harvest: true);

            before.AssertUnchanged(fixture, "TOC recovery must write nothing");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Each recovered TOC carries the series its fragment's peek reported — the check the comparison phase
    ///  will use to refuse a leftover copy from another series.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void Harvest_RecoveredTocMatchesTheFragmentsIdentity(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        var trees = SeedSets(fixture, setCount: 2);

        try
        {
            MediaScanMap map = Scan(fixture, harvest: true);

            foreach (TapeMediaFragment copy in ScanMapAssert.OfKind(map, FragmentKind.TOC))
            {
                Assert.NotNull(copy.HarvestedToc);
                Assert.Equal(copy.Id, copy.HarvestedToc!.MediaId);
                Assert.Equal(map.MediaId, copy.HarvestedToc.MediaId);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion
}
