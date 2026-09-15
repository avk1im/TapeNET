using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Tests IO error handling across backup, restore, and TOC operations.
/// Validates the three user responses (Skip, Retry, Abort), proactive abort
/// via <see cref="ITapeFileNotifiable"/>, empty-set removal, and the dual-TOC
/// copy resilience mechanism.
/// <para>
/// All tests that use <c>SimulateFileFailures</c> or <c>SimulateTOCFailureMask</c>
/// are <c>#if DEBUG</c>-only because those fields exist only in Debug builds.
/// </para>
/// </summary>
public class ErrorHandlingTests
{
    #region *** Test Data ***

    /// <summary>All three drive profiles for parameterized theories.</summary>
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    /// <summary>
    /// Profiles with partitioned TOC storage. SeqFilemarks stores the TOC
    /// sequentially after content, so recovery from a corrupted 1st TOC copy
    /// is unreliable (the tape navigator cannot reliably skip past the
    /// damaged data to reach the 2nd copy).
    /// </summary>
    public static TheoryData<DriveProfile> PartitionedTOCProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
    ];

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
        string description = "Error Test Set",
        Action<TapeFileAgent>? configureAgent = null)
    {
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = description;
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();
        configureAgent?.Invoke(agent);

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
        ITapeFileNotifiable? notifiable,
        Action<TapeFileAgent>? configureAgent = null)
    {
        using var agent = fixture.CreateRestoreAgent(targetDir);
        configureAgent?.Invoke(agent);
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
        const int failEveryN = 2; // every 2nd file fails ? 4 failures

        using var tree = new TempFileTree();
        tree.AddFiles("skip", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        using var fixture = new VirtualTapeFixture(profile);

        bool success = BackupFilesRaw(fixture, tree.Files, ignoreFailures: true, notifiable,
            configureAgent: a => { a.SimulateFileFailures.Enabled = true; a.SimulateFileFailures.EveryNth = failEveryN; });

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
                retriedFiles.Add(fd.FileDescr.FullName) ? FileFailedAction.Retry : FileFailedAction.Skip
        };

        using var fixture = new VirtualTapeFixture(profile);

        bool success = BackupFilesRaw(fixture, tree.Files, ignoreFailures: true, notifiable,
            configureAgent: a => { a.SimulateFileFailures.Enabled = true; a.SimulateFileFailures.EveryNth = failEveryN; });

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

        using var fixture = new VirtualTapeFixture(profile);

        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Abort test";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();
        agent.SimulateFileFailures.Enabled = true;
        agent.SimulateFileFailures.EveryNth = failEveryN;

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

    /// <summary>
    /// Simulates an interleaved failure pattern during backup (ok ? fail ? ok ? fail ? ok),
    /// then performs a full restore and verifies that the succeeded files are restored
    /// correctly byte-for-byte. Exercises block-based positioning recovery when restoring
    /// files that follow a failed-file gap on tape.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_InterleavedFailure_SurvivorFilesRestoredCorrectly(DriveProfile profile)
    {
        // 9 files: fail every 4th (files 4 and 8 fail) ? 7 succeed
        //  positions: ok ok ok FAIL ok ok ok FAIL ok
        const int fileCount = 9;
        const int failEveryN = 4;

        using var tree = new TempFileTree();
        tree.AddFiles("ifmk", count: fileCount, minSize: 512, maxSize: 8 * 1024);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_IFMk_{Guid.NewGuid():N}");

        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Configure set manually
            fixture.TOC.AddNewSetTOC(0, incremental: false);
            fixture.TOC.CurrentSetTOC.Description = "Interleaved Failure Test";
            fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
            fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

            var backupNotify = new TestNotifiable { FailedAction = FileFailedAction.Skip };

            using var backupAgent = fixture.CreateBackupAgent();
            backupAgent.SimulateFileFailures.Enabled = true;
            backupAgent.SimulateFileFailures.EveryNth = failEveryN;

            bool backupOk = backupAgent.BackupFileListToCurrentSet(
                newSet: true, tree.Files, ignoreFailures: true, backupNotify);

            // Backup should complete (ignoreFailures=true)
            backupNotify.AssertStatsInvariant();
            var backupStats = backupNotify.BatchEnds[^1].Stats;
            int succeededCount = backupStats.FilesSucceeded;
            Assert.True(succeededCount > 0, "Expected at least one succeeded file");
            Assert.Equal(fileCount / failEveryN, backupStats.FilesFailed);

            // Save TOC
            Assert.True(backupAgent.BackupTOC(), "Failed to save TOC after partial backup");

            // Collect the set of succeeded source paths for verification
            var succeededPaths = backupNotify.PostProcessed
                .Select(p => p.FileInfo.FileDescr.FullName)
                .ToList();

            // Restore — all TOC-registered (succeeded) files should restore correctly
            var restoreNotify = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            bool restoreOk = restoreAgent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, restoreNotify);

            Assert.True(restoreOk, "Restore of survived files should succeed");
            restoreNotify.AssertStatsInvariant();
            var restoreStats = restoreNotify.BatchEnds[^1].Stats;
            Assert.Equal(succeededCount, restoreStats.FilesSucceeded);
            Assert.Equal(0, restoreStats.FilesFailed);

            // Byte-for-byte content verification — the restore agent mirrors the original
            //  absolute path structure under restoreDir (stripped of the drive root)
            string restoreEquivalent1 = Path.Combine(
                restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath));
            FileComparer.AssertFilesMatch(tree.RootPath, succeededPaths, restoreEquivalent1);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Simulates an interleaved failure pattern during backup (ok ? fail ? ok ? fail ? ok)
    /// with filemarks <em>disabled</em>
    /// restore and verifies that the succeeded files are restored correctly byte-for-byte.
    /// Exercises the non-filemark restore path where tape position is tracked solely via
    /// TOC block offsets.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_InterleavedFailure_WithoutFilemarks_SurvivorFilesRestoredCorrectly(DriveProfile profile)
    {
        // 9 files: fail every 4th (files 4 and 8 fail) ? 7 succeed
        //  positions: ok ok ok FAIL ok ok ok FAIL ok
        const int fileCount = 9;
        const int failEveryN = 4;

        using var tree = new TempFileTree();
        tree.AddFiles("inofmk", count: fileCount, minSize: 512, maxSize: 8 * 1024);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_INoFMk_{Guid.NewGuid():N}");

        try
        {
            using var fixture = new VirtualTapeFixture(profile);

            // Configure set manually
            fixture.TOC.AddNewSetTOC(0, incremental: false);
            fixture.TOC.CurrentSetTOC.Description = "Interleaved Failure Test";
            fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
            fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

            var backupNotify = new TestNotifiable { FailedAction = FileFailedAction.Skip };

            using var backupAgent = fixture.CreateBackupAgent();
            backupAgent.SimulateFileFailures.Enabled = true;
            backupAgent.SimulateFileFailures.EveryNth = failEveryN;

            bool backupOk = backupAgent.BackupFileListToCurrentSet(
                newSet: true, tree.Files, ignoreFailures: true, backupNotify);

            backupNotify.AssertStatsInvariant();
            var backupStats = backupNotify.BatchEnds[^1].Stats;
            int succeededCount = backupStats.FilesSucceeded;
            Assert.True(succeededCount > 0, "Expected at least one succeeded file");
            Assert.Equal(fileCount / failEveryN, backupStats.FilesFailed);

            // Save TOC
            Assert.True(backupAgent.BackupTOC(), "Failed to save TOC after partial backup");

            // Collect succeeded source paths
            var succeededPaths = backupNotify.PostProcessed
                .Select(p => p.FileInfo.FileDescr.FullName)
                .ToList();

            // Restore
            var restoreNotify = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            bool restoreOk = restoreAgent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, restoreNotify);

            Assert.True(restoreOk, "Restore of survived files should succeed");
            restoreNotify.AssertStatsInvariant();
            var restoreStats = restoreNotify.BatchEnds[^1].Stats;
            Assert.Equal(succeededCount, restoreStats.FilesSucceeded);
            Assert.Equal(0, restoreStats.FilesFailed);

            // Byte-for-byte content verification — mirror the original path structure under restoreDir
            string restoreEquivalent2 = Path.Combine(
                restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath));
            FileComparer.AssertFilesMatch(tree.RootPath, succeededPaths, restoreEquivalent2);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
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
        const int failEveryN = 2; // every 2nd file fails ? 4 succeed

        using var tree = new TempFileTree();
        tree.AddFiles("partial", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var backupNotify = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        using var fixture = new VirtualTapeFixture(profile);

        // Backup with simulated failures
        bool backupOk = BackupFilesRaw(fixture, tree.Files, ignoreFailures: true, backupNotify,
            configureAgent: a => { a.SimulateFileFailures.Enabled = true; a.SimulateFileFailures.EveryNth = failEveryN; });
        var backupStats = backupNotify.BatchEnds[^1].Stats;
        int succeededCount = backupStats.FilesSucceeded;
        Assert.True(succeededCount > 0, "Expected at least one succeeded file");

        // TOC should contain only the succeeded files
        Assert.Equal(succeededCount, fixture.TOC.CurrentSetTOC.Count);

        // Verify each TOC entry references a valid source file (by name)
        var succeededNames = backupNotify.PostProcessed
            .Select(p => p.FileInfo.FileDescr.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tfi in fixture.TOC.CurrentSetTOC)
        {
            Assert.NotNull(tfi);
            Assert.True(tfi!.IsValid, $"TOC entry should be valid: {tfi.FileDescr.FullName}");
            Assert.Contains(tfi.FileDescr.FullName, succeededNames);
        }

        // Failed files should NOT appear in the TOC
        var failedNames = backupNotify.FilesFailed
            .Select(f => f.FileInfo.FileDescr.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tfi in fixture.TOC.CurrentSetTOC)
        {
            Assert.DoesNotContain(tfi!.FileDescr.FullName, failedNames);
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

        using var fixture = new VirtualTapeFixture(profile);

        // Fail every file
        bool success = BackupFilesRaw(fixture, tree.Files, ignoreFailures: true, notifiable,
            configureAgent: a => { a.SimulateFileFailures.Enabled = true; a.SimulateFileFailures.EveryNth = 1; });

        notifiable.AssertStatsInvariant();
        var stats = notifiable.BatchEnds[^1].Stats;

        Assert.Equal(fileCount, stats.FilesFailed);
        Assert.Equal(0, stats.FilesSucceeded);

        // The set should be empty ? removable
        Assert.Empty(fixture.TOC.CurrentSetTOC);
        Assert.True(fixture.TOC.RemoveLastEmptySet(), "Empty set should be removable");
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

        // The set should be empty ? removable
        Assert.Empty(fixture.TOC.CurrentSetTOC);
        Assert.True(fixture.TOC.RemoveLastEmptySet(), "Empty set should be removable");
    }

    #endregion

    #region *** (C) Backup Proactive Abort ***

    /// User aborts from <see cref="ITapeFileNotifiable.PreProcessFile"/> after N
    /// files have succeeded. The remaining files should not be processed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_UserAbort_FromPreProcess(DriveProfile profile)
    {
        const int fileCount = 10;
        const int abortAfter = 3; // abort before 4th file

        using var tree = new TempFileTree();
        tree.AddFiles("abort_pre", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { AbortAfterNPreProcessed = abortAfter };

        using var fixture = new VirtualTapeFixture(profile);

        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Abort pre-process test";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();

        bool success = agent.BackupFileListToCurrentSet(
            newSet: true,
            tree.Files,
            ignoreFailures: true,
            fileNotify: notifiable);

        // TapeAbortRequestedException from PreProcess is rethrown ? caught as failure
        Assert.False(success, "Backup should fail on proactive abort");

        notifiable.AssertStatsInvariant();
        var stats = notifiable.BatchEnds[^1].Stats;

        // Exactly abortAfter files should have succeeded before the abort
        Assert.Equal(abortAfter, stats.FilesSucceeded);
        Assert.True(stats.FilesProcessed < fileCount,
            $"Expected fewer than {fileCount} processed, got {stats.FilesProcessed}");
    }

    /// <summary>
    /// User aborts from <see cref="ITapeFileNotifiable.PreProcessFile"/> after N
    /// files have succeeded. The remaining files should not be processed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_UserAbort_AfterNSucceeded(DriveProfile profile)
    {
        const int fileCount = 10;
        const int abortAfter = 3; // abort before 4th file

        using var tree = new TempFileTree();
        tree.AddFiles("abort_pre", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { AbortAfterNSucceeded = abortAfter };

        using var fixture = new VirtualTapeFixture(profile);

        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Abort pre-process test";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();

        bool success = agent.BackupFileListToCurrentSet(
            newSet: true,
            tree.Files,
            ignoreFailures: true,
            fileNotify: notifiable);

        // TapeAbortRequestedException from PreProcess is rethrown ? caught as failure
        Assert.False(success, "Backup should fail on proactive abort");

        notifiable.AssertStatsInvariant();
        var stats = notifiable.BatchEnds[^1].Stats;

        // Exactly abortAfter files should have succeeded before the abort
        //  CAREFUL: this won't be true for the packing path if aborted from pre-process!
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

            bool restoreOk = RestoreAllFiles(fixture, restoreDir, ignoreFailures: true, notifiable,
                configureAgent: a => { a.SimulateFileFailures.Enabled = true; a.SimulateFileFailures.EveryNth = failEveryN; });

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
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Simulates restore failures with Retry. The retry naturally
    /// succeeds because the counter advances past the failing modulus. Offsets
    /// the failure counter so that the very first file triggers a failure,
    /// exercising both the first-file-on-volume recovery path and the regular
    /// block repositioning for subsequent files.
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

            // Backup cleanly
            fixture.BackupFiles(tree.Files);

            // Per-file retry limit: allow one retry per file, then skip
            var retriedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var notifiable = new TestNotifiable
            {
                FailedActionFunc = (fd, _) =>
                    retriedFiles.Add(fd.FileDescr.FullName) ? FileFailedAction.Retry : FileFailedAction.Skip
            };

            // Create agent directly so we can offset the failure counter:
            //  counter 2 ? file 1 bumps to 3 (3%3=0 ? fail, first-file-on-volume path),
            //  file 3 ? counter 6 (fail, two-step filemark retry), file 5 ? counter 9 (fail)
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            agent.SimulateFileFailures.Enabled = true;
            agent.SimulateFileFailures.EveryNth = failEveryN;
            agent.SimulateFileFailures.Counter = failEveryN - 1;

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
        const int failEveryN = 2; // 2nd file fails ? abort

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

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            agent.SimulateFileFailures.Enabled = true;
            agent.SimulateFileFailures.EveryNth = failEveryN;

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

        using var fixture = new VirtualTapeFixture(profile);

        // Backup files normally
        fixture.BackupFiles(tree.Files);
        int expectedSets = fixture.TOC.Count;

        // Now re-write TOC with 1st copy failing (bit 0 = 1)
        using var writeAgent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        writeAgent.SimulateTOCFailureMask = 1;
        bool tocWriteOk = writeAgent.BackupTOC();
        Assert.True(tocWriteOk, "BackupTOC should succeed when only 1st copy fails");

        // Restore TOC — should recover from the 2nd copy
        using var readAgent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        bool tocReadOk = readAgent.RestoreTOC();
        Assert.True(tocReadOk, "RestoreTOC should succeed from 2nd copy");
        Assert.Equal(expectedSets, readAgent.TOC.Count);
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

        using var fixture = new VirtualTapeFixture(profile);

        // Backup files normally first (writes a valid TOC)
        fixture.BackupFiles(tree.Files);

        // Now try to re-write TOC with both copies failing (bits 0+1 = 3)
        using var writeAgent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        writeAgent.SimulateTOCFailureMask = 3;
        bool tocWriteOk = writeAgent.BackupTOC(enforce: true);
        Assert.False(tocWriteOk, "BackupTOC should fail when both copies fail");
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

        using var fixture = new VirtualTapeFixture(profile);

        // Backup files and TOC normally
        fixture.BackupFiles(tree.Files);
        int expectedSets = fixture.TOC.Count;

        // Now restore TOC with 1st copy failing during read (bit 0 = 1)
        using var readAgent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        readAgent.SimulateTOCFailureMask = 1;
        bool tocReadOk = readAgent.RestoreTOC();
        Assert.True(tocReadOk, "RestoreTOC should succeed from 2nd copy when 1st fails");
        Assert.Equal(expectedSets, readAgent.TOC.Count);
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

        using var fixture = new VirtualTapeFixture(profile);

        // Backup files and TOC normally
        fixture.BackupFiles(tree.Files);

        // Both copies fail during restore (bits 0+1+2 = 7 covers the 3rd attempt too)
        using var readAgent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        readAgent.SimulateTOCFailureMask = 7;
        bool tocReadOk = readAgent.RestoreTOC();
        Assert.False(tocReadOk, "RestoreTOC should fail when all copies fail");
    }

#endif // DEBUG

    #endregion


    // ═══════════════════════════════════════════════════════════════════════════
    //  Step 9.0 addendum to ErrorHandlingTests.cs
    //
    //  The existing suite asserts COUNTERS ("4 failed"). These assert the
    //  DIAGNOSIS ("…and here is why") — the thing the latch was built to preserve
    //  and the thing no existing test would notice the loss of.
    // ═══════════════════════════════════════════════════════════════════════════

    #region *** (H) Failure diagnosis survives the operation ***

#if DEBUG

    /// <summary>
    /// The core latch property: a failure on an early file must still be reported after LATER files
    ///  succeed — every success calls <c>ResetError()</c>, so the live error state is long gone by the
    ///  time the operation returns.
    /// </summary>
    /// <remarks>
    /// Deliberately fails only the FIRST file (<c>Counter = EveryNth - 1</c> fires immediately, then
    ///  <c>EveryNth</c> is large enough that nothing else fails). Without the latch the returned result
    ///  would be <c>(false, 0, "")</c> — a failure with no diagnosis, which is precisely the defect.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_EarlyFailure_LateSuccesses_DiagnosisSurvives(DriveProfile profile)
    {
        const int fileCount = 8;

        using var tree = new TempFileTree();
        tree.AddFiles("earlyfail", count: fileCount, minSize: 512, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        using var fixture = new VirtualTapeFixture(profile);
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Early failure";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();
        agent.SimulateFileFailures.Enabled = true;
        agent.SimulateFileFailures.EveryNth = 100;          // effectively "never again"
        agent.SimulateFileFailures.Counter = 99;            // …but fire on the very first file

        TapeResult result = agent.BackupFileListToCurrentSet(
            newSet: true, tree.Files, ignoreFailures: true, notifiable);

        Assert.False(result.Success, "one file failed, so the operation must report failure");

        // THE assertion: the diagnosis survived seven subsequent successes.
        Assert.NotEqual(0u, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage),
            "the latched diagnosis must survive later successes");
        Assert.Contains("Simulated", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // …and the counters confirm the later files really did succeed.
        var stats = notifiable.BatchEnds[^1].Stats;
        Assert.Equal(1, stats.FilesFailed);
        Assert.Equal(fileCount - 1, stats.FilesSucceeded);
    }

    /// <summary>
    /// FIRST failure, not last: later failures are usually consequences, so the earliest one names the
    ///  cause. Two distinct failure kinds are injected; the result must carry the first.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_MultipleFailures_ReportsTheFirst(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("multifail", count: 6, minSize: 512, maxSize: 4 * 1024);

        // A missing file inserted mid-list produces a DIFFERENT exception (FileNotFound) than the
        //  simulator's TapeIOException — so the message alone identifies which one was latched.
        var fileList = new List<string>(tree.Files);
        fileList.Insert(4, Path.Combine(tree.RootPath, "does_not_exist.dat"));

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        using var fixture = new VirtualTapeFixture(profile);
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Multiple failures";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();
        agent.SimulateFileFailures.Enabled = true;
        agent.SimulateFileFailures.EveryNth = 100;
        agent.SimulateFileFailures.Counter = 99;            // file #1 fails (simulated)

        TapeResult result = agent.BackupFileListToCurrentSet(
            newSet: true, fileList, ignoreFailures: true, notifiable);

        Assert.False(result.Success);
        Assert.True(notifiable.FilesFailed.Count >= 2, "both failures must have occurred");

        // The FIRST failure (simulated, file #1) — not the later FileNotFound.
        Assert.Contains("Simulated", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A user-requested abort deliberately does NOT latch (it is not a fault), so the fallback in
    ///  <c>BuildFailure</c> is what supplies the diagnosis. Without it the result would be
    ///  <c>(false, 0, "")</c> — failure with nothing to say.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_Abort_YieldsCancelledNotEmptyDiagnosis(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("abortdiag", count: 10, minSize: 512, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable { AbortAfterNPreProcessed = 3 };

        using var fixture = new VirtualTapeFixture(profile);
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Abort diagnosis";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();
        TapeResult result = agent.BackupFileListToCurrentSet(
            newSet: true, tree.Files, ignoreFailures: true, notifiable);

        Assert.False(result.Success);
        Assert.True(agent.IsAbortRequested);

        // Never a silent failure: the fallback names the cause.
        Assert.NotEqual(0u, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage),
            "an aborted operation must still carry a diagnosis");
    }

    /// <summary>
    /// A successful operation must leave NO latched failure — otherwise a later
    ///  <c>FailedOperationResult</c> on the same agent would report a stale diagnosis.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_CleanRun_LeavesNoLatchedFailure(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("clean", count: 5, minSize: 512, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Clean run";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();
        Assert.True(agent.BackupFileListToCurrentSet(
            newSet: true, tree.Files, ignoreFailures: false, fileNotify: null));

        Assert.True(agent.LastResult.Success, "a clean run must leave LastResult clean");
    }

    /// <summary>
    /// A retry that succeeds must clear the way for a clean result: the retry path calls
    ///  <c>ResetError()</c>, and nothing should latch a failure the retry then repaired.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_SuccessfulRetry_LeavesCleanResult(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("retrydiag", count: 6, minSize: 512, maxSize: 4 * 1024);

        var retried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notifiable = new TestNotifiable
        {
            FailedActionFunc = (fd, _) =>
                retried.Add(fd.FileDescr.FullName) ? FileFailedAction.Retry : FileFailedAction.Skip
        };

        using var fixture = new VirtualTapeFixture(profile);
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Retry diagnosis";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();
        agent.SimulateFileFailures.Enabled = true;
        agent.SimulateFileFailures.EveryNth = 3;

        TapeResult result = agent.BackupFileListToCurrentSet(
            newSet: true, tree.Files, ignoreFailures: true, notifiable);

        Assert.True(result.Success, "every file succeeded after its retry");
        Assert.True(agent.LastResult.Success,
            "a repaired failure must not linger in the latch");
        Assert.True(notifiable.FilesFailed.Count > 0, "retries must actually have been exercised");
    }

    /// <summary>
    /// The dual-copy rescue: a failed FIRST TOC copy that the second copy saves must leave the agent
    ///  reporting success — the reset before the second attempt is what makes this true.
    /// </summary>
    [Theory]
    [MemberData(nameof(PartitionedTOCProfiles))]
    public void TOCBackup_FirstCopyRescued_LeavesCleanResult(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("tocrescue", count: 3, minSize: 512, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files);

        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        agent.SimulateTOCFailureMask = 1;   // 1st copy fails, 2nd succeeds

        TapeResult result = agent.BackupTOC();

        Assert.True(result.Success, "one good copy is enough");
        Assert.True(agent.LastResult.Success,
            "the rescued first copy must not leave a latched failure behind");
    }

    /// <summary>
    /// Both copies failing must yield a diagnosis, not a bare false.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOCBackup_BothCopiesFail_CarriesDiagnosis(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("tocboth", count: 3, minSize: 512, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files);

        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        agent.SimulateTOCFailureMask = 3;

        TapeResult result = agent.BackupTOC(enforce: true);

        Assert.False(result.Success);
        Assert.NotEqual(0u, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage),
            "a total TOC failure must explain itself");
    }

#endif // DEBUG

    #endregion

    #region *** (I) Medium-level faults — the injector as a real drive error ***

#if DEBUG
    /// <summary>
    /// A genuine drive-level write fault, injected below the agent. Unlike <c>SimulateFileFailures</c>
    ///  — which fabricates an exception inside the agent — this is an error the MEDIUM produces, so it
    ///  exercises the real error-propagation chain: medium → drive → manager → agent.
    /// </summary>
    /// <remarks>
    /// <c>EveryNth</c> rather than <c>FailOnce</c>: a single block failure inside the packer may be
    ///  absorbed by buffering, so a recurring fault is needed to guarantee the agent sees one.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_MediaWriteFaults_ProduceADiagnosis(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mwfault", count: 10, minSize: 8 * 1024, maxSize: 32 * 1024);

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        using var fixture = new VirtualTapeFixture(profile);
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Media write faults";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        // Fault the medium itself, not the agent.
        fixture.Backend.ContentWriteFaults.Enabled = true;
        fixture.Backend.ContentWriteFaults.EveryNth = 4;

        using var agent = fixture.CreateBackupAgent();
        TapeResult result = agent.BackupFileListToCurrentSet(
            newSet: true, tree.Files, ignoreFailures: true, notifiable);

        fixture.Backend.ContentWriteFaults.Reset();

        Assert.True(fixture.Backend.ContentWriteFaults.Occurrences > 0,
            "the injector must actually have fired");

        // Whatever the agent made of them, a medium fault must never yield a silent failure.
        if (!result.Success)
        {
            Assert.NotEqual(0u, result.ErrorCode);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage),
                "a medium-level write fault must carry a diagnosis");
        }
    }

    /// <summary>
    /// Read faults on restore: the agent must surface them as per-file failures WITH a reason, and the
    ///  files that were not faulted must still restore.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_MediaReadFaults_ProduceADiagnosis(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mrfault", count: 10, minSize: 8 * 1024, maxSize: 32 * 1024);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_MRFault_{Guid.NewGuid():N}");
        try
        {
            using var fixture = new VirtualTapeFixture(profile);
            fixture.BackupFiles(tree.Files);

            var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

            fixture.Backend.ContentReadFaults.Enabled = true;
            fixture.Backend.ContentReadFaults.EveryNth = 6;

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            TapeResult result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, notifiable);

            fixture.Backend.ContentReadFaults.Reset();

            Assert.True(fixture.Backend.ContentReadFaults.Occurrences > 0,
                "the injector must actually have fired");

            if (!result.Success)
            {
                Assert.NotEqual(0u, result.ErrorCode);
                Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage),
                    "a medium-level read fault must carry a diagnosis");
            }

            notifiable.AssertStatsInvariant();
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// SILENT corruption — the mode no error code can describe. The drive reports success, the bytes are
    ///  wrong, and only the per-file CRC catches it. This is the end-to-end proof that the hash chain
    ///  earns its keep, and that the resulting failure carries a CRC diagnosis rather than a generic one.
    /// </summary>
    /// <remarks>
    /// Corrupting on the READ side keeps the tape itself intact, so the failure is attributable purely to
    ///  the delivered bytes. The agent should raise <c>ERROR_CRC</c> for the affected file.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_SilentCorruption_IsCaughtByCrc(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("corrupt", count: 6, minSize: 16 * 1024, maxSize: 32 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, hashAlgorithm: TapeHashAlgorithm.Crc64);

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        // Silent: full byte count, no error, a couple of bits wrong near the front of a block.
        fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 64);

        using var agent = fixture.CreateValidateAgent();
        TapeResult result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, notifiable);

        Assert.Equal(1, fixture.Backend.ContentReadFaults.Occurrences);
        fixture.Backend.ContentReadFaults.Reset();

        // The drive said nothing was wrong — only the CRC knows.
        Assert.False(result.Success, "silent corruption must be caught by the per-file hash");
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Contains("CRC", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var stats = notifiable.BatchEnds[^1].Stats;
        Assert.True(stats.FilesFailed >= 1, "at least the corrupted file must fail");
    }

#endif // DEBUG

    #endregion

}
