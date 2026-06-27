using System.IO;

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia

namespace TapeLibNET.Tests.Services.Remote;

/// <summary>
/// Remote selective-restore tests: restoring a hand-picked subset of
///  <see cref="TapeFileInfo"/> entries from one or more backup sets, over a gRPC backend.
/// <para>
/// Mirrors <see cref="ServiceSelectiveRestoreTests"/> but routes all drive I/O through
///  the in-process <see cref="LocalHostTapeServiceFixture"/> gRPC server.
///  Covers the same plan items (A-3 and A-4) as the local selective-restore suite.
/// </para>
/// </summary>
[Collection(LocalHostTapeServiceCollection.Name)]
public class RemoteServiceSelectiveRestoreTests(LocalHostTapeServiceFixture fixture)
    : RemoteServiceTestBase(fixture)
{
    /// <summary>
    /// Verifies that a remote restore honours an explicit <see cref="TapeFileInfo"/> list
    ///  passed via <see cref="RestoreRequest.CheckedFilesBySet"/>: only the nominated
    ///  files are extracted, leaving all others absent from the restore directory.
    /// <para>
    /// Setup: two backup sets (6 files each). From set 1 restore 3 specific files;
    ///  from set 2 restore 2 specific files. Assert that exactly 5 files land in the
    ///  restore directory and that byte content matches the originals.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Remote_SelectiveRestore_SpecificFiles_ByTapeFileInfo()
    {
        using var media      = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src1       = new TempFileTree();
        using var src2       = new TempFileTree();
        const int FilesPerSet  = 6;
        const int PickFromSet1 = 3;
        const int PickFromSet2 = 2;

        src1.AddFiles("sel1", count: FilesPerSet, minSize: 1_024, maxSize: 8_192);
        src2.AddFiles("sel2", count: FilesPerSet, minSize: 1_024, maxSize: 8_192);

        // ── Backup set 1 ──────────────────────────────────────────────────────
        var (svc1, _) = await OpenAndFormatRemoteAsync(media);
        using (svc1)
        {
            var r = await svc1.ExecuteBackupAsync(MakeBackupRequest(svc1, src1.RootPath, "Sel-1"));
            Assert.True(r.Success, "Backup of Sel-1 failed");
        }

        // ── Backup set 2 (append) ─────────────────────────────────────────────
        var (svc2, _) = await ReopenRemoteAsync(media);
        using (svc2)
        {
            var r = await svc2.ExecuteBackupAsync(
                MakeBackupRequest(svc2, src2.RootPath, "Sel-2", append: true));
            Assert.True(r.Success, "Backup of Sel-2 failed");
        }

        // ── Selective restore ─────────────────────────────────────────────────
        var restoreRoot = Path.Combine(media.Root, "restore_sel");
        Directory.CreateDirectory(restoreRoot);

        var (svcR, hostR) = await ReopenRemoteAsync(media);
        using (svcR)
        {
            var toc = svcR.TOC!;
            int s1  = toc.FirstSetOnVolume;
            int s2  = toc.LastSetOnVolume;

            IReadOnlyList<TapeFileInfo> sel1 = [.. toc[s1].Take(PickFromSet1)];
            IReadOnlyList<TapeFileInfo> sel2 = [.. toc[s2].Take(PickFromSet2)];

            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?>
                                           { [s1] = sel1, [s2] = sel2 },
                Incremental:           false,
                TargetDirectory:       restoreRoot,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false,
                EjectWhenDone:         false);

            var result = await svcR.ExecuteRestoreAsync(req);

            Assert.True(result.Success, "Selective restore failed");
            Assert.Equal(0,                           result.FilesFailed);
            Assert.Equal(PickFromSet1 + PickFromSet2, result.FilesSucceeded);
            Assert.False(hostR.HasErrors, "Selective restore host received unexpected errors");
        }

        // ── Byte comparison for the selected files ────────────────────────────
        FileComparer.AssertFilesMatch(
            src1.RootPath,
            [.. src1.Files.Take(PickFromSet1)],
            FindRestoredRoot(restoreRoot, src1.RootPath));

        FileComparer.AssertFilesMatch(
            src2.RootPath,
            [.. src2.Files.Take(PickFromSet2)],
            FindRestoredRoot(restoreRoot, src2.RootPath));
    }

    /// <summary>
    /// Verifies that a single remote <see cref="RestoreRequest"/> can select specific
    ///  files from two different backup sets simultaneously.
    /// <para>
    /// Setup: two backup sets (8 files each). From set 1 pick files at even indexes
    ///  (4 files); from set 2 pick files at odd indexes (4 files). Both selections
    ///  are placed into a single <see cref="RestoreRequest.CheckedFilesBySet"/> dictionary
    ///  and restored in one call. Assert exactly 8 files restored, byte-exact.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Remote_SelectiveRestore_AcrossMultipleSets_MixedSelection()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src1  = new TempFileTree();
        using var src2  = new TempFileTree();
        const int FilesPerSet = 8;

        src1.AddFiles("mix1", count: FilesPerSet, minSize: 1_024, maxSize: 8_192);
        src2.AddFiles("mix2", count: FilesPerSet, minSize: 1_024, maxSize: 8_192);

        // ── Backup set 1 ──────────────────────────────────────────────────────
        var (svc1, _) = await OpenAndFormatRemoteAsync(media);
        using (svc1)
        {
            var r = await svc1.ExecuteBackupAsync(MakeBackupRequest(svc1, src1.RootPath, "Mix-1"));
            Assert.True(r.Success, "Backup of Mix-1 failed");
        }

        // ── Backup set 2 (append) ─────────────────────────────────────────────
        var (svc2, _) = await ReopenRemoteAsync(media);
        using (svc2)
        {
            var r = await svc2.ExecuteBackupAsync(
                MakeBackupRequest(svc2, src2.RootPath, "Mix-2", append: true));
            Assert.True(r.Success, "Backup of Mix-2 failed");
        }

        // ── Mixed selective restore ───────────────────────────────────────────
        var restoreRoot = Path.Combine(media.Root, "restore_mix");
        Directory.CreateDirectory(restoreRoot);

        List<string> expectedSrc1Files;
        List<string> expectedSrc2Files;

        var (svcR, hostR) = await ReopenRemoteAsync(media);
        using (svcR)
        {
            var toc = svcR.TOC!;
            int s1  = toc.FirstSetOnVolume;
            int s2  = toc.LastSetOnVolume;

            IReadOnlyList<TapeFileInfo> sel1 =
                [.. toc[s1].Where((_, i) => i % 2 == 0)];
            IReadOnlyList<TapeFileInfo> sel2 =
                [.. toc[s2].Where((_, i) => i % 2 == 1)];

            expectedSrc1Files = [.. src1.Files.Where((_, i) => i % 2 == 0)];
            expectedSrc2Files = [.. src2.Files.Where((_, i) => i % 2 == 1)];

            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?>
                                           { [s1] = sel1, [s2] = sel2 },
                Incremental:           false,
                TargetDirectory:       restoreRoot,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false,
                EjectWhenDone:         false);

            var result = await svcR.ExecuteRestoreAsync(req);

            Assert.True(result.Success, "Mixed selective restore failed");
            Assert.Equal(0,                       result.FilesFailed);
            Assert.Equal(sel1.Count + sel2.Count, result.FilesSucceeded);
            Assert.False(hostR.HasErrors, "Mixed restore host received unexpected errors");
        }

        // ── Byte comparison for both selection halves ─────────────────────────
        FileComparer.AssertFilesMatch(
            src1.RootPath, expectedSrc1Files,
            FindRestoredRoot(restoreRoot, src1.RootPath));

        FileComparer.AssertFilesMatch(
            src2.RootPath, expectedSrc2Files,
            FindRestoredRoot(restoreRoot, src2.RootPath));
    }
}
