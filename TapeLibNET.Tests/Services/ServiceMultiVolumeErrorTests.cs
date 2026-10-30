using TapeLibNET.Headers; // TapeTocPlacement
using TapeLibNET.Agents;
using TapeLibNET.Services;
using TapeLibNET.Toc;
using TapeLibNET.Virtual;

using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia, MultiVolumeTapeServiceHost

namespace TapeLibNET.Tests.Services;

#if TEST_HOOKS
/// <summary>
/// Service-level multi-volume error handling: Skip Volume (voluntary and after a volume error), a volume that cannot
///  be read (restore) or written (backup), and the three answers to <see cref="ITapeServiceHost.OnVolumeErrorSelect"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The originating defect is the first guard here:</b> a volume whose TOC cannot be read made every caller looping
///  on <c>CanResumeFromAnotherVolume</c> spin forever. Each operation therefore runs under <see cref="NoHang"/> — a
///  regression fails the test instead of hanging the run.
/// </para>
/// <para>
/// Sizes as in <c>ServiceMultiVolumeTests</c> A-5: 16 × 350 KiB over exactly two volumes on both profiles.
/// </para>
/// </remarks>
public class ServiceMultiVolumeErrorTests : ServiceTestBase
{
    #region *** Data and helpers ***

    private const int FileCount = 16;
    private const long FileSize = 350L * 1024;
    private const long Capacity_Setmarks = 3L * 1024 * 1024; // TOC Reserve update. Was: 36L * 1024 * 1024;
    private const long Capacity_Initiator = 3L * 1024 * 1024;

    /// <summary>Generous upper bound for one operation — a loop regression must fail, not hang.</summary>
    private static readonly TimeSpan NoHang = TimeSpan.FromMinutes(2);

    private static (TempVirtualMedia vol1, TempVirtualMedia vol2) MakeVolumes(bool withInitiator)
    {
        long capacity = withInitiator ? Capacity_Initiator : Capacity_Setmarks;
        return (new TempVirtualMedia(withInitiator, capacity), new TempVirtualMedia(withInitiator, capacity));
    }

    private static TempFileTree MakeTree()
    {
        var tree = new TempFileTree();
        for (int i = 0; i < FileCount; i++)
            tree.AddFile($"mv_err_{i:D2}.bin", FileSize);
        return tree;
    }

    /// <summary>A clean two-volume backup of <paramref name="tree"/>.</summary>
    private async Task BackupAcrossTwoVolumesAsync(IReadOnlyList<TempVirtualMedia> volumes, TempFileTree tree)
    {
        var (svc, host) = await OpenAndFormatMultiVolumeAsync(volumes);
        using (svc)
        {
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "MV-Errors"))
                .WaitAsync(NoHang);
            Assert.True(result.Success, $"seeding backup failed: {svc.LastError}\n{host.DumpReports()}");
            Assert.Equal(FileCount, result.FilesSucceeded);
            Assert.True(host.VolumesInserted >= 1, "the seeding backup must span two volumes");
        }
    }

    private static RestoreRequest MakeRestoreRequest(int setIndex, string targetDir) => new(
        Mode:                  RestoreMode.Restore,
        CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
        Incremental:           false,
        TargetDirectory:       targetDir,
        RecurseSubdirectories: true,
        HandleExisting:        TapeHowToHandleExisting.Overwrite,
        SkipAllErrors:         false,
        EjectWhenDone:         false);

    /// <summary>Source paths of the files the logical set ending at <paramref name="lastPart"/> holds on <paramref name="volume"/>.</summary>
    private static HashSet<string> FilesOnVolume(TapeTOC toc, int lastPart, int volume)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int s = lastPart; s >= 1; s--)
        {
            if (toc[s].Volume == volume)
                foreach (TapeFileInfo tfi in toc[s])
                    names.Add(tfi.FileDescr.FullName);
            if (!toc[s].ContinuedFromPrevVolume)
                break;
        }
        return names;
    }

    /// <summary>Exactly <paramref name="expected"/> restored, byte for byte; every other file of the tree absent.</summary>
    private static void AssertExactlyRestored(TempFileTree tree, string restoreDir, HashSet<string> expected)
    {
        string root = FindRestoredRoot(restoreDir, tree.RootPath);
        FileComparer.AssertFilesMatch(tree.RootPath, [.. tree.Files.Where(expected.Contains)], root);
        foreach (string missing in tree.Files.Where(f => !expected.Contains(f)))
            Assert.False(File.Exists(Path.Combine(root, Path.GetRelativePath(tree.RootPath, missing))),
                $"{Path.GetFileName(missing)} must not have been restored");
    }

    /// <summary>
    /// Asserts a single set-anomaly prompt was raised, returns it, and CLEARS the ledger so the teardown
    ///  sweep stays meaningful.
    /// </summary>
    private static TestTapeServiceHost.VolumeErrorPrompt TakeSingleVolumeErrorPrompt(TestTapeServiceHost host)
    {
        var prompt = Assert.Single(host.VolumeErrorPrompts);
        host.VolumeErrorPrompts.Clear();
        return prompt;
    }

    /// <summary>
    /// Asserts a single media mismatch prompt was raised, returns it, and CLEARS the ledger so the teardown
    ///  sweep stays meaningful.
    /// </summary>
    private static TestTapeServiceHost.MediaMismatchPrompt TakeSingleMediaMismatchPrompt(TestTapeServiceHost host)
    {
        var prompt = Assert.Single(host.MediaMismatchPrompts);
        host.MediaMismatchPrompts.Clear();
        return prompt;
    }

    #endregion

    #region *** Restore: Skip Volume ***

    /// <summary>
    /// The user skips volume 1 at the continue prompt (the cartridge is not at hand): its files are counted as SKIPPED,
    ///  the operation goes on with volume 2, restores exactly its files, and reports no failure.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_VolumeSkippedAtContinuePrompt_RestoresTheRest_CountsSkipped(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeTree();
            await BackupAcrossTwoVolumesAsync(volumes, tree);

            string restoreDir = Path.Combine(vol1.Root, "restore_skip");
            Directory.CreateDirectory(restoreDir);
            var (svc, host) = await ReopenMultiVolumeAsync(volumes);
            using (svc)
            {
                var toc = svc.TOC!;
                int setIdx = toc.LastSetOnVolume;
                var onVol1 = FilesOnVolume(toc, setIdx, 1);
                var onVol2 = FilesOnVolume(toc, setIdx, 2);
                Assert.NotEmpty(onVol1);
                Assert.NotEmpty(onVol2);

                host.VolumeContinueAnswers.Enqueue(VolumeAction.Skip);   // volume 1: skip; then Insert by default

                var result = await svc.ExecuteRestoreAsync(MakeRestoreRequest(setIdx, restoreDir)).WaitAsync(NoHang);

                Assert.True(result.Success, $"a voluntary skip is no failure: {result.Message}\n{host.DumpReports()}");
                Assert.False(result.WasAborted);
                Assert.Equal(0, result.FilesFailed);
                Assert.Equal(onVol1.Count, result.FilesSkipped);
                Assert.Equal(onVol2.Count, result.FilesSucceeded);
                Assert.Equal(result.FilesProcessed, result.FilesSucceeded + result.FilesFailed + result.FilesSkipped);
                Assert.Empty(host.VolumeErrorPrompts);
                Assert.True(host.ContainsMessage("Volume #1 skipped"), host.DumpReports());

                AssertExactlyRestored(tree, restoreDir, onVol2);
            }
        }
    }

    #endregion

    #region *** Restore: a volume whose TOC cannot be read ***

    /// <summary>
    /// The originating defect, at service level, with the unattended answer (End): the operation ENDS — no loop — with
    ///  the volume error as its diagnosis and a failed result, though no file counter shows a failure.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_UnreadableVolume_End_StopsWithDiagnosis(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeTree();
            await BackupAcrossTwoVolumesAsync(volumes, tree);

            string restoreDir = Path.Combine(vol1.Root, "restore_end");
            Directory.CreateDirectory(restoreDir);
            var (svc, host) = await ReopenMultiVolumeAsync(volumes);
            using (svc)
            {
                // INSUFFICIENT: svc.OnAgentReady = a => a.SimulateTOCFailureMask = 7;   // every TOC copy of every requested volume fails
                host.MakeVolumeUnreadable(1);                       // nothing on volume 1 identifies
                // VolumeErrorAnswers left empty: the test host answers End as the unattended default

                var result = await svc.ExecuteRestoreAsync(MakeRestoreRequest(svc.TOC!.LastSetOnVolume, restoreDir))
                    .WaitAsync(NoHang);

                var prompt = TakeSingleVolumeErrorPrompt(host);
                Assert.Equal(1, prompt.Volume);
                Assert.True(prompt.AllowSkip, "a restore may skip a volume");

                Assert.False(result.Success);
                Assert.True(result.HasFailed, "an operation ended on an unusable volume has failed");
                Assert.False(result.WasAborted);
                Assert.Equal(0, result.FilesSucceeded);                 // volume 1 comes first: nothing restored yet
                Assert.Contains("table of contents", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.True(host.ContainsMessage("Volume #1 cannot be used"), host.DumpReports());

                Assert.DoesNotContain(result.TocAnomalies,
                    a => a.IsRecovery && a.Volume == 1); // volume rejection isn't reported as a recovered TOC anomaly

                AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
            }
        }
    }

    /// <summary>
    /// Insert: the user inserts volume 1 again (a cleaned cartridge — the fault is lifted at the prompt), and the whole
    ///  set restores; the rejected attempt leaves no trace in the result.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_UnreadableVolume_InsertAgain_RestoresEverything(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeTree();
            await BackupAcrossTwoVolumesAsync(volumes, tree);

            string restoreDir = Path.Combine(vol1.Root, "restore_retry");
            Directory.CreateDirectory(restoreDir);
            var (svc, host) = await ReopenMultiVolumeAsync(volumes);
            using (svc)
            {
                // INSUFFICIENT: svc.OnAgentReady = a => a.SimulateTOCFailureMask = 7;
                host.MakeVolumeUnreadable(1);
                host.VolumeErrorAnswers.Enqueue(VolumeAction.Insert);
                host.OnVolumeErrorPrompted = _ => host.VolumeFaults.Remove(1);   // "cleaned": the re-insertion is clean

                var result = await svc.ExecuteRestoreAsync(MakeRestoreRequest(svc.TOC!.LastSetOnVolume, restoreDir))
                    .WaitAsync(NoHang);

                var prompt = TakeSingleVolumeErrorPrompt(host);
                Assert.Equal(1, prompt.Volume);
                Assert.True(prompt.AllowSkip, "a restore may skip a volume");
                
                Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
                Assert.True(result.Diagnosis.Success, "the rejected attempt must not linger in the diagnosis");
                Assert.Equal(FileCount, result.FilesSucceeded);
                Assert.Equal(0, result.FilesFailed);
                FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(restoreDir, tree.RootPath));

                // The first insertion WAS unreadable: on record, settled by the re-insertion — and nothing left to advise.
                var detected = Assert.Single(result.TocAnomalies, a => !a.IsRecovery);
                Assert.Equal((TapeTocAnomalyKind.NoReadableCopy, 1), (detected.Kind, detected.Volume));
                var settled = Assert.Single(result.TocAnomalies, a => a.IsRecovery);
                Assert.Equal(TapeTocAnomalyStage.Retried, settled.Stage);
                var placement = withInitiator ? TapeTocPlacement.InPartition : TapeTocPlacement.InSet;
                Assert.Equal(TocAnomalyAdvice.None, TapeServiceBase.AdviseOnTocAnomalies(result.TocAnomalies, placement).Advice);

                AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC)); // the clean re-insertion is identified by its header, without asking
            }
        }
    }

    /// <summary>
    /// Skip after the error: volume 1's files are given up and counted as FAILED, volume 2 restores, and the result names
    ///  the skipped volume.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_UnreadableVolume_Skip_RestoresTheRest_CountsFailed(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeTree();
            await BackupAcrossTwoVolumesAsync(volumes, tree);

            string restoreDir = Path.Combine(vol1.Root, "restore_skip_err");
            Directory.CreateDirectory(restoreDir);
            var (svc, host) = await ReopenMultiVolumeAsync(volumes);
            using (svc)
            {
                var toc = svc.TOC!;
                int setIdx = toc.LastSetOnVolume;
                var onVol1 = FilesOnVolume(toc, setIdx, 1);
                var onVol2 = FilesOnVolume(toc, setIdx, 2);

                // INSUFFICIENT: svc.OnAgentReady = a => a.SimulateTOCFailureMask = 7;
                host.MakeVolumeUnreadable(1);                       // volume 2 is inserted clean: no failure plan for it
                host.VolumeErrorAnswers.Enqueue(VolumeAction.Skip);
                // INSUFFICIENT: host.OnVolumeErrorPrompted = _ => svc.Agent!.SimulateTOCFailureMask = 0;   // only volume 1 is bad

                var result = await svc.ExecuteRestoreAsync(MakeRestoreRequest(setIdx, restoreDir)).WaitAsync(NoHang);

                var prompt = TakeSingleVolumeErrorPrompt(host);
                Assert.Equal(1, prompt.Volume);
                Assert.True(prompt.AllowSkip, "a restore may skip a volume");
                Assert.False(result.Success);
                Assert.Equal(onVol1.Count, result.FilesFailed);
                Assert.Equal(onVol2.Count, result.FilesSucceeded);
                Assert.Equal(result.FilesProcessed, result.FilesSucceeded + result.FilesFailed + result.FilesSkipped);
                Assert.Contains("Volume #1 skipped", result.Message ?? "", StringComparison.Ordinal);

                AssertExactlyRestored(tree, restoreDir, onVol2);

                Assert.DoesNotContain(result.TocAnomalies,
                    a => a.IsRecovery && a.Volume == 1); // volume rejection isn't reported as a recovered TOC anomaly

                AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
            }
        }
    }

    #region *** Restore: a volume whose TOC cannot be read, but which identifies ***

    /// <summary>
    /// Design-TocLocation §7 at service level: no TOC copy of any requested volume reads, yet every volume is identified —
    ///  by its media header — and the whole set restores through the series TOC. No volume-error prompt; the unreadable
    ///  TOCs are on record in the result, and the closing advice names them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_UnreadableToc_VolumeIdentified_RestoresEverything_ReportsToc(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeTree();
            await BackupAcrossTwoVolumesAsync(volumes, tree);

            string restoreDir = Path.Combine(vol1.Root, "restore_toc_unreadable");
            Directory.CreateDirectory(restoreDir);

            var (svc, host) = await ReopenMultiVolumeAsync(volumes);
            using (svc)
            {
                svc.OnAgentReady = a => a.SimulateTOCFailureMask = -1;   // no TOC copy of any cartridge reads

                var result = await svc.ExecuteRestoreAsync(MakeRestoreRequest(svc.TOC!.LastSetOnVolume, restoreDir))
                    .WaitAsync(NoHang);

                // The restore itself: complete, byte for byte, no prompt about the volume
                Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
                Assert.Equal(FileCount, result.FilesSucceeded);
                Assert.Equal(0, result.FilesFailed);
                Assert.Empty(host.VolumeErrorPrompts);
                FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(restoreDir, tree.RootPath));

                // The diagnostics: the unreadable TOC is on record, unsettled, and advised on
                Assert.Contains(result.TocAnomalies,
                    a => a.Kind == TapeTocAnomalyKind.NoReadableCopy && a.Volume == 1 && !a.IsRecovery);
                Assert.DoesNotContain(result.TocAnomalies, a => a.IsRecovery && a.Volume == 1);
                Assert.True(result.TocStats.AnomaliesDetected >= 1);
                Assert.True(host.ContainsMessage("could not be read"), host.DumpReports());
            }
        }
    }

    #endregion

    #endregion

    #region *** Backup: a continuation cartridge that refuses the first write ***

    /// <summary>
    /// The fresh volume 2 refuses its media header; the user ends the backup. Volume 1 and its TOC stand as saved (they
    ///  hold exactly the files reported as backed up), nothing is written to volume 2, and the result is a failure with
    ///  the volume error as its diagnosis. Skip is not offered.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_ContinuationCartridgeRefused_End_Vol1Stands_NothingOnVol2(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeTree();
            int filesOnVol1;

            var (svc, host) = await OpenAndFormatMultiVolumeAsync(volumes);
            using (svc)
            {
                host.AfterMediaInserted = v =>
                {
                    if (v == 2)
                        svc.VirtualBackend!.ContentWriteFaults.FailOnce();     // the first write: the media header
                };
                // VolumeErrorAnswers left empty: End

                var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "MV-Refused"))
                    .WaitAsync(NoHang);

                var prompt = TakeSingleVolumeErrorPrompt(host);
                Assert.Equal(2, prompt.Volume);
                Assert.False(prompt.AllowSkip, "a backup cannot skip a volume");
                Assert.Equal("Backup", prompt.OperationName);
                Assert.False(result.Success);
                Assert.True(result.HasFailed);
                Assert.False(result.WasAborted);
                Assert.True(result.FilesSucceeded is > 0 and < FileCount, $"{result.FilesSucceeded} file(s) backed up");
                Assert.Contains("Volume #2", result.Message ?? "", StringComparison.Ordinal);
                filesOnVol1 = result.FilesSucceeded;
            }

            // Volume 1's TOC describes exactly what was backed up, and still points at a continuation
            var (check, _) = await ReopenMultiVolumeAsync((IReadOnlyList<TempVirtualMedia>)[vol1]);
            using (check)
            {
                var toc = check.TOC!;
                Assert.Equal(1, toc.Volume);
                Assert.Equal(filesOnVol1, toc[toc.LastSetOnVolume].Count);
            }

            // Nothing reached volume 2 — in particular no TOC
            var (blank, blankHost) = CreateMultiVolumeService(volumes);
            using (blank)
            {
                var caps = vol2.HasInitiator
                    ? VirtualTapeDriveCapabilities.WithPartitions
                    : VirtualTapeDriveCapabilities.WithSetmarks;
                var vmd = new VirtualMediaDescriptor(vol2.ContentPath, vol2.ContentCapacity,
                    vol2.InitiatorPath, vol2.HasInitiator ? vol2.InitiatorCapacity : 0);
                Assert.True(await blank.OpenVirtualDriveAsync(caps, vmd, FileMode.Open), blank.LastError);
                Assert.True(await blank.LoadMediaAsync(), blank.LastError);
                Assert.Null(blank.LoadedMediaHeader);
                Assert.False(await blank.RestoreTOCAsync().WaitAsync(NoHang), "the refused cartridge must hold no TOC");

                var prompt = TakeSingleMediaMismatchPrompt(blankHost);
                Assert.Equal(TapeMediaVerdict.Unidentified, prompt.Verdict);
                Assert.Equal(MediaPromptContext.FullWalkForTOC, prompt.Context);
            }
        }
    }

    /// <summary>
    /// Insert: the user offers another cartridge for volume 2 (here: the same, now writable), and the backup completes
    ///  across both volumes; everything restores.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_ContinuationCartridgeRefused_InsertAnother_Completes(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeTree();

            var (svc, host) = await OpenAndFormatMultiVolumeAsync(volumes);
            using (svc)
            {
                bool armed = false;
                host.AfterMediaInserted = v =>
                {
                    if (v == 2 && !armed)
                    {
                        armed = true;                                         // only the first offer is refused
                        svc.VirtualBackend!.ContentWriteFaults.FailOnce();
                    }
                };
                host.VolumeErrorAnswers.Enqueue(VolumeAction.Insert);

                var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "MV-Retried"))
                    .WaitAsync(NoHang);

                var prompt = TakeSingleVolumeErrorPrompt(host);
                Assert.Equal(2, prompt.Volume);
                Assert.False(prompt.AllowSkip, "a backup cannot skip a volume");
                Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
                Assert.True(result.Diagnosis.Success, "the refused cartridge must not linger in the diagnosis");
                Assert.Equal(FileCount, result.FilesSucceeded);
                Assert.Equal(0, result.FilesFailed);
            }

            string restoreDir = Path.Combine(vol1.Root, "restore_after_retry");
            Directory.CreateDirectory(restoreDir);
            var (rsvc, rhost) = await ReopenMultiVolumeAsync(volumes);
            using (rsvc)
            {
                var result = await rsvc.ExecuteRestoreAsync(MakeRestoreRequest(rsvc.TOC!.LastSetOnVolume, restoreDir))
                    .WaitAsync(NoHang);
                Assert.True(result.Success, $"{result.Message}\n{rhost.DumpReports()}");
                Assert.Equal(FileCount, result.FilesSucceeded);
            }
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(restoreDir, tree.RootPath));
        }
    }

    #endregion

    #region *** Backup: user refuses continuation cartridge ***

    /// <summary>
    /// Only one cartridge on offer: the backup fills it, the next insertion is cancelled. Volume 1 and its TOC stand, and
    ///  the result is a failure that says how many files were left out — never "completed successfully".
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_SeriesEndedByUser_FailsNamingTheFilesLeftOut(bool withInitiator)
    {
        var (vol1, vol2) = MakeVolumes(withInitiator);
        using (vol1) using (vol2)
        {
            using var tree = MakeTree();
            var (svc, host) = await OpenAndFormatMultiVolumeAsync((IReadOnlyList<TempVirtualMedia>)[vol1]);
            using (svc)
            {
                var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "MV-Ended"))
                    .WaitAsync(NoHang);

                Assert.False(result.Success);
                Assert.True(result.HasFailed);
                Assert.False(result.WasAborted);
                Assert.InRange(result.FilesSucceeded, 1, FileCount - 1);
                Assert.Contains($"{FileCount - result.FilesSucceeded} file(s) not backed up", result.Message ?? "");
                Assert.False(host.ContainsMessage("Backup completed successfully"), host.DumpReports());
            }
        }
    }

    #endregion
}
#endif
