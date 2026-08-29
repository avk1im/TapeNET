using System.IO;
using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Mid-tape set replacement via <see cref="BackupRequest.AppendAfterSetIndex"/> — the service-level
/// regression guard for the stale-remaining / write-position-notification fix. Overwriting a set that sits
/// BEFORE the tape's end leaves the drive reporting <c>capacity − EOD</c> (stale, since a reposition does
/// not move EOD), under-stating true writable by the size of the discarded trailing set. Without
/// <see cref="TapeDrive.NotifyNextContentWritePosition"/> (armed by the backup agent for an overwrite), the
/// replacement set is clamped short — the "partial / zero files" bug. This test fails on revert of the fix.
/// </summary>
public class ServiceOverwriteTests : ServiceTestBase
{
    // capacity 64 MiB − 32 MiB fixed non-LTO TOC reserve = 32 MiB usable from BOM.
    private const long OverwriteCapacity = 64L * 1024 * 1024;
    private const int HalfMiB = 512 * 1024;

    private static void AddExact(TempFileTree tree, string name, int count) =>
        tree.AddFiles(name, count, minSize: HalfMiB, maxSize: HalfMiB + 1); // exactly 512 KiB each

    [Fact]
    public async Task Overwrite_MidTapeSet_ReplacesWithLargerSet_WritesAllFilesAndRestores()
    {
        using var media = new TempVirtualMedia(withInitiator: false, OverwriteCapacity);

        using var tree1 = new TempFileTree(seed: 301); AddExact(tree1, "set1", 4);  // 2 MiB  (kept)
        using var tree2 = new TempFileTree(seed: 302); AddExact(tree2, "set2", 4);  // 2 MiB  (kept)
        using var tree3 = new TempFileTree(seed: 303); AddExact(tree3, "set3", 24);  // 12 MiB (overwritten)
        using var treeNew = new TempFileTree(seed: 304); AddExact(treeNew, "new3", 40);  // 20 MiB (replacement)

        // ── Phase 1: three sequential sets → EOD ≈ 16 MiB. None near-full (usable 32 MiB from BOM). ──
        {
            var (svc, _) = await OpenAndFormatAsync(media); using (svc)
                Assert.Equal(tree1.Files.Count,
                    (await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree1.RootPath, "Set 1"))).FilesSucceeded);
        }

        {
            var (svc, _) = await ReopenAsync(media); using (svc)
                Assert.Equal(tree2.Files.Count,
                    (await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree2.RootPath, "Set 2", append: true))).FilesSucceeded);
        }

        {
            var (svc, _) = await ReopenAsync(media); using (svc)
                Assert.Equal(tree3.Files.Count,
                    (await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree3.RootPath, "Set 3", append: true))).FilesSucceeded);
        }

        // ── Phase 2: OVERWRITE everything after set 2 (i.e. replace set 3) with a LARGER set. ──
        //  Mode 1 (AppendAfterSetIndex strictly inside the volume): newSet=false ⇒ the agent arms
        //  NotifyNextContentWritePosition(size-before-set3 ≈ 4 MiB). Write start = 4 MiB, physical EOD = 16
        //  MiB, so reported (capacity−EOD = 48 MiB) UNDER-states true writable by set 3's 12 MiB. Without
        //  the notification, (48−32)=16 MiB usable would clamp the 20 MiB set to ~32 files; with it,
        //  (64−4−32)=28 MiB usable fits all 40. This assert is the regression guard.
        {
            var (svc, _) = await ReopenAsync(media);
            using (svc)
            {
                uint blockSize = svc.DefaultBlockSize > 0 ? svc.DefaultBlockSize : FallbackBlockSize;
                var overwrite = new BackupRequest(
                    FileList: [treeNew.RootPath],
                    ListContainsPatterns: true,
                    Description: "Set 3 (overwritten, larger)",
                    IncludeSubdirectories: true,
                    Incremental: false,
                    BlockSize: blockSize,
                    HashAlgorithm: TapeHashAlgorithm.Crc32,
                    AppendMode: true,
                    AppendAfterSetIndex: 2,      // overwrite everything after set 2  (Mode 1)
                    SkipAllErrors: false,
                    EjectWhenDone: false);

                var rov = await svc.ExecuteBackupAsync(overwrite);

                Assert.False(rov.WasAborted, "Overwrite must not abort");
                Assert.False(rov.HasFailed, $"Overwrite reported failure: {svc.LastError}");
                Assert.True(rov.Success, $"Overwrite did not succeed: {svc.LastError}");
                Assert.Equal(0, rov.FilesFailed);
                Assert.Equal(treeNew.Files.Count, rov.FilesSucceeded); // ← 40, not the clamped ~32 of the bug
            }
        }

        // ── Phase 3: TOC = {set1, set2, new-set3}; the kept set and the replacement both restore intact. ──
        {
            var (svc, _) = await ReopenAsync(media);
            using (svc)
            {
                var toc = svc.TOC!;
                Assert.Equal(3, toc.Count); // set 3 replaced in place, not appended as a 4th

                var newRoot = Path.Combine(media.Root, "restore_new");
                Directory.CreateDirectory(newRoot);
                int latest = toc.SetIndexToStd(toc.CapSetIndex(0));
                var rrNew = await svc.ExecuteRestoreAsync(new RestoreRequest(
                    Mode: RestoreMode.Restore,
                    CheckedFilesBySet: new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [latest] = null },
                    Incremental: false, TargetDirectory: newRoot, RecurseSubdirectories: true,
                    HandleExisting: TapeHowToHandleExisting.Overwrite, SkipAllErrors: false, EjectWhenDone: false));
                Assert.True(rrNew.Success, $"Restore of overwritten set failed: {svc.LastError}");
                Assert.Equal(treeNew.Files.Count, rrNew.FilesSucceeded);
                FileComparer.AssertFilesMatch(treeNew.RootPath, treeNew.Files,
                    FindRestoredRoot(newRoot, treeNew.RootPath));

                var keptRoot = Path.Combine(media.Root, "restore_set1");
                Directory.CreateDirectory(keptRoot);
                int first = toc.FirstSetOnVolume; // = 1
                var rr1 = await svc.ExecuteRestoreAsync(new RestoreRequest(
                    Mode: RestoreMode.Restore,
                    CheckedFilesBySet: new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [first] = null },
                    Incremental: false, TargetDirectory: keptRoot, RecurseSubdirectories: true,
                    HandleExisting: TapeHowToHandleExisting.Overwrite, SkipAllErrors: false, EjectWhenDone: false));
                Assert.True(rr1.Success, $"Restore of preserved set 1 failed: {svc.LastError}");
                Assert.Equal(tree1.Files.Count, rr1.FilesSucceeded);
                FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                    FindRestoredRoot(keptRoot, tree1.RootPath));
            }
        }
    }
}
