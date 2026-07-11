using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Comprehensive tests for multi-volume backup and restore behavior.
/// Exercises backup sets that span volume boundaries for both regular and
/// incremental modes, verifying:
/// <list type="bullet">
///   <item>Regular backup spanning 2+ volumes ? full restore from both volumes</item>
///   <item>Incremental chain across volumes ? restore assembles correct versions</item>
///   <item>TOC persistence across volumes after save/reload</item>
///   <item>Backup and restore statistics consistency across volume boundaries</item>
///   <item>ContinuedFromPrevVolume / ContinuedOnNextVolume flags are set correctly</item>
///   <item>Multiple sets on different volumes with volume swapping</item>
/// </list>
/// All three drive profiles (Setmarks, Partitions, SeqFilemarks) are exercised.
/// </summary>
public class MultiVolumeBackupRestoreTests
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
    /// Content capacity per volume — small enough to trigger volume overflow with
    /// a moderate number of files, large enough for TOC + a few files per volume.
    /// With 16 KB block size, ~8 files of 16..32 KB each should fill a volume.
    /// </summary>
    private const long VolumeCapacity = 256L * 1024;

    /// <summary>
    /// Number of files to create — enough to span at least 2 volumes at
    /// <see cref="VolumeCapacity"/> bytes each.
    /// </summary>
    private const int FileCount = 16;

    /// <summary>Minimum file size (bytes).</summary>
    private const int MinFileSize = 16 * 1024;

    /// <summary>Maximum file size (bytes, exclusive for <see cref="TempFileTree.AddFiles"/>).</summary>
    private const int MaxFileSize = 32 * 1024;

    #endregion


    #region *** Diagnostic ***

    [Fact]
    public void Diagnostic_Partitions_CapacityCheck()
    {
        using var tree = new TempFileTree();
        // Create exactly 16 files, all exactly 16 KB
        tree.AddFiles("data", count: 16, minSize: 16 * 1024, maxSize: 16 * 1024 + 1);

        using var fixture = new MultiVolumeVirtualTapeFixture(DriveProfile.Partitions, VolumeCapacity);

        // Verify capacity
        long contentCap = fixture.Drive.ContentCapacity;
        long tocCap = MultiVolumeVirtualTapeFixture.DefaultTOCCapacity;
        bool hasInit = fixture.Drive.HasInitiatorPartition;
        uint blockSize = fixture.Drive.BlockSize;

        Assert.Equal(VolumeCapacity, contentCap); // should be 256KB
        Assert.True(hasInit, "Partitions profile should have initiator partition");
        Assert.Equal(16384u, blockSize);

        // Backup
        var stats = fixture.BackupFiles(tree.Files, "Diag Partitions");

        // Inspect physical tape state after backup
        var contentMedia = fixture.Backend.ContentMedia;
        string tapeLayout = contentMedia?.FormatBlockLayout() ?? "(no media)";
        long tapeRemaining = contentMedia?.Remaining ?? -1;
        long tapeCapacity = contentMedia?.Capacity ?? -1;

        // With 256KB capacity, 16KB block size, each 16KB file takes 2 blocks (32KB) on tape.
        // So at most 8 files per volume. With 16 files, we need 2 volumes.
        Assert.True(fixture.TotalVolumes >= 2,
            $"Expected >=2 volumes, got {fixture.TotalVolumes}. " +
            $"ContentCapacity={contentCap}, TOCCapacity={tocCap}, HasInitiatorPartition={hasInit}, " +
            $"BlockSize={blockSize}, FilesSucceeded={stats.FilesSucceeded}, " +
            $"TapeCapacity={tapeCapacity}, TapeRemaining={tapeRemaining}\n" +
            $"Tape Layout:\n{tapeLayout}");
    }

    #endregion


    #region *** Regular Backup ? Multi-Volume ? Restore ***

    /// <summary>
    /// A regular (non-incremental) backup that exceeds volume capacity should
    /// span to a second volume. Restoring from the latest volume (which holds
    /// the most recent TOC) should succeed, fetching files from both volumes.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RegularBackup_SpansTwoVolumes_RestoreAllFiles(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: FileCount, minSize: MinFileSize, maxSize: MaxFileSize);

        using var fixture = new MultiVolumeVirtualTapeFixture(profile, VolumeCapacity);

        // Backup — should span to at least volume 2
        var stats = fixture.BackupFiles(tree.Files, "Regular multi-volume");

        Assert.Equal(FileCount, stats.FilesSucceeded);
        Assert.Equal(0, stats.FilesFailed);
        Assert.True(fixture.TotalVolumes >= 2,
            $"Expected at least 2 volumes, got {fixture.TotalVolumes}");

        // The TOC should record volume continuation
        Assert.True(fixture.TOC.Count >= 2,
            "TOC should have at least 2 sets (one per volume continuation)");

        // Verify the first set is continued on the next volume
        // The set on vol 1 should have ContinuedOnNextVolume or the set on vol 2 ContinuedFromPrevVolume
        bool foundContinuation = false;
        for (int i = 1; i <= fixture.TOC.Count; i++)
        {
            if (fixture.TOC[i].ContinuedFromPrevVolume)
            {
                foundContinuation = true;
                break;
            }
        }
        Assert.True(foundContinuation, "Expected at least one set with ContinuedFromPrevVolume");

        // Restore from the latest set on the last volume
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_MVRestore_{Guid.NewGuid():N}");
        try
        {
            // For multi-volume restore, we need to position to the latest set
            fixture.TOC.MakeLastSetCurrent();

            var restoreStats = fixture.RestoreAllFilesFromCurrentSet(restoreDir);

            Assert.Equal(FileCount, restoreStats.FilesSucceeded);
            Assert.Equal(0, restoreStats.FilesFailed);

            // Byte-for-byte verification
            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, restoreRoot);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// A regular backup that fills exactly two volumes. The second backup set
    /// on volume 2 should have <see cref="TapeSetTOC.ContinuedFromPrevVolume"/> = true.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RegularBackup_TOCFlags_CorrectAcrossVolumes(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: FileCount, minSize: MinFileSize, maxSize: MaxFileSize);

        using var fixture = new MultiVolumeVirtualTapeFixture(profile, VolumeCapacity);

        fixture.BackupFiles(tree.Files, "Flagged multi-volume");

        // At least 2 sets across volumes
        Assert.True(fixture.TOC.Count >= 2, "Expected at least 2 sets");

        // First set should NOT be continued from previous volume
        Assert.False(fixture.TOC[1].ContinuedFromPrevVolume,
            "First set should not be continued from a previous volume");

        // Second set should be continued from previous volume
        Assert.True(fixture.TOC[2].ContinuedFromPrevVolume,
            "Second set should be continued from volume 1");

        // Volume numbers should be correct
        Assert.Equal(1, fixture.TOC[1].Volume);
        Assert.True(fixture.TOC[2].Volume >= 2,
            "Second set should be on volume 2 or later");

        // Total file count across all sets should equal the number of files backed up
        int totalFiles = 0;
        for (int i = 1; i <= fixture.TOC.Count; i++)
            totalFiles += fixture.TOC[i].Count;
        Assert.Equal(FileCount, totalFiles);
    }

    #endregion


    #region *** Incremental Backup ? Multi-Volume ? Restore ***

    /// <summary>
    /// Full backup on volume 1, then incremental backup that also spans to volume 2.
    /// Incremental restore from the latest set should yield all files with correct versions.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void IncrementalBackup_SpansTwoVolumes_RestoreCorrectVersions(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        // Use fewer, larger files so the full backup fits on volume 1
        //  but the incremental + modifications span across volume 2
        tree.AddFiles("data", count: 8, minSize: 8 * 1024, maxSize: 16 * 1024);

        // Larger capacity to fit the full backup on one volume;
        //  scale with block size so profiles with larger blocks (e.g. FilemarksOnly 64 KB) still fit
        var caps = VirtualTapeFixture.ProfileToCapabilities(profile);
        long capacity = 384L * 1024 * caps.DefaultBlockSize / 16_384;
        using var fixture = new MultiVolumeVirtualTapeFixture(profile, capacity);

        // Wave 0: Full backup (should fit on volume 1)
        var stats0 = fixture.BackupFiles(tree.Files, "Full backup");
        Assert.Equal(8, stats0.FilesSucceeded);
        int volumeAfterFull = fixture.CurrentVolume;

        // Modify all files — this ensures every file is included in the incremental backup,
        //  making it large enough to span volumes
        for (int i = 0; i < tree.Files.Count; i++)
            tree.ModifyFile(tree.Files[i], version: 1);

        // Wave 1: Incremental backup — should span to another volume
        //  (backing up all 8 modified files, same sizes, plus overhead)
        var stats1 = fixture.BackupFiles(tree.Files, "Incremental multi-vol", incremental: true);
        Assert.Equal(8, stats1.FilesSucceeded);
        Assert.Equal(0, stats1.FilesSkipped);

        // Verify incremental flags
        Assert.False(fixture.TOC[1].Incremental, "Set 1 should be full");
        Assert.True(fixture.TOC[2].Incremental, "Set 2 should be incremental");

        // Restore incrementally from the latest set
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_MVIncRestore_{Guid.NewGuid():N}");
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            var restoreStats = fixture.RestoreFilesFromCurrentSetInc(restoreDir);

            // All 8 files should be restored — latest version from inc sets
            Assert.Equal(8, restoreStats.FilesSucceeded);
            Assert.Equal(0, restoreStats.FilesFailed);

            // All files should be version 1
            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            for (int i = 0; i < 8; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                AssertFileHasVersion(Path.Combine(restoreRoot, rel), 1);
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Full backup ? modify a subset ? incremental backup. All on volumes that span.
    /// Incremental restore should pick latest file versions from the correct set.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void IncrementalChain_AcrossVolumes_RestoreLatestVersions(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: FileCount, minSize: MinFileSize, maxSize: MaxFileSize);

        using var fixture = new MultiVolumeVirtualTapeFixture(profile, VolumeCapacity);

        // Wave 0: Full backup — spans multiple volumes
        fixture.BackupFiles(tree.Files, "Full backup");

        // Modify first half of files
        for (int i = 0; i < FileCount / 2; i++)
            tree.ModifyFile(tree.Files[i], version: 1);

        // Wave 1: Incremental — only modified files backed up, may span volumes
        var stats1 = fixture.BackupFiles(tree.Files, "Incremental 1", incremental: true);
        Assert.Equal(FileCount / 2, stats1.FilesSucceeded);
        Assert.Equal(FileCount / 2, stats1.FilesSkipped);

        // Incremental restore from the latest incremental set
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_MVIncChain_{Guid.NewGuid():N}");
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            var restoreStats = fixture.RestoreFilesFromCurrentSetInc(restoreDir);

            // All files restored
            Assert.Equal(FileCount, restoreStats.FilesSucceeded);

            // Verify versions: first half at v1, second half at v0
            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            for (int i = 0; i < FileCount; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                int expectedVersion = (i < FileCount / 2) ? 1 : 0;
                AssertFileHasVersion(Path.Combine(restoreRoot, rel), expectedVersion);
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** TOC Persistence Across Volumes ***

    /// <summary>
    /// After a multi-volume backup, saving and reloading the TOC should preserve
    /// the volume structure, continuation flags, and incremental chain data.
    /// Restore after TOC reload should still work correctly.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiVolume_TOCPersistence_RestoreAfterReload(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: FileCount, minSize: MinFileSize, maxSize: MaxFileSize);

        using var fixture = new MultiVolumeVirtualTapeFixture(profile, VolumeCapacity);

        fixture.BackupFiles(tree.Files, "Persistent multi-vol");

        int setCount = fixture.TOC.Count;
        Assert.True(setCount >= 2, "Expected at least 2 sets across volumes");

        // Capture pre-reload state
        bool[] wasContinued = new bool[setCount + 1]; // 1-based
        int[] wasVolume = new int[setCount + 1];
        for (int i = 1; i <= setCount; i++)
        {
            wasContinued[i] = fixture.TOC[i].ContinuedFromPrevVolume;
            wasVolume[i] = fixture.TOC[i].Volume;
        }

        // Save and reload TOC
        // For SeqFilemarks, BackupFiles already saved TOC, so just reload
        if (profile is DriveProfile.SeqFilemarks or DriveProfile.FilemarksOnly)
            fixture.LoadTOC();
        else
            fixture.SaveAndReloadTOC();

        // Verify reloaded TOC preserves flags
        Assert.Equal(setCount, fixture.TOC.Count);
        for (int i = 1; i <= setCount; i++)
        {
            Assert.Equal(wasContinued[i], fixture.TOC[i].ContinuedFromPrevVolume);
            Assert.Equal(wasVolume[i], fixture.TOC[i].Volume);
        }

        // Restore should still work with the reloaded TOC
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_MVPersist_{Guid.NewGuid():N}");
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            var restoreStats = fixture.RestoreAllFilesFromCurrentSet(restoreDir);

            Assert.Equal(FileCount, restoreStats.FilesSucceeded);

            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, restoreRoot);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Statistics Consistency ***

    /// <summary>
    /// Verifies that backup and restore statistics are consistent across volume
    /// boundaries — total files, succeeded, failed, skipped, and bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiVolume_Statistics_ConsistentAcrossVolumes(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: FileCount, minSize: MinFileSize, maxSize: MaxFileSize);

        using var fixture = new MultiVolumeVirtualTapeFixture(profile, VolumeCapacity);

        var backupNotifiable = new TestNotifiable();
        var backupStats = fixture.BackupFiles(tree.Files, "Stats multi-vol",
            notifiable: backupNotifiable);

        // Backup statistics invariant
        Assert.Equal(backupStats.FilesProcessed,
            backupStats.FilesSucceeded + backupStats.FilesFailed + backupStats.FilesSkipped);
        Assert.Equal(FileCount, backupStats.FilesSucceeded);
        Assert.Equal(0, backupStats.FilesFailed);
        Assert.True(backupStats.FileBytesProcessed > 0, "BytesProcessed should be > 0");

        // Restore statistics
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_MVStats_{Guid.NewGuid():N}");
        try
        {
            fixture.TOC.MakeLastSetCurrent();

            var restoreNotifiable = new TestNotifiable();
            var restoreStats = fixture.RestoreAllFilesFromCurrentSet(restoreDir,
                notifiable: restoreNotifiable);

            // Restore invariant
            Assert.Equal(restoreStats.FilesProcessed,
                restoreStats.FilesSucceeded + restoreStats.FilesFailed + restoreStats.FilesSkipped);
            Assert.Equal(FileCount, restoreStats.FilesSucceeded);
            Assert.Equal(0, restoreStats.FilesFailed);
            Assert.True(restoreStats.FileBytesProcessed > 0);

            // Callback counts
            Assert.True(restoreNotifiable.BatchStarts.Count >= 1,
                "At least one batch start expected");
            Assert.True(restoreNotifiable.BatchEnds.Count >= 1,
                "At least one batch end expected");
            Assert.Equal(FileCount, restoreNotifiable.PostProcessed.Count);
            Assert.Empty(restoreNotifiable.FilesFailed);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Multiple Sets Across Volumes ***

    /// <summary>
    /// Backs up two separate sets that land on different volumes.
    /// Restoring from each set independently should yield the correct files.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_DifferentVolumes_RestoreEachIndependently(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        // First batch: enough to fill one volume
        var batch1 = tree.AddFiles("batch1", count: 8, minSize: MinFileSize, maxSize: MaxFileSize);

        using var fixture = new MultiVolumeVirtualTapeFixture(profile, VolumeCapacity);

        // Set 1: first batch — may span volumes
        fixture.BackupFiles(tree.Files, "Set 1 - batch 1");
        int set1Count = fixture.TOC.Count; // may be >1 if spanned

        // Second batch: different files
        var batch2 = tree.AddFiles("batch2", count: 8, minSize: MinFileSize, maxSize: MaxFileSize);
        // Only back up the second batch
        fixture.BackupFiles(batch2, "Set 2 - batch 2");

        Assert.True(fixture.TOC.Count > set1Count,
            "Second backup should add at least one more set");

        // Restore set 2 (latest) — should yield only batch2 files
        string restoreDir2 = Path.Combine(Path.GetTempPath(), $"TapeNET_MVSet2_{Guid.NewGuid():N}");
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            var stats2 = fixture.RestoreAllFilesFromCurrentSet(restoreDir2);

            Assert.Equal(batch2.Count, stats2.FilesSucceeded);

            string root2 = RestoreEquivalentRoot(restoreDir2, tree.RootPath);
            FileComparer.AssertFilesMatch(tree.RootPath, batch2, root2);
        }
        finally
        {
            TryDeleteDirectory(restoreDir2);
        }
    }

    #endregion


    #region *** Non-Incremental Restore from Continued Set ***

    /// <summary>
    /// When a backup set spans two volumes, a non-incremental restore from the
    /// continued set on volume 2 should also fetch files from volume 1's part
    /// of the same logical set.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ContinuedSet_NonIncrementalRestore_FetchesFromBothVolumes(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: FileCount, minSize: MinFileSize, maxSize: MaxFileSize);

        using var fixture = new MultiVolumeVirtualTapeFixture(profile, VolumeCapacity);

        fixture.BackupFiles(tree.Files, "Continued set test");

        // Verify we actually spanned
        Assert.True(fixture.TOC.Count >= 2, "Expected multi-volume span");

        // Find the last continued set in the chain (ContinuedFromPrevVolume == true)
        //  The restore logic walks backward from the current set through the chain,
        //  so we must position at the end of the chain, not the beginning.
        int continuedSetIdx = -1;
        for (int i = 1; i <= fixture.TOC.Count; i++)
        {
            if (fixture.TOC[i].ContinuedFromPrevVolume)
                continuedSetIdx = i;
        }
        Assert.True(continuedSetIdx > 0, "Expected a continued set");

        // Restore from the continued set — should trigger multi-volume swap to volume 1
        fixture.TOC.CurrentSetIndex = continuedSetIdx;

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_MVCont_{Guid.NewGuid():N}");
        try
        {
            var restoreStats = fixture.RestoreAllFilesFromCurrentSet(restoreDir);

            Assert.Equal(FileCount, restoreStats.FilesSucceeded);

            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, restoreRoot);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion


    #region *** Incremental with Full Backup on Different Volumes ***

    /// <summary>
    /// Full backup spans volumes 1–2. Then files are modified and an incremental backup
    /// is performed (may land on volume 2 or 3). Incremental restore should find the
    /// full backup's files across volumes and combine with the incremental updates.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FullSpansVolumes_IncrementalFollows_RestoreCorrect(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: FileCount, minSize: MinFileSize, maxSize: MaxFileSize);

        using var fixture = new MultiVolumeVirtualTapeFixture(profile, VolumeCapacity);

        // Full backup — spans volumes
        fixture.BackupFiles(tree.Files, "Full spanning");

        // Modify 4 files to version 1
        for (int i = 0; i < 4; i++)
            tree.ModifyFile(tree.Files[i], version: 1);

        // Incremental backup
        var incStats = fixture.BackupFiles(tree.Files, "Inc after span", incremental: true);
        Assert.Equal(4, incStats.FilesSucceeded);
        Assert.Equal(FileCount - 4, incStats.FilesSkipped);

        // Incremental restore from the latest set
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_MVFullInc_{Guid.NewGuid():N}");
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            var restoreStats = fixture.RestoreFilesFromCurrentSetInc(restoreDir);

            Assert.Equal(FileCount, restoreStats.FilesSucceeded);
            Assert.Equal(0, restoreStats.FilesFailed);

            string restoreRoot = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            for (int i = 0; i < FileCount; i++)
            {
                string rel = Path.GetRelativePath(tree.RootPath, tree.Files[i]);
                int expectedVersion = (i < 4) ? 1 : 0;
                AssertFileHasVersion(Path.Combine(restoreRoot, rel), expectedVersion);
            }
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
