using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Focused tests for the restore agent hierarchy —
/// <see cref="TapeFileRestoreAgent"/>, <see cref="TapeFileValidateAgent"/>,
/// <see cref="TapeFileVerifyAgent"/>, and <see cref="TapeFileRestoreAgentEx"/>.
/// <para>
/// These tests isolate the restore path from the full backup?restore round-trip tests
/// by building known tape content via backup, then exercising:
/// <list type="bullet">
///   <item>Single-set restore to disk with byte-level verification</item>
///   <item>Multi-set restore — restoring set 1 vs set 2 independently (Partitions bug hunt)</item>
///   <item>Validate agent (CRC-only, no disk writes)</item>
///   <item>Verify agent (byte-for-byte comparison with originals)</item>
///   <item>TOC reload before restore — ensures tape positioning uses deserialized data</item>
///   <item>Block-level file positioning across profiles</item>
///   <item>Statistics and callback correctness during restore</item>
///   <item>Edge-case files through restore path</item>
/// </list>
/// All four tape organizations (Setmarks, Partitions, SeqFilemarks) are exercised.
/// </para>
/// </summary>
public class TapeRestoreAgentTests
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

    /// <summary>
    /// Cross-product of drive profile × hash algorithm.
    /// </summary>
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
    /// Creates a restore agent, restores all files from the given set index,
    /// and returns the result. Disposes the agent.
    /// </summary>
    private static (bool Success, TestNotifiable Notifiable) RestoreSet(
        VirtualTapeFixture fixture,
        int setIndex,
        string restoreDir)
    {
        var notifiable = new TestNotifiable();
        using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
        fixture.TOC.CurrentSetIndex = setIndex;
        bool result = restoreAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);
        return (result, notifiable);
    }

    /// <summary>
    /// Creates a validate agent and validates all files from the given set index.
    /// </summary>
    private static (bool Success, TestNotifiable Notifiable) ValidateSet(
        VirtualTapeFixture fixture,
        int setIndex)
    {
        var notifiable = new TestNotifiable();
        using var validateAgent = fixture.CreateValidateAgent();
        fixture.TOC.CurrentSetIndex = setIndex;
        bool result = validateAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);
        return (result, notifiable);
    }

    /// <summary>
    /// Creates a verify agent and verifies all files from the given set index
    /// against the originals on disk.
    /// </summary>
    private static (bool Success, TestNotifiable Notifiable) VerifySet(
        VirtualTapeFixture fixture,
        int setIndex)
    {
        var notifiable = new TestNotifiable();
        using var verifyAgent = fixture.CreateVerifyAgent();
        fixture.TOC.CurrentSetIndex = setIndex;
        bool result = verifyAgent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);
        return (result, notifiable);
    }

    #endregion


    #region *** Single-Set Restore ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SingleSet_RestoreToDisk_ByteForByteMatch(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 5, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Single Restore");

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, notifiable) = RestoreSet(fixture, 1, restoreDir);

            Assert.True(success,
                $"Restore failed for {profile}: " +
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
    public void SingleSet_RestoreWithHash_VerifiesCRC(DriveProfile profile, TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("hash_restore", count: 4, minSize: 256, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: $"Hash={hash}", hashAlgorithm: hash);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, notifiable) = RestoreSet(fixture, 1, restoreDir);

            Assert.True(success,
                $"Restore failed for {profile}/{hash}: " +
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


    #region *** Single-Set Validate & Verify ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SingleSet_Validate_PassesCRC(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("validate", count: 5, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, hashAlgorithm: TapeHashAlgorithm.Crc64);

        var (success, notifiable) = ValidateSet(fixture, 1);

        Assert.True(success,
            $"Validation failed for {profile}: " +
            $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SingleSet_Verify_MatchesOriginals(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("verify", count: 5, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, hashAlgorithm: TapeHashAlgorithm.XxHash3);

        var (success, notifiable) = VerifySet(fixture, 1);

        Assert.True(success,
            $"Verification failed for {profile}: " +
            $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    #endregion


    #region *** Multi-Set Restore — Key Scenario for Partitions Bug ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_RestoreSet1_ByteForByteMatch(DriveProfile profile)
    {
        // Key test: after backing up 2 sets, can we restore SET 1 correctly?
        // This is where the Partitions "header mismatch" bug manifests.
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        Assert.Equal(2, fixture.TOC.Count);

        // Restore SET 1
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, notifiable) = RestoreSet(fixture, 1, restoreDir);

            Assert.True(success,
                $"Restore set 1 failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree1.Files.Count);

            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                RestoreEquivalentRoot(restoreDir, tree1.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_RestoreSet2_ByteForByteMatch(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        Assert.Equal(2, fixture.TOC.Count);

        // Restore SET 2
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, notifiable) = RestoreSet(fixture, 2, restoreDir);

            Assert.True(success,
                $"Restore set 2 failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree2.Files.Count);

            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_RestoreBothIndependently_ByteForByteMatch(DriveProfile profile)
    {
        // Full independent restore of both sets — the exact scenario that fails for Partitions
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        // Restore set 1
        string restoreDir1 = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success1, notifiable1) = RestoreSet(fixture, 1, restoreDir1);
            Assert.True(success1,
                $"Restore set 1 failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable1.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable1.AssertAllSucceeded(tree1.Files.Count);

            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                RestoreEquivalentRoot(restoreDir1, tree1.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir1);
        }

        // Restore set 2 (with a FRESH agent — tape must reposition)
        string restoreDir2 = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success2, notifiable2) = RestoreSet(fixture, 2, restoreDir2);
            Assert.True(success2,
                $"Restore set 2 failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable2.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable2.AssertAllSucceeded(tree2.Files.Count);

            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir2, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir2);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_RestoreInReverseOrder_Set2ThenSet1(DriveProfile profile)
    {
        // Restore in reverse order to stress tape re-positioning
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        // Restore set 2 FIRST
        string restoreDir2 = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success2, notifiable2) = RestoreSet(fixture, 2, restoreDir2);
            Assert.True(success2,
                $"Restore set 2 failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable2.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");

            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir2, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir2);
        }

        // THEN restore set 1 (requires backward tape positioning)
        string restoreDir1 = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success1, notifiable1) = RestoreSet(fixture, 1, restoreDir1);
            Assert.True(success1,
                $"Restore set 1 failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable1.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");

            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                RestoreEquivalentRoot(restoreDir1, tree1.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir1);
        }
    }

    #endregion


    #region *** Multi-Set Validate & Verify ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_ValidateBothSets_Independently(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        // Validate set 1
        var (success1, notifiable1) = ValidateSet(fixture, 1);
        Assert.True(success1,
            $"Validate set 1 failed for {profile}: " +
            $"Failures=[{string.Join("; ", notifiable1.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
        notifiable1.AssertAllSucceeded(tree1.Files.Count);

        // Validate set 2
        var (success2, notifiable2) = ValidateSet(fixture, 2);
        Assert.True(success2,
            $"Validate set 2 failed for {profile}: " +
            $"Failures=[{string.Join("; ", notifiable2.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
        notifiable2.AssertAllSucceeded(tree2.Files.Count);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_VerifyBothSets_Independently(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        // Verify set 1
        var (success1, notifiable1) = VerifySet(fixture, 1);
        Assert.True(success1,
            $"Verify set 1 failed for {profile}: " +
            $"Failures=[{string.Join("; ", notifiable1.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
        notifiable1.AssertAllSucceeded(tree1.Files.Count);

        // Verify set 2
        var (success2, notifiable2) = VerifySet(fixture, 2);
        Assert.True(success2,
            $"Verify set 2 failed for {profile}: " +
            $"Failures=[{string.Join("; ", notifiable2.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
        notifiable2.AssertAllSucceeded(tree2.Files.Count);
    }

    #endregion


    #region *** TOC Reload Before Restore — Deserialized Positioning ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_ReloadTOC_ThenRestoreSet1(DriveProfile profile)
    {
        // After TOC reload, the block numbers come from deserialized data.
        // This tests that deserialized block numbers correctly position the tape.
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        // Reload TOC from tape — now all data comes from deserialization
        fixture.LoadTOC();

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, notifiable) = RestoreSet(fixture, 1, restoreDir);

            Assert.True(success,
                $"Restore set 1 after TOC reload failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree1.Files.Count);

            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                RestoreEquivalentRoot(restoreDir, tree1.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_ReloadTOC_ThenRestoreSet2(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        fixture.LoadTOC();

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, notifiable) = RestoreSet(fixture, 2, restoreDir);

            Assert.True(success,
                $"Restore set 2 after TOC reload failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree2.Files.Count);

            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_ReloadTOC_ThenRestoreBothIndependently(DriveProfile profile)
    {
        // The full diagnostic scenario: reload TOC then restore both sets with fresh agents
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        fixture.LoadTOC();

        // Restore set 1
        string restoreDir1 = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success1, notifiable1) = RestoreSet(fixture, 1, restoreDir1);
            Assert.True(success1,
                $"Restore set 1 after reload failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable1.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable1.AssertAllSucceeded(tree1.Files.Count);

            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                RestoreEquivalentRoot(restoreDir1, tree1.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir1);
        }

        // Restore set 2
        string restoreDir2 = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success2, notifiable2) = RestoreSet(fixture, 2, restoreDir2);
            Assert.True(success2,
                $"Restore set 2 after reload failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable2.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable2.AssertAllSucceeded(tree2.Files.Count);

            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir2, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir2);
        }
    }

    #endregion


    #region *** Three Sets — Set 2 Specifically ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ThreeSets_RestoreMiddleSet_ByteForByteMatch(DriveProfile profile)
    {
        // Restoring the middle set requires precise set-boundary navigation
        using var tree1 = new TempFileTree(seed: 10);
        tree1.AddFiles("a", count: 3, minSize: 100, maxSize: 4 * 1024);

        using var tree2 = new TempFileTree(seed: 20);
        tree2.AddFiles("b", count: 5, minSize: 200, maxSize: 8 * 1024);

        using var tree3 = new TempFileTree(seed: 30);
        tree3.AddFiles("c", count: 2, minSize: 500, maxSize: 12 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1");
        fixture.BackupFiles(tree2.Files, description: "Set 2");
        fixture.BackupFiles(tree3.Files, description: "Set 3");

        // Restore set 2 specifically
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, notifiable) = RestoreSet(fixture, 2, restoreDir);

            Assert.True(success,
                $"Restore set 2 (middle) failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(tree2.Files.Count);

            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ThreeSets_ValidateAllThree_Independently(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 10);
        tree1.AddFiles("a", count: 3, minSize: 100, maxSize: 4 * 1024);

        using var tree2 = new TempFileTree(seed: 20);
        tree2.AddFiles("b", count: 5, minSize: 200, maxSize: 8 * 1024);

        using var tree3 = new TempFileTree(seed: 30);
        tree3.AddFiles("c", count: 2, minSize: 500, maxSize: 12 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);
        fixture.BackupFiles(tree3.Files, description: "Set 3", hashAlgorithm: TapeHashAlgorithm.Crc64);

        for (int setIdx = 1; setIdx <= 3; setIdx++)
        {
            var (success, notifiable) = ValidateSet(fixture, setIdx);
            int expectedCount = setIdx switch { 1 => 3, 2 => 5, 3 => 2, _ => 0 };

            Assert.True(success,
                $"Validate set {setIdx} failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
            notifiable.AssertAllSucceeded(expectedCount);
        }
    }

    #endregion


    #region *** Edge-Case Restore ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_ZeroByteFile_Succeeds(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("zero.dat", 0);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Zero", hashAlgorithm: TapeHashAlgorithm.Crc64);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, _) = RestoreSet(fixture, 1, restoreDir);
            Assert.True(success, $"Zero-byte restore failed for {profile}");

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
    public void Restore_ExactBlockSizeFile_Succeeds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.BlockSize;

        using var tree = new TempFileTree();
        tree.AddFile("exact_block.dat", blockSize);

        fixture.BackupFiles(tree.Files, description: "ExactBlock", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, _) = RestoreSet(fixture, 1, restoreDir);
            Assert.True(success, $"Block-aligned restore failed for {profile}");

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
    public void Restore_MixedEdgeCaseFiles_Succeeds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.BlockSize;

        using var tree = new TempFileTree();
        tree.AddEdgeCases(blockSize);

        var notifiable = new TestNotifiable();
        fixture.BackupFiles(tree.Files, description: "Edge Restore",
            hashAlgorithm: TapeHashAlgorithm.Crc64, notifiable: notifiable);
        notifiable.AssertAllSucceeded(tree.Files.Count);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, restoreNotifiable) = RestoreSet(fixture, 1, restoreDir);

            Assert.True(success,
                $"Edge case restore failed for {profile}: " +
                $"Failures=[{string.Join("; ", restoreNotifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
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


    #region *** Restore Statistics ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_Statistics_MatchFileCount(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("rstats", count: 7, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Restore Stats",
            hashAlgorithm: TapeHashAlgorithm.Crc64);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success, notifiable) = RestoreSet(fixture, 1, restoreDir);
            Assert.True(success, "Restore failed");

            notifiable.AssertStatsInvariant();

            var finalStats = notifiable.BatchEnds[^1].Stats;
            Assert.Equal(tree.Files.Count, finalStats.FilesTotal);
            Assert.Equal(tree.Files.Count, finalStats.FilesSucceeded);
            Assert.Equal(0, finalStats.FilesFailed);
            Assert.Equal(0, finalStats.FilesSkipped);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_BytesRestored_IncrementsCorrectly(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("bytes", count: 5, minSize: 1024, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "BytesRestored");

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            using var restoreAgent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;

            Assert.Equal(0L, restoreAgent.BytesRestored);
            restoreAgent.RestoreAllFilesFromCurrentSet();
            Assert.True(restoreAgent.BytesRestored > 0,
                "BytesRestored should be positive after restore");
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Diagnostic: Header Mismatch Investigation ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DiagHeaderMismatch_TwoSets_ValidateEachAfterBackup(DriveProfile profile)
    {
        // Backup two sets, then validate each set immediately — no TOC reload.
        // If this passes but TOC-reload versions fail, the bug is in TOC serialization
        //  or block-number recording.
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        // Validate set 1 (no TOC reload)
        var (v1ok, v1not) = ValidateSet(fixture, 1);
        Assert.True(v1ok,
            $"[NO RELOAD] Validate set 1 failed for {profile}: " +
            $"Failures=[{string.Join("; ", v1not.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");

        // Validate set 2 (no TOC reload)
        var (v2ok, v2not) = ValidateSet(fixture, 2);
        Assert.True(v2ok,
            $"[NO RELOAD] Validate set 2 failed for {profile}: " +
            $"Failures=[{string.Join("; ", v2not.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DiagHeaderMismatch_TwoSets_ValidateEachAfterTOCReload(DriveProfile profile)
    {
        // Same as above, but with TOC reload — if this fails but the no-reload version
        //  passes, the block numbers changed during serialization/deserialization.
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        // Reload TOC
        fixture.LoadTOC();

        // Validate set 1
        var (v1ok, v1not) = ValidateSet(fixture, 1);
        Assert.True(v1ok,
            $"[AFTER RELOAD] Validate set 1 failed for {profile}: " +
            $"Failures=[{string.Join("; ", v1not.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");

        // Validate set 2
        var (v2ok, v2not) = ValidateSet(fixture, 2);
        Assert.True(v2ok,
            $"[AFTER RELOAD] Validate set 2 failed for {profile}: " +
            $"Failures=[{string.Join("; ", v2not.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DiagHeaderMismatch_ThreeSets_ValidateMiddleSet(DriveProfile profile)
    {
        // Validates the middle set of three — exercises set-boundary crossing
        using var tree1 = new TempFileTree(seed: 10);
        tree1.AddFiles("a", count: 3, minSize: 100, maxSize: 4 * 1024);

        using var tree2 = new TempFileTree(seed: 20);
        tree2.AddFiles("b", count: 5, minSize: 200, maxSize: 8 * 1024);

        using var tree3 = new TempFileTree(seed: 30);
        tree3.AddFiles("c", count: 2, minSize: 500, maxSize: 12 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "S1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "S2", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree3.Files, description: "S3", hashAlgorithm: TapeHashAlgorithm.Crc64);

        fixture.LoadTOC();

        var (success, notifiable) = ValidateSet(fixture, 2);
        Assert.True(success,
            $"Validate middle set failed for {profile}: " +
            $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
        notifiable.AssertAllSucceeded(tree2.Files.Count);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DiagHeaderMismatch_SingleSet_ValidateAfterTOCReload(DriveProfile profile)
    {
        // Baseline: a single set should never fail — isolates multi-set from single-set issues
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 5, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Single",
            hashAlgorithm: TapeHashAlgorithm.Crc64);

        fixture.LoadTOC();

        var (success, notifiable) = ValidateSet(fixture, 1);
        Assert.True(success,
            $"Single-set validate after reload failed for {profile}: " +
            $"Failures=[{string.Join("; ", notifiable.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");
        notifiable.AssertAllSucceeded(tree.Files.Count);
    }

    #endregion


    #region *** Different Block Sizes Per Set ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_DifferentBlockSizes_RestoreBothCorrectly(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("s1", count: 3, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("s2", count: 3, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        // Set 1 with 16K blocks
        fixture.BackupFiles(tree1.Files, description: "16K blocks",
            hashAlgorithm: TapeHashAlgorithm.Crc64, blockSize: 16384);

        // Set 2 with 32K blocks
        fixture.BackupFiles(tree2.Files, description: "32K blocks",
            hashAlgorithm: TapeHashAlgorithm.Crc64, blockSize: 32768);

        // Restore both sets
        string restoreDir1 = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        string restoreDir2 = Path.Combine(Path.GetTempPath(), $"TapeNET_AgentRestore_{Guid.NewGuid():N}");
        try
        {
            var (success1, notifiable1) = RestoreSet(fixture, 1, restoreDir1);
            Assert.True(success1,
                $"Restore set 1 (16K) failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable1.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");

            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files,
                RestoreEquivalentRoot(restoreDir1, tree1.RootPath));

            var (success2, notifiable2) = RestoreSet(fixture, 2, restoreDir2);
            Assert.True(success2,
                $"Restore set 2 (32K) failed for {profile}: " +
                $"Failures=[{string.Join("; ", notifiable2.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"))}]");

            FileComparer.AssertFilesMatch(tree2.RootPath, tree2.Files,
                RestoreEquivalentRoot(restoreDir2, tree2.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir1);
            TryDeleteDirectory(restoreDir2);
        }
    }

    #endregion
}
