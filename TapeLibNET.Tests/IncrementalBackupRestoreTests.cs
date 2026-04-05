using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Comprehensive tests for incremental backup and restore behavior.
/// Exercises multi-wave backup chains (full → modify → incremental → modify → incremental)
/// and verifies:
/// <list type="bullet">
///   <item>Backup statistics: correct skip/succeed counts per wave</item>
///   <item>Incremental restore: yields latest file versions across the chain</item>
///   <item>Non-incremental restore: yields only files from the targeted set</item>
///   <item>Version correctness: byte-level content matches expected version pattern</item>
///   <item>Edge cases: all-unchanged wave, new files, chain reset via full backup</item>
///   <item>TOC persistence: incremental restore after save/reload TOC</item>
///   <item>Restore statistics: consistent counts across multi-set incremental chain</item>
/// </list>
/// All three drive profiles (Setmarks, Partitions, SeqFilemarks) are exercised.
/// </summary>
public class IncrementalBackupRestoreTests
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
#pragma warning restore CA1825 // Avoid zero-length array allocations

    #endregion


    #region *** Multi-Wave Chain Setup ***

    /// <summary>Number of initial files created in the multi-wave chain.</summary>
    private const int InitialFileCount = 8;

    /// <summary>
    /// Performs a 4-wave incremental backup chain on the given fixture and temp tree:
    /// <list type="bullet">
    ///   <item>Wave 0 → set 1: Full backup of 8 files at version 0</item>
    ///   <item>Wave 1 → set 2: Modify files[0,1,2] to v1 → incremental (3 backed up, 5 skipped)</item>
    ///   <item>Wave 2 → set 3: Modify files[3,4] to v2 → incremental (2 backed up, 6 skipped)</item>
    ///   <item>Wave 3 → set 4: Modify files[0] to v3 + add new file → incremental (2 backed up, 7 skipped)</item>
    /// </list>
    /// </summary>
    /// <returns>Backup statistics per wave and the path of the file added in wave 3.</returns>
    private static (TapeFileStatistics[] Stats, string NewFilePath) SetupFourWaveChain(
        VirtualTapeFixture fixture, TempFileTree tree)
    {
        var stats = new TapeFileStatistics[4];

        // Create initial 8 files (1–8 KB each)
        tree.AddFiles("data", count: InitialFileCount, minSize: 1024, maxSize: 8 * 1024);

        // Wave 0: Full backup — all 8 files at version 0
        stats[0] = fixture.BackupFiles(tree.Files, "Full backup");

        // Wave 1: Modify files[0,1,2] to version 1 → incremental
        tree.ModifyFile(tree.Files[0], version: 1);
        tree.ModifyFile(tree.Files[1], version: 1);
        tree.ModifyFile(tree.Files[2], version: 1);
        stats[1] = fixture.BackupFiles(tree.Files, "Incremental 1", incremental: true);

        // Wave 2: Modify files[3,4] to version 2 → incremental
        tree.ModifyFile(tree.Files[3], version: 2);
        tree.ModifyFile(tree.Files[4], version: 2);
        stats[2] = fixture.BackupFiles(tree.Files, "Incremental 2", incremental: true);

        // Wave 3: Modify files[0] to version 3 + add a new file → incremental
        tree.ModifyFile(tree.Files[0], version: 3);
        string newFile = tree.AddFile("data/file_new.dat", 4096);
        stats[3] = fixture.BackupFiles(tree.Files, "Incremental 3", incremental: true);

        return (stats, newFile);
    }

    /// <summary>
    /// Expected file version after incremental restore from set 4 (latest wave).
    /// Index matches position in <see cref="TempFileTree.Files"/>:
    ///   0..7 = original files, 8 = new file added in wave 3.
    /// </summary>
    private static readonly int[] s_versionsAtWave3 = [3, 1, 1, 2, 2, 0, 0, 0, 0];

    /// <summary>
    /// Expected file version after incremental restore from set 3 (wave 2).
    /// Only the 8 original files — new file (added in wave 3) is absent.
    /// </summary>
    private static readonly int[] s_versionsAtWave2 = [1, 1, 1, 2, 2, 0, 0, 0];

    /// <summary>
    /// Expected file version after incremental restore from set 2 (wave 1).
    /// Only the 8 original files.
    /// </summary>
    private static readonly int[] s_versionsAtWave1 = [1, 1, 1, 0, 0, 0, 0, 0];

    #endregion


    #region *** Backup Statistics Tests ***

    /// <summary>
    /// Verifies that incremental backup correctly reports FilesSucceeded and FilesSkipped
    /// at each wave of the 4-wave chain.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiWave_IncrementalBackup_CorrectSkipAndSucceedCounts(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);

        var (stats, _) = SetupFourWaveChain(fixture, tree);

        // Wave 0: Full backup — all 8 files backed up, none skipped
        Assert.Equal(InitialFileCount, stats[0].FilesTotal);
        Assert.Equal(InitialFileCount, stats[0].FilesSucceeded);
        Assert.Equal(0, stats[0].FilesSkipped);
        Assert.Equal(0, stats[0].FilesFailed);

        // Wave 1: 3 modified → backed up, 5 unchanged → skipped
        Assert.Equal(InitialFileCount, stats[1].FilesTotal);
        Assert.Equal(3, stats[1].FilesSucceeded);
        Assert.Equal(5, stats[1].FilesSkipped);
        Assert.Equal(0, stats[1].FilesFailed);

        // Wave 2: 2 modified → backed up, 6 unchanged → skipped
        Assert.Equal(InitialFileCount, stats[2].FilesTotal);
        Assert.Equal(2, stats[2].FilesSucceeded);
        Assert.Equal(6, stats[2].FilesSkipped);
        Assert.Equal(0, stats[2].FilesFailed);

        // Wave 3: 1 re-modified + 1 new file → 2 backed up, 7 unchanged → skipped
        Assert.Equal(InitialFileCount + 1, stats[3].FilesTotal);
        Assert.Equal(2, stats[3].FilesSucceeded);
        Assert.Equal(7, stats[3].FilesSkipped);
        Assert.Equal(0, stats[3].FilesFailed);

        // TOC should have 4 sets with correct incremental flags
        Assert.Equal(4, fixture.TOC.Count);
        Assert.False(fixture.TOC[1].Incremental, "Set 1 should not be incremental");
        Assert.True(fixture.TOC[2].Incremental, "Set 2 should be incremental");
        Assert.True(fixture.TOC[3].Incremental, "Set 3 should be incremental");
        Assert.True(fixture.TOC[4].Incremental, "Set 4 should be incremental");

        // TOC set file counts should reflect only the files actually backed up (not skipped)
        Assert.Equal(InitialFileCount, fixture.TOC[1].Count);
        Assert.Equal(3, fixture.TOC[2].Count);
        Assert.Equal(2, fixture.TOC[3].Count);
        Assert.Equal(2, fixture.TOC[4].Count);
    }

    #endregion


    #region *** Incremental Restore Tests ***

    /// <summary>
    /// Incremental restore from the latest wave (set 4) should yield all files
    /// with their most recent version from anywhere in the chain.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void IncrementalRestore_FromLatestWave_AllFilesWithCorrectVersions(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);

        SetupFourWaveChain(fixture, tree);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_IncRestore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            fixture.TOC.CurrentSetIndex = 4;
            bool restored = restoreAgent.RestoreFilesFromCurrentSetInc(
                null, ignoreFailures: true);

            Assert.True(restored, "Incremental restore from set 4 failed");

            // All 9 files should be restored (8 original + 1 new)
            Assert.Equal(tree.Files.Count, restoreAgent.Statistics.FilesSucceeded);
            Assert.Equal(0, restoreAgent.Statistics.FilesFailed);

            // Source files on disk have the latest versions — FileComparer byte-for-byte works
            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, restoreRoot);

            // Also verify version patterns for explicit traceability
            AssertAllFileVersions(tree, restoreRoot, s_versionsAtWave3);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Incremental restore from set 3 (wave 2) should yield file versions as of that wave:
    /// files[0,1,2]=v1, files[3,4]=v2, files[5-7]=v0 — and NO new file from wave 3.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void IncrementalRestore_FromMiddleWave_VersionsAsOfThatPoint(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);

        SetupFourWaveChain(fixture, tree);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_IncRestore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            fixture.TOC.CurrentSetIndex = 3;
            bool restored = restoreAgent.RestoreFilesFromCurrentSetInc(
                null, ignoreFailures: true);

            Assert.True(restored, "Incremental restore from set 3 failed");

            // 8 files restored (no new file from wave 3)
            Assert.Equal(InitialFileCount, restoreAgent.Statistics.FilesSucceeded);
            Assert.Equal(0, restoreAgent.Statistics.FilesFailed);

            // Verify each original file has the expected version
            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            for (int i = 0; i < InitialFileCount; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                AssertFileHasVersion(Path.Combine(restoreRoot, rel), s_versionsAtWave2[i]);
            }

            // New file from wave 3 should NOT be present
            string newFileRel = Path.GetRelativePath(tree.RootPath, tree.Files[^1]);
            Assert.False(File.Exists(Path.Combine(restoreRoot, newFileRel)),
                "New file from wave 3 should not appear in restore from wave 2");
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Incremental restore from set 2 (wave 1) should yield:
    /// files[0,1,2]=v1, files[3-7]=v0 — the state after only the first incremental wave.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void IncrementalRestore_FromSecondWave_VersionsAsOfThatPoint(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);

        SetupFourWaveChain(fixture, tree);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_IncRestore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            fixture.TOC.CurrentSetIndex = 2;
            bool restored = restoreAgent.RestoreFilesFromCurrentSetInc(
                null, ignoreFailures: true);

            Assert.True(restored, "Incremental restore from set 2 failed");

            Assert.Equal(InitialFileCount, restoreAgent.Statistics.FilesSucceeded);

            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            for (int i = 0; i < InitialFileCount; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                AssertFileHasVersion(Path.Combine(restoreRoot, rel), s_versionsAtWave1[i]);
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Incremental restore from the full backup set (set 1, non-incremental) should
    /// behave identically to a non-incremental restore — returning all 8 files at version 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void IncrementalRestore_FromFullSet_SameAsNonIncremental(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);

        SetupFourWaveChain(fixture, tree);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_IncRestore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            fixture.TOC.CurrentSetIndex = 1;
            bool restored = restoreAgent.RestoreFilesFromCurrentSetInc(
                null, ignoreFailures: true);

            Assert.True(restored, "Incremental restore from full set failed");

            // All 8 original files at version 0
            Assert.Equal(InitialFileCount, restoreAgent.Statistics.FilesSucceeded);

            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            for (int i = 0; i < InitialFileCount; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                AssertFileHasVersion(Path.Combine(restoreRoot, rel), 0);
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Non-Incremental Restore Tests ***

    /// <summary>
    /// Non-incremental restore from each incremental set should yield ONLY the files
    /// that were actually backed up in that specific set — not the entire chain.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void NonIncrementalRestore_FromIncrementalSet_OnlyThatSetsFiles(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);

        SetupFourWaveChain(fixture, tree);

        // --- Set 2 (wave 1): files[0,1,2] at version 1 ---
        string restoreDir2 = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            using var agent2 = fixture.CreateRestoreAgent(restoreDir2);
            fixture.TOC.CurrentSetIndex = 2;
            Assert.True(agent2.RestoreAllFilesFromCurrentSet(ignoreFailures: true),
                "Non-incremental restore from set 2 failed");

            Assert.Equal(3, agent2.Statistics.FilesSucceeded);

            string root2 = RestoreEquivalentRoot(restoreDir2, tree.RootPath);
            for (int i = 0; i < 3; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                AssertFileHasVersion(Path.Combine(root2, rel), 1);
            }
            // files[3-7] should NOT be present
            for (int i = 3; i < InitialFileCount; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                Assert.False(File.Exists(Path.Combine(root2, rel)),
                    $"File index {i} should not be present in non-incremental restore from set 2");
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir2);
        }

        // --- Set 3 (wave 2): files[3,4] at version 2 ---
        string restoreDir3 = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            using var agent3 = fixture.CreateRestoreAgent(restoreDir3);
            fixture.TOC.CurrentSetIndex = 3;
            Assert.True(agent3.RestoreAllFilesFromCurrentSet(ignoreFailures: true),
                "Non-incremental restore from set 3 failed");

            Assert.Equal(2, agent3.Statistics.FilesSucceeded);

            string root3 = RestoreEquivalentRoot(restoreDir3, tree.RootPath);
            for (int i = 3; i < 5; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                AssertFileHasVersion(Path.Combine(root3, rel), 2);
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir3);
        }

        // --- Set 4 (wave 3): files[0] at v3 + new file at v0 ---
        string restoreDir4 = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            using var agent4 = fixture.CreateRestoreAgent(restoreDir4);
            fixture.TOC.CurrentSetIndex = 4;
            Assert.True(agent4.RestoreAllFilesFromCurrentSet(ignoreFailures: true),
                "Non-incremental restore from set 4 failed");

            Assert.Equal(2, agent4.Statistics.FilesSucceeded);

            string root4 = RestoreEquivalentRoot(restoreDir4, tree.RootPath);

            // files[0] at version 3
            string relFile0 = Path.GetRelativePath(tree.RootPath, tree.Files[0]);
            AssertFileHasVersion(Path.Combine(root4, relFile0), 3);

            // new file at version 0 (original TempFileTree pattern)
            string relNew = Path.GetRelativePath(tree.RootPath, tree.Files[^1]);
            AssertFileHasVersion(Path.Combine(root4, relNew), 0);

            // files[1-7] should NOT be present
            for (int i = 1; i < InitialFileCount; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                Assert.False(File.Exists(Path.Combine(root4, rel)),
                    $"File index {i} should not be present in non-incremental restore from set 4");
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir4);
        }
    }

    /// <summary>
    /// Non-incremental restore from the full backup set (set 1) should yield
    /// all 8 original files at version 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void NonIncrementalRestore_FromFullSet_AllOriginalVersions(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);

        SetupFourWaveChain(fixture, tree);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet(ignoreFailures: true),
                "Non-incremental restore from full set failed");

            Assert.Equal(InitialFileCount, restoreAgent.Statistics.FilesSucceeded);

            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            for (int i = 0; i < InitialFileCount; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                AssertFileHasVersion(Path.Combine(restoreRoot, rel), 0);
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Edge Case Tests ***

    /// <summary>
    /// An incremental backup with no modifications should skip all files.
    /// The resulting set should contain zero files.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void NoChanges_IncrementalBackup_AllFilesSkipped(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 6, minSize: 1024, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        // Full backup (set 1)
        fixture.BackupFiles(tree.Files, "Full backup");

        // Incremental backup with zero modifications (set 2)
        var notifiable = new TestNotifiable();
        var stats = fixture.BackupFiles(tree.Files, "No changes",
            incremental: true, notifiable: notifiable);

        Assert.Equal(6, stats.FilesTotal);
        Assert.Equal(0, stats.FilesSucceeded);
        Assert.Equal(6, stats.FilesSkipped);
        Assert.Equal(0, stats.FilesFailed);

        // The incremental set should be empty (no files actually written)
        Assert.Empty(fixture.TOC[2]);
        Assert.True(fixture.TOC[2].Incremental);

        // Incremental restore from set 2 should still yield all 6 files from the chain
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_IncRestore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 2;
            Assert.True(restoreAgent.RestoreFilesFromCurrentSetInc(null, ignoreFailures: true),
                "Incremental restore after no-changes wave failed");

            Assert.Equal(6, restoreAgent.Statistics.FilesSucceeded);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// A full backup after an incremental chain should break the chain.
    /// Subsequent incremental restore should only walk back to the new full backup, not the original.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FullBackupAfterChain_BreaksIncrementalChain(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 5, minSize: 1024, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        // Set 1: Full backup (chain 1 base)
        fixture.BackupFiles(tree.Files, "Full 1");

        // Set 2: Modify files[0] → incremental
        tree.ModifyFile(tree.Files[0], version: 1);
        fixture.BackupFiles(tree.Files, "Inc 1", incremental: true);

        // Set 3: New full backup (breaks chain 1, starts chain 2)
        tree.ModifyFile(tree.Files[1], version: 2);
        fixture.BackupFiles(tree.Files, "Full 2");

        // Set 4: Modify files[2] → incremental (chains to set 3)
        tree.ModifyFile(tree.Files[2], version: 3);
        fixture.BackupFiles(tree.Files, "Inc 2", incremental: true);

        Assert.Equal(4, fixture.TOC.Count);
        Assert.False(fixture.TOC[1].Incremental);
        Assert.True(fixture.TOC[2].Incremental);
        Assert.False(fixture.TOC[3].Incremental);
        Assert.True(fixture.TOC[4].Incremental);

        // Incremental restore from set 4 should chain back to set 3 (Full 2), NOT set 1
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_IncRestore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 4;
            Assert.True(restoreAgent.RestoreFilesFromCurrentSetInc(null, ignoreFailures: true),
                "Incremental restore after chain break failed");

            // All 5 files restored from the chain (set 3 + set 4)
            Assert.Equal(5, restoreAgent.Statistics.FilesSucceeded);

            string root = RestoreEquivalentRoot(restoreDir, tree.RootPath);

            // files[0]: v1 (backed up in Full 2 with the v1 content from earlier modification)
            AssertFileHasVersion(
                Path.Combine(root, Path.GetRelativePath(tree.RootPath, tree.Files[0])), 1);
            // files[1]: v2 (modified before Full 2)
            AssertFileHasVersion(
                Path.Combine(root, Path.GetRelativePath(tree.RootPath, tree.Files[1])), 2);
            // files[2]: v3 (backed up in Inc 2)
            AssertFileHasVersion(
                Path.Combine(root, Path.GetRelativePath(tree.RootPath, tree.Files[2])), 3);
            // files[3,4]: v0 (never modified, from Full 2)
            for (int i = 3; i < 5; i++)
            {
                AssertFileHasVersion(
                    Path.Combine(root, Path.GetRelativePath(tree.RootPath, tree.Files[i])), 0);
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** TOC Persistence Tests ***

    /// <summary>
    /// Incremental restore should work correctly after a full TOC save/reload cycle,
    /// verifying that the <see cref="TapeSetTOC.Incremental"/> flag and chain structure
    /// survive serialization.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void IncrementalRestore_AfterTOCReload_CorrectVersions(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);

        SetupFourWaveChain(fixture, tree);

        // Save and reload TOC from tape — exercises serialization round-trip.
        // For SeqFilemarks, BackupFiles already saves the TOC after each wave and
        // a standalone SaveTOC creates a duplicate TOC mark, so just reload.
        if (profile == DriveProfile.SeqFilemarks)
            fixture.LoadTOC();
        else
            fixture.SaveAndReloadTOC();

        // Verify the reloaded TOC preserved incremental flags
        Assert.Equal(4, fixture.TOC.Count);
        Assert.False(fixture.TOC[1].Incremental, "Reloaded set 1 should not be incremental");
        Assert.True(fixture.TOC[2].Incremental, "Reloaded set 2 should be incremental");
        Assert.True(fixture.TOC[3].Incremental, "Reloaded set 3 should be incremental");
        Assert.True(fixture.TOC[4].Incremental, "Reloaded set 4 should be incremental");

        // Incremental restore from the reloaded TOC
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_IncRestore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 4;
            Assert.True(restoreAgent.RestoreFilesFromCurrentSetInc(null, ignoreFailures: true),
                "Incremental restore after TOC reload failed");

            Assert.Equal(tree.Files.Count, restoreAgent.Statistics.FilesSucceeded);

            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            AssertAllFileVersions(tree, restoreRoot, s_versionsAtWave3);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Restore Statistics Tests ***

    /// <summary>
    /// Verifies statistics consistency and callback counts for an incremental restore
    /// that spans multiple sets in the chain.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void IncrementalRestore_Statistics_AreConsistent(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);

        SetupFourWaveChain(fixture, tree);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_IncRestore_{Guid.NewGuid():N}");
        try
        {
            var notifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            fixture.TOC.CurrentSetIndex = 4;
            Assert.True(restoreAgent.RestoreFilesFromCurrentSetInc(
                null, ignoreFailures: true, fileNotify: notifiable),
                "Incremental restore failed");

            var stats = restoreAgent.Statistics;

            // Fundamental invariant: Processed == Succeeded + Failed + Skipped
            Assert.Equal(stats.FilesProcessed,
                stats.FilesSucceeded + stats.FilesFailed + stats.FilesSkipped);

            // All 9 files across the chain were restored
            Assert.Equal(tree.Files.Count, stats.FilesTotal);
            Assert.Equal(tree.Files.Count, stats.FilesSucceeded);
            Assert.Equal(0, stats.FilesFailed);
            Assert.Equal(0, stats.FilesSkipped);
            Assert.True(stats.BytesProcessed > 0, "BytesProcessed should be > 0");

            // One batch start/end per set in the chain (4 sets: 1→2→3→4)
            Assert.Equal(4, notifiable.BatchStarts.Count);
            Assert.Equal(4, notifiable.BatchEnds.Count);

            // Total post-processed across all batches should match file count
            Assert.Equal(tree.Files.Count, notifiable.PostProcessed.Count);
            Assert.Empty(notifiable.FilesFailed);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Helpers ***

    /// <summary>
    /// Byte pattern prefix for original (version 0) files created by <see cref="TempFileTree"/>.
    /// </summary>
    private static readonly byte[] s_originalPattern = "TapeNET-TestData-"u8.ToArray();

    /// <summary>
    /// Asserts that a restored file's content starts with the expected version pattern.
    /// Version 0 = original <see cref="TempFileTree"/> pattern ("TapeNET-TestData-").
    /// Version N &gt; 0 = versioned pattern from <see cref="TempFileTree.ModifyFile"/> ("TapeNET-vNNNN-").
    /// </summary>
    private static void AssertFileHasVersion(string filePath, int expectedVersion)
    {
        Assert.True(File.Exists(filePath),
            $"Expected file not found: {filePath}");

        byte[] expectedPrefix = expectedVersion == 0
            ? s_originalPattern
            : System.Text.Encoding.UTF8.GetBytes($"TapeNET-v{expectedVersion:D4}-");

        byte[] actual = new byte[expectedPrefix.Length];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        int bytesRead = fs.Read(actual, 0, actual.Length);
        Assert.True(bytesRead >= expectedPrefix.Length,
            $"File {Path.GetFileName(filePath)} too small ({bytesRead} bytes) for version {expectedVersion} check");

        Assert.True(actual.AsSpan().SequenceEqual(expectedPrefix),
            $"File {Path.GetFileName(filePath)} content mismatch: " +
            $"expected version {expectedVersion} pattern \"{System.Text.Encoding.UTF8.GetString(expectedPrefix)}\", " +
            $"got \"{System.Text.Encoding.UTF8.GetString(actual)}\"");
    }

    /// <summary>
    /// Asserts all files in the tree have the expected version patterns in the restore directory.
    /// </summary>
    private static void AssertAllFileVersions(
        TempFileTree tree, string restoreRoot, int[] expectedVersions)
    {
        Assert.Equal(tree.Files.Count, expectedVersions.Length);
        for (int i = 0; i < tree.Files.Count; i++)
        {
            string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
            AssertFileHasVersion(Path.Combine(restoreRoot, rel), expectedVersions[i]);
        }
    }

    /// <summary>
    /// Computes the directory under <paramref name="restoreDir"/> where
    /// <see cref="TapeFileRestoreAgentEx"/> places files that were originally under
    /// <paramref name="originalRoot"/>.
    /// </summary>
    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relativeFromDriveRoot = Path.GetRelativePath(pathRoot, originalRoot);
        return Path.Combine(restoreDir, relativeFromDriveRoot);
    }

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
            // Best effort — temp directories may be locked
        }
    }

    #endregion
}
