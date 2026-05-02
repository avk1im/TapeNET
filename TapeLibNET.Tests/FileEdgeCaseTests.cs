using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Focused edge-case file tests: one [Fact] or [Theory] per tricky file scenario.
/// Each test exercises the full backup ? restore ? verify pipeline through the
/// virtual tape drive, confirming byte-for-byte fidelity and metadata preservation.
/// </summary>
public class FileEdgeCaseTests
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

    #region *** Zero-Byte File ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ZeroByteFile_BackupRestoreVerify_ContentIsEmpty(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("empty.dat", 0);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "Zero byte", notifiable: notifiable);
        notifiable.AssertAllSucceeded(1);

        string restoreDir = CreateRestoreDir();
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet(), "Restore failed");

            // Verify restored file exists and is empty
            string restoredPath = MapRestoredPath(restoreDir, tree.RootPath, tree.Files[0]);
            Assert.True(File.Exists(restoredPath), $"Restored file not found: {restoredPath}");
            Assert.Equal(0L, new FileInfo(restoredPath).Length);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Exact Block Size ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ExactOneBlock_BackupRestoreVerify_RoundTrips(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.BlockSize;

        using var tree = new TempFileTree();
        tree.AddFile("exact_block.dat", blockSize);

        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "Exact block", notifiable: notifiable);
        notifiable.AssertAllSucceeded(1);

        string restoreDir = CreateRestoreDir();
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet(), "Restore failed");

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Block + 1 Byte ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BlockPlusOneByte_ForcesSecondBlock_RoundTrips(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.BlockSize;

        using var tree = new TempFileTree();
        tree.AddFile("block_plus_one.dat", blockSize + 1);

        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "Block + 1", notifiable: notifiable);
        notifiable.AssertAllSucceeded(1);

        string restoreDir = CreateRestoreDir();
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet(), "Restore failed");

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Large Multi-Block File ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LargeFile_SeveralMB_RoundTrips(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        // 4 MB — exercises multi-block read/write across many blocks
        tree.AddFile("large_4mb.dat", 4L * 1024 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(
            tree.Files,
            description: "Large file",
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertAllSucceeded(1);

        string restoreDir = CreateRestoreDir();
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet(), "Restore failed");

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Special Characters in File Names ***

    [Theory]
    [InlineData("file with spaces.txt")]
    [InlineData("dots.in.name.2024.01.dat")]
    [InlineData("special (copy) [1] {test}.txt")]
    [InlineData("leading space .dat")]
    [InlineData("UPPER_lower_MiXeD.BIN")]
    [InlineData("name-with-dashes--double.log")]
    [InlineData("name_with_underscores__.xml")]
    public void SpecialCharacterFileName_BackupRestore_RoundTrips(string fileName)
    {
        using var tree = new TempFileTree();
        tree.AddFile(Path.Combine("names", fileName), 256);

        // Use Setmarks as representative — name handling is profile-independent
        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "Special names", notifiable: notifiable);
        notifiable.AssertAllSucceeded(1);

        string restoreDir = CreateRestoreDir();
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet(), "Restore failed");

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Long Path (200+ chars) ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LongPath_Over200Chars_RoundTrips(DriveProfile profile)
    {
        using var tree = new TempFileTree();

        // Build a deeply nested path that exceeds 200 characters total
        string deepDir = string.Join(Path.DirectorySeparatorChar.ToString(),
            "level_one_directory",
            "level_two_directory_name",
            "level_three_has_longer_name",
            "level_four_even_longer_name_here",
            "level_five_deep_nesting_path");
        string fileName = "a_file_with_a_reasonably_long_name_for_testing.dat";
        string relativePath = Path.Combine(deepDir, fileName);

        // Verify the total path exceeds 200 chars
        string fullPath = Path.Combine(tree.RootPath, relativePath);
        Assert.True(fullPath.Length > 200,
            $"Expected path > 200 chars, got {fullPath.Length}: {fullPath}");

        tree.AddFile(relativePath, 512);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "Long path", notifiable: notifiable);
        notifiable.AssertAllSucceeded(1);

        string restoreDir = CreateRestoreDir();
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet(), "Restore failed");

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** File Attributes Preservation ***

    [Theory]
    [InlineData(FileAttributes.ReadOnly)]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.Archive)]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.Hidden)]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.Archive)]
    public void FileAttributes_BackupRestore_PreservesAttributes(FileAttributes attributes)
    {
        using var tree = new TempFileTree();
        tree.AddFile("attrs/test_file.dat", 1024, attributes);

        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "Attributes", notifiable: notifiable);
        notifiable.AssertAllSucceeded(1);

        string restoreDir = CreateRestoreDir();
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet(), "Restore failed");

            // Verify attributes are preserved (byte content + attributes)
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath),
                compareAttributes: true);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Verifies attribute preservation works across all drive profiles.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ReadOnlyFile_AllProfiles_PreservesAttribute(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("readonly/important.dat", 2048, FileAttributes.ReadOnly);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(tree.Files, description: "ReadOnly", notifiable: notifiable);
        notifiable.AssertAllSucceeded(1);

        string restoreDir = CreateRestoreDir();
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet(), "Restore failed");

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath),
                compareAttributes: true);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Many Small Files (500+) ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ManySmallFiles_500Plus_StressesTocAndFileIndex(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        // 500 small files (10–500 bytes each) — stresses TOC serialization and file-index machinery
        tree.AddFiles("batch", count: 500, minSize: 10, maxSize: 500);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();

        fixture.BackupFiles(
            tree.Files,
            description: "500 small files",
            hashAlgorithm: TapeHashAlgorithm.Crc64,
            notifiable: notifiable);

        notifiable.AssertAllSucceeded(500);

        // Verify TOC recorded all 500 files
        fixture.LoadTOC();
        var setToc = fixture.TOC[fixture.TOC.Count];
        Assert.Equal(500, setToc.Count);

        string restoreDir = CreateRestoreDir();
        try
        {
            var restoreNotifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            bool restored = restoreAgent.RestoreAllFilesFromCurrentSet(
                ignoreFailures: true, fileNotify: restoreNotifiable);

            Assert.True(restored, "Restore failed");
            restoreNotifiable.AssertAllSucceeded(500);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Block-Boundary Sizes (Theory) ***

    /// <summary>
    /// Parameterized test for sizes around the block boundary:
    /// blockSize - 1, blockSize, blockSize + 1, 2 × blockSize, 2 × blockSize + 1.
    /// </summary>
    [Theory]
    [InlineData(-1, "block minus 1")]
    [InlineData(0, "exact block")]
    [InlineData(1, "block plus 1")]
    [InlineData(16384, "two blocks")]       // blockSize * 1 offset ? 2 × blockSize
    [InlineData(16385, "two blocks plus 1")] // blockSize * 1 + 1 offset ? 2 × blockSize + 1
    public void BlockBoundarySizes_BackupRestore_RoundTrips(int offsetFromBlock, string label)
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
        uint blockSize = fixture.Drive.BlockSize;

        // Compute the actual file size: offsets < blockSize are relative to blockSize,
        //  offsets >= blockSize are absolute (used for multi-block cases)
        long fileSize = offsetFromBlock >= 0 && offsetFromBlock < (int)blockSize
            ? blockSize + offsetFromBlock
            : offsetFromBlock < 0
                ? blockSize + offsetFromBlock
                : offsetFromBlock;

        Assert.True(fileSize >= 0, $"Computed negative file size for '{label}': {fileSize}");

        using var tree = new TempFileTree();
        tree.AddFile($"boundary/{label.Replace(' ', '_')}.dat", fileSize);

        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: label, notifiable: notifiable);
        notifiable.AssertAllSucceeded(1);

        string restoreDir = CreateRestoreDir();
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
            Assert.True(restoreAgent.RestoreAllFilesFromCurrentSet(), "Restore failed");

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Validate Agent on Edge Cases ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Validate_EdgeCaseFiles_AllPassCrc(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.BlockSize;

        tree.AddEdgeCases(blockSize);

        fixture.BackupFiles(
            tree.Files,
            description: "Edge validate",
            hashAlgorithm: TapeHashAlgorithm.Crc64);

        // CRC-only validation — no disk writes
        var notifiable = new TestNotifiable();
        using var validateAgent = fixture.CreateValidateAgent();
        fixture.TOC.CurrentSetIndex = fixture.TOC.Count;
        bool validated = validateAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);

        Assert.True(validated, "Validation failed on edge-case files");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    #endregion

    #region *** Helpers ***

    /// <summary>Creates a unique temporary restore directory.</summary>
    private static string CreateRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_Restore_{Guid.NewGuid():N}");

    /// <summary>
    /// Computes the directory under <paramref name="restoreDir"/> where the restore agent
    /// places files originally under <paramref name="originalRoot"/>.
    /// </summary>
    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relativeFromDriveRoot = Path.GetRelativePath(pathRoot, originalRoot);
        return Path.Combine(restoreDir, relativeFromDriveRoot);
    }

    /// <summary>
    /// Maps a single original file path to its expected restored location.
    /// </summary>
    private static string MapRestoredPath(string restoreDir, string originalRoot, string originalFile)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relativeFromDriveRoot = Path.GetRelativePath(pathRoot, originalFile);
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
