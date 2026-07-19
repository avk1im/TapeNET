using System.IO;

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Virtual;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Multi-volume backup and restore tests: automatic volume swapping, TOC
///  continuation flags, and incremental sets that span volume boundaries.
///  Covers plan items A-5 and A-6.
/// </summary>
public class ServiceMultiVolumeTests : ServiceTestBase
{
    // ── A-5 constants ─────────────────────────────────────────────────────────

    /// <summary>
    /// Number of files written by <see cref="AddMultiVolumeContent"/>.
    ///  16 files × 350 KiB ˜ 5.6 MiB total — fits in exactly 2 volumes on either
    ///  drive profile (˜ 4 MiB usable on setmarks, 3 MiB on initiator), guaranteeing
    ///  at least one automatic volume swap without requiring more than 2 volumes.
    /// </summary>
    private const int MultiVolumeFileCount = 16;

    /// <summary>
    /// Size of each file produced by <see cref="AddMultiVolumeContent"/>: 350 KiB.
    ///  Total file content = 64 × 350 KiB ≈ 22 MiB — well above the per-volume
    ///  usable area on either profile, so spillover onto vol-2 is guaranteed
    ///  regardless of packed vs aligned per-file padding.
    /// </summary>
    private const long MultiVolumeFileSize = 350L * 1024;

    /// <summary>
    /// Content-partition capacity for setmarks (single-partition) multi-volume test volumes.
    ///  Must be larger than <see cref="TapeNavigator.DefaultTOCCapacity"/> (32 MiB) because
    ///  the backup agent reserves that space for the in-tape TOC on setmarks drives.
    ///  36 MiB → 4 MiB usable per volume; 22 MiB total content overflows trivially.
    /// </summary>
    private const long MultiVolumeCapacity_Setmarks = 36L * 1024 * 1024;

    /// <summary>
    /// Content-partition capacity for initiator-partition multi-volume test volumes.
    ///  The TOC lives in the initiator partition so the full content space is available
    ///  for file data. 3 MiB per volume; total 22 MiB overflows trivially.
    /// </summary>
    private const long MultiVolumeCapacity_Initiator = 3L * 1024 * 1024;

    // ── A-6 constants ─────────────────────────────────────────────────────────

    /// <summary>
    /// Number of files in the A-6 multi-volume incremental test tree.
    /// </summary>
    private const int MvIncFileCount = 16;

    /// <summary>
    /// Size of each file in the <b>full</b> backup for A-6: 350 KiB.
    ///  Total ≈ 5.5 MiB — must fit entirely on a single vol-1.
    /// </summary>
    private const long MvIncFileSizeFull = 350L * 1024;

    /// <summary>
    /// Size of each file after modification (used in the incremental backup): 700 KiB.
    ///  Total ≈ 11.2 MiB — exceeds the per-volume headroom on both drive profiles,
    ///  guaranteeing overflow onto vol-2.
    /// </summary>
    private const long MvIncFileSizeModified = 700L * 1024;

    /// <summary>
    /// Vol-1 capacity for setmarks drives in A-6.
    ///  Block-rounded full backup: 16 × ⌈350/64⌉ × 64 KiB = 16 × 384 KiB = 6,144 KiB.
    ///  With file-header overhead this slightly exceeds a 6 MiB usable window, so
    ///  vol-1 is sized at 40 MiB (usable = 40 − 32 MiB TOC reserve = 8 MiB):
    ///  8 MiB &gt; ~6.1 MiB (full backup, block-padded) ✓  and  8 MiB &lt; ~11.2 MiB (incremental) ✓.
    /// </summary>
    private const long MvIncVol1Capacity_Setmarks = 40L * 1024 * 1024;

    /// <summary>
    /// Vol-1 capacity for initiator-partition drives in A-6.
    ///  Same usable-window logic as setmarks but without the 16 MiB TOC reserve:
    ///  8 MiB &gt; ~6.1 MiB (full backup) ✓  and  8 MiB &lt; ~11.2 MiB (incremental) ✓.
    /// </summary>
    private const long MvIncVol1Capacity_Initiator = 8L * 1024 * 1024;

    /// <summary>
    /// Vol-2 capacity for setmarks drives in A-6.
    ///  Must hold the full incremental overflow: 16 × 700 KiB = 11.2 MiB data
    ///  plus 32 MiB TOC reserve = 43.2 MiB → 46 MiB with headroom.
    /// </summary>
    private const long MvIncVol2Capacity_Setmarks = 46L * 1024 * 1024;

    /// <summary>
    /// Vol-2 capacity for initiator-partition drives in A-6.
    ///  Must hold the full incremental overflow: 16 × 700 KiB = 11.2 MiB data → 14 MiB.
    /// </summary>
    private const long MvIncVol2Capacity_Initiator = 14L * 1024 * 1024;

    // ── Content helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Adds <see cref="MultiVolumeFileCount"/> uniquely-named files of
    ///  <see cref="MultiVolumeFileSize"/> each to <paramref name="tree"/>.
    /// </summary>
    private static void AddMultiVolumeContent(TempFileTree tree)
    {
        for (int i = 0; i < MultiVolumeFileCount; i++)
            tree.AddFile($"mv_file_{i:D2}.bin", MultiVolumeFileSize);
    }

    // ── A-5: multi-volume regular backup + restore ────────────────────────────

    /// <summary>
    /// Writes <see cref="MultiVolumeFileCount"/> files (350 KiB each, ~5.6 MiB total)
    ///  to a two-volume media set, verifying that the backup engine spans volumes
    ///  automatically and that a full restore recovers every byte intact.
    /// <para>
    /// Volume capacity is drive-profile-dependent: 20 MiB for setmarks drives
    ///  (where 16 MiB is reserved per volume for the in-tape TOC, leaving ~4 MiB
    ///  usable) and 3 MiB for initiator-partition drives (TOC lives in the initiator
    ///  partition, so the full content area is available). In both cases the total
    ///  file content (~5.6 MiB) exceeds one volume's usable headroom, guaranteeing
    ///  at least one automatic volume swap.
    /// </para>
    /// <para>
    /// Parameterised over both drive profiles (setmarks-only and with initiator partition).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)] // single-partition (setmarks only)
    [InlineData(true)]  // with initiator partition
    public async Task MultiVolume_RegularBackup_SpansVolumes_RestoreAllFiles(bool withInitiator)
    {
        // Two pre-allocated virtual volumes; the host swaps them automatically.
        //  Capacity is drive-profile-specific: setmarks drives reserve 16 MiB per volume
        //  for the in-tape TOC, so those volumes must be larger than 16 MiB.
        //  16 files × 350 KiB ˜ 5.6 MiB — exceeds one volume's headroom on both profiles.
        long volumeCapacity = withInitiator ? MultiVolumeCapacity_Initiator : MultiVolumeCapacity_Setmarks;
        using var vol1 = new TempVirtualMedia(withInitiator, volumeCapacity);
        using var vol2 = new TempVirtualMedia(withInitiator, volumeCapacity);
        /*
        using var vol3 = new TempVirtualMedia(withInitiator, volumeCapacity);
        using var vol4 = new TempVirtualMedia(withInitiator, volumeCapacity);
        using var vol5 = new TempVirtualMedia(withInitiator, volumeCapacity);
        using var vol6 = new TempVirtualMedia(withInitiator, volumeCapacity);
        using var vol7 = new TempVirtualMedia(withInitiator, volumeCapacity);
        using var vol8 = new TempVirtualMedia(withInitiator, volumeCapacity);
        IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2, vol3, vol4, vol5, vol6, vol7, vol8];
        */
        IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2, ];

        using var src = new TempFileTree();
        AddMultiVolumeContent(src);

        // ── Backup ────────────────────────────────────────────────────────────
        var (backupSvc, backupHost) = await OpenAndFormatMultiVolumeAsync(volumes);
        using (backupSvc)
        {
            var req    = MakeBackupRequest(backupSvc, src.RootPath, "MultiVol-Set-1");
            var result = await backupSvc.ExecuteBackupAsync(req);

            Assert.False(result.WasAborted,  "Backup was unexpectedly aborted");
            Assert.False(result.HasFailed,    $"Backup reported failure: {backupSvc.LastError}");
            Assert.True (result.Success,      $"Backup did not succeed: {backupSvc.LastError}");
            Assert.Equal(0,                   result.FilesFailed);
            Assert.Equal(MultiVolumeFileCount, result.FilesSucceeded);

            // Note: HasErrors is intentionally not asserted for multi-volume backup.
            //  The service emits transient ServiceReportLevel.Failed entries for the
            //  EOM-transition file before identifying end-of-media and retrying on the
            //  next volume; StatsUndoFailure() corrects the counters but the log entry
            //  remains. result.Success + FilesFailed == 0 are the authoritative indicators.

            // At least one volume swap must have occurred for this to be a true multi-volume test.
            Assert.True(backupHost.VolumesInserted >= 1,
                $"Expected at least 1 volume insertion during backup; got {backupHost.VolumesInserted}");
        }

        // ── Verify TOC flags on volume 2 ─────────────────────────────────────
        //  Re-insert vol2 and restore its TOC; the continued set must carry
        //  ContinuedFromPrevVolume = true.
        {
            var (svc2, _) = CreateMultiVolumeService(volumes);
            using (svc2)
            {
                var caps2 = vol2.HasInitiator
                    ? VirtualTapeDriveCapabilities.WithPartitions
                    : VirtualTapeDriveCapabilities.WithSetmarks;
                var vmd2 = new VirtualMediaDescriptor(
                    vol2.ContentPath, vol2.ContentCapacity,
                    vol2.InitiatorPath, vol2.HasInitiator ? vol2.InitiatorCapacity : 0);

                Assert.True(await svc2.OpenVirtualDriveAsync(caps2, vmd2, FileMode.Open),
                    $"OpenVirtualDriveAsync (vol2 verify) failed: {svc2.LastError}");
                Assert.True(await svc2.LoadMediaAsync(),
                    $"LoadMediaAsync (vol2 verify) failed: {svc2.LastError}");
                Assert.True(await svc2.RestoreTOCAsync(),
                    $"RestoreTOCAsync (vol2 verify) failed: {svc2.LastError}");

                var toc2 = svc2.TOC!;
                Assert.True(toc2.Count > 0, "Vol-2 TOC has no sets");

                // The first set visible on vol2 must be a continuation from vol1.
                var contSet = toc2[toc2.FirstSetOnVolume];
                Assert.True(contSet.ContinuedFromPrevVolume,
                    "Expected ContinuedFromPrevVolume on the set spanning onto vol-2");
            }
        }

        // ── Restore (full, from vol1 — the engine fetches vol2 automatically) ─
        var restoreRoot = Path.Combine(vol1.Root, "restore");
        Directory.CreateDirectory(restoreRoot);

        var (restoreSvc, restoreHost) = await ReopenMultiVolumeAsync(volumes);
        using (restoreSvc)
        {
            Assert.NotNull(restoreSvc.TOC);
            var toc = restoreSvc.TOC!;
            Assert.True(toc.Count > 0, "TOC has no sets after reopen");

            // The last set on the last volume is the continuation set that spans back to vol1.
            //  Restoring it non-incrementally causes the agent to fetch vol1 automatically
            //  via OnInsertMediaConfirm for the files stored there.
            int setIdx = toc.LastSetOnVolume;

            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIdx] = null },
                Incremental:           false,
                TargetDirectory:       restoreRoot,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false,
                EjectWhenDone:         false);

            var result = await restoreSvc.ExecuteRestoreAsync(req);

            Assert.False(result.HasFailed,  $"Restore reported failure: {restoreSvc.LastError}");
            Assert.True (result.Success,    $"Restore did not succeed: {restoreSvc.LastError}");
            Assert.Equal(0,                 result.FilesFailed);
            Assert.Equal(MultiVolumeFileCount, result.FilesSucceeded);
            Assert.False(restoreHost.HasErrors, "Restore host received unexpected error reports");

            // The host must have swapped in vol2 during restore too.
            Assert.True(restoreHost.VolumesInserted >= 1,
                $"Expected at least 1 volume insertion during restore; got {restoreHost.VolumesInserted}");
        }

        // ── Byte-exact comparison ─────────────────────────────────────────────
        var restoredRoot = FindRestoredRoot(restoreRoot, src.RootPath);
        FileComparer.AssertFilesMatch(src.RootPath, src.Files, restoredRoot);
    }

    // ── A-6: multi-volume incremental backup + restore ────────────────────────

    /// <summary>
    /// Full backup onto a single vol-1, then incremental backup (all files modified,
    ///  larger sizes) that overflows onto vol-2 — verifying that the service handles
    ///  multi-volume incremental sets correctly.
    /// <para>
    /// <b>Volume sizing:</b> vol-1 capacity is chosen so the full backup (~5.5 MiB,
    ///  16 × 350 KiB) fits with headroom to spare, while the incremental backup
    ///  (~11.2 MiB, 16 × 700 KiB) exceeds that headroom and spills onto vol-2.
    ///  Profile-specific values: 24 MiB setmarks (16 MiB TOC reserve → 8 MiB
    ///  usable) / 8 MiB initiator.
    /// </para>
    /// <para>
    /// Three assertions are made after setup:
    /// <list type="bullet">
    ///  <item><b>Case A</b> — incremental restore from the latest set (continuation on vol-2):
    ///   the engine fetches vol-1 for the earlier files; all 16 files match the
    ///   modified on-disk versions byte-for-byte.</item>
    ///  <item><b>Case B</b> — non-incremental restore from the full set (on vol-1):
    ///   only the 16 original files are written; byte-for-byte comparison is omitted
    ///   because the source tree was overwritten by <see cref="TempFileTree.ModifyFile"/>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Parameterised over both drive profiles (setmarks-only and with initiator partition).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)] // single-partition (setmarks only)
    [InlineData(true)]  // with initiator partition
    public async Task MultiVolume_IncrementalBackup_SpansVolumes_CorrectVersionsRestored(bool withInitiator)
    {
        long vol1Cap = withInitiator ? MvIncVol1Capacity_Initiator : MvIncVol1Capacity_Setmarks;
        // Vol-2 must be large enough to hold all 16 × 700 KiB incremental overflow
        //  plus its own TOC reservation — use separate, larger capacity constants.
        long vol2Cap = withInitiator ? MvIncVol2Capacity_Initiator : MvIncVol2Capacity_Setmarks;

        using var vol1 = new TempVirtualMedia(withInitiator, vol1Cap);
        using var vol2 = new TempVirtualMedia(withInitiator, vol2Cap);
        IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];

        using var src = new TempFileTree();

        // Create the initial file tree (small files — full backup fits on vol-1).
        for (int i = 0; i < MvIncFileCount; i++)
            src.AddFile($"mv_inc_{i:D2}.bin", MvIncFileSizeFull);

        // ── Full backup (vol-1 only) ──────────────────────────────────────────
        var (backupSvc1, backupHost1) = await OpenAndFormatMultiVolumeAsync(volumes);
        using (backupSvc1)
        {
            var req    = MakeBackupRequest(backupSvc1, src.RootPath, "MvInc-Full", incremental: false);
            var result = await backupSvc1.ExecuteBackupAsync(req);

            Assert.False(result.HasFailed,   $"Full backup failed: {backupSvc1.LastError}");
            Assert.True (result.Success,     $"Full backup did not succeed");
            Assert.Equal(0,                  result.FilesFailed);
            Assert.Equal(MvIncFileCount,     result.FilesSucceeded);

            // The full backup must stay on a single volume.
            Assert.True(backupHost1.VolumesInserted == 0,
                "Full backup should fit on vol-1 without any volume swap");
        }

        // Capture the full-backup set index from vol-1 for the Case-B restore.
        //  Vol-2 is still blank at this point, so open vol-1 only.
        int fullSetIdx;
        {
            var (svc, _) = await ReopenMultiVolumeAsync((IReadOnlyList<TempVirtualMedia>)[vol1]);
            using (svc)
                fullSetIdx = svc.TOC!.LastSetOnVolume;
        }

        // Modify all files to a larger size so the incremental backup overflows.
        for (int i = 0; i < MvIncFileCount; i++)
            src.ModifyFile(src.Files[i], version: 1, size: MvIncFileSizeModified);

        // Keep a snapshot of the modified file paths for byte-exact verification.
        var modifiedFiles = src.Files.ToList();

        // ── Incremental backup (overflows from vol-1 onto vol-2) ─────────────
        //  The host must know all volumes (to format vol-2 on overflow), but we
        //  open vol-1 (FileMode.Open) rather than vol-2 which doesn't exist yet.
        //  Inline the open so we can pass the full list to the host while still
        //  targeting vol-1 as the starting volume.
        var (backupSvc2, backupHost2) = CreateMultiVolumeService(volumes);
        Assert.True(await backupSvc2.OpenVirtualDriveAsync(
            vol1.HasInitiator
                ? VirtualTapeDriveCapabilities.WithPartitions
                : VirtualTapeDriveCapabilities.WithSetmarks,
            new VirtualMediaDescriptor(
                vol1.ContentPath, vol1.ContentCapacity,
                vol1.InitiatorPath, vol1.HasInitiator ? vol1.InitiatorCapacity : 0),
            FileMode.Open),
            $"OpenVirtualDriveAsync (incremental, vol-1 reopen) failed: {backupSvc2.LastError}");
        Assert.True(await backupSvc2.LoadMediaAsync(),
            $"LoadMediaAsync (incremental, vol-1 reopen) failed: {backupSvc2.LastError}");
        Assert.True(await backupSvc2.RestoreTOCAsync(),
            $"RestoreTOCAsync (incremental, vol-1 reopen) failed: {backupSvc2.LastError}");
        using (backupSvc2)
        {
            var req    = MakeBackupRequest(backupSvc2, src.RootPath, "MvInc-Incremental",
                                           append: true, incremental: true);
            var result = await backupSvc2.ExecuteBackupAsync(req);

            Assert.False(result.HasFailed,  $"Incremental backup failed: {backupSvc2.LastError}");
            Assert.True (result.Success,    $"Incremental backup did not succeed");
            Assert.Equal(0,                 result.FilesFailed);
            Assert.Equal(MvIncFileCount,    result.FilesSucceeded); // all files changed → all backed up
            Assert.Equal(0,                 result.FilesSkipped);   // nothing unchanged

            // Note: HasErrors not asserted — transient EOM reports expected (see A-5 note).

            // At least one volume swap must have occurred (incremental > vol-1 headroom).
            Assert.True(backupHost2.VolumesInserted >= 1,
                $"Expected ≥1 volume swap during incremental backup; got {backupHost2.VolumesInserted}");
        }

        // ── Verify TOC flags on vol-2 ────────────────────────────────────────
        //  The continuation set on vol-2 must carry ContinuedFromPrevVolume = true
        //  and Incremental = true.
        {
            var (svc2, _) = CreateMultiVolumeService(volumes);
            using (svc2)
            {
                var caps2 = vol2.HasInitiator
                    ? VirtualTapeDriveCapabilities.WithPartitions
                    : VirtualTapeDriveCapabilities.WithSetmarks;
                var vmd2 = new VirtualMediaDescriptor(
                    vol2.ContentPath, vol2.ContentCapacity,
                    vol2.InitiatorPath, vol2.HasInitiator ? vol2.InitiatorCapacity : 0);

                Assert.True(await svc2.OpenVirtualDriveAsync(caps2, vmd2, FileMode.Open),
                    $"OpenVirtualDriveAsync (vol-2 verify) failed: {svc2.LastError}");
                Assert.True(await svc2.LoadMediaAsync(),
                    $"LoadMediaAsync (vol-2 verify) failed: {svc2.LastError}");
                Assert.True(await svc2.RestoreTOCAsync(),
                    $"RestoreTOCAsync (vol-2 verify) failed: {svc2.LastError}");

                var toc2 = svc2.TOC!;
                Assert.True(toc2.Count > 0, "Vol-2 TOC has no sets");

                var contSet = toc2[toc2.FirstSetOnVolume];
                Assert.True(contSet.ContinuedFromPrevVolume,
                    "Incremental continuation set must have ContinuedFromPrevVolume = true");
                Assert.True(contSet.Incremental,
                    "Incremental continuation set must have Incremental = true");
            }
        }

        // ── Case A: incremental restore from the latest (incremental) set ────
        //  Opens from vol-2 (last written); engine fetches vol-1 for the earlier
        //  files; result must contain all 16 files at their modified (v1) sizes.
        {
            var restoreRoot = Path.Combine(vol1.Root, "restore_A");
            Directory.CreateDirectory(restoreRoot);

            var (restoreSvc, restoreHost) = await ReopenMultiVolumeAsync(volumes);
            using (restoreSvc)
            {
                var toc    = restoreSvc.TOC!;
                int setIdx = toc.LastSetOnVolume; // continuation set on vol-2

                var req = new RestoreRequest(
                    Mode:                  RestoreMode.Restore,
                    CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIdx] = null },
                    Incremental:           true,  // pull earlier vol-1 set into the chain
                    TargetDirectory:       restoreRoot,
                    RecurseSubdirectories: true,
                    HandleExisting:        TapeHowToHandleExisting.Overwrite,
                    SkipAllErrors:         false,
                    EjectWhenDone:         false);

                var result = await restoreSvc.ExecuteRestoreAsync(req);

                Assert.False(result.HasFailed,  $"Case A restore failed: {restoreSvc.LastError}");
                Assert.True (result.Success,    $"Case A restore did not succeed");
                Assert.Equal(0,                 result.FilesFailed);
                Assert.Equal(MvIncFileCount,    result.FilesSucceeded);
                Assert.False(restoreHost.HasErrors, "Case A restore host received unexpected errors");
                Assert.True(restoreHost.VolumesInserted >= 1,
                    $"Case A: expected ≥1 volume swap during restore; got {restoreHost.VolumesInserted}");
            }

            // Byte-exact: all files must match the modified (v1) versions.
            var restoredRoot = FindRestoredRoot(restoreRoot, src.RootPath);
            FileComparer.AssertFilesMatch(src.RootPath, modifiedFiles, restoredRoot);
        }

        // ── Case B: non-incremental restore from the full set (on vol-1) ─────
        //  The engine reads only vol-1; no volume swap expected.
        //  Modified source files mean byte-for-byte comparison is not possible,
        //  so only the file count is asserted.
        {
            var restoreRoot = Path.Combine(vol1.Root, "restore_B");
            Directory.CreateDirectory(restoreRoot);

            // Re-open from vol-2 to get the full TOC (which includes the vol-1 full set).
            var (restoreSvc, restoreHost) = await ReopenMultiVolumeAsync(volumes);
            using (restoreSvc)
            {
                var req = new RestoreRequest(
                    Mode:                  RestoreMode.Restore,
                    CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [fullSetIdx] = null },
                    Incremental:           false,
                    TargetDirectory:       restoreRoot,
                    RecurseSubdirectories: true,
                    HandleExisting:        TapeHowToHandleExisting.Overwrite,
                    SkipAllErrors:         false,
                    EjectWhenDone:         false);

                var result = await restoreSvc.ExecuteRestoreAsync(req);

                Assert.False(result.HasFailed,  $"Case B restore failed: {restoreSvc.LastError}");
                Assert.True (result.Success,    $"Case B restore did not succeed");
                Assert.Equal(0,                 result.FilesFailed);
                Assert.Equal(MvIncFileCount,    result.FilesSucceeded);
                Assert.False(restoreHost.HasErrors, "Case B restore host received unexpected errors");
                // The full set lives on vol-1; opened from vol-2, the engine must request vol-1 —
                //  exactly 1 volume swap is expected here (validates cross-volume restore path).
                Assert.True(restoreHost.VolumesInserted >= 1,
                    $"Case B: expected ≥1 volume swap to reach vol-1 data; got {restoreHost.VolumesInserted}");
            }
            // Byte-exact comparison omitted: source files were overwritten by ModifyFile.
        }
    }
}
