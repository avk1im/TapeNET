using TapeLibNET.Agents;
using TapeLibNET.Services;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia, MultiVolumeTapeServiceHost

namespace TapeLibNET.Tests.Services;

/// <summary>
/// The series TOC after a multi-volume restore (<see cref="TOCSource.Series"/>): a swap leaves the series TOC held while
///  the drive holds another cartridge. The result says so, the media sheet marks it, and every write that would save the
///  held TOC onto the loaded cartridge refuses — until the loaded volume's own TOC is read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scenario, as a user meets it:</b> the series is opened on its last volume (2), whose TOC is the series'. A
///  restore of the spanning set asks for volume 1 first (oldest part first) — the user inserts it — and then for volume 2
///  again for the rest: the user declines (<see cref="VolumeAction.End"/>, e.g. the cartridge is not at hand). The restore
///  ends with volume 1 in the drive and the series TOC held.
/// </para>
/// <para>
/// Sizes as in <c>ServiceMultiVolumeErrorTests</c>: 16 × 350 KiB over exactly two volumes on both profiles.
/// </para>
/// </remarks>
public class ServiceSeriesTocTests : ServiceTestBase
{
    #region *** Data and helpers ***

    private const int FileCount = 16;
    private const long FileSize = 350L * 1024;
    private const long Capacity = 3L * 1024 * 1024;

    /// <summary>Generous upper bound for one operation — a loop regression must fail, not hang.</summary>
    private static readonly TimeSpan NoHang = TimeSpan.FromMinutes(2);

    private static (TempVirtualMedia vol1, TempVirtualMedia vol2) MakeVolumes(bool withInitiator)
        => (new TempVirtualMedia(withInitiator, Capacity), new TempVirtualMedia(withInitiator, Capacity));

    private static TempFileTree MakeTree()
    {
        var tree = new TempFileTree();
        for (int i = 0; i < FileCount; i++)
            tree.AddFile($"series_{i:D2}.bin", FileSize);
        return tree;
    }

    private async Task BackupAcrossTwoVolumesAsync(IReadOnlyList<TempVirtualMedia> volumes, TempFileTree tree)
    {
        var (svc, host) = await OpenAndFormatMultiVolumeAsync(volumes);
        using (svc)
        {
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "Series")).WaitAsync(NoHang);
            Assert.True(result.Success, $"seeding backup failed: {svc.LastError}\n{host.DumpReports()}");
            Assert.True(host.VolumesInserted >= 1, "the seeding backup must span two volumes");
        }
    }

    private static RestoreRequest RestoreSet(int setIndex, string targetDir) => new(
        Mode:                  RestoreMode.Restore,
        CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
        Incremental:           false,
        TargetDirectory:       targetDir,
        RecurseSubdirectories: true,
        HandleExisting:        TapeHowToHandleExisting.Overwrite,
        SkipAllErrors:         false,
        EjectWhenDone:         false);

    private static string RestoreDir(TempVirtualMedia media, string name)
    {
        string dir = Path.Combine(media.Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>How many files the logical set ending at <paramref name="lastPart"/> holds on <paramref name="volume"/>.</summary>
    private static int FilesOnVolume(TapeTOC toc, int lastPart, int volume)
    {
        int count = 0;
        for (int s = lastPart; s >= 1; s--)
        {
            if (toc[s].Volume == volume)
                count += toc[s].Count;
            if (!toc[s].ContinuedFromPrevVolume)
                break;
        }
        return count;
    }

    /// <summary>
    /// Seeds two volumes, reopens the series on volume 2 and restores its spanning set: volume 1 is inserted when asked,
    ///  the request for volume 2 that follows is DECLINED. Returns the service with volume 1 in the drive.
    /// </summary>
    private async Task<(TestTapeService svc, MultiVolumeTapeServiceHost host, RestoreResult result, int setIdx)>
        RestoreEndingOnVolume1Async(IReadOnlyList<TempVirtualMedia> volumes, TempFileTree tree, string restoreDir)
    {
        await BackupAcrossTwoVolumesAsync(volumes, tree);
        var (svc, host) = await ReopenMultiVolumeAsync(volumes);
        var toc = svc.TOC!;
        Assert.Equal(TOCSource.Media, svc.TOCIsFrom);
        Assert.Equal(2, toc.Volume);
        int setIdx = toc.LastSetOnVolume;
        int onVol1 = FilesOnVolume(toc, setIdx, 1);
        Assert.True(onVol1 > 0 && FilesOnVolume(toc, setIdx, 2) > 0, "the set must span both volumes");

        host.VolumeContinueAnswers.Enqueue(VolumeAction.Insert);   // "volume #1, please" — inserted
        host.VolumeContinueAnswers.Enqueue(VolumeAction.End);      // "volume #2 again, please" — declined

        var result = await svc.ExecuteRestoreAsync(RestoreSet(setIdx, restoreDir)).WaitAsync(NoHang);
        Assert.False(result.WasAborted);
        Assert.Equal(onVol1, result.FilesSucceeded);                // exactly volume 1's part
        Assert.Equal(1, host.VolumesInserted);
        Assert.Empty(host.VolumeContinueAnswers);                   // both prompts were asked
        return (svc, host, result, setIdx);
    }

    #endregion

    #region *** The state after the restore ***

    /// <summary>
    /// The user declined the volume the restore came back for: volume 1 stays in the drive. The result names it and says
    ///  the held TOC does not describe it; the service marks the TOC as the series'; the log and the media sheet say so.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_NextVolumeDeclined_EndsOnEarlierVolume_SeriesTocReported(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            using var tree = MakeTree();
            var (svc, host, result, _) = await RestoreEndingOnVolume1Async([vol1, vol2], tree, RestoreDir(vol1, "r"));
            using (svc)
            {
                Assert.True(result.MediaSwapped);
                Assert.Equal(1, result.VolumeInDrive);
                Assert.False(result.TocDescribesDrive);
                Assert.Equal(TOCSource.Series, svc.TOCIsFrom);
                Assert.Equal(1, svc.LoadedMediaHeader?.Volume);
                Assert.True(host.ContainsMessage("Volume #1 remains in the drive"), host.DumpReports());

                var placement = Assert.Single(svc.DescribeMedia(), p => p.Label == "TOC Placement");
                Assert.Contains("series TOC", placement.Value);
                Assert.Equal(ServiceReportLevel.Warning, placement.Level);
            }
        }
    }

    /// <summary>
    /// The same restore, both volumes inserted when asked: it swaps away and comes BACK to the volume whose TOC it holds —
    ///  swapped, yet the TOC describes the drive again. Nothing is flagged.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_AcrossVolumes_BackOnStartVolume_TocStaysMedia(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            using var tree = MakeTree();
            await BackupAcrossTwoVolumesAsync([vol1, vol2], tree);
            var (svc, host) = await ReopenMultiVolumeAsync([vol1, vol2]);
            using (svc)
            {
                string target = RestoreDir(vol1, "r_full");
                var result = await svc.ExecuteRestoreAsync(RestoreSet(svc.TOC!.LastSetOnVolume, target)).WaitAsync(NoHang);
                Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
                Assert.Equal(FileCount, result.FilesSucceeded);
                Assert.True(result.MediaSwapped);
                Assert.Equal(2, result.VolumeInDrive);
                Assert.True(result.TocDescribesDrive);
                Assert.Equal(TOCSource.Media, svc.TOCIsFrom);
                Assert.False(host.ContainsMessage("remains in the drive"), host.DumpReports());
                FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(target, tree.RootPath));
            }
        }
    }

    /// <summary>No swap — a set on the loaded volume only: nothing changes, the TOC stays the media's own.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_SingleVolume_TocStaysMedia(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        using var tree = new TempFileTree();
        tree.AddFiles("single", count: 4, minSize: 512, maxSize: 4 * 1024);
        var (seed, seedHost) = await OpenAndFormatAsync(media);
        using (seed)
            Assert.True((await seed.ExecuteBackupAsync(MakeBackupRequest(seed, tree.RootPath, "Single"))).Success,
                seedHost.DumpReports());

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var result = await svc.ExecuteRestoreAsync(RestoreSet(svc.TOC!.LastSetOnVolume, RestoreDir(media, "r")))
                .WaitAsync(NoHang);
            Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
            Assert.False(result.MediaSwapped);
            Assert.True(result.TocDescribesDrive);
            Assert.Equal(TOCSource.Media, svc.TOCIsFrom);
            Assert.False(host.ContainsMessage("remains in the drive"), host.DumpReports());
        }
    }

    #endregion

    #region *** Writes refuse on the series TOC ***

    /// <summary>
    /// Every write that would save the held TOC onto the loaded cartridge refuses — and leaves the cartridge as it was:
    ///  volume 1's own TOC still reads afterwards with its own set list and volume number.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeriesToc_Writes_Refuse_CartridgeUntouched(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            using var tree = MakeTree();
            var (svc, host, _, setIdx) = await RestoreEndingOnVolume1Async([vol1, vol2], tree, RestoreDir(vol1, "r"));
            using (svc)
            {
                int setsBefore = svc.TOC!.Count;

                Assert.False(await svc.RenameMediaAsync("Renamed"));
                Assert.Contains("series", svc.LastError ?? "");

                Assert.False(await svc.RenameBackupSetAsync(setIdx, "Renamed set"));
                Assert.Contains("series", svc.LastError ?? "");

                var deleted = await svc.DeleteBackupSetsExAsync(svc.TOC.LastSetOnVolume);
                Assert.False(deleted.Success);
                Assert.Contains("series", deleted.Message ?? "");

                using var more = new TempFileTree();
                more.AddFiles("more", count: 2, minSize: 512, maxSize: 2 * 1024);
                var append = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, more.RootPath, "Append", append: true))
                    .WaitAsync(NoHang);
                Assert.False(append.Success);
                Assert.Contains("series", append.Message ?? "");

                Assert.Equal(setsBefore, svc.TOC.Count);
                Assert.Equal(TOCSource.Series, svc.TOCIsFrom);
            }

            // Volume 1 was never written: its own TOC reads as before
            var (check, _) = await ReopenMultiVolumeAsync((IReadOnlyList<TempVirtualMedia>)[vol1]);
            using (check)
            {
                Assert.Equal(1, check.TOC!.Volume);
                Assert.Equal(TOCSource.Media, check.TOCIsFrom);
                Assert.DoesNotContain(check.TOC, s => s.Volume > 1);
                Assert.DoesNotContain(check.TOC, s => s.Description == "Renamed set");
                Assert.NotEqual("Renamed", check.TOC.Description);
            }
        }
    }

    /// <summary>Reading the loaded volume's TOC ends the state: the TOC is the media's again, and writes work.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeriesToc_Reread_ClearsState_RenameWorks(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            using var tree = MakeTree();
            var (svc, host, _, _) = await RestoreEndingOnVolume1Async([vol1, vol2], tree, RestoreDir(vol1, "r"));
            using (svc)
            {
                Assert.Equal(TOCSource.Series, svc.TOCIsFrom);

                Assert.True(await svc.RestoreTOCAsync(), svc.LastError);
                Assert.Equal(TOCSource.Media, svc.TOCIsFrom);
                Assert.Equal(1, svc.TOC!.Volume);
                Assert.DoesNotContain(svc.TOC, s => s.Volume > 1);

                Assert.True(await svc.RenameMediaAsync("Volume one"), svc.LastError);
                Assert.Equal("Volume one", svc.TOC.Description);
            }
        }
    }

    #endregion
}
