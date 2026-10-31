using TapeLibNET.Agents;
using TapeLibNET.Media;
using TapeLibNET.Services;
using TapeLibNET.Toc;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Services;


/// <summary>
/// Design-TocReserve §9.6, service level: why each volume ended reaches the result and the user; a TOC partition that
///  ends a volume is warned about; a series whose TOC fills the partition of a fresh volume ends as failed, with advice —
///  never as "completed successfully", never with a loop.
/// </summary>
public class ServiceTocBudgetTests : ServiceTestBase
{
    private const long KiB = 1024;
    private const long VolumeContent = 4 * KiB * KiB;
    private const long SmallTocPartition = 96 * KiB;        // 80 KiB budget: two copies of up to 2 blocks
    private const long LargeTocPartition = 1 * KiB * KiB;
    private const int Files = 600;
    private static readonly TimeSpan NoHang = TimeSpan.FromMinutes(2);

    #region *** (A) The policy, pure ***

    private static VolumeEnd End(int volume, ContentStopReason reason) => new(volume, reason);

    [Fact]
    public void Advise_OrdinaryEnds_SayNothing()
        => Assert.Equal(VolumeSpaceAdvice.None, TapeServiceBase.AdviseOnVolumeEnds(
            [End(1, ContentStopReason.EndOfMedia), End(2, ContentStopReason.TocReserve)], seriesStopped: false).Advice);

    [Fact]
    public void Advise_TocPartitionEndedAVolume_Warns_NamingIt()
    {
        var (advice, volumes) = TapeServiceBase.AdviseOnVolumeEnds(
            [End(1, ContentStopReason.TocPartitionFull), End(2, ContentStopReason.EndOfMedia)], seriesStopped: false);
        Assert.Equal(VolumeSpaceAdvice.VolumesEndedByTocPartition, advice);
        Assert.Equal([1], volumes);
    }

    [Theory]
    [InlineData(ContentStopReason.TocPartitionFull, VolumeSpaceAdvice.TocPartitionTooSmallForSeries)]
    [InlineData(ContentStopReason.TocReserve, VolumeSpaceAdvice.VolumeTooSmallForToc)]
    public void Advise_SeriesStopped_ByLayout_NamesTheFreshVolume(ContentStopReason reason, VolumeSpaceAdvice expected)
    {
        var (advice, volumes) = TapeServiceBase.AdviseOnVolumeEnds(
            [End(1, reason), End(2, reason)], seriesStopped: true);
        Assert.Equal(expected, advice);
        Assert.Equal([2], volumes);
    }

    [Theory]
    [InlineData(VolumeSpaceAdvice.VolumesEndedByTocPartition, ServiceReportLevel.Warning)]
    [InlineData(VolumeSpaceAdvice.TocPartitionTooSmallForSeries, ServiceReportLevel.Failed)]
    [InlineData(VolumeSpaceAdvice.VolumeTooSmallForToc, ServiceReportLevel.Failed)]
    public void AdviseText_NamesTheVolume_AndAnAction_PointingToHelp(VolumeSpaceAdvice advice, ServiceReportLevel level)
    {
        var (l, headline, action) = TapeServiceBase.AdviseVolumeSpaceText(advice, [2]);
        Assert.Equal(level, l);
        Assert.Contains("volume #2", headline);
        Assert.Contains(TapeServiceBase.TocSpaceHelpTopic, action);
    }

    [Fact]
    public void AdviseText_None_SaysNothing()
    {
        var (_, headline, action) = TapeServiceBase.AdviseVolumeSpaceText(VolumeSpaceAdvice.None, []);
        Assert.Empty(headline);
        Assert.Empty(action);
    }

    #endregion

    #region *** (B) Backup on a small TOC partition ***

    /// <summary>Tiny files with long, unshared names: the TOC fills its partition long before the content fills its own.</summary>
    private static TempFileTree LongNamedFiles()
    {
        var tree = new TempFileTree();
        for (int i = 0; i < Files; i++)
        {
            var bytes = new byte[45];
            new Random(i).NextBytes(bytes);
            tree.AddFile($"{i:D4}_{Convert.ToHexString(bytes)}.bin", 1 + i % 64);
        }
        return tree;
    }

    /// <summary>Opens volume 1 of <paramref name="volumes"/> and formats it with a TOC partition of <paramref name="tocPartition"/>.</summary>
    private async Task<(TestTapeService, MultiVolumeTapeServiceHost)> OpenAndFormatAsync(
        IReadOnlyList<TempVirtualMedia> volumes, long tocPartition)
    {
        var (svc, host) = CreateMultiVolumeService(volumes);
        var first = volumes[0];
        Assert.True(await svc.OpenVirtualDriveAsync(CapsFor(first), VmdFor(first), FileMode.Create), svc.LastError);
        Assert.True(await svc.LoadMediaAsync(), svc.LastError);
        Assert.True(await svc.FormatMediaAsync(tocPartition, MediaName), svc.LastError);
        return (svc, host);
    }

    private static RestoreRequest RestoreAll(int setIndex, string targetDir) => new(
        Mode: RestoreMode.Restore,
        CheckedFilesBySet: new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
        Incremental: false,
        TargetDirectory: targetDir,
        RecurseSubdirectories: true,
        HandleExisting: TapeHowToHandleExisting.Overwrite,
        SkipAllErrors: false,
        EjectWhenDone: false);

    /// <summary>
    /// Volume 1's TOC partition ends it early; volume 2 has a larger one and takes the rest. The backup succeeds, the end
    ///  of volume 1 is on record with its reason, the user is warned and advised — and every file restores.
    /// </summary>
    [Fact]
    public async Task TocPartitionEndsVolume_LargerNextPartition_Completes_WarnsAndAdvises()
    {
        using var vol1 = new TempVirtualMedia(withInitiator: true, VolumeContent, SmallTocPartition);
        using var vol2 = new TempVirtualMedia(withInitiator: true, VolumeContent, LargeTocPartition);
        IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
        using var tree = LongNamedFiles();

        var (svc, host) = await OpenAndFormatAsync(volumes, SmallTocPartition);
        using (svc)
        {
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "Budget")).WaitAsync(NoHang);

            Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
            Assert.Equal(Files, result.FilesSucceeded);
            Assert.Equal([new VolumeEnd(1, ContentStopReason.TocPartitionFull)], result.VolumeEnds);
            Assert.True(host.ContainsMessage("ends early: its TOC partition"), host.DumpReports());
            Assert.True(host.ContainsMessage("TOC partition was full on volume #1"), host.DumpReports());
            Assert.DoesNotContain(result.TocAnomalies, a => a.Kind
                is TapeTocAnomalyKind.CopyWriteFailed or TapeTocAnomalyKind.NoCopyWritten);
        }

        string restoreDir = Path.Combine(vol1.Root, "restore_budget");
        Directory.CreateDirectory(restoreDir);
        var (rsvc, rhost) = await ReopenMultiVolumeAsync(volumes);
        using (rsvc)
        {
            var restored = await rsvc.ExecuteRestoreAsync(RestoreAll(rsvc.TOC!.LastSetOnVolume, restoreDir))
                .WaitAsync(NoHang);
            Assert.True(restored.Success, $"{restored.Message}\n{rhost.DumpReports()}");
            Assert.Equal(Files, restored.FilesSucceeded);
        }
        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(restoreDir, tree.RootPath));
    }

    /// <summary>
    /// Both volumes have the same small TOC partition: volume 2 cannot take a single file next to the series' TOC. The
    ///  backup ends as FAILED — not "completed successfully" — names the files left out, and advises a larger partition.
    ///  No third volume is requested, no prompt raised.
    /// </summary>
    [Fact]
    public async Task TocPartitionEndsVolume_EqualNextPartition_SeriesStops_FailsWithAdvice()
    {
        using var vol1 = new TempVirtualMedia(withInitiator: true, VolumeContent, SmallTocPartition);
        using var vol2 = new TempVirtualMedia(withInitiator: true, VolumeContent, SmallTocPartition);
        IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
        using var tree = LongNamedFiles();

        var (svc, host) = await OpenAndFormatAsync(volumes, SmallTocPartition);
        using (svc)
        {
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "Stopped")).WaitAsync(NoHang);

            Assert.False(result.Success);
            Assert.True(result.HasFailed);
            Assert.False(result.WasAborted);
            Assert.False(result.IsFullSuccess);
            Assert.InRange(result.FilesSucceeded, 1, Files - 1);
            Assert.Equal(
                [new VolumeEnd(1, ContentStopReason.TocPartitionFull), new VolumeEnd(2, ContentStopReason.TocPartitionFull)],
                result.VolumeEnds);
            Assert.Contains("TOC partition", result.Message ?? "");
            Assert.Equal(1, host.VolumesInserted);
            Assert.True(host.ContainsMessage("not backed up"), host.DumpReports());
            Assert.True(host.ContainsMessage("larger TOC partition"), host.DumpReports());
            Assert.False(host.ContainsMessage("completed successfully"), host.DumpReports());
        }
    }

    #endregion
}
