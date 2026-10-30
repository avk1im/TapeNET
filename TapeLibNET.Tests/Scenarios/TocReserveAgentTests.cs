using TapeLibNET.Agents;
using TapeLibNET.Media;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Tests.Services;
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Scenarios;

/// <summary>
/// Design-TocReserve §4 (revision 2): each write plans against the TOC of the files it starts — so a TOC far beyond a
///  fixed reserve still gets both copies (safe), every volume takes files (live), a volume that cannot take one ends the
///  backup instead of looping, skipped files cost nothing, and a TOC partition arms none.
/// </summary>
public class TocReserveAgentTests
{
    public static TheoryData<DriveProfile> InSetProfiles =>
        [DriveProfile.Setmarks, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly];

    #region *** Helpers ***

    /// <summary>Files with long, unshared names (~95 chars) — a TOC that is large compared with the content.</summary>
    private static TempFileTree LongNamedFiles(int count, int minSize = 1, int maxSize = 64)
    {
        var tree = new TempFileTree();
        for (int i = 0; i < count; i++)
        {
            var bytes = new byte[45];
            new Random(i).NextBytes(bytes);
            tree.AddFile($"{i:D4}_{Convert.ToHexString(bytes)}.bin", minSize + i % (maxSize - minSize + 1));
        }
        return tree;
    }

    /// <summary>The loaded volume holds both TOC copies, each closed by its filemark — wherever the layout keeps them.</summary>
    private static void AssertBothTocCopies(MultiVolumeVirtualTapeFixture fixture, int volume)
    {
        var media = fixture.Backend.InitiatorMedia ?? fixture.Backend.ContentMedia!;
        var layout = media.Layout;
        Assert.True(layout.Count >= 4 && !layout[^4].IsMark && layout[^3].IsMark && !layout[^2].IsMark && layout[^1].IsMark,
            $"volume #{volume}: both TOC copies, each closed by its filemark\n{media.DescribeLayout()}");
    }

    private static int FilesOnVolume(TapeTOC series, int volume)
        => series.Where(s => s.Volume == volume).Sum(s => s.Count);

    private static long SavedSize(TapeTOC toc)
    {
        using var ms = new MemoryStream();
        new TapeTOC(toc).SaveTo(ms);
        return ms.Length;
    }

    private static string RestoreDir() => Path.Combine(Path.GetTempPath(), $"TapeNET_TocReserve_{Guid.NewGuid():N}");

    private static void AssertNoCopyAnomaly(TestNotifiable notify)
        => Assert.DoesNotContain(notify.TocAnomalies, i => i.Anomaly.Kind
            is TapeTocAnomalyKind.CopyWriteFailed or TapeTocAnomalyKind.NoCopyWritten);

    #endregion

    #region *** Safe and live ***

    /// <summary>
    /// 800 long-named files of 1–2 KiB on equal 1 MiB volumes, live reserve. Safe: each volume's TOC outgrows a one-block
    ///  copy (the most the 5d parity reserve held), yet both copies fit on every volume and the series restores. Live:
    ///  every volume takes files — though the series TOC it must hold grows from volume to volume.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void LongNames_SafeAndLive_BothCopiesFit_EveryVolumeTakesFiles(DriveProfile profile)
    {
        const int files = 800;
        const long volume = 1L * 1024 * 1024;
        const long oneTocBlock = 16 * 1024;
        using var tree = LongNamedFiles(files, minSize: 1024, maxSize: 2048);
        using var fixture = new MultiVolumeVirtualTapeFixture(profile, contentCapacity: volume,
            headerMode: VolumeHeaderMode.All)
        { UseLiveTocReserve = true };
        var notify = new TestNotifiable();
        fixture.BackupFiles(tree.Files, "Long names", notifiable: notify);
        Assert.True(fixture.TotalVolumes >= 2, "the series must span");
        AssertNoCopyAnomaly(notify);

        for (int v = 1; v <= fixture.TotalVolumes; v++)
        {
            fixture.SwapToVolume(v);
            AssertBothTocCopies(fixture, v);
            fixture.LoadTOC();
            Assert.Equal(v, fixture.TOC.Volume);
            long tocBytes = SavedSize(fixture.TOC);
            Assert.True(tocBytes > oneTocBlock, $"volume #{v}'s TOC ({tocBytes} B) should outgrow a one-block reserve");
        }

        // The last volume's TOC describes the series: every volume holds files, and together all of them
        for (int v = 1; v <= fixture.TotalVolumes; v++)
            Assert.True(FilesOnVolume(fixture.TOC, v) > 0, $"volume #{v} took no file");
        Assert.Equal(files, Enumerable.Range(1, fixture.TotalVolumes).Sum(v => FilesOnVolume(fixture.TOC, v)));

        string restoreDir = RestoreDir();
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            Assert.Equal(files, fixture.RestoreAllFilesFromCurrentSet(restoreDir).FilesSucceeded);
        }
        finally
        {
            try { Directory.Delete(restoreDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The same series, encrypted: the sealed TOC's entries are planned too — both copies everywhere.</summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void EncryptedSeries_BothCopiesFit(DriveProfile profile)
    {
        using var tree = LongNamedFiles(800, minSize: 1024, maxSize: 2048);
        using var fixture = new MultiVolumeVirtualTapeFixture(profile,
            contentCapacity: 1L * 1024 * 1024, headerMode: VolumeHeaderMode.All)
        {
            UseLiveTocReserve = true,
        };
        var notify = new TestNotifiable();
        fixture.BackupFiles(tree.Files, "Encrypted", notifiable: notify, password: "reserve");
        Assert.True(fixture.TotalVolumes >= 2, "the series must span");
        AssertNoCopyAnomaly(notify);

        for (int v = 1; v <= fixture.TotalVolumes; v++)
        {
            fixture.SwapToVolume(v);
            AssertBothTocCopies(fixture, v);
            fixture.LoadTOC();
            Assert.Equal(v, fixture.TOC.Volume);
        }
    }

    #endregion

    #region *** Volumes of different sizes ***

    public static TheoryData<DriveProfile, bool> ProfilesAndOrder
    {
        get
        {
            TheoryData<DriveProfile, bool> data = [];
            foreach (var p in new[] { DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly })
            {
                data.Add(p, true);
                data.Add(p, false);
            }
            return data;
        }
    }

    /// <summary>
    /// A series on two cartridge sizes, 2 MiB and 1 MiB, alternating — larger first, or smaller first. Every volume is
    ///  written to ITS capacity: the drive reports it after the swap (nothing stale from the previous cartridge), a full
    ///  large volume takes more files than the full small one after it (or fewer, smaller first), both TOC copies stand
    ///  on every volume, and the series restores byte for byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProfilesAndOrder))]
    public void MixedVolumeSizes_EachVolumeUsesItsOwnCapacity_SeriesRestores(DriveProfile profile, bool largerFirst)
    {
        const long large = 2L * 1024 * 1024, small = 1L * 1024 * 1024;
        long CapacityOf(int v) => (v % 2 == 1) == largerFirst ? large : small;

        using var tree = new TempFileTree();
        tree.AddFiles("mixed", count: 120, minSize: 16 * 1024, maxSize: 48 * 1024);     // ~3.8 MB: three volumes or more
        using var fixture = new MultiVolumeVirtualTapeFixture(profile, contentCapacity: CapacityOf(1),
            headerMode: VolumeHeaderMode.All, volumeCapacity: CapacityOf)
        { UseLiveTocReserve = true };
        var notify = new TestNotifiable();
        fixture.BackupFiles(tree.Files, "Mixed sizes", notifiable: notify);
        Assert.True(fixture.TotalVolumes >= 3, $"the series must span three volumes ({fixture.TotalVolumes})");
        AssertNoCopyAnomaly(notify);

        for (int v = 1; v <= fixture.TotalVolumes; v++)
        {
            fixture.SwapToVolume(v);
            Assert.Equal(CapacityOf(v), fixture.Drive.ContentCapacity);      // nothing stale from the previous cartridge
            AssertBothTocCopies(fixture, v);
            fixture.LoadTOC();
            Assert.Equal(v, fixture.TOC.Volume);
        }

        // Volumes 1 and 2 are both full: the larger one holds clearly more
        int on1 = FilesOnVolume(fixture.TOC, 1), on2 = FilesOnVolume(fixture.TOC, 2);
        Assert.True(largerFirst ? on1 > on2 : on1 < on2,
            $"volume #1 ({CapacityOf(1)} B) holds {on1} files, volume #2 ({CapacityOf(2)} B) holds {on2}");

        string restoreDir = RestoreDir();
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            Assert.Equal(tree.Files.Count, fixture.RestoreAllFilesFromCurrentSet(restoreDir).FilesSucceeded);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                Path.Combine(restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath)));
        }
        finally
        {
            try { Directory.Delete(restoreDir, recursive: true); } catch { /* best effort */ }
        }
    }

    #endregion

    #region *** No loop ***

    /// <summary>
    /// A reserve larger than any volume: not a single file fits on the fresh continuation volume either. The backup ends
    ///  with the "not a single file" error (TR-7) instead of requesting volumes forever.
    ///  Volume 1 takes nothing, the continuation takes nothing, and the error ends the backup at 2 volumes.
    /// </summary>
    [Fact]
    public void ReserveBeyondEveryVolume_EndsWithAnError_NoLoop()
    {
        using var tree = LongNamedFiles(20);
        using var fixture = new MultiVolumeVirtualTapeFixture(DriveProfile.Setmarks)
        {
            TOCCapacityOverride = 1024L * 1024 * 1024,     // beyond volume 1, and beyond every (equal, 256 KiB) volume
        };

        var ex = Assert.ThrowsAny<Exception>(() => fixture.BackupFiles(tree.Files, "Too big"));
        Assert.Contains("not a single file", ex.Message);
        Assert.True(fixture.TotalVolumes <= 2, $"{fixture.TotalVolumes} volumes requested");
    }

    #endregion

    #region *** Grows with the files, and only with them ***

    // 1.5 MiB: the floor is capped at a 32nd (48 KiB), so ~900 long-named entries outgrow it within one set.
    private const long SmallMedium = 3L * 512 * 1024;

    /// <summary>
    /// A full set grows the reserve past the floor as its writes start files; an incremental of the SAME files starts
    ///  none — it keeps exactly the reserve armed at its set start, from the TOC as it stands.
    /// </summary>
    [Fact]
    public void FullSetRaises_UnchangedIncrementalKeepsItsArmedReserve()
    {
        using var tree = LongNamedFiles(900);
        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks, contentCapacity: SmallMedium,
            withMediaHeader: true, withSetHeaders: true);
        long floor = SmallMedium / 32;

        fixture.BackupFiles(tree.Files, description: "Full");
        long afterFull = fixture.Drive.EarlyWarning;
        Assert.True(afterFull > floor, $"the full set should raise the reserve past the floor ({afterFull} ≤ {floor})");

        fixture.LoadTOC();
        fixture.BackupFiles(tree.Files, description: "Incremental", incremental: true);
        Assert.Equal(tree.Files.Count, fixture.TOC[1].Count);
        Assert.Empty(fixture.TOC[2]);                           // every file up to date: none started

        using var probe = new TapeAgentBase(fixture.Drive, fixture.TOC);
        Assert.Equal(probe.Navigator.TocReserveFor(fixture.TOC.EstimateSerializedSize()), fixture.Drive.EarlyWarning);
    }

    [Fact]
    public void Partitions_NoReserveArmed()
    {
        using var tree = LongNamedFiles(50);
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions, withMediaHeader: true, withSetHeaders: true);
        fixture.BackupFiles(tree.Files, description: "Partitions");
        Assert.Equal(0L, fixture.Drive.EarlyWarning);
    }

    #endregion
}

/// <summary>Design-TocReserve §5: the service reports the reserve the next backup starts with, and deducts it.</summary>
public class ServiceTocReserveTests : ServiceTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_ReportsTheReserve(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        var (svc, _) = await OpenAndFormatAsync(media);
        using (svc)
        {
            // The floor rule (TR-6) for an empty TOC: 4 MiB, capped at 1/32 of the medium, never below two copies + marks
            long emptyToc = 2 * 16 * 1024 + TapeNavigator.TocMarkAllowance;
            long expected = withInitiator
                ? 0L
                : Math.Max(Math.Min(TapeNavigator.TocReserveFloor, svc.Capacity / 32), emptyToc);
            Assert.Equal(expected, svc.TocReserve);
            Assert.Equal(Math.Max(0L, svc.EstimatedContentRemaining - expected), svc.WritableRemaining);
            Assert.Equal(!withInitiator, svc.DescribeMedia().Any(p => p.Label == "TOC Reserve"));
        }
    }
}
