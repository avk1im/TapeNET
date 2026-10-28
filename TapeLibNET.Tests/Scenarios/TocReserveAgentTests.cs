using TapeLibNET.Agents;
using TapeLibNET.Media;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Tests.Services;
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Scenarios;


/// <summary>
/// Design-TocReserve §4: the backup sizes its TOC reserve from the TOC and raises it as files are handed off — so a TOC far
///  beyond a fixed reserve still gets both copies; skipped files cost nothing; a TOC partition arms none.
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

    #endregion

    #region *** The safety case ***

    /// <summary>
    /// ~800 long-named small files on 256 KiB volumes, LIVE reserve: volume 1's TOC grows far beyond one block (the 5d
    ///  parity reserve would hold one copy at most), yet the raised reserve leaves room for both — no copy fails anywhere,
    ///  every volume's TOC loads, and the series restores.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void LongNames_TocOutgrowsAFixedReserve_BothCopiesFit(DriveProfile profile)
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

        Assert.DoesNotContain(notify.TocAnomalies, i => i.Anomaly.Kind
            is TapeTocAnomalyKind.CopyWriteFailed or TapeTocAnomalyKind.NoCopyWritten);

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
            fixture.TOC.MakeLastSetCurrent();
            Assert.Equal(files, fixture.RestoreAllFilesFromCurrentSet(restoreDir).FilesSucceeded);
        }
        finally
        {
            try { Directory.Delete(restoreDir, recursive: true); } catch { /* best effort */ }
        }
    }

    #endregion

    #region *** Raise, and no raise ***

    // 1.5 MiB: the floor is capped at a quarter (384 KiB), so ~900 long-named entries outgrow it within one set.
    private const long SmallMedium = 3L * 512 * 1024;

    /// <summary>
    /// A full set raises the reserve past the floor as its entries are handed off; an incremental of the SAME files hands
    ///  off none — its reserve is armed from the TOC as it stands and never raised.
    /// </summary>
    [Fact]
    public void FullSetRaises_UnchangedIncrementalDoesNot()
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
        Assert.Equal(0, fixture.TOC[2].Count);                           // every file up to date: none handed off
        Assert.True(fixture.Drive.EarlyWarning <= afterFull,
            $"an unchanged incremental must not raise ({fixture.Drive.EarlyWarning} > {afterFull})");
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
            long expected = withInitiator ? 0L : TapeNavigator.TocReserveFloor;
            Assert.Equal(expected, svc.TocReserve);
            Assert.Equal(Math.Max(0L, svc.EstimatedContentRemaining - expected), svc.WritableRemaining);
            Assert.Equal(!withInitiator, svc.DescribeMedia().Any(p => p.Label == "TOC Reserve"));
        }
    }
}
