using System.IO;

using TapeLibNET;
using TapeLibNET.Remote;
using TapeLibNET.Services;
using TapeLibNET.Virtual;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia, RemoteMultiVolumeServiceHost

namespace TapeLibNET.Tests.Services.Remote;

/// <summary>
/// Remote multi-volume backup and restore tests: automatic volume swapping,
///  TOC continuation flags, and incremental sets that span volume boundaries,
///  all over a gRPC backend.
/// <para>
/// Mirrors <see cref="ServiceMultiVolumeTests"/> but routes all drive I/O through
///  the in-process <see cref="LocalHostTapeServiceFixture"/> gRPC server.
///  Volume swaps are serviced by <see cref="RemoteMultiVolumeServiceHost"/> via the
///  <c>InsertMedia</c> gRPC RPC instead of the local <c>InsertVirtualMedia</c> call.
///  Covers the same plan items (A-5 and A-6) as the local multi-volume suite.
/// </para>
/// </summary>
[Collection(LocalHostTapeServiceCollection.Name)]
public class RemoteServiceMultiVolumeTests(LocalHostTapeServiceFixture fixture)
    : RemoteServiceTestBase(fixture)
{
    // ── A-5 constants ─────────────────────────────────────────────────────────

    /// <summary>Number of files written by <see cref="AddMultiVolumeContent"/>.</summary>
    private const int MultiVolumeFileCount = 16;

    /// <summary>Size of each file: 350 KiB.</summary>
    private const long MultiVolumeFileSize = 350L * 1024;

    /// <summary>
    /// Content-partition capacity for setmarks multi-volume test volumes (20 MiB).
    ///  TOC reserve is 16 MiB → 4 MiB usable; total content ~5.6 MiB overflows trivially.
    /// </summary>
    private const long MultiVolumeCapacity_Setmarks = 20L * 1024 * 1024;

    /// <summary>
    /// Content-partition capacity for initiator-partition multi-volume test volumes (3 MiB).
    ///  TOC lives in the initiator partition; full content space available.
    /// </summary>
    private const long MultiVolumeCapacity_Initiator = 3L * 1024 * 1024;

    // ── A-6 constants ─────────────────────────────────────────────────────────

    private const int  MvIncFileCount            = 16;
    private const long MvIncFileSizeFull          = 350L * 1024;
    private const long MvIncFileSizeModified      = 700L * 1024;
    private const long MvIncVol1Capacity_Setmarks = 24L * 1024 * 1024;
    private const long MvIncVol1Capacity_Initiator = 8L * 1024 * 1024;
    private const long MvIncVol2Capacity_Setmarks = 30L * 1024 * 1024;
    private const long MvIncVol2Capacity_Initiator = 14L * 1024 * 1024;

    // ── Content helper ────────────────────────────────────────────────────────

    private static void AddMultiVolumeContent(TempFileTree tree)
    {
        for (int i = 0; i < MultiVolumeFileCount; i++)
            tree.AddFile($"mv_file_{i:D2}.bin", MultiVolumeFileSize);
    }

    // ── A-5: multi-volume regular backup + restore ────────────────────────────

    /// <summary>
    /// Writes <see cref="MultiVolumeFileCount"/> files (350 KiB each, ~5.6 MiB total)
    ///  to a two-volume remote media set via gRPC, verifying that the backup engine spans
    ///  volumes automatically and that a full restore recovers every byte intact.
    /// <para>
    /// The same sizing rationale as <see cref="ServiceMultiVolumeTests"/> applies; both
    ///  drive profiles are parameterised.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)] // single-partition (setmarks only)
    [InlineData(true)]  // with initiator partition
    public async Task Remote_MultiVolume_RegularBackup_SpansVolumes_RestoreAllFiles(bool withInitiator)
    {
        long volumeCapacity = withInitiator ? MultiVolumeCapacity_Initiator : MultiVolumeCapacity_Setmarks;
        using var vol1 = new TempVirtualMedia(withInitiator, volumeCapacity);
        using var vol2 = new TempVirtualMedia(withInitiator, volumeCapacity);
        IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];

        using var src = new TempFileTree();
        AddMultiVolumeContent(src);

        // ── Backup ────────────────────────────────────────────────────────────
        var (backupSvc, backupHost) = await OpenAndFormatRemoteMultiVolumeAsync(volumes);
        using (backupSvc)
        {
            var req    = MakeBackupRequest(backupSvc, src.RootPath, "MultiVol-Set-1");
            var result = await backupSvc.ExecuteBackupAsync(req);

            Assert.False(result.WasAborted,  "Backup was unexpectedly aborted");
            Assert.False(result.HasFailed,   $"Backup reported failure: {backupSvc.LastError}");
            Assert.True (result.Success,     $"Backup did not succeed: {backupSvc.LastError}");
            Assert.Equal(0,                  result.FilesFailed);
            Assert.Equal(MultiVolumeFileCount, result.FilesSucceeded);

            // Note: HasErrors is intentionally not asserted — transient EOM reports are expected.
            Assert.True(backupHost.VolumesInserted >= 1,
                $"Expected at least 1 volume insertion during backup; got {backupHost.VolumesInserted}");
        }

        // ── Verify TOC flags on vol-2 ─────────────────────────────────────────
        {
            var (svc2, _) = CreateRemoteMultiVolumeService(volumes);
            using (svc2)
            {
                var caps2 = vol2.HasInitiator
                    ? VirtualTapeDriveCapabilities.WithPartitions
                    : VirtualTapeDriveCapabilities.WithSetmarks;

                Assert.True(
                    await svc2.OpenRemoteVirtualFileAsync(
                        RemoteSettings, vol2.ToVmd(), caps2),
                    $"OpenRemoteVirtualFileAsync (vol2 verify) failed: {svc2.LastError}");
                Assert.True(await svc2.LoadMediaAsync(),
                    $"LoadMediaAsync (vol2 verify) failed: {svc2.LastError}");
                Assert.True(await svc2.RestoreTOCAsync(),
                    $"RestoreTOCAsync (vol2 verify) failed: {svc2.LastError}");

                var toc2 = svc2.TOC!;
                Assert.True(toc2.Count > 0, "Vol-2 TOC has no sets");

                var contSet = toc2[toc2.FirstSetOnVolume];
                Assert.True(contSet.ContinuedFromPrevVolume,
                    "Expected ContinuedFromPrevVolume on the set spanning onto vol-2");
            }
        }

        // ── Restore (full, from vol-2 — engine fetches vol-1 automatically) ──
        var restoreRoot = Path.Combine(vol1.Root, "restore");
        Directory.CreateDirectory(restoreRoot);

        var (restoreSvc, restoreHost) = await ReopenRemoteMultiVolumeAsync(volumes);
        using (restoreSvc)
        {
            Assert.NotNull(restoreSvc.TOC);
            var toc = restoreSvc.TOC!;
            Assert.True(toc.Count > 0, "TOC has no sets after reopen");

            int setIdx = toc.LastSetOnVolume;

            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIdx] = null },
                Incremental:           false,
                TargetDirectory:       restoreRoot,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false);

            var result = await restoreSvc.ExecuteRestoreAsync(req);

            Assert.False(result.HasFailed,  $"Restore reported failure: {restoreSvc.LastError}");
            Assert.True (result.Success,    $"Restore did not succeed: {restoreSvc.LastError}");
            Assert.Equal(0,                 result.FilesFailed);
            Assert.Equal(MultiVolumeFileCount, result.FilesSucceeded);
            Assert.False(restoreHost.HasErrors, "Restore host received unexpected error reports");
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
    ///  larger sizes) that overflows onto vol-2 — all over a gRPC backend.
    /// <para>
    /// Mirrors <see cref="ServiceMultiVolumeTests.MultiVolume_IncrementalBackup_SpansVolumes_CorrectVersionsRestored"/>:
    ///  same three assertions (backup statistics, TOC flags, Case-A incremental restore,
    ///  Case-B non-incremental restore). Both drive profiles are parameterised.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)] // single-partition (setmarks only)
    [InlineData(true)]  // with initiator partition
    public async Task Remote_MultiVolume_IncrementalBackup_SpansVolumes_CorrectVersionsRestored(bool withInitiator)
    {
        long vol1Cap = withInitiator ? MvIncVol1Capacity_Initiator : MvIncVol1Capacity_Setmarks;
        long vol2Cap = withInitiator ? MvIncVol2Capacity_Initiator : MvIncVol2Capacity_Setmarks;

        using var vol1 = new TempVirtualMedia(withInitiator, vol1Cap);
        using var vol2 = new TempVirtualMedia(withInitiator, vol2Cap);
        IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];

        using var src = new TempFileTree();

        for (int i = 0; i < MvIncFileCount; i++)
            src.AddFile($"mv_inc_{i:D2}.bin", MvIncFileSizeFull);

        // ── Full backup (vol-1 only) ──────────────────────────────────────────
        var (backupSvc1, backupHost1) = await OpenAndFormatRemoteMultiVolumeAsync(volumes);
        using (backupSvc1)
        {
            var req    = MakeBackupRequest(backupSvc1, src.RootPath, "MvInc-Full", incremental: false);
            var result = await backupSvc1.ExecuteBackupAsync(req);

            Assert.False(result.HasFailed,  $"Full backup failed: {backupSvc1.LastError}");
            Assert.True (result.Success,    "Full backup did not succeed");
            Assert.Equal(0,                 result.FilesFailed);
            Assert.Equal(MvIncFileCount,    result.FilesSucceeded);
            Assert.True(backupHost1.VolumesInserted == 0,
                "Full backup should fit on vol-1 without any volume swap");
        }

        // Capture the full-backup set index from vol-1 for the Case-B restore.
        int fullSetIdx;
        {
            var (svc, _) = await ReopenRemoteAsync(vol1);
            using (svc)
                fullSetIdx = svc.TOC!.LastSetOnVolume;
        }

        // Modify all files to a larger size so the incremental backup overflows.
        for (int i = 0; i < MvIncFileCount; i++)
            src.ModifyFile(src.Files[i], version: 1, size: MvIncFileSizeModified);

        var modifiedFiles = src.Files.ToList();

        // ── Incremental backup (overflows from vol-1 onto vol-2) ─────────────
        //  Open vol-1 with FileMode.Open so the TOC can be restored before appending.
        var caps = vol1.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        var (backupSvc2, backupHost2) = CreateRemoteMultiVolumeService(volumes);
        Assert.True(
            await backupSvc2.OpenRemoteVirtualFileAsync(
                RemoteSettings, vol1.ToVmd(), caps),
            $"OpenRemoteVirtualFileAsync (incremental, vol-1 reopen) failed: {backupSvc2.LastError}");
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
            Assert.True (result.Success,    "Incremental backup did not succeed");
            Assert.Equal(0,                 result.FilesFailed);
            Assert.Equal(MvIncFileCount,    result.FilesSucceeded);
            Assert.Equal(0,                 result.FilesSkipped);

            // Note: HasErrors not asserted — transient EOM reports expected.
            Assert.True(backupHost2.VolumesInserted >= 1,
                $"Expected ≥1 volume swap during incremental backup; got {backupHost2.VolumesInserted}");
        }

        // ── Verify TOC flags on vol-2 ────────────────────────────────────────
        {
            var (svc2, _) = CreateRemoteMultiVolumeService(volumes);
            using (svc2)
            {
                var caps2 = vol2.HasInitiator
                    ? VirtualTapeDriveCapabilities.WithPartitions
                    : VirtualTapeDriveCapabilities.WithSetmarks;

                Assert.True(
                    await svc2.OpenRemoteVirtualFileAsync(
                        RemoteSettings, vol2.ToVmd(), caps2),
                    $"OpenRemoteVirtualFileAsync (vol-2 verify) failed: {svc2.LastError}");
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
        {
            var restoreRoot = Path.Combine(vol1.Root, "restore_A");
            Directory.CreateDirectory(restoreRoot);

            var (restoreSvc, restoreHost) = await ReopenRemoteMultiVolumeAsync(volumes);
            using (restoreSvc)
            {
                var toc    = restoreSvc.TOC!;
                int setIdx = toc.LastSetOnVolume;

                var req = new RestoreRequest(
                    Mode:                  RestoreMode.Restore,
                    CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIdx] = null },
                    Incremental:           true,
                    TargetDirectory:       restoreRoot,
                    RecurseSubdirectories: true,
                    HandleExisting:        TapeHowToHandleExisting.Overwrite,
                    SkipAllErrors:         false);

                var result = await restoreSvc.ExecuteRestoreAsync(req);

                Assert.False(result.HasFailed,  $"Case A restore failed: {restoreSvc.LastError}");
                Assert.True (result.Success,    "Case A restore did not succeed");
                Assert.Equal(0,                 result.FilesFailed);
                Assert.Equal(MvIncFileCount,    result.FilesSucceeded);
                Assert.False(restoreHost.HasErrors, "Case A restore host received unexpected errors");
                Assert.True(restoreHost.VolumesInserted >= 1,
                    $"Case A: expected ≥1 volume swap during restore; got {restoreHost.VolumesInserted}");
            }

            var restoredRoot = FindRestoredRoot(restoreRoot, src.RootPath);
            FileComparer.AssertFilesMatch(src.RootPath, modifiedFiles, restoredRoot);
        }

        // ── Case B: non-incremental restore from the full set (on vol-1) ─────
        {
            var restoreRoot = Path.Combine(vol1.Root, "restore_B");
            Directory.CreateDirectory(restoreRoot);

            var (restoreSvc, restoreHost) = await ReopenRemoteMultiVolumeAsync(volumes);
            using (restoreSvc)
            {
                var req = new RestoreRequest(
                    Mode:                  RestoreMode.Restore,
                    CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [fullSetIdx] = null },
                    Incremental:           false,
                    TargetDirectory:       restoreRoot,
                    RecurseSubdirectories: true,
                    HandleExisting:        TapeHowToHandleExisting.Overwrite,
                    SkipAllErrors:         false);

                var result = await restoreSvc.ExecuteRestoreAsync(req);

                Assert.False(result.HasFailed,  $"Case B restore failed: {restoreSvc.LastError}");
                Assert.True (result.Success,    "Case B restore did not succeed");
                Assert.Equal(0,                 result.FilesFailed);
                Assert.Equal(MvIncFileCount,    result.FilesSucceeded);
                Assert.False(restoreHost.HasErrors, "Case B restore host received unexpected errors");
                Assert.True(restoreHost.VolumesInserted >= 1,
                    $"Case B: expected ≥1 volume swap to reach vol-1 data; got {restoreHost.VolumesInserted}");
            }
            // Byte-exact comparison omitted: source files were overwritten by ModifyFile.
        }
    }

    /// <summary>
    /// Capacity used for the catalog-driven test volumes: matches <see cref="MultiVolumeCapacity_Setmarks"/>
    /// (20 MiB) so that the setmarks TOC overhead (16 MiB) leaves 4 MiB of usable data space, forcing
    /// at least one volume swap when writing 16 × 350 KiB files (~5.6 MiB total).
    /// No initiator partition — <c>CreateTempVirtual</c> does not support one.
    /// </summary>
    private const long CatalogDrivenVolumeCapacity = 20L * 1024 * 1024;

    // ── 8.10: catalog-driven multi-volume backup + restore ────────────────────

    /// <summary>
    /// Verifies the production catalog-driven volume-swap path end-to-end:
    /// <list type="bullet">
    ///  <item><description>
    ///   Volume 1 is opened with <see cref="TapeServiceBase.CreateRemoteVirtualDriveAsync"/>
    ///    (named temp, the actual production entry point used by <c>OpenRemoteVirtualDriveAsync</c>).
    ///  </description></item>
    ///  <item><description>
    ///   A <see cref="CatalogDrivenRemoteServiceHost"/> services backup volume swaps by calling
    ///    <see cref="TapeServiceBase.InsertRemoteVirtualMedia"/> with a freshly-named VMD —
    ///    no pre-injected <see cref="TempVirtualMedia"/> list.
    ///  </description></item>
    ///  <item><description>
    ///   After backup, the session catalog (via <see cref="TapeServiceBase.ListRemoteSessionVolumesAsync"/>)
    ///    is asserted to contain both volumes with the correct <c>IsCurrent</c> flag and non-zero
    ///    <c>BytesWritten</c> on the completed first volume.
    ///  </description></item>
    ///  <item><description>
    ///   Restore re-uses the catalog paths; the catalog-driven host resolves volume swaps
    ///    by listing the catalog and picking by 1-based index — no pre-injected paths.
    ///  </description></item>
    /// </list>
    /// This test validates that <see cref="RemoteMultiVolumeServiceHost"/>'s shortcut
    /// (pre-injected volumes list) and the catalog-driven flow produce the same end state.
    /// <para>
    /// Uses setmarks only: <c>CreateTempVirtual</c> does not support initiator partitions.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Remote_MultiVolume_CatalogDriven_BackupAndRestore_RoundTrips()
    {
        long volumeCapacity = CatalogDrivenVolumeCapacity;

        // Shared temp directory for the backup host's new-volume files (server runs in-process).
        string tempDir = Path.Combine(Path.GetTempPath(), $"TapeNET_CatalogTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        // Setmarks only: CreateTempVirtual does not support initiator partitions.
        var caps = VirtualTapeDriveCapabilities.WithSetmarks;

        try
        {
            const string BaseVolumeName = "catalog_tape";

            // Build the vol-01 VMD: pass only the bare name as ContentPath.
            // CreateRemoteVirtualDriveAsync forwards this to CreateTempVirtualAsync(name: …),
            //  which the server turns into a temp-folder path: tapenet_tmp_{name}_{guid}.vtape.
            // The actual server-side path is read back from the catalog after backup.
            string vol01Name = $"{BaseVolumeName}_vol01";
            var vol01Vmd = new VirtualMediaDescriptor(
                ContentPath:                vol01Name,
                ContentCapacity:            volumeCapacity,
                InitiatorPath:              null,
                InitiatorPartitionCapacity: 0);

            // ── Backup ────────────────────────────────────────────────────────
            using var src = new TempFileTree();
            AddMultiVolumeContent(src);

            var backupHost = new CatalogDrivenRemoteServiceHost(
                BaseVolumeName, caps, volumeCapacity, tempDir);
            var backupSvc  = new TapeServiceBase(TestLoggerFactory.Default, backupHost);
            backupHost.Service = backupSvc;

            // Open vol-01 via the production CreateRemoteVirtualDriveAsync path
            Assert.True(
                await backupSvc.CreateRemoteVirtualDriveAsync(RemoteSettings, vol01Vmd, caps),
                $"CreateRemoteVirtualDriveAsync (vol-01) failed: {backupSvc.LastError}");
            Assert.True(
                await backupSvc.LoadMediaAsync(),
                $"LoadMediaAsync (vol-01) failed: {backupSvc.LastError}");

            long initSize = -1L; // setmarks only — no initiator partition
            Assert.True(
                await backupSvc.FormatMediaAsync(initSize, MediaName),
                $"FormatMediaAsync (vol-01) failed: {backupSvc.LastError}");

            BackupResult backupResult;
            IReadOnlyList<RemoteVirtualVolumeInfo> finalCatalog;
            using (backupSvc)
            {
                var req = MakeBackupRequest(backupSvc, src.RootPath, "CatalogDriven-Set-1");
                backupResult = await backupSvc.ExecuteBackupAsync(req);

                // ── Assert catalog state right after backup ────────────────────
                finalCatalog = await backupSvc.ListRemoteSessionVolumesAsync();

                Assert.True(finalCatalog.Count >= 2,
                    $"Expected ≥2 catalog entries after multi-volume backup; got {finalCatalog.Count}");

                // The last entry must be current; the first must have non-zero BytesWritten.
                Assert.True(finalCatalog[^1].IsCurrent,
                    "Last catalog entry should be flagged as current after backup");
                Assert.False(finalCatalog[0].IsCurrent,
                    "First catalog entry should no longer be current after volume swap");
                Assert.True(finalCatalog[0].BytesWritten > 0,
                    "First volume should have non-zero BytesWritten after being swapped out");

                // All catalog entries must carry the correct volume name prefix.
                Assert.All(finalCatalog, v =>
                    Assert.Contains(BaseVolumeName, v.Name, StringComparison.OrdinalIgnoreCase));
            }

            Assert.False(backupResult.WasAborted,  "Backup was unexpectedly aborted");
            Assert.False(backupResult.HasFailed,   $"Backup reported failure");
            Assert.True (backupResult.Success,     "Backup did not succeed");
            Assert.Equal(0,                        backupResult.FilesFailed);
            Assert.Equal(MultiVolumeFileCount,     backupResult.FilesSucceeded);
            Assert.True (backupHost.VolumesInserted >= 1,
                $"Expected ≥1 volume insertion during backup; got {backupHost.VolumesInserted}");

            // ── Restore (catalog-driven, fresh session) ───────────────────────
            // Re-open vol-01 (from catalog) and then swap to each subsequent volume to
            //  re-populate the new session's catalog.  This mirrors the production flow
            //  where the user picks a volume from the dialog and the service re-inserts it.
            // The catalog-driven host resolves volume swaps by listing the catalog.
            var restoreHost = new CatalogDrivenRemoteServiceHost(
                BaseVolumeName, caps, volumeCapacity, tempDir);
            var restoreSvc  = new TapeServiceBase(TestLoggerFactory.Default, restoreHost);
            restoreHost.Service = restoreSvc;

            // Open vol-01 from its actual server-side path (read from finalCatalog).
            var seedVol01 = finalCatalog[0].Media;
            Assert.True(
                await restoreSvc.OpenRemoteVirtualFileAsync(RemoteSettings, seedVol01, caps),
                $"OpenRemoteVirtualFileAsync (restore seed vol-01) failed: {restoreSvc.LastError}");

            // Seed remaining volumes into the session catalog so the host can look them up.
            for (int i = 1; i < finalCatalog.Count; i++)
            {
                var vol  = finalCatalog[i];
                var mode = System.IO.FileMode.Open;
                Assert.True(
                    await restoreSvc.InsertRemoteVirtualMediaAsync(vol.Media, vol.Capabilities, mode),
                    $"InsertRemoteVirtualMediaAsync (restore seed vol-{i + 1}) failed: {restoreSvc.LastError}");
            }

            Assert.True(
                await restoreSvc.LoadMediaAsync(),
                $"LoadMediaAsync (restore) failed: {restoreSvc.LastError}");
            Assert.True(
                await restoreSvc.RestoreTOCAsync(),
                $"RestoreTOCAsync (restore) failed: {restoreSvc.LastError}");

            var restoreRoot = Path.Combine(tempDir, "restore");
            Directory.CreateDirectory(restoreRoot);

            RestoreResult restoreResult;
            using (restoreSvc)
            {
                var toc    = restoreSvc.TOC!;
                int setIdx = toc.LastSetOnVolume;

                var req = new RestoreRequest(
                    Mode:                  RestoreMode.Restore,
                    CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIdx] = null },
                    Incremental:           false,
                    TargetDirectory:       restoreRoot,
                    RecurseSubdirectories: true,
                    HandleExisting:        TapeHowToHandleExisting.Overwrite,
                    SkipAllErrors:         false);

                restoreResult = await restoreSvc.ExecuteRestoreAsync(req);
            }

            Assert.False(restoreResult.HasFailed,  $"Restore reported failure");
            Assert.True (restoreResult.Success,    "Restore did not succeed");
            Assert.Equal(0,                        restoreResult.FilesFailed);
            Assert.Equal(MultiVolumeFileCount,     restoreResult.FilesSucceeded);
            Assert.False(restoreHost.HasErrors,    "Restore host received unexpected error reports");
            Assert.True (restoreHost.VolumesInserted >= 1,
                $"Expected ≥1 catalog-driven volume swap during restore; got {restoreHost.VolumesInserted}");

            // ── Byte-exact comparison ─────────────────────────────────────────
            var restoredRoot = FindRestoredRoot(restoreRoot, src.RootPath);
            FileComparer.AssertFilesMatch(src.RootPath, src.Files, restoredRoot);
        }
        finally
        {
            // Best-effort cleanup of temp files created by the server (in-process, same FS).
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignored */ }
        }
    }
}
