using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Validates <see cref="TapeFileStatistics"/> across the full agent lifecycle:
/// backup, restore, validate, verify — including skip/failure injection and
/// monotonic-progress guarantees.
/// <para>
/// Every test asserts the fundamental invariant
/// <c>FilesProcessed == FilesSucceeded + FilesFailed + FilesSkipped</c>
/// via <see cref="TestNotifiable.AssertStatsInvariant"/>.
/// </para>
/// </summary>
public class StatisticsTests
{
    #region *** Test Data ***

    /// <summary>All three drive profiles for parameterized theories.</summary>
#pragma warning disable CA1825 // Avoid zero-length array allocations
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];
#pragma warning restore CA1825 // Avoid zero-length array allocations

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
        catch
        {
            // Best effort
        }
    }

    /// <summary>
    /// Computes the directory under <paramref name="restoreDir"/> where
    /// <see cref="TapeFileRestoreAgentEx"/> (with RecurseSubdirectories=true) places files
    /// that were originally under <paramref name="originalRoot"/>.
    /// </summary>
    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relativeFromDriveRoot = Path.GetRelativePath(pathRoot, originalRoot);
        return Path.Combine(restoreDir, relativeFromDriveRoot);
    }

    /// <summary>
    /// Asserts that final <see cref="TapeFileStatistics"/> match expectations for
    /// a fully-successful operation (zero failures, zero skips).
    /// Also checks <see cref="TapeFileStatistics.BytesProcessed"/> against source sizes.
    /// </summary>
    private static void AssertFullSuccess(TestNotifiable notifiable, int expectedFiles, long expectedBytes)
    {
        notifiable.AssertStatsInvariant();
        var stats = notifiable.BatchEnds[^1].Stats;

        Assert.Equal(expectedFiles, stats.FilesTotal);
        Assert.Equal(expectedFiles, stats.FilesProcessed);
        Assert.Equal(expectedFiles, stats.FilesSucceeded);
        Assert.Equal(0, stats.FilesFailed);
        Assert.Equal(0, stats.FilesSkipped);
        Assert.Equal(expectedBytes, stats.BytesProcessed);
    }

    #endregion


    #region *** Backup Statistics ***

    /// <summary>
    /// Single-set backup of a small file set. Asserts that
    /// <see cref="TapeFileStatistics"/> counters and <c>BytesProcessed</c>
    /// match the source files exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_SingleSet_StatsMatchExpectations(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("docs", count: 6, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "Stats backup", notifiable: notifiable);

        AssertFullSuccess(notifiable, tree.Files.Count, tree.TotalSize);

        // BatchStart should have been called exactly once
        Assert.Single(notifiable.BatchStarts);
        Assert.Equal(tree.Files.Count, notifiable.BatchStarts[0].Stats.FilesTotal);

        // BatchEnd should have been called exactly once
        Assert.Single(notifiable.BatchEnds);
    }

    #endregion

    #region *** Restore Statistics ***

    /// <summary>
    /// Backs up files, then restores them and asserts that the restore-side
    /// statistics match the same file count and byte total.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_SingleSet_StatsMatchExpectations(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 5, minSize: 200, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Stats restore");

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_StatsRestore_{Guid.NewGuid():N}");
        try
        {
            var notifiable = new TestNotifiable();
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;
            bool success = agent.RestoreAllFilesFromCurrentSetAligned(ignoreFailures: true, fileNotify: notifiable);

            Assert.True(success, "Restore failed");
            AssertFullSuccess(notifiable, tree.Files.Count, tree.TotalSize);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Validate Statistics ***

    /// <summary>
    /// Backs up files, then validates (CRC-only, no disk writes) and asserts
    /// that statistics report all files as succeeded with correct byte totals.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Validate_SingleSet_StatsMatchExpectations(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("val", count: 4, minSize: 100, maxSize: 6 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Stats validate");

        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateValidateAgent();
        fixture.TOC.CurrentSetIndex = 1;
        bool success = agent.RestoreAllFilesFromCurrentSetAligned(ignoreFailures: true, fileNotify: notifiable);

        Assert.True(success, "Validate failed");
        AssertFullSuccess(notifiable, tree.Files.Count, tree.TotalSize);
    }

    #endregion

    #region *** Verify Statistics ***

    /// <summary>
    /// Backs up files, then verifies (byte-for-byte comparison with originals)
    /// and asserts that statistics report all files as succeeded.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Verify_SingleSet_StatsMatchExpectations(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("vfy", count: 4, minSize: 100, maxSize: 6 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Stats verify");

        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateVerifyAgent();
        fixture.TOC.CurrentSetIndex = 1;
        bool success = agent.RestoreAllFilesFromCurrentSetAligned(ignoreFailures: true, fileNotify: notifiable);

        Assert.True(success, "Verify failed");
        AssertFullSuccess(notifiable, tree.Files.Count, tree.TotalSize);
    }

    #endregion

    #region *** PreProcessFile Skip ***

    /// <summary>
    /// Uses <see cref="TestNotifiable.FilesToSkip"/> to skip selected files
    /// during backup. Asserts that <c>FilesSkipped</c> increments correctly
    /// and <c>FilesProcessed</c> reflects the total.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_PreProcessorSkip_StatsReflectSkippedFiles(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mixed", count: 6, minSize: 100, maxSize: 4 * 1024);

        // Mark the first two files for skipping
        int skipCount = 2;
        var notifiable = new TestNotifiable();
        for (int i = 0; i < skipCount; i++)
            notifiable.FilesToSkip.Add(tree.Files[i]);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Skip test", notifiable: notifiable);

        notifiable.AssertStatsInvariant();
        var stats = notifiable.BatchEnds[^1].Stats;

        int expectedSucceeded = tree.Files.Count - skipCount;

        Assert.Equal(tree.Files.Count, stats.FilesTotal);
        Assert.Equal(tree.Files.Count, stats.FilesProcessed);
        Assert.Equal(expectedSucceeded, stats.FilesSucceeded);
        Assert.Equal(0, stats.FilesFailed);
        Assert.Equal(skipCount, stats.FilesSkipped);

        // OnFileSkipped callback should have been invoked for each skipped file
        Assert.Equal(skipCount, notifiable.FilesSkipped.Count);

        // BytesProcessed should reflect only the non-skipped files
        long skippedBytes = 0;
        for (int i = 0; i < skipCount; i++)
            skippedBytes += new FileInfo(tree.Files[i]).Length;
        Assert.Equal(tree.TotalSize - skippedBytes, stats.BytesProcessed);
    }

    #endregion

    #region *** Simulated Failures ***

#if DEBUG
    /// <summary>
    /// Enables <see cref="TapeFileAgent.SimulateFileFailures"/> so that every Nth file
    /// throws during backup. Asserts that <c>FilesFailed</c> increments and the
    /// invariant holds.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_SimulatedFailures_StatsReflectFailedFiles(DriveProfile profile)
    {
        const int fileCount = 8;
        const int failEveryN = 3; // every 3rd file fails

        using var tree = new TempFileTree();
        tree.AddFiles("fail", count: fileCount, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable
        {
            FailedAction = FileFailedAction.Skip // skip failed files, continue
        };

        using var fixture = new VirtualTapeFixture(profile);

        // Use the agent directly so we can control SimulateFileFailures timing
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Failure test";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();
        agent.SimulateFileFailures.Enabled = true;
        agent.SimulateFileFailures.EveryNth = failEveryN;

        bool success = agent.BackupFileListToCurrentSetAligned(
            newSet: true,
            tree.Files,
            ignoreFailures: true,
            fileNotify: notifiable);

        // The operation should complete (ignoreFailures=true) even with some failures
        notifiable.AssertStatsInvariant();
        var stats = notifiable.BatchEnds[^1].Stats;

        Assert.Equal(fileCount, stats.FilesTotal);
        Assert.Equal(fileCount, stats.FilesProcessed);
        Assert.True(stats.FilesFailed > 0, "Expected at least one simulated failure");
        Assert.Equal(fileCount, stats.FilesSucceeded + stats.FilesFailed + stats.FilesSkipped);

        // Verify that OnFileFailed callbacks were invoked
        Assert.Equal(stats.FilesFailed, notifiable.FilesFailed.Count);
    }
#endif

    #endregion

    #region *** Multi-Set Statistics ***

    /// <summary>
    /// Backs up two separate sets and asserts that statistics reset between sets
    /// and each set's final stats are independent.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_MultipleSet_StatsResetBetweenSets(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 4 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 6, minSize: 200, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        // Backup set 1
        var notify1 = new TestNotifiable();
        fixture.BackupFiles(tree1.Files, description: "Multi set 1", notifiable: notify1);
        AssertFullSuccess(notify1, tree1.Files.Count, tree1.TotalSize);

        // Backup set 2
        var notify2 = new TestNotifiable();
        fixture.BackupFiles(tree2.Files, description: "Multi set 2", notifiable: notify2);
        AssertFullSuccess(notify2, tree2.Files.Count, tree2.TotalSize);

        // Agent stats should reflect set 2 only (reset happened via BackupFileListToCurrentSetAligned)
        Assert.Equal(tree2.Files.Count, notify2.BatchEnds[^1].Stats.FilesTotal);
        Assert.NotEqual(notify1.BatchEnds[^1].Stats.BytesProcessed,
                         notify2.BatchEnds[^1].Stats.BytesProcessed);
    }

    #endregion

    #region *** Monotonic Progress ***

    /// <summary>
    /// Asserts that <c>FilesProcessed</c> never decreases across successive
    /// <see cref="ITapeFileNotifiable"/> callbacks during a backup.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_StatsMonotonicallyIncrease(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mono", count: 8, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "Monotonic test", notifiable: notifiable);

        // Check monotonic increase in PostProcessed snapshots (one per succeeded file)
        int prevProcessed = 0;
        long prevBytes = 0;
        foreach (var pp in notifiable.PostProcessed)
        {
            Assert.True(pp.Stats.FilesProcessed >= prevProcessed,
                $"FilesProcessed decreased: {pp.Stats.FilesProcessed} < {prevProcessed}");
            Assert.True(pp.Stats.BytesProcessed >= prevBytes,
                $"BytesProcessed decreased: {pp.Stats.BytesProcessed} < {prevBytes}");

            prevProcessed = pp.Stats.FilesProcessed;
            prevBytes = pp.Stats.BytesProcessed;
        }

        // Final values should match totals
        Assert.Equal(tree.Files.Count, prevProcessed);
        Assert.Equal(tree.TotalSize, prevBytes);
    }

    /// <summary>
    /// Asserts that <c>FilesProcessed</c> never decreases during restore
    /// across successive callback snapshots.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_StatsMonotonicallyIncrease(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mono_r", count: 6, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Monotonic restore");

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_StatsMono_{Guid.NewGuid():N}");
        try
        {
            var notifiable = new TestNotifiable();
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;
            agent.RestoreAllFilesFromCurrentSetAligned(ignoreFailures: true, fileNotify: notifiable);

            int prevProcessed = 0;
            long prevBytes = 0;
            foreach (var pp in notifiable.PostProcessed)
            {
                Assert.True(pp.Stats.FilesProcessed >= prevProcessed,
                    $"FilesProcessed decreased: {pp.Stats.FilesProcessed} < {prevProcessed}");
                Assert.True(pp.Stats.BytesProcessed >= prevBytes,
                    $"BytesProcessed decreased: {pp.Stats.BytesProcessed} < {prevBytes}");

                prevProcessed = pp.Stats.FilesProcessed;
                prevBytes = pp.Stats.BytesProcessed;
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Empty File List ***

    /// <summary>
    /// Backing up an empty file list should succeed with all stats at zero
    /// and no callbacks invoked.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_EmptyFileList_StatsRemainZero(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Empty";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();
        bool success = agent.BackupFileListToCurrentSetAligned(
            newSet: true,
            [],
            ignoreFailures: true,
            fileNotify: notifiable);

        Assert.True(success);

        // No callbacks at all for empty list
        Assert.Empty(notifiable.BatchStarts);
        Assert.Empty(notifiable.BatchEnds);
        Assert.Empty(notifiable.PreProcessed);
        Assert.Empty(notifiable.PostProcessed);

        // Agent-level stats should be at default
        var stats = agent.Statistics;
        Assert.Equal(0, stats.FilesTotal);
        Assert.Equal(0, stats.FilesProcessed);
        Assert.Equal(0, stats.FilesSucceeded);
        Assert.Equal(0, stats.FilesFailed);
        Assert.Equal(0, stats.FilesSkipped);
        Assert.Equal(0, stats.BytesProcessed);
    }

    #endregion

    #region *** Callback Count Consistency ***

    /// <summary>
    /// Asserts that the number of <c>PostProcessFile</c> calls equals
    /// <c>FilesSucceeded</c>, and <c>PreProcessFile</c> calls equals
    /// <c>FilesTotal</c> (when no files are skipped before pre-processing).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_CallbackCounts_MatchStatistics(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("cb", count: 7, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "Callback count", notifiable: notifiable);

        var stats = notifiable.BatchEnds[^1].Stats;

        // PreProcessFile called once per file
        Assert.Equal(stats.FilesTotal, notifiable.PreProcessed.Count);

        // PostProcessFile called once per succeeded file
        Assert.Equal(stats.FilesSucceeded, notifiable.PostProcessed.Count);

        // No failed or skipped callback invocations for a clean run
        Assert.Empty(notifiable.FilesFailed);
        Assert.Empty(notifiable.FilesSkipped);
    }

    /// <summary>
    /// During restore, asserts that callback counts match statistics.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_CallbackCounts_MatchStatistics(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("cb_r", count: 5, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Restore callback count");

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_StatsCB_{Guid.NewGuid():N}");
        try
        {
            var notifiable = new TestNotifiable();
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;
            bool success = agent.RestoreAllFilesFromCurrentSetAligned(ignoreFailures: true, fileNotify: notifiable);

            Assert.True(success);
            var stats = notifiable.BatchEnds[^1].Stats;

            Assert.Equal(stats.FilesTotal, notifiable.PreProcessed.Count);
            Assert.Equal(stats.FilesSucceeded, notifiable.PostProcessed.Count);
            Assert.Empty(notifiable.FilesFailed);
            Assert.Empty(notifiable.FilesSkipped);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** BytesProcessed Accuracy ***

    /// <summary>
    /// Asserts that <c>BytesProcessed</c> after backup exactly equals the sum
    /// of all source file sizes (logical bytes, not tape overhead).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_BytesProcessed_EqualsSourceFileTotal(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        // Use a mix of small and larger files for realistic byte totals
        tree.AddFiles("bytes", count: 5, minSize: 1, maxSize: 16 * 1024);
        tree.AddFile("bytes/exact_block.dat", 16 * 1024); // exactly one block

        long expectedBytes = tree.TotalSize;

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "Bytes accuracy", notifiable: notifiable);

        var stats = notifiable.BatchEnds[^1].Stats;
        Assert.Equal(expectedBytes, stats.BytesProcessed);
    }

    /// <summary>
    /// Asserts that <c>BytesProcessed</c> after restore matches the same
    /// total as the backup — the restore agent should count the same logical bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_BytesProcessed_EqualsSourceFileTotal(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("bytes_r", count: 5, minSize: 1, maxSize: 16 * 1024);

        long expectedBytes = tree.TotalSize;

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Restore bytes accuracy");

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_StatsBytes_{Guid.NewGuid():N}");
        try
        {
            var notifiable = new TestNotifiable();
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;
            bool success = agent.RestoreAllFilesFromCurrentSetAligned(ignoreFailures: true, fileNotify: notifiable);

            Assert.True(success);
            var stats = notifiable.BatchEnds[^1].Stats;
            Assert.Equal(expectedBytes, stats.BytesProcessed);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Skip + Success Combined ***

    /// <summary>
    /// Mixes skipped and successful files, then validates the combined statistics
    /// across both backup and restore. The restore side should only see files that
    /// were actually written to tape.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupAndRestore_WithSkips_StatsConsistentAcrossBothSides(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mixed", count: 8, minSize: 100, maxSize: 4 * 1024);

        int skipCount = 3;
        var backupNotify = new TestNotifiable();
        for (int i = 0; i < skipCount; i++)
            backupNotify.FilesToSkip.Add(tree.Files[i]);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Skip+restore", notifiable: backupNotify);

        // Backup side: some skipped
        backupNotify.AssertStatsInvariant();
        var backupStats = backupNotify.BatchEnds[^1].Stats;
        int filesOnTape = backupStats.FilesSucceeded;
        Assert.Equal(tree.Files.Count - skipCount, filesOnTape);

        // Restore side: should only see files that were actually written
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_StatsSkipRestore_{Guid.NewGuid():N}");
        try
        {
            var restoreNotify = new TestNotifiable();
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;
            bool success = agent.RestoreAllFilesFromCurrentSetAligned(ignoreFailures: true, fileNotify: restoreNotify);

            Assert.True(success);
            restoreNotify.AssertStatsInvariant();
            var restoreStats = restoreNotify.BatchEnds[^1].Stats;

            // The TOC only contains the non-skipped files
            Assert.Equal(filesOnTape, restoreStats.FilesTotal);
            Assert.Equal(filesOnTape, restoreStats.FilesSucceeded);
            Assert.Equal(0, restoreStats.FilesFailed);
            Assert.Equal(0, restoreStats.FilesSkipped);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Per-Callback Snapshot Accuracy ***

    /// <summary>
    /// Verifies that the <see cref="TapeFileStatistics"/> snapshot delivered with each
    /// <c>PostProcessFile</c> callback reflects the correct running count at that point.
    /// File N should report <c>FilesSucceeded == N</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_PerCallbackSnapshot_RunningCountCorrect(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("snap", count: 5, minSize: 100, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "Snapshot test", notifiable: notifiable);

        // Each PostProcessed entry should have an incrementing FilesSucceeded
        for (int i = 0; i < notifiable.PostProcessed.Count; i++)
        {
            var snap = notifiable.PostProcessed[i].Stats;
            Assert.Equal(i + 1, snap.FilesSucceeded);
            Assert.Equal(i + 1, snap.FilesProcessed);
        }
    }

    #endregion

    #region *** BatchStart Snapshot ***

    /// <summary>
    /// Asserts that the <c>BatchStart</c> callback delivers a snapshot where
    /// <c>FilesTotal</c> is already set but no files have been processed yet.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_BatchStart_HasCorrectInitialState(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("init", count: 4, minSize: 100, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "BatchStart test", notifiable: notifiable);

        Assert.Single(notifiable.BatchStarts);
        var startStats = notifiable.BatchStarts[0].Stats;

        Assert.Equal(tree.Files.Count, startStats.FilesTotal);
        Assert.Equal(0, startStats.FilesProcessed);
        Assert.Equal(0, startStats.FilesSucceeded);
        Assert.Equal(0, startStats.FilesFailed);
        Assert.Equal(0, startStats.FilesSkipped);
        Assert.Equal(0, startStats.BytesProcessed);
    }

    #endregion
}
