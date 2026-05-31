using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Round-trip (Backup → Restore) tests for the packed (shared-block) path
/// introduced in Phase 2 Step E.
/// <para>
/// Files are written via the packer
/// (<see cref="TapeFileBackupAgent.BackupFileListToCurrentSet"/>) and
/// read back via the packed restore entry points
/// (<see cref="TapeFileRestoreBaseAgent.RestoreAllFilesFromCurrentSet(bool, ITapeFileNotifiable?)"/>
/// and
/// <see cref="TapeFileRestoreBaseAgent.RestoreFilesFromCurrentSet(ITapeFileFilter?, bool, ITapeFileNotifiable?)"/>).
/// Because packed backups frequently store multiple files in the same tape
/// block, these tests exercise the read-side packer's intra-block offset
/// handling and small-LRU cache.
/// </para>
/// <para>
/// All four drive profiles are exercised, plus byte-level verification via the
/// shared <see cref="FileComparer"/> helper. Validate / Verify packed agents are
/// also covered to confirm that the new <c>RestoreFileCorePacked</c> overrides
/// in the validate and verify subclasses behave correctly.
/// </para>
/// </summary>
public class TapeRestoreAgentPackedTests
{
    #region *** Test Data ***

#pragma warning disable CA1825 // Avoid zero-length array allocations
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];
#pragma warning restore CA1825 // Avoid zero-length array allocations

    public static TheoryData<DriveProfile, TapeHashAlgorithm> ProfilesAndHashes
    {
        get
        {
            TheoryData<DriveProfile, TapeHashAlgorithm> data = [];
            foreach (var profile in new[] { DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly })
                foreach (var hash in new[] { TapeHashAlgorithm.None, TapeHashAlgorithm.Crc64, TapeHashAlgorithm.XxHash3 })
                    data.Add(profile, hash);
            return data;
        }
    }

    #endregion


    #region *** Helpers ***

    /// <summary>
    /// Backs up <paramref name="fileList"/> through the packed entry point and
    /// saves the TOC. Mirrors <see cref="VirtualTapeFixture.BackupFiles"/> but
    /// uses <see cref="TapeFileBackupAgent.BackupFileListToCurrentSet"/>.
    /// </summary>
    private static void BackupPackedAndSaveTOC(
        VirtualTapeFixture fixture,
        List<string> fileList,
        string description,
        TapeHashAlgorithm hash = TapeHashAlgorithm.Crc64,
        ITapeFileNotifiable? notifiable = null,
        TapeCompression compression = TapeCompression.None,
        int level = ZstdLevel.Default)
    {
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = description;
        fixture.TOC.CurrentSetTOC.HashAlgorithm = hash;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        fixture.TOC.CurrentSetTOC.Compression = compression;
        fixture.TOC.CurrentSetTOC.CompressionLevel = level;

        using var backupAgent = fixture.CreateBackupAgent();
        bool success = backupAgent.BackupFileListToCurrentSet(
            newSet: true,
            fileList,
            ignoreFailures: true,
            fileNotify: notifiable);
        Assert.True(success, $"Packed backup failed: {description}");
        Assert.True(backupAgent.BackupTOC(), "TOC save failed after packed backup");
    }

    /// <summary>
    /// Computes the directory under <paramref name="restoreDir"/> where
    /// <see cref="TapeFileRestoreAgentEx"/> (with RecurseSubdirectories=true) places
    /// files originally rooted at <paramref name="originalRoot"/>.
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
            // Best effort
        }
    }

    private static string MakeRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_PackedRestore_{Guid.NewGuid():N}");

    /// <summary>
    /// Drives the packed restore-all path on the given set index, returning
    /// success and the populated notifiable.
    /// </summary>
    private static (bool Success, TestNotifiable Notifiable) RestorePackedSet(
        VirtualTapeFixture fixture,
        int setIndex,
        string restoreDir)
    {
        var notifiable = new TestNotifiable();
        using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
        fixture.TOC.CurrentSetIndex = setIndex;
        var result = restoreAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);
        return ((bool)result, notifiable);
    }

    #endregion


    #region *** Single-Set Round Trip ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_SingleFile_RoundTrip_ByteForByteMatch(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("solo.dat", 4096);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, "Packed Single Restore");

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestorePackedSet(fixture, 1, restoreDir);
            Assert.True(success,
                $"Packed restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree.Files.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(ProfilesAndHashes))]
    public void Packed_MultiFile_RoundTrip_ByteForByteMatch(DriveProfile profile, TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("packed_rt", count: 12, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, $"Packed RT Hash={hash}", hash);

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestorePackedSet(fixture, 1, restoreDir);
            Assert.True(success,
                $"Packed restore failed for {profile}/{hash}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree.Files.Count);

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
    public void Packed_ManySmallFiles_SharingBlocks_RoundTrip(DriveProfile profile)
    {
        // Many tiny files force the packer to pack multiple files per block,
        //  which is the central scenario the packed read path must handle.
        const int count = 64;
        const long size = 256;

        using var tree = new TempFileTree();
        for (int i = 0; i < count; i++)
            tree.AddFile($"tiny_{i:D3}.dat", size);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, "Packed Tiny RT");

        // Sanity: at least one pair of files should share a tape block, otherwise
        //  this test is no longer exercising the packed code path on this profile.
        var setToc = fixture.TOC[1];
        bool sharedAny = false;
        for (int i = 1; i < setToc.Count; i++)
        {
            if (setToc[i].Address.Block == setToc[i - 1].Address.Block)
            {
                sharedAny = true;
                break;
            }
        }
        Assert.True(sharedAny, "Expected at least one pair of small files to share a block");

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestorePackedSet(fixture, 1, restoreDir);
            Assert.True(success,
                $"Packed restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(count);

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
    public void Packed_MixedSizes_RoundTrip(DriveProfile profile)
    {
        // Mix of sizes: tiny, sub-block, exact block, multi-block — verifies the
        //  packer handles transitions between shared and dedicated blocks cleanly.
        using var tree = new TempFileTree();
        tree.AddFile("tiny_a.dat", 100);
        tree.AddFile("tiny_b.dat", 200);
        tree.AddFile("tiny_c.dat", 300);
        tree.AddFile("sub_block.dat", 4 * 1024);
        tree.AddFile("exact_block.dat", 16 * 1024);
        tree.AddFile("two_blocks.dat", 32 * 1024);
        tree.AddFile("block_plus_one.dat", 16 * 1024 + 1);
        tree.AddFile("tiny_d.dat", 50);
        tree.AddFile("big.dat", 96 * 1024);
        tree.AddFile("tiny_e.dat", 75);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, "Packed Mixed RT");

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestorePackedSet(fixture, 1, restoreDir);
            Assert.True(success,
                $"Packed restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree.Files.Count);

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
    public void Packed_EdgeCases_RoundTrip(DriveProfile profile)
    {
        // Use the canonical edge-case fileset: empty, 1 byte, exact block, block+1,
        //  two blocks, just-under-block, and various attribute combinations.
        using var tree = new TempFileTree();
        tree.AddEdgeCases(blockSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, "Packed Edge RT");

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestorePackedSet(fixture, 1, restoreDir);
            Assert.True(success,
                $"Packed restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree.Files.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** TOC Reload Round Trip ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_RoundTrip_AfterTOCReload_ByteForByteMatch(DriveProfile profile)
    {
        // Validates that the packed restore path correctly consumes a TOC that has
        //  been serialized and deserialized — i.e. uses TapeAddress (block + offset)
        //  faithfully reconstructed from the on-tape TOC.
        using var tree = new TempFileTree();
        tree.AddFile("a.dat", 200);
        tree.AddFile("b.dat", 300);
        tree.AddFile("c.dat", 4 * 1024);
        tree.AddFile("d.dat", 32 * 1024);
        tree.AddFile("e.dat", 75);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, "Packed Reload RT");

        // Reload TOC from tape — restore must use the deserialized addresses.
        fixture.LoadTOC();
        Assert.Equal(tree.Files.Count, fixture.TOC[1].Count);

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestorePackedSet(fixture, 1, restoreDir);
            Assert.True(success,
                $"Packed restore (post-reload) failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree.Files.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Selective Restore via Wildcard Filter ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_SelectiveRestore_OnlyFilteredFilesRestored(DriveProfile profile)
    {
        // Back up a mix of names; restore only "*.dat" via the packed selective entry.
        //  Verifies that selecting a sparse subset across packed (shared-block) files
        //  works -- the read packer must seek to each file's exact (block, offset)
        //  rather than scanning sequentially.
        using var tree = new TempFileTree();
        tree.AddFile("keep_01.dat", 200);
        tree.AddFile("skip_01.log", 250);
        tree.AddFile("keep_02.dat", 300);
        tree.AddFile("skip_02.log", 350);
        tree.AddFile("keep_03.dat", 400);
        tree.AddFile("skip_03.log", 450);
        tree.AddFile("keep_04.dat", 5 * 1024);
        tree.AddFile("skip_04.log", 6 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, "Packed Selective RT");

        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.CurrentSetIndex = 1;
            var notifiable = new TestNotifiable();
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);

            var filter = new WildcardFileFilter(["*.dat"]);
            var result = restoreAgent.RestoreFilesFromCurrentSet(
                filter, ignoreFailures: true, fileNotify: notifiable);
            Assert.True((bool)result,
                $"Packed selective restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");

            var expected = tree.Files.Where(f => f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)).ToList();
            notifiable.AssertAllSucceeded(expected.Count);

            // Restored .dat files must match originals byte-for-byte.
            FileComparer.AssertFilesMatch(tree.RootPath, expected,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));

            // .log files must NOT be present in the restore tree.
            string equivRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            foreach (var skipped in tree.Files.Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
            {
                string relativePath = Path.GetRelativePath(tree.RootPath, skipped);
                string restoredPath = Path.Combine(equivRoot, relativePath);
                Assert.False(File.Exists(restoredPath),
                    $"File should have been filtered out but was restored: {restoredPath}");
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Multi-Set Round Trip ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_TwoSets_RoundTripIndependently(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 8, minSize: 100, maxSize: 4 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 6, minSize: 200, maxSize: 6 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree1.Files, "Packed Set 1", TapeHashAlgorithm.Crc64);
        BackupPackedAndSaveTOC(fixture, tree2.Files, "Packed Set 2", TapeHashAlgorithm.XxHash3);

        Assert.Equal(2, fixture.TOC.Count);

        // Restore set 1
        string restoreDir1 = MakeRestoreDir();
        try
        {
            var (s1, n1) = RestorePackedSet(fixture, 1, restoreDir1);
            Assert.True(s1,
                $"Set 1 packed restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", n1.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            n1.AssertAllSucceeded(tree1.Files.Count);
            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                RestoreEquivalentRoot(restoreDir1, tree1.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir1);
        }

        // Restore set 2 (different hash algorithm)
        string restoreDir2 = MakeRestoreDir();
        try
        {
            var (s2, n2) = RestorePackedSet(fixture, 2, restoreDir2);
            Assert.True(s2,
                $"Set 2 packed restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", n2.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            n2.AssertAllSucceeded(tree2.Files.Count);
            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir2, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir2);
        }
    }

    #endregion


    #region *** Validate / Verify Packed Pendants ***

    [Theory]
    [MemberData(nameof(ProfilesAndHashes))]
    public void Packed_Validate_AllFilesFromCurrentSet_Succeeds(DriveProfile profile, TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("packed_validate", count: 10, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, $"Packed Validate Hash={hash}", hash);

        fixture.TOC.CurrentSetIndex = 1;
        var notifiable = new TestNotifiable();
        using var validate = fixture.CreateValidateAgent();
        var result = validate.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);
        Assert.True((bool)result,
            $"Packed validate failed for {profile}/{hash}: " +
            $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    [Theory]
    [MemberData(nameof(ProfilesAndHashes))]
    public void Packed_Verify_AllFilesFromCurrentSet_MatchesOriginals(DriveProfile profile, TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("packed_verify", count: 10, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        BackupPackedAndSaveTOC(fixture, tree.Files, $"Packed Verify Hash={hash}", hash);

        fixture.TOC.CurrentSetIndex = 1;
        var notifiable = new TestNotifiable();
        using var verify = fixture.CreateVerifyAgent();
        var result = verify.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);
        Assert.True((bool)result,
            $"Packed verify failed for {profile}/{hash}: " +
            $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    #endregion


    #region *** Cross-Path: Legacy Backup → Packed Restore (and vice versa) ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LegacyBackup_PackedRestore_RoundTrip(DriveProfile profile)
    {
        // Files written via the legacy backup path are block-aligned per file
        //  (Address.Offset == 0), but the packed restore agent should still
        //  consume them transparently through the read packer.
        using var tree = new TempFileTree();
        tree.AddFiles("legacy_to_packed", count: 8, minSize: 200, maxSize: 6 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, useAligned: true, description: "Legacy backup, packed restore");

        // All addresses should be block-aligned for legacy backup.
        Assert.All(fixture.TOC[1], tfi => Assert.Equal(0u, tfi.Address.Offset));

        string restoreDir = MakeRestoreDir();
        try
        {
            var (success, notifiable) = RestorePackedSet(fixture, 1, restoreDir);
            Assert.True(success,
                $"Packed restore over legacy backup failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree.Files.Count);

            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files,
                RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    // Note: There is intentionally no PackedBackup -> LegacyRestore test.
    //  Packed backup may place files at non-zero intra-block offsets even when
    //  every file is larger than one block (the tail of one file shares a block
    //  with the head of the next). The legacy restore path uses
    //  Drive.MoveToBlock(tfi.Address.Block) and assumes Offset == 0, so it
    //  cannot consume packed-backup output in general. The packed restore path
    //  is the only correct consumer of packed-backup output.

    #endregion
}
