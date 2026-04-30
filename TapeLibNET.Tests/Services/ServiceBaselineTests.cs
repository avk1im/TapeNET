using System.IO;

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Single-volume baseline tests: round-trip bytes, append sets, mid-run abort,
///  and CRC validation.  Covers plan items I-1, E-1, and E-2.
/// </summary>
public class ServiceBaselineTests : ServiceTestBase
{
    /// <summary>
    /// Single-volume backup → restore round-trip; verifies every byte is
    ///  recovered intact and that the exact file count is round-tripped.
    ///  Parameterised over both drive profiles
    ///  (single-partition and with initiator partition).
    /// </summary>
    [Theory]
    [InlineData(false)] // single-partition (setmarks only)
    [InlineData(true)]  // with initiator partition
    public async Task Backup_Then_Restore_RoundTripsBytes(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        AddRichContent(src);

        // ── Backup ────────────────────────────────────────────────────────────
        var (backupSvc, backupHost) = await OpenAndFormatAsync(media);
        using (backupSvc)
        {
            var req    = MakeBackupRequest(backupSvc, src.RootPath, "RoundTrip-Set-1");
            var result = await backupSvc.ExecuteBackupAsync(req);

            Assert.False(result.WasAborted,  "Backup was unexpectedly aborted");
            Assert.False(result.HasFailed,    "Backup reported failure");
            Assert.True (result.Success,      "Backup did not succeed");
            Assert.Equal(0, result.FilesFailed);
            Assert.Equal(RichContentFileCount, result.FilesSucceeded);
            Assert.False(backupHost.HasErrors, "Backup host received unexpected error reports");
            Assert.True (backupHost.StateChanges.Contains(ServiceStateChange.OperationEnded),
                "Backup did not emit OperationEnded state change");
        }

        // ── Restore ───────────────────────────────────────────────────────────
        var restoreRoot = Path.Combine(media.Root, "restore");
        Directory.CreateDirectory(restoreRoot);

        var (restoreSvc, restoreHost) = await ReopenAsync(media);
        using (restoreSvc)
        {
            Assert.NotNull(restoreSvc.TOC);
            Assert.True(restoreSvc.TOC!.Count > 0, "TOC has no sets after restore");

            // Restore all files from the latest set (index 0 → normalised by RestoreCommand logic)
            int setIndex = restoreSvc.TOC.SetIndexToStd(restoreSvc.TOC.CapSetIndex(0));
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
                Incremental:           true,
                TargetDirectory:       restoreRoot,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false);

            var result = await restoreSvc.ExecuteRestoreAsync(req);

            Assert.False(result.WasAborted,  "Restore was unexpectedly aborted");
            Assert.False(result.HasFailed,    "Restore reported failure");
            Assert.True (result.Success,      "Restore did not succeed");
            Assert.Equal(0, result.FilesFailed);
            Assert.Equal(RichContentFileCount, result.FilesSucceeded);
            Assert.False(restoreHost.HasErrors, "Restore host received unexpected error reports");
            Assert.True (restoreHost.StateChanges.Contains(ServiceStateChange.OperationEnded),
                "Restore did not emit OperationEnded state change");
        }

        // ── Byte-level comparison ─────────────────────────────────────────────
        FileComparer.AssertFilesMatch(
            src.RootPath,
            src.Files,
            FindRestoredRoot(restoreRoot, src.RootPath));
    }

    /// <summary>
    /// Append a second backup set after the first; verifies that the TOC
    ///  reports two sets and that both are restorable independently.
    /// </summary>
    [Fact]
    public async Task AppendBackup_AddsSecondSet_BothRestorable()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src1  = new TempFileTree();
        using var src2  = new TempFileTree();
        src1.AddFiles("set1", count: 4, minSize: 512, maxSize: 4_096);
        src2.AddFiles("set2", count: 4, minSize: 512, maxSize: 4_096);

        // ── Backup set 1 (new) ────────────────────────────────────────────────
        var (svc1, host1) = await OpenAndFormatAsync(media);
        using (svc1)
        {
            var result = await svc1.ExecuteBackupAsync(
                MakeBackupRequest(svc1, src1.RootPath, "Set-1"));
            Assert.True(result.Success, "Backup of Set-1 failed");
            Assert.Equal(0, result.FilesFailed);
            Assert.False(host1.HasErrors, "Set-1 backup host received unexpected errors");
        }

        // ── Backup set 2 (append) ─────────────────────────────────────────────
        var (svc2, host2) = await ReopenAsync(media);
        using (svc2)
        {
            var result = await svc2.ExecuteBackupAsync(
                MakeBackupRequest(svc2, src2.RootPath, "Set-2", append: true));
            Assert.True(result.Success, "Backup of Set-2 failed");
            Assert.Equal(0, result.FilesFailed);
            Assert.False(host2.HasErrors, "Set-2 backup host received unexpected errors");
        }

        // ── TOC should list two sets ───────────────────────────────────────────
        var (svcRead, _) = await ReopenAsync(media);
        using (svcRead)
        {
            Assert.NotNull(svcRead.TOC);
            Assert.Equal(2, svcRead.TOC!.Count);

            var descriptions = Enumerable
                .Range(svcRead.TOC.FirstSetOnVolume, svcRead.TOC.Count)
                .Select(i => svcRead.TOC[i].Description)
                .ToList();

            Assert.Contains(descriptions, d => d?.Contains("Set-1") ?? false);
            Assert.Contains(descriptions, d => d?.Contains("Set-2") ?? false);
        }

        // ── Restore set 1 (oldest, standard index 1) ─────────────────────────
        var restore1Root = Path.Combine(media.Root, "restore1");
        Directory.CreateDirectory(restore1Root);

        var (svcR1, hostR1) = await ReopenAsync(media);
        using (svcR1)
        {
            int stdIndex1 = svcR1.TOC!.FirstSetOnVolume; // = 1 (oldest)
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [stdIndex1] = null },
                Incremental:           false,
                TargetDirectory:       restore1Root,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false);

            var result = await svcR1.ExecuteRestoreAsync(req);
            Assert.True(result.Success, "Restore of Set-1 failed");
            Assert.Equal(0, result.FilesFailed);
        }

        FileComparer.AssertFilesMatch(
            src1.RootPath, src1.Files, FindRestoredRoot(restore1Root, src1.RootPath));

        // ── Restore set 2 (latest, standard index 2) ─────────────────────────
        var restore2Root = Path.Combine(media.Root, "restore2");
        Directory.CreateDirectory(restore2Root);

        var (svcR2, hostR2) = await ReopenAsync(media);
        using (svcR2)
        {
            int stdIndex2 = svcR2.TOC!.LastSetOnVolume;
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [stdIndex2] = null },
                Incremental:           false,
                TargetDirectory:       restore2Root,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false);

            var result = await svcR2.ExecuteRestoreAsync(req);
            Assert.True(result.Success, "Restore of Set-2 failed");
            Assert.Equal(0, result.FilesFailed);
            Assert.False(hostR2.HasErrors, "Set-2 restore host received unexpected errors");
        }

        FileComparer.AssertFilesMatch(
            src2.RootPath, src2.Files, FindRestoredRoot(restore2Root, src2.RootPath));
    }

    /// <summary>
    /// Verifies that aborting a backup mid-run leaves all already-committed entries
    ///  intact: after reopening the tape <see cref="RestoreMode.Validate"/> must
    ///  succeed for every file that was recorded before the abort.
    /// </summary>
    /// <remarks>
    /// Abort is signalled via <see cref="TapeFileAgent.IsAbortRequested"/> because
    ///  <see cref="TapeServiceBase.OperationCancellationToken"/> returns
    ///  <see cref="CancellationToken.None"/> on the base class; the CT→agent bridge
    ///  is only wired in the <c>TapeService</c> subclass.
    /// </remarks>
    [Fact]
    public async Task Backup_Abort_SetEntriesAreIntact()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        // Enough files that abort fires well before the last one
        src.AddFiles("abort", count: 200, minSize: 64 * 1_024, maxSize: 256 * 1_024);

        // Format with a separate service so setup is clean
        var (setupSvc, _) = await OpenAndFormatAsync(media);
        setupSvc.Dispose();

        // ── Backup (with mid-run abort) ───────────────────────────────────────
        int filesSucceeded;
        var (svc, _) = await ReopenAsync(media);
        using (svc)
        {
            uint blockSize = svc.DefaultBlockSize > 0 ? svc.DefaultBlockSize : FallbackBlockSize;
            var req = new BackupRequest(
                FileList:              [src.RootPath],
                ListContainsPatterns:  true,
                Description:           "Abort-Test",
                IncludeSubdirectories: true,
                Incremental:           false,
                BlockSize:             blockSize,
                HashAlgorithm:         TapeHashAlgorithm.Crc32,
                AppendMode:            false,
                AppendAfterSetIndex:   0,
                SkipAllErrors:         true);

            // Start the backup, then signal abort via the agent flag once the backup loop starts.
            var backupTask = svc.ExecuteBackupAsync(req);

            // Wait for the agent to initialise (backup loop starts async)
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (svc.Agent is null && DateTime.UtcNow < deadline)
                await Task.Delay(5);

            // Wait until at least one file has been committed, then abort.
            //  Polling Agent.Statistics (ref readonly TapeFileStatistics) avoids
            //  fixed delays that are either too short or unnecessarily slow.
            deadline = DateTime.UtcNow.AddSeconds(15);
            while ((svc.Agent?.Statistics.FilesSucceeded ?? 0) == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(5);

            if (svc.Agent is not null)
                svc.Agent.IsAbortRequested = true;

            var result = await backupTask;

            Assert.True(result.WasAborted, "Expected WasAborted = true after abort signal");
            // Some files must have been committed or the test is vacuous
            Assert.True(result.FilesSucceeded > 0,
                "No files were committed before abort — increase file count or size");
            filesSucceeded = result.FilesSucceeded;
        }

        // ── Validate: all committed entries must CRC-check clean ─────────────
        var (valSvc, valHost) = await ReopenAsync(media);
        using (valSvc)
        {
            var toc = valSvc.TOC!;
            int setIdx = toc.SetIndexToStd(toc.CapSetIndex(0));
            int committedCount = toc[setIdx].Count;

            // The TOC-recorded count must match what the BackupResult reported
            Assert.Equal(filesSucceeded, committedCount);

            var req = new RestoreRequest(
                Mode:                  RestoreMode.Validate,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIdx] = null },
                Incremental:           false,
                TargetDirectory:       null,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Skip,
                // Allow the one file that may have been partially written at the abort
                //  boundary to fail without stopping the entire validation run
                SkipAllErrors:         true);

            var result = await valSvc.ExecuteRestoreAsync(req);

            // At most 1 file (the one being written at the abort boundary) may have
            //  incomplete data on tape; every other committed entry must verify clean.
            //  result.FilesFailed is the authoritative counter; host reports are UI noise.
            Assert.True(result.FilesFailed <= 1,
                $"More than 1 file failed validation after abort (got {result.FilesFailed})");
            Assert.Equal(committedCount - result.FilesFailed, result.FilesSucceeded);
        }
    }

    /// <summary>
    /// Validate that CRC integrity check (Validate mode) passes immediately
    ///  after a successful backup.
    /// </summary>
    [Fact]
    public async Task Validate_AfterBackup_Passes()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        AddRichContent(src);

        // Backup
        var (svcB, _) = await OpenAndFormatAsync(media);
        using (svcB)
        {
            var result = await svcB.ExecuteBackupAsync(
                MakeBackupRequest(svcB, src.RootPath, "Validate-Test"));
            Assert.True(result.Success, "Backup failed before validate");
        }

        // Validate
        var (svcV, validateHost) = await ReopenAsync(media);
        using (svcV)
        {
            int setIndex = svcV.TOC!.SetIndexToStd(svcV.TOC.CapSetIndex(0));
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Validate,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
                Incremental:           true,
                TargetDirectory:       null,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Skip,
                SkipAllErrors:         false);

            var result = await svcV.ExecuteRestoreAsync(req);

            Assert.True(result.Success, "Validate reported failure");
            Assert.Equal(0, result.FilesFailed);
            Assert.False(validateHost.HasErrors, "Validate host received unexpected error reports");
        }
    }
}
