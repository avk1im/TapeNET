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

    /// <summary>Small files with long, unshared names: a TOC that is large compared with the content.</summary>
    private static TempFileTree LongNamedFiles(int count)
    {
        var tree = new TempFileTree();
        for (int i = 0; i < count; i++)
        {
            var bytes = new byte[45];
            new Random(i).NextBytes(bytes);
            tree.AddFile($"{i:D4}_{Convert.ToHexString(bytes)}.bin", 1 + i % 64);    // ~95-char names, no long prefix
        }
        return tree;
    }

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
    /// ~800 long-named small files on 256 KiB volumes, live reserve. Safe: volume 1's TOC grows far beyond one block (the
    ///  5d parity reserve would hold one copy at most), yet both copies fit everywhere and the series restores. Live:
    ///  every volume takes files — the hand-off design of revision 1 left volume 1 empty here.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void LongNames_SafeAndLive_BothCopiesFit_EveryVolumeTakesFiles(DriveProfile profile)
    {
        const int files = 800;
        using var tree = LongNamedFiles(files);
        using var fixture = new MultiVolumeVirtualTapeFixture(profile, headerMode: VolumeHeaderMode.All)
        {
            UseLiveTocReserve = true,
        };
        var notify = new TestNotifiable();
        fixture.BackupFiles(tree.Files, "Long names", notifiable: notify);
        Assert.True(fixture.TotalVolumes >= 2, "the series must span");
        AssertNoCopyAnomaly(notify);

        fixture.SwapToVolume(1);
        fixture.LoadTOC();
        Assert.Equal(1, fixture.TOC.Volume);
        long tocBytes = SavedSize(fixture.TOC);
        Assert.True(tocBytes > 2 * 16 * 1024, $"volume 1's TOC ({tocBytes} B) should outgrow a one-block reserve");

        string restoreDir = RestoreDir();
        try
        {
            fixture.SwapToVolume(fixture.TotalVolumes);
            fixture.LoadTOC();

            // Live: every part of the set — one per volume — holds files, and together they hold all
            int sum = 0;
            for (int s = 1; s <= fixture.TOC.Count; s++)
            {
                Assert.True(fixture.TOC[s].Count > 0, $"volume #{fixture.TOC[s].Volume} took no file");
                sum += fixture.TOC[s].Count;
            }
            Assert.Equal(files, sum);

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
        using var tree = LongNamedFiles(400);
        using var fixture = new MultiVolumeVirtualTapeFixture(profile, headerMode: VolumeHeaderMode.All)
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
            fixture.LoadTOC();
            Assert.Equal(v, fixture.TOC.Volume);
        }
    }

    #endregion

    #region *** No loop ***

    /// <summary>
    /// A reserve larger than any volume: not a single file fits on the fresh continuation volume either. The backup ends
    ///  with the "not a single file" error (TR-7) instead of requesting volumes forever.
    /// </summary>
    [Fact]
    public void ReserveBeyondEveryVolume_EndsWithAnError_NoLoop()
    {
        using var tree = LongNamedFiles(20);
        using var fixture = new MultiVolumeVirtualTapeFixture(DriveProfile.Setmarks)
        {
            TOCCapacityOverride = 1024L * 1024 * 1024,     // beyond volume 1, and beyond every later (32 MiB-padded) one
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
