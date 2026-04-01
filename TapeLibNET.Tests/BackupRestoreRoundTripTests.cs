using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// End-to-end backup → restore round-trip tests that exercise the full
/// agent pipeline through the virtual tape drive.
/// <para>
/// Each test creates a <see cref="TempFileTree"/> with deterministic content,
/// backs it up via <see cref="VirtualTapeFixture.BackupFiles"/>, restores to
/// a separate directory, and verifies byte-for-byte equivalence via
/// <see cref="FileComparer"/>.
/// </para>
/// </summary>
public class BackupRestoreRoundTripTests
{
    #region *** Test Data ***

    /// <summary>All three drive profiles for parameterized theories.</summary>
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
    ];

    /// <summary>
    /// Cross-product of drive profile × hash algorithm for round-trip theories.
    /// Exercises every integrity-check path through the backup/restore pipeline.
    /// </summary>
    public static TheoryData<DriveProfile, TapeHashAlgorithm> ProfilesAndHashes
    {
        get
        {
            TheoryData<DriveProfile, TapeHashAlgorithm> data = [];

            DriveProfile[] profiles =
            [
                DriveProfile.Setmarks,
                DriveProfile.Partitions,
                DriveProfile.SeqFilemarks,
            ];

            TapeHashAlgorithm[] hashes =
            [
                TapeHashAlgorithm.None,
                TapeHashAlgorithm.Crc64,
                TapeHashAlgorithm.XxHash3,
            ];

            foreach (var profile in profiles)
                foreach (var hash in hashes)
                    data.Add(profile, hash);

            return data;
        }
    }

    /// <summary>
    /// Cross-product of drive profile × filemark mode for round-trip theories.
    /// </summary>
    public static TheoryData<DriveProfile, bool> ProfilesAndFmksModes
    {
        get
        {
            TheoryData<DriveProfile, bool> data = [];

            DriveProfile[] profiles =
            [
                DriveProfile.Setmarks,
                DriveProfile.Partitions,
                DriveProfile.SeqFilemarks,
            ];

            foreach (var profile in profiles)
            {
                data.Add(profile, true);  // with filemarks
                data.Add(profile, false); // without filemarks
            }

            return data;
        }
    }

    #endregion

    #region *** Core Round-Trip Tests ***

    [Theory]
    [MemberData(nameof(ProfilesAndHashes))]
    public void BackupAndRestore_SmallFileSet_RoundTrips(DriveProfile profile, TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("docs", count: 5, minSize: 100, maxSize: 8 * 1024);
        tree.AddFiles("data", count: 3, minSize: 1024, maxSize: 32 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "Small set", hashAlgorithm: hash, notifiable: notifiable);

        notifiable.AssertAllSucceeded(tree.Files.Count);

        // Restore to a separate directory
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            fixture.TOC.CurrentSetIndex = fixture.TOC.Count; // point to the last (only) set
            bool restored = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore failed");
            restoreNotifiable.AssertAllSucceeded(tree.Files.Count);

            // Byte-for-byte comparison
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(ProfilesAndFmksModes))]
    public void BackupAndRestore_FmksMode_RoundTrips(DriveProfile profile, bool fmksMode)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("batch", count: 8, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(
            tree.Files,
            description: $"FmksMode={fmksMode}",
            useFilemarks: fmksMode,
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertAllSucceeded(tree.Files.Count);

        // Restore
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            bool restored = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore failed");
            restoreNotifiable.AssertAllSucceeded(tree.Files.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupAndRestore_LargerFileSet_RoundTrips(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mixed", count: 20, minSize: 0, maxSize: 64 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(
            tree.Files,
            description: "Larger set",
            hashAlgorithm: TapeHashAlgorithm.XxHash3,
            notifiable: notifiable);

        notifiable.AssertAllSucceeded(tree.Files.Count);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            bool restored = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore failed");
            restoreNotifiable.AssertAllSucceeded(tree.Files.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Edge-Case File Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupAndRestore_EdgeCaseFiles_RoundTrips(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.BlockSize;

        tree.AddEdgeCases(blockSize);

        var notifiable = new TestNotifiable();
        fixture.BackupFiles(
            tree.Files,
            description: "Edge cases",
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertAllSucceeded(tree.Files.Count);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            bool restored = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore failed");
            restoreNotifiable.AssertAllSucceeded(tree.Files.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupAndRestore_SingleZeroByteFile_RoundTrips(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("zero.dat", 0);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(
            tree.Files,
            description: "Zero byte",
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertAllSucceeded(1);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet());

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupAndRestore_ExactBlockSizeFile_RoundTrips(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.BlockSize;

        using var tree = new TempFileTree();
        tree.AddFile("exact_block.dat", blockSize);

        var notifiable = new TestNotifiable();
        fixture.BackupFiles(
            tree.Files,
            description: "Block-aligned",
            hashAlgorithm: TapeHashAlgorithm.XxHash3,
            notifiable: notifiable);

        notifiable.AssertAllSucceeded(1);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet());

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Validate Agent Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Validate_AfterBackup_Succeeds(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 5, minSize: 100, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, hashAlgorithm: TapeHashAlgorithm.Crc64);

        // Validate (CRC-only, no disk writes)
        var notifiable = new TestNotifiable();
        using var validateAgent = fixture.CreateValidateAgent();

        fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
        bool validated = validateAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);

        Assert.True(validated, "Validation failed");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    #endregion

    #region *** Verify Agent Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Verify_AfterBackup_Succeeds(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 5, minSize: 100, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, hashAlgorithm: TapeHashAlgorithm.XxHash3);

        // Verify (byte-for-byte comparison with original disk files)
        var notifiable = new TestNotifiable();
        using var verifyAgent = fixture.CreateVerifyAgent();

        fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
        bool verified = verifyAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);

        Assert.True(verified, "Verification failed");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    #endregion

    #region *** Statistics & Callback Tests ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_Statistics_AreConsistent(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("stats", count: 10, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        var stats = fixture.BackupFiles(
            tree.Files,
            description: "Stats test",
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        // Statistics invariant
        notifiable.AssertStatsInvariant();

        // All files backed up successfully
        Assert.Equal(tree.Files.Count, stats.FilesTotal);
        Assert.Equal(tree.Files.Count, stats.FilesSucceeded);
        Assert.Equal(0, stats.FilesFailed);
        Assert.Equal(0, stats.FilesSkipped);
        Assert.True(stats.BytesProcessed > 0, "BytesProcessed should be > 0");

        // Callback counts match
        Assert.Single(notifiable.BatchStarts);
        Assert.Single(notifiable.BatchEnds);
        Assert.Equal(tree.Files.Count, notifiable.PreProcessed.Count);
        Assert.Equal(tree.Files.Count, notifiable.PostProcessed.Count);
        Assert.Empty(notifiable.FilesFailed);
        Assert.Empty(notifiable.FilesSkipped);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_WithSkippedFiles_ReportsCorrectly(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("skip", count: 6, minSize: 100, maxSize: 4 * 1024);

        // Mark the first two files for skipping
        var notifiable = new TestNotifiable();
        notifiable.FilesToSkip.Add(tree.Files[0]);
        notifiable.FilesToSkip.Add(tree.Files[1]);

        using var fixture = new VirtualTapeFixture(profile);
        var stats = fixture.BackupFiles(
            tree.Files,
            description: "Skip test",
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertStatsInvariant();

        // 4 files succeeded, 2 skipped
        Assert.Equal(tree.Files.Count, stats.FilesTotal);
        Assert.Equal(tree.Files.Count - 2, stats.FilesSucceeded);
        Assert.Equal(0, stats.FilesFailed);
        Assert.Equal(2, stats.FilesSkipped);

        // The skip callback should have been invoked twice
        Assert.Equal(2, notifiable.FilesSkipped.Count);
    }

    #endregion

    #region *** TOC Integrity After Backup ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_AfterBackup_ContainsCorrectFileEntries(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("toc_check", count: 5, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(
            tree.Files,
            description: "TOC integrity",
            hashAlgorithm: TapeHashAlgorithm.Crc64);

        // Reload TOC from tape
        fixture.LoadTOC();

        Assert.Equal(1, fixture.TOC.Count);
        var setToc = fixture.TOC[1]; // 1-based indexing

        Assert.Equal("TOC integrity", setToc.Description);
        Assert.Equal(TapeHashAlgorithm.Crc64, setToc.HashAlgorithm);
        Assert.Equal(tree.Files.Count, setToc.Count);

        // Verify every file name is present in the TOC
        var tocFileNames = new HashSet<string>(
            setToc.Select(tfi => tfi.FileDescr.FullName),
            StringComparer.OrdinalIgnoreCase);

        foreach (string file in tree.Files)
            Assert.Contains(file, tocFileNames);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_AfterBackup_PreservesBlockSize(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint expectedBlockSize = fixture.Drive.DefaultBlockSize;

        using var tree = new TempFileTree();
        tree.AddFiles("bs_check", count: 3, minSize: 100, maxSize: 4 * 1024);

        fixture.BackupFiles(tree.Files, description: "Block size check");

        fixture.LoadTOC();

        Assert.Equal(expectedBlockSize, fixture.TOC[1].BlockSize);
    }

    #endregion

    #region *** Multiple Sets on Single Tape ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultipleSets_BackupAndRestore_Independently(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        // Backup set 1
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);

        // Backup set 2
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        Assert.Equal(2, fixture.TOC.Count);

        // Restore set 1
        string restoreDir1 = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent1 = fixture.CreateRestoreAgent(restoreDir1);
            fixture.TOC.CurrentSetIndex = 1;
            Assert.True(restoreAgent1.RestoreAllFilesFromCurrentSet());

            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                RestoreEquivalentRoot(restoreDir1, tree1.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir1);
        }

        // Restore set 2
        string restoreDir2 = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent2 = fixture.CreateRestoreAgent(restoreDir2);
            fixture.TOC.CurrentSetIndex = 2;
            Assert.True(restoreAgent2.RestoreAllFilesFromCurrentSet());

            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir2, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir2);
        }
    }

    #endregion

    #region *** Hash Algorithm Coverage ***

    [Theory]
    [InlineData(TapeHashAlgorithm.None)]
    [InlineData(TapeHashAlgorithm.Crc32)]
    [InlineData(TapeHashAlgorithm.Crc64)]
    [InlineData(TapeHashAlgorithm.XxHash32)]
    [InlineData(TapeHashAlgorithm.XxHash3)]
    [InlineData(TapeHashAlgorithm.XxHash64)]
    [InlineData(TapeHashAlgorithm.XxHash128)]
    public void AllHashAlgorithms_BackupAndValidate_Succeeds(TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("hash_test", count: 3, minSize: 1024, maxSize: 8 * 1024);

        // Use Setmarks profile as representative — hash logic is profile-independent
        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
        fixture.BackupFiles(tree.Files, hashAlgorithm: hash);

        // Validate via CRC check (or raw read for None)
        var notifiable = new TestNotifiable();
        using var validateAgent = fixture.CreateValidateAgent();

        fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
        bool validated = validateAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);

        Assert.True(validated, $"Validation failed for hash algorithm {hash}");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    #endregion

    #region *** Restore to Different Directory Structure ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_RecreatesSubdirectoryStructure(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        // Create files in a nested directory structure
        tree.AddFile("level1/file_a.txt", 500);
        tree.AddFile("level1/level2/file_b.txt", 1000);
        tree.AddFile("level1/level2/level3/file_c.txt", 1500);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, hashAlgorithm: TapeHashAlgorithm.Crc64);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet());

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Helpers ***

    /// <summary>
    /// Computes the directory under <paramref name="restoreDir"/> where
    /// <see cref="TapeFileRestoreAgentEx"/> (with RecurseSubdirectories=true) places files
    /// that were originally under <paramref name="originalRoot"/>.
    /// The restore agent strips only the drive root (e.g., "C:\"), so the full
    /// directory hierarchy from the original path is preserved under the target.
    /// </summary>
    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relativeFromDriveRoot = Path.GetRelativePath(pathRoot, originalRoot);
        return Path.Combine(restoreDir, relativeFromDriveRoot);
    }

    /// <summary>
    /// Best-effort cleanup of a temporary restore directory.
    /// Resets read-only attributes before deletion.
    /// </summary>
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
