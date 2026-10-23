using Windows.Win32.Foundation;
using TapeLibNET.Agents;
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Services;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Design-TocLocation Phase 2, service level: the TOC anomalies of an operation — all its volumes — reach the result and
///  the user, with advice by cause and layout (§5.4). Modelled on <see cref="ServiceSetAnomalyTests"/>.
/// </summary>
/// <remarks>
/// Provable silence on the happy path is half the contract: an advice channel that speaks on a clean cartridge trains
///  users to ignore it.
/// </remarks>
public class ServiceTocAnomalyTests : ServiceTestBase
{
    #region *** (A) The policy, pure ***

    private static TapeTocAnomaly A(TapeTocAnomalyKind kind, int volume = 1, byte copy = 0,
        uint error = (uint)WIN32_ERROR.ERROR_WRITE_FAULT, TapeTocAnomalyStage stage = TapeTocAnomalyStage.Detected)
        => new(kind, stage, volume, copy, MediaPartition.Content, -1, false,
            stage == TapeTocAnomalyStage.Detected ? TapeResult.Fail(error, "cause") : TapeResult.OK);

    private const uint Eom = (uint)WIN32_ERROR.ERROR_END_OF_MEDIA;

    [Fact]
    public void Advise_NoAnomalies_IsNone()
        => Assert.Equal(TocAnomalyAdvice.None,
            TapeServiceBase.AdviseOnTocAnomalies([], TapeTocPlacement.InSet).Advice);

    /// <summary>The same symptom — one copy for lack of room — advises by LAYOUT: calibrate, or fewer files.</summary>
    [Theory]
    [InlineData(TapeTocPlacement.InSet, TocAnomalyAdvice.SingleCopyReserve)]
    [InlineData(TapeTocPlacement.InPartition, TocAnomalyAdvice.TocPartitionFull)]
    public void Advise_OneCopyAtEom_ByLayout(TapeTocPlacement placement, TocAnomalyAdvice expected)
        => Assert.Equal(expected, TapeServiceBase.AdviseOnTocAnomalies(
            [A(TapeTocAnomalyKind.CopyWriteFailed, copy: 2, error: Eom)], placement).Advice);

    /// <summary>One copy for ANOTHER reason is damage, not room — and so is a copy that did not read.</summary>
    [Fact]
    public void Advise_OneCopyOtherCause_OrUnreadableCopy_IsDamaged()
    {
        Assert.Equal(TocAnomalyAdvice.SingleCopyDamaged, TapeServiceBase.AdviseOnTocAnomalies(
            [A(TapeTocAnomalyKind.CopyWriteFailed, copy: 2)], TapeTocPlacement.InSet).Advice);

        // Recovered by its sibling — yet the copy on tape is still damaged.
        Assert.Equal(TocAnomalyAdvice.SingleCopyDamaged, TapeServiceBase.AdviseOnTocAnomalies(
            [A(TapeTocAnomalyKind.CopyUnreadable, copy: 1),
             A(TapeTocAnomalyKind.CopyUnreadable, copy: 1, stage: TapeTocAnomalyStage.Sibling)],
            TapeTocPlacement.InSet).Advice);
    }

    [Fact]
    public void Advise_NoCopy_IsTocLost_AndOutranksEverything()
    {
        var (advice, volumes) = TapeServiceBase.AdviseOnTocAnomalies(
            [A(TapeTocAnomalyKind.CopyWriteFailed, volume: 1, copy: 2, error: Eom),
             A(TapeTocAnomalyKind.NoCopyWritten, volume: 3),
             A(TapeTocAnomalyKind.NoCopyWritten, volume: 2)],
            TapeTocPlacement.InSet);

        Assert.Equal(TocAnomalyAdvice.TocLost, advice);
        Assert.Equal(new[] { 2, 3 }, volumes);          // the volumes it concerns, sorted, once each
    }

    /// <summary>A lost TOC that a later attempt wrote after all advises nothing.</summary>
    [Fact]
    public void Advise_LostThenRetried_IsSettled()
        => Assert.Equal(TocAnomalyAdvice.None, TapeServiceBase.AdviseOnTocAnomalies(
            [A(TapeTocAnomalyKind.NoCopyWritten),
             A(TapeTocAnomalyKind.NoCopyWritten, stage: TapeTocAnomalyStage.Retried)],
            TapeTocPlacement.InSet).Advice);

    [Theory]
    [InlineData(TapeTocAnomalyKind.NoReadableCopy)]
    [InlineData(TapeTocAnomalyKind.NotFoundNatively)]
    public void Advise_Unread_IsTocUnreadable(TapeTocAnomalyKind kind)
        => Assert.Equal(TocAnomalyAdvice.TocUnreadable, TapeServiceBase.AdviseOnTocAnomalies(
            [A(kind)], TapeTocPlacement.InSet).Advice);

    /// <summary>Every advice names something to DO; the reserve-related ones point to the Help topic.</summary>
    [Theory]
    [InlineData(TocAnomalyAdvice.TocLost, true)]
    [InlineData(TocAnomalyAdvice.TocUnreadable, false)]
    [InlineData(TocAnomalyAdvice.SingleCopyReserve, true)]
    [InlineData(TocAnomalyAdvice.TocPartitionFull, true)]
    [InlineData(TocAnomalyAdvice.SingleCopyDamaged, false)]
    [InlineData(TocAnomalyAdvice.TocFoundBySearch, false)]
    public void AdviseTocText_CarriesAnAction(TocAnomalyAdvice advice, bool pointsToHelp)
    {
        var (level, headline, action) = TapeServiceBase.AdviseTocText(advice, [2]);
        Assert.Contains("volume #2", headline);
        Assert.False(string.IsNullOrWhiteSpace(action));
        Assert.True(level is ServiceReportLevel.Warning or ServiceReportLevel.Failed);
        Assert.Equal(pointsToHelp, action.Contains(TapeServiceBase.TocSpaceHelpTopic));
    }

    [Fact]
    public void AdviseTocText_None_SaysNothing()
    {
        var (_, headline, action) = TapeServiceBase.AdviseTocText(TocAnomalyAdvice.None, []);
        Assert.Empty(headline);
        Assert.Empty(action);
    }

    [Fact]
    public void DescribeVolumes_NamesOneMany_OrTheLoadedVolume()
    {
        Assert.Equal("volume #2", TapeServiceBase.DescribeVolumes([2]));
        Assert.Equal("volumes #1, #3", TapeServiceBase.DescribeVolumes([1, 3]));
        Assert.Equal("the loaded volume", TapeServiceBase.DescribeVolumes([0]));
    }

    #endregion

    #region *** (B) Backup ***

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_Clean_SaysNothingAboutTocAnomalies(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        using var src = new TempFileTree();
        src.AddFiles("clean", count: 4, minSize: 512, maxSize: 4_096);

        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src.RootPath, "Clean"));

            Assert.True(result.Success, host.DumpReports());
            Assert.False(result.HasTocAnomalies);
            Assert.Empty(result.TocAnomalies);
            Assert.Equal(2, result.TocStats.CopiesWritten);
            Assert.False(host.ContainsMessage("TOC copy"), host.DumpReports());
            Assert.False(host.ContainsMessage("TOC anomaly"), host.DumpReports());
        }
    }

#if TEST_HOOKS
    /// <summary>
    /// One copy missing: the backup SUCCEEDS, the anomaly reaches the result, the user is told — and advised. The
    ///  simulated failure is no end-of-media, so the advice is "damaged", not "calibrate".
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_SecondTocCopyFails_SucceedsWithAdvice(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        using var src = new TempFileTree();
        src.AddFiles("copy2", count: 4, minSize: 512, maxSize: 4_096);

        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            svc.OnAgentReady = a => a.SimulateTOCFailureMask = 2;

            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src.RootPath, "Copy2"));

            Assert.True(result.Success, $"one copy on tape is a success. {host.DumpReports()}");
            Assert.True(result.HasTocAnomalies);
            var a = Assert.Single(result.TocAnomalies);
            Assert.Equal((TapeTocAnomalyKind.CopyWriteFailed, (byte)2), (a.Kind, a.CopyIndex));
            Assert.Equal(1, result.TocStats.CopyWriteFailures);

            Assert.True(host.ContainsMessage("TOC copy 2"), host.DumpReports());
            Assert.True(host.ContainsMessage("damaged"), $"the user must be advised. {host.DumpReports()}");
        }

        // The TOC on tape is good: its 1st copy reads.
        var (check, _) = await ReopenAsync(media);
        using (check)
            Assert.Equal(1, check.TOC!.Count);
    }
#endif // TEST_HOOKS

    #endregion

    #region *** (C) Delete ***

#if TEST_HOOKS
    [Fact]
    public async Task Delete_SecondTocCopyFails_ReportsTocAdvice()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var t1 = new TempFileTree();
        using var t2 = new TempFileTree();
        t1.AddFiles("d1", count: 3, minSize: 512, maxSize: 4_096);
        t2.AddFiles("d2", count: 3, minSize: 512, maxSize: 4_096);

        var (seed, _) = await OpenAndFormatAsync(media);
        using (seed)
        {
            Assert.True((await seed.ExecuteBackupAsync(MakeBackupRequest(seed, t1.RootPath, "Keep")
                with { ProceedOnMediaMismatch = true })).Success);
            Assert.True((await seed.ExecuteBackupAsync(MakeBackupRequest(seed, t2.RootPath, "Delete", append: true)
                with { ProceedOnMediaMismatch = true })).Success);
        }

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            svc.OnAgentReady = a => a.SimulateTOCFailureMask = 2;

            var result = await svc.DeleteBackupSetsExAsync(svc.TOC!.LastSetOnVolume);

            Assert.True(result.Success, host.DumpReports());
            Assert.Equal(1, result.SetsDeleted);
            Assert.Contains(result.TocAnomalies, a => a.Kind == TapeTocAnomalyKind.CopyWriteFailed);
            Assert.Equal(ServiceReportLevel.Warning, result.Outcome);   // succeeded, but the medium spoke
            Assert.True(host.ContainsMessage("damaged"), host.DumpReports());
        }
    }
#endif // TEST_HOOKS

    #endregion

    #region *** (D) Multi-volume restore: the trail spans the volumes ***

#if TEST_HOOKS
    private const int MvFileCount = 16;
    private const long MvFileSize = 350L * 1024;
    private const long MvCapacity_Setmarks = 36L * 1024 * 1024;
    private const long MvCapacity_Initiator = 3L * 1024 * 1024;

    /// <summary>
    /// The resume reads each requested volume's TOC to identify the cartridge. Its 1st copy does not read, its 2nd does:
    ///  the restore completes, and the result names the volume — anomalies of EARLIER volumes survive to the final
    ///  report (Design-TocLocation §5.3).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiVolumeRestore_ResumeReadsSecondCopy_ResultNamesTheVolume(bool withInitiator)
    {
        long capacity = withInitiator ? MvCapacity_Initiator : MvCapacity_Setmarks;
        using var vol1 = new TempVirtualMedia(withInitiator, capacity);
        using var vol2 = new TempVirtualMedia(withInitiator, capacity);
        IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];

        using var tree = new TempFileTree();
        for (int i = 0; i < MvFileCount; i++)
            tree.AddFile($"mv_toc_{i:D2}.bin", MvFileSize);

        var (bsvc, bhost) = await OpenAndFormatMultiVolumeAsync(volumes);
        using (bsvc)
        {
            var backup = await bsvc.ExecuteBackupAsync(MakeBackupRequest(bsvc, tree.RootPath, "MV-TOC"));
            Assert.True(backup.Success, bhost.DumpReports());
            Assert.True(bhost.VolumesInserted >= 1, "the backup must span two volumes");
        }

        string restoreDir = Path.Combine(vol1.Root, "restore_toc");
        Directory.CreateDirectory(restoreDir);

        var (svc, host) = await ReopenMultiVolumeAsync(volumes);
        using (svc)
        {
            svc.OnAgentReady = a => a.SimulateTOCFailureMask = 1;   // every resume: copy 1 fails, copy 2 reads

            var result = await svc.ExecuteRestoreAsync(new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [svc.TOC!.LastSetOnVolume] = null },
                Incremental:           false,
                TargetDirectory:       restoreDir,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false,
                EjectWhenDone:         false));

            Assert.True(result.Success, host.DumpReports());
            Assert.Equal(MvFileCount, result.FilesSucceeded);

            Assert.Contains(result.TocAnomalies, a => a is
                { Kind: TapeTocAnomalyKind.CopyUnreadable, Volume: 1, Stage: TapeTocAnomalyStage.Detected });
            Assert.Contains(result.TocAnomalies, a => a is
                { Kind: TapeTocAnomalyKind.CopyUnreadable, Volume: 1, Stage: TapeTocAnomalyStage.Sibling });
            Assert.True(result.TocStats.AnomaliesRecovered >= 1);
            Assert.True(host.ContainsMessage("damaged"), host.DumpReports());
        }

        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(restoreDir, tree.RootPath));
    }
#endif // TEST_HOOKS

    #endregion
}
