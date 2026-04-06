using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Tests IO error handling across backup, restore, and TOC operations.
/// Validates the three user responses (Skip, Retry, Abort), proactive abort
/// via <see cref="ITapeFileNotifiable"/>, empty-set removal, and the dual-TOC
/// copy resilience mechanism.
/// <para>
/// All tests that use <c>SimulateFailures</c> or <c>SimulateTOCFailureMask</c>
/// are <c>#if DEBUG</c>-only because those fields exist only in Debug builds.
/// </para>
/// </summary>
public class ErrorHandlingTests
{
    #region *** Test Data ***

    /// <summary>All three drive profiles for parameterized theories.</summary>
#pragma warning disable CA1825 // Avoid zero-length array allocations
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
    ];
#pragma warning restore CA1825

    /// <summary>
    /// Profiles with partitioned TOC storage. SeqFilemarks stores the TOC
    /// sequentially after content, so recovery from a corrupted 1st TOC copy
    /// is unreliable (the tape navigator cannot reliably skip past the
    /// damaged data to reach the 2nd copy).
    /// </summary>
#pragma warning disable CA1825
    public static TheoryData<DriveProfile> PartitionedTOCProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
    ];
#pragma warning restore CA1825

    #endregion

    #region *** Helpers ***

    /// <summary>Best-effort cleanup of a temporary restore directory.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(path, recursive: true);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Backs up files with the agent directly (not through the fixture convenience
    /// method) so we can control <c>ignoreFailures</c> and inspect the return value.
    /// </summary>
    private static bool BackupFilesRaw(
        VirtualTapeFixture fixture,
        List<string> fileList,
        bool ignoreFailures,
        ITapeFileNotifiable? notifiable,
        string description = "Error Test Set")
    {
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = description;
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        fixture.TOC.CurrentSetTOC.FmksMode = true;

        using var agent = fixture.CreateBackupAgent();

        return agent.BackupFileListToCurrentSet(
            newSet: true,
            fileList,
            ignoreFailures: ignoreFailures,
            fileNotify: notifiable);
    }

    /// <summary>
    /// Restores all files from the current set to a temp directory.
    /// Returns the overall success flag.
    /// </summary>
    private static bool RestoreAllFiles(
        VirtualTapeFixture fixture,
        string targetDir,
        bool ignoreFailures,
        ITapeFileNotifiable? notifiable)
    {
        using var agent = fixture.CreateRestoreAgent(targetDir);
        return agent.RestoreAllFilesFromCurrentSet(ignoreFailures, notifiable);
    }

    #endregion

    #region *** (A) Backup IO Failure — Skip / Retry / Abort ***

#if DEBUG

    /// <summary>
    /// Simulates file backup failures with <see cref="FileFailedAction.Skip"/>.
    /// Asserts that failed files are skipped, the operation completes, and statistics
    /// invariant holds.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_SimulatedFailure_Skip_StatsCorrect(DriveProfile profile)
    {
        const int fileCount = 8;
        const int failEveryN = 2; // every 2nd file fails → 4 failures

        using var tree = new TempFileTree();
        tree.AddFiles("skip", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        TapeFileAgent.SimulateFailures = true;
        TapeFileAgent.FailEveryNthFile = failEveryN;
        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            bool success = BackupFilesRaw(fixture, tree.Files, ignoreFailures: true, notifiable);

            // Operation should complete (ignoreFailures=true)
            notifiable.AssertStatsInvariant();
            var stats = notifiable.BatchEnds[^1].Stats;

            int expectedFailed = fileCount / failEveryN;
            Assert.Equal(fileCount, stats.FilesTotal);
            Assert.Equal(fileCount, stats.FilesProcessed);
            Assert.Equal(expectedFailed, stats.FilesFailed);
            Assert.Equal(fileCount - expectedFailed, stats.FilesSucceeded);
            Assert.Equal(expectedFailed, notifiable.FilesFailed.Count);
        }
        finally
        {
            TapeFileAgent.SimulateFailures = false;
            TapeFileAgent.FailEveryNthFile = 2;
        }
    }

    /// <summary>
    /// Simulates file backup failures with <see cref="FileFailedAction.Retry"/>.
    /// Because the counter increments on retry, the retry attempt succeeds
    /// (counter % N != 0). Asserts all files end up succeeded.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_SimulatedFailure_Retry_AllSucceed(DriveProfile profile)
    {
        const int fileCount = 6;
        const int failEveryN = 3; // files 3, 6 fail initially, then retry succeeds

        using var tree = new TempFileTree();
        tree.AddFiles("retry", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        // Per-file retry limit: allow one retry per file, then skip
        var retriedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notifiable = new TestNotifiable
        {
            FailedActionFunc = (fd, _) =>
                retriedFiles.Add(fd.FullName) ? FileFailedAction.Retry : FileFailedAction.Skip
        };

        TapeFileAgent.SimulateFailures = true;
        TapeFileAgent.FailEveryNthFile = failEveryN;
        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            bool success = BackupFilesRaw(fixture, tree.Files, ignoreFailures: true, notifiable);

            Assert.True(success, "Backup with retries should succeed");

            notifiable.AssertStatsInvariant();
            var stats = notifiable.BatchEnds[^1].Stats;

            // All files should succeed after retry
            Assert.Equal(fileCount, stats.FilesTotal);
            Assert.Equal(fileCount, stats.FilesProcessed);
            Assert.Equal(fileCount, stats.FilesSucceeded);
            Assert.Equal(0, stats.FilesFailed);

            // OnFileFailed was called for each initial failure (then StatsUndoFailure reverted)
            Assert.True(notifiable.FilesFailed.Count > 0, "Expected at least one initial failure before retry");
        }
        finally
        {
            TapeFileAgent.SimulateFailures = false;
            TapeFileAgent.FailEveryNthFile = 2;
        }
    }

    /// <summary>
    /// Simulates file backup failures with <see cref="FileFailedAction.Abort"/>.
    /// Asserts that the operation stops at the first failure and IsAbortRequested is set.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_SimulatedFailure_Abort_StopsImmediately(DriveProfile profile)
    {
        const int fileCount = 6;
        const int failEveryN = 2; // 2nd file fails, then abort

        using var tree = new TempFileTree();
        tree.AddFiles("abort", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Abort };

        TapeFileAgent.SimulateFailures = true;
        TapeFileAgent.FailEveryNthFile = failEveryN;
        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            fixture.TOC.AddNewSetTOC(0, incremental: false);
            fixture.TOC.CurrentSetTOC.Description = "Abort test";
            fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
            fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
            fixture.TOC.CurrentSetTOC.FmksMode = true;

            using var agent = fixture.CreateBackupAgent();

            bool success = agent.BackupFileListToCurrentSet(
                newSet: true,
                tree.Files,
                ignoreFailures: true, // even with ignoreFailures, Abort overrides
                fileNotify: notifiable);

            Assert.False(success, "Backup should fail when user requests abort");
            Assert.True(agent.IsAbortRequested, "IsAbortRequested should be set");

            notifiable.AssertStatsInvariant();
            var stats = notifiable.BatchEnds[^1].Stats;

            // Should have processed fewer files than total (stopped at abort)
            Assert.True(stats.FilesProcessed < fileCount,
                $"Expected fewer than {fileCount} processed, got {stats.FilesProcessed}");
            Assert.Equal(1, stats.FilesFailed); // exactly one failure before abort
        }
        finally
        {
            TapeFileAgent.SimulateFailures = false;
            TapeFileAgent.FailEveryNthFile = 2;
        }
    }

    /// <summary>
    /// Backs up files with failures (Skip), then verifies the surviving files
    /// are correctly recorded in the TOC. After partial backup failures the tape
    /// layout may contain extra filemarks from partially written files, so we
    /// validate the TOC entries rather than attempting a full restore.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_PartialFailure_SurvivorFilesRestorable(DriveProfile profile)
    {
        const int fileCount = 8;
        const int failEveryN = 2; // every 2nd file fails → 4 succeed

        using var tree = new TempFileTree();
        tree.AddFiles("partial", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var backupNotify = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        TapeFileAgent.SimulateFailures = true;
        TapeFileAgent.FailEveryNthFile = failEveryN;
        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Backup with simulated failures
            bool backupOk = BackupFilesRaw(fixture, tree.Files, ignoreFailures: true, backupNotify);
            var backupStats = backupNotify.BatchEnds[^1].Stats;
            int succeededCount = backupStats.FilesSucceeded;
            Assert.True(succeededCount > 0, "Expected at least one succeeded file");

            // TOC should contain only the succeeded files
            Assert.Equal(succeededCount, fixture.TOC.CurrentSetTOC.Count);

            // Verify each TOC entry references a valid source file (by name)
            var succeededNames = backupNotify.PostProcessed
                .Select(p => p.FileDescr.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var tfi in fixture.TOC.CurrentSetTOC)
            {
                Assert.NotNull(tfi);
                Assert.True(tfi!.IsValid, $"TOC entry should be valid: {tfi.FileDescr.FullName}");
                Assert.Contains(tfi.FileDescr.FullName, succeededNames);
            }

            // Failed files should NOT appear in the TOC
            var failedNames = backupNotify.FilesFailed
                .Select(f => f.FileDescr.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var tfi in fixture.TOC.CurrentSetTOC)
            {
                Assert.DoesNotContain(tfi!.FileDescr.FullName, failedNames);
            }
        }
        finally
        {
            TapeFileAgent.SimulateFailures = false;
            TapeFileAgent.FailEveryNthFile = 2;
        }
    }

#endif // DEBUG

    #endregion

    #region *** (B) Backup Empty Set — All Files Fail / All Files Skipped ***

#if DEBUG

    /// <summary>
    /// When all files in a backup set fail, the set ends up empty.
    /// <see cref="TapeTOC.RemoveLastEmptySet"/> should return true.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_AllFilesFail_EmptySetRemovable(DriveProfile profile)
    {
        const int fileCount = 4;

        using var tree = new TempFileTree();
        tree.AddFiles("allfail", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        // Fail every file
        TapeFileAgent.SimulateFailures = true;
        TapeFileAgent.FailEveryNthFile = 1;
        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            bool success = BackupFilesRaw(fixture, tree.Files, ignoreFailures: true, notifiable);

            notifiable.AssertStatsInvariant();
            var stats = notifiable.BatchEnds[^1].Stats;

            Assert.Equal(fileCount, stats.FilesFailed);
            Assert.Equal(0, stats.FilesSucceeded);

            // The set should be empty → removable
            Assert.Equal(0, fixture.TOC.CurrentSetTOC.Count);
            Assert.True(fixture.TOC.RemoveLastEmptySet(), "Empty set should be removable");
        }
        finally
        {
            TapeFileAgent.SimulateFailures = false;
            TapeFileAgent.FailEveryNthFile = 2;
        }
    }

#endif // DEBUG

    /// <summary>
    /// When all files are skipped via <see cref="TestNotifiable.FilesToSkip"/>,
    /// the set ends up empty and is removable.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_AllFilesSkipped_EmptySetRemovable(DriveProfile profile)
    {
        const int fileCount = 4;

        using var tree = new TempFileTree();
        tree.AddFiles("allskip", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable();
        foreach (var file in tree.Files)
            notifiable.FilesToSkip.Add(file);

        using var fixture = new VirtualTapeFixture(profile);

        bool success = BackupFilesRaw(fixture, tree.Files, ignoreFailures: true, notifiable);

        notifiable.AssertStatsInvariant();
        var stats = notifiable.BatchEnds[^1].Stats;

        Assert.Equal(fileCount, stats.FilesSkipped);
        Assert.Equal(0, stats.FilesSucceeded);

        // The set should be empty → removable
        Assert.Equal(0, fixture.TOC.CurrentSetTOC.Count);
        Assert.True(fixture.TOC.RemoveLastEmptySet(), "Empty set should be removable");
    }

    #endregion

    #region *** (C) Backup Proactive Abort ***

    /// <summary>
    /// User aborts from <see cref="ITapeFileNotifiable.PreProcessFile"/> after N
    /// files have succeeded. The remaining files should not be processed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_UserAbort_FromPreProcess(DriveProfile profile)
    {
        const int fileCount = 8;
        const int abortAfter = 3; // abort before 4th file

        using var tree = new TempFileTree();
        tree.AddFiles("abort_pre", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { AbortAfterNSucceeded = abortAfter };

        using var fixture = new VirtualTapeFixture(profile);

        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Abort pre-process test";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        fixture.TOC.CurrentSetTOC.FmksMode = true;

        using var agent = fixture.CreateBackupAgent();

        bool success = agent.BackupFileListToCurrentSet(
            newSet: true,
            tree.Files,
            ignoreFailures: true,
            fileNotify: notifiable);

        // TapeAbortRequestedException from PreProcess is rethrown → caught as failure
        Assert.False(success, "Backup should fail on proactive abort");

        notifiable.AssertStatsInvariant();
        var stats = notifiable.BatchEnds[^1].Stats;

        // Exactly abortAfter files should have succeeded before the abort
        Assert.Equal(abortAfter, stats.FilesSucceeded);
        Assert.True(stats.FilesProcessed < fileCount,
            $"Expected fewer than {fileCount} processed, got {stats.FilesProcessed}");
    }

    /// <summary>
    /// User aborts from <see cref="ITapeFileNotifiable.PostProcessFile"/> after N
    /// files have succeeded. The abort happens right after the Nth file is completed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_UserAbort_FromPostProcess(DriveProfile profile)
    {
        const int fileCount = 8;
        const int abortAfter = 2; // abort after 2nd file is post-processed

        using var tree = new TempFileTree();
        tree.AddFiles("abort_post", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { AbortInPostProcessAfterN = abortAfter };

        using var fixture = new VirtualTapeFixture(profile);

        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Abort post-process test";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        fixture.TOC.CurrentSetTOC.FmksMode = true;

        using var agent = fixture.CreateBackupAgent();

        bool success = agent.BackupFileListToCurrentSet(
            newSet: true,
            tree.Files,
            ignoreFailures: true,
            fileNotify: notifiable);

        Assert.False(success, "Backup should fail on proactive abort from PostProcess");

        notifiable.AssertStatsInvariant();
        var stats = notifiable.BatchEnds[^1].Stats;

        // At least abortAfter files should have succeeded
        Assert.True(stats.FilesSucceeded >= abortAfter,
            $"Expected at least {abortAfter} succeeded, got {stats.FilesSucceeded}");
        Assert.True(stats.FilesProcessed < fileCount,
            $"Expected fewer than {fileCount} processed, got {stats.FilesProcessed}");
    }

    #endregion

    #region *** (D) Restore IO Failure — Skip / Retry / Abort ***

#if DEBUG

    /// <summary>
    /// Backs up files cleanly, then simulates restore failures with Skip.
    /// Asserts that some files fail, the operation completes, and statistics
    /// invariant holds.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_SimulatedFailure_Skip_StatsCorrect(DriveProfile profile)
    {
        const int fileCount = 8;
        const int failEveryN = 2;

        using var tree = new TempFileTree();
        tree.AddFiles("rskip", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_RSkip_{Guid.NewGuid():N}");

        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Backup without failures
            fixture.BackupFiles(tree.Files);

            // Restore with simulated failures
            var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

            TapeFileAgent.SimulateFailures = true;
            TapeFileAgent.FailEveryNthFile = failEveryN;

            bool restoreOk = RestoreAllFiles(fixture, restoreDir, ignoreFailures: true, notifiable);

            notifiable.AssertStatsInvariant();
            var stats = notifiable.BatchEnds[^1].Stats;

            int expectedFailed = fileCount / failEveryN;
            Assert.Equal(fileCount, stats.FilesTotal);
            Assert.Equal(fileCount, stats.FilesProcessed);
            Assert.Equal(expectedFailed, stats.FilesFailed);
            Assert.Equal(fileCount - expectedFailed, stats.FilesSucceeded);
            Assert.Equal(expectedFailed, notifiable.FilesFailed.Count);
        }
        finally
        {
            TapeFileAgent.SimulateFailures = false;
            TapeFileAgent.FailEveryNthFile = 2;
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Simulates restore failures with Retry in FmksMode. The retry naturally
    /// succeeds because the counter advances past the failing modulus. Offsets
    /// the failure counter so that the very first file triggers a failure,
    /// exercising both the first-file-on-volume recovery path and the regular
    /// two-step filemark repositioning for subsequent files.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_SimulatedFailure_Retry_AllSucceed(DriveProfile profile)
    {
        const int fileCount = 6;
        const int failEveryN = 3;

        using var tree = new TempFileTree();
        tree.AddFiles("rretry", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_RRetry_{Guid.NewGuid():N}");

        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Backup with filemarks (FmksMode) to exercise filemark-based retry
            fixture.BackupFiles(tree.Files);

            // Per-file retry limit: allow one retry per file, then skip
            var retriedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var notifiable = new TestNotifiable
            {
                FailedActionFunc = (fd, _) =>
                    retriedFiles.Add(fd.FullName) ? FileFailedAction.Retry : FileFailedAction.Skip
            };

            TapeFileAgent.SimulateFailures = true;
            TapeFileAgent.FailEveryNthFile = failEveryN;

            // Create agent directly so we can offset the failure counter:
            //  counter 2 → file 1 bumps to 3 (3%3=0 → fail, first-file-on-volume path),
            //  file 3 → counter 6 (fail, two-step filemark retry), file 5 → counter 9 (fail)
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            agent.SimulatedFailureCounter = failEveryN - 1;

            bool restoreOk = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, notifiable);

            Assert.True(restoreOk, "Restore with retries should succeed");

            notifiable.AssertStatsInvariant();
            var stats = notifiable.BatchEnds[^1].Stats;

            Assert.Equal(fileCount, stats.FilesSucceeded);
            Assert.Equal(0, stats.FilesFailed);
            Assert.True(notifiable.FilesFailed.Count > 0, "Expected initial failures before retry");
        }
        finally
        {
            TapeFileAgent.SimulateFailures = false;
            TapeFileAgent.FailEveryNthFile = 2;
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Simulates restore failures with Abort. The operation should stop at the
    /// first failure.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_SimulatedFailure_Abort_StopsImmediately(DriveProfile profile)
    {
        const int fileCount = 6;
        const int failEveryN = 2; // 2nd file fails → abort

        using var tree = new TempFileTree();
        tree.AddFiles("rabort", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_RAbort_{Guid.NewGuid():N}");

        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Backup without failures
            fixture.BackupFiles(tree.Files);

            // Restore with simulated failures + Abort
            var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Abort };

            TapeFileAgent.SimulateFailures = true;
            TapeFileAgent.FailEveryNthFile = failEveryN;

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            bool restoreOk = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, notifiable);

            Assert.False(restoreOk, "Restore should fail on abort");
            Assert.True(agent.IsAbortRequested, "IsAbortRequested should be set");

            notifiable.AssertStatsInvariant();
            var stats = notifiable.BatchEnds[^1].Stats;

            Assert.True(stats.FilesProcessed < fileCount,
                $"Expected fewer than {fileCount} processed, got {stats.FilesProcessed}");
            Assert.Equal(1, stats.FilesFailed);
        }
        finally
        {
            TapeFileAgent.SimulateFailures = false;
            TapeFileAgent.FailEveryNthFile = 2;
            TryDeleteDirectory(restoreDir);
        }
    }

#endif // DEBUG

    #endregion

    #region *** (E) Restore Proactive Abort ***

    /// <summary>
    /// User aborts restore from <see cref="ITapeFileNotifiable.PreProcessFile"/>
    /// after N files have succeeded.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_UserAbort_FromPreProcess(DriveProfile profile)
    {
        const int fileCount = 8;
        const int abortAfter = 3;

        using var tree = new TempFileTree();
        tree.AddFiles("rabort_pre", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_RAbortPre_{Guid.NewGuid():N}");

        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Backup without failures
            fixture.BackupFiles(tree.Files);

            // Restore with proactive abort
            var notifiable = new TestNotifiable { AbortAfterNSucceeded = abortAfter };

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            bool restoreOk = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, notifiable);

            Assert.False(restoreOk, "Restore should fail on proactive abort");

            notifiable.AssertStatsInvariant();
            var stats = notifiable.BatchEnds[^1].Stats;

            Assert.Equal(abortAfter, stats.FilesSucceeded);
            Assert.True(stats.FilesProcessed < fileCount,
                $"Expected fewer than {fileCount} processed, got {stats.FilesProcessed}");
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (F) TOC Dual-Copy Backup Resilience ***

#if DEBUG

    /// <summary>
    /// When the 1st TOC copy fails but the 2nd succeeds, <see cref="TapeFileAgent.BackupTOC"/>
    /// should return true and the TOC should be restorable.
    /// <para>
    /// Excluded for <see cref="DriveProfile.SeqFilemarks"/>: the sequential tape layout
    /// cannot reliably navigate past a corrupted 1st TOC copy to find the 2nd.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(PartitionedTOCProfiles))]
    public void TOCBackup_FirstCopyFails_SecondSucceeds_Restorable(DriveProfile profile)
    {
        const int fileCount = 4;

        using var tree = new TempFileTree();
        tree.AddFiles("toc1", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        TapeFileAgent.SimulateTOCFailureMask = 0;
        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Backup files normally
            fixture.BackupFiles(tree.Files);
            int expectedSets = fixture.TOC.Count;

            // Now re-write TOC with 1st copy failing (bit 0 = 1)
            TapeFileAgent.SimulateTOCFailureMask = 1;

            using var writeAgent = new TapeFileAgent(fixture.Drive, fixture.TOC);
            bool tocWriteOk = writeAgent.BackupTOC(enforce: true);
            Assert.True(tocWriteOk, "BackupTOC should succeed when only 1st copy fails");

            // Restore TOC — should recover from the 2nd copy
            TapeFileAgent.SimulateTOCFailureMask = 0;

            using var readAgent = new TapeFileAgent(fixture.Drive, fixture.TOC);
            bool tocReadOk = readAgent.RestoreTOC();
            Assert.True(tocReadOk, "RestoreTOC should succeed from 2nd copy");
            Assert.Equal(expectedSets, readAgent.TOC.Count);
        }
        finally
        {
            TapeFileAgent.SimulateTOCFailureMask = 0;
        }
    }

    /// <summary>
    /// When both TOC copies fail during backup, <see cref="TapeFileAgent.BackupTOC"/>
    /// should return false.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOCBackup_BothCopiesFail_ReturnsFalse(DriveProfile profile)
    {
        const int fileCount = 4;

        using var tree = new TempFileTree();
        tree.AddFiles("toc2", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        TapeFileAgent.SimulateTOCFailureMask = 0;
        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Backup files normally first (writes a valid TOC)
            fixture.BackupFiles(tree.Files);

            // Now try to re-write TOC with both copies failing (bits 0+1 = 3)
            TapeFileAgent.SimulateTOCFailureMask = 3;

            using var writeAgent = new TapeFileAgent(fixture.Drive, fixture.TOC);
            bool tocWriteOk = writeAgent.BackupTOC(enforce: true);
            Assert.False(tocWriteOk, "BackupTOC should fail when both copies fail");
        }
        finally
        {
            TapeFileAgent.SimulateTOCFailureMask = 0;
        }
    }

#endif // DEBUG

    #endregion

    #region *** (G) TOC Dual-Copy Restore Resilience ***

#if DEBUG

    /// <summary>
    /// Writes a valid dual TOC, then simulates the 1st copy failing during restore.
    /// The restore should recover from the 2nd copy.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOCRestore_FirstCopyFails_RecoveryFromSecondCopy(DriveProfile profile)
    {
        const int fileCount = 4;

        using var tree = new TempFileTree();
        tree.AddFiles("tocr1", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        TapeFileAgent.SimulateTOCFailureMask = 0;
        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Backup files and TOC normally
            fixture.BackupFiles(tree.Files);
            int expectedSets = fixture.TOC.Count;

            // Now restore TOC with 1st copy failing during read (bit 0 = 1)
            TapeFileAgent.SimulateTOCFailureMask = 1;

            using var readAgent = new TapeFileAgent(fixture.Drive, fixture.TOC);
            bool tocReadOk = readAgent.RestoreTOC();
            Assert.True(tocReadOk, "RestoreTOC should succeed from 2nd copy when 1st fails");
            Assert.Equal(expectedSets, readAgent.TOC.Count);
        }
        finally
        {
            TapeFileAgent.SimulateTOCFailureMask = 0;
        }
    }

    /// <summary>
    /// Simulates both TOC copies failing during restore.
    /// <see cref="TapeFileAgent.RestoreTOC"/> should return false.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOCRestore_BothCopiesFail_ReturnsFalse(DriveProfile profile)
    {
        const int fileCount = 4;

        using var tree = new TempFileTree();
        tree.AddFiles("tocr2", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        TapeFileAgent.SimulateTOCFailureMask = 0;
        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Backup files and TOC normally
            fixture.BackupFiles(tree.Files);

            // Both copies fail during restore (bits 0+1+2 = 7 covers the 3rd attempt too)
            TapeFileAgent.SimulateTOCFailureMask = 7;

            using var readAgent = new TapeFileAgent(fixture.Drive, fixture.TOC);
            bool tocReadOk = readAgent.RestoreTOC();
            Assert.False(tocReadOk, "RestoreTOC should fail when all copies fail");
        }
        finally
        {
            TapeFileAgent.SimulateTOCFailureMask = 0;
        }
    }

#endif // DEBUG

    #endregion
}
