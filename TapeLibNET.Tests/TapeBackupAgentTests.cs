using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Focused tests for <see cref="TapeFileBackupAgent"/> — the middle layer between
/// low-level tape stream/navigator tests and full backup→restore round-trips.
/// <para>
/// These tests verify that the backup agent correctly:
/// <list type="bullet">
///   <item>Writes files to tape and records them in the TOC</item>
///   <item>Handles block sizes, hash algorithms, and filemark modes</item>
///   <item>Backs up multiple sets sequentially with correct tape positioning</item>
///   <item>Saves and reloads the TOC preserving all metadata</item>
///   <item>Reports accurate statistics and invokes callbacks correctly</item>
///   <item>Handles edge-case files (zero-byte, block-aligned, large)</item>
/// </list>
/// All profiles are tested to surface any profile-specific positioning bugs.
/// </para>
/// </summary>
public class TapeBackupAgentTests
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
    /// Cross-product of drive profile × hash algorithm for backup theories.
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

    /// <summary>
    /// Cross-product of drive profile × filemark mode.
    /// SeqFilemarks does not support FmksMode=true, but the agent gracefully
    ///  falls back — we test that the agent handles the fallback correctly.
    /// </summary>
    public static TheoryData<DriveProfile, bool> ProfilesAndFmksModes
    {
        get
        {
            TheoryData<DriveProfile, bool> data = [];
            foreach (var profile in new[] { DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly })
            {
                data.Add(profile, true);
                data.Add(profile, false);
            }
            return data;
        }
    }

    #endregion


    #region *** Helpers ***

    /// <summary>
    /// Backs up a file list to a new set using the given agent, with common defaults.
    /// Does NOT save the TOC — caller controls when TOC is written.
    /// </summary>
    private static bool BackupFileList(
        TapeFileBackupAgent agent,
        TapeTOC toc,
        List<string> fileList,
        string description = "Test Set",
        bool newSet = true,
        TapeHashAlgorithm hash = TapeHashAlgorithm.Crc64,
        bool fmksMode = false,
        uint blockSize = 0,
        ITapeFileNotifiable? notifiable = null)
    {
        toc.AddNewSetTOC(0);
        toc.CurrentSetTOC.Description = description;
        toc.CurrentSetTOC.HashAlgorithm = hash;
        toc.CurrentSetTOC.BlockSize = blockSize == 0 ? agent.Manager.Navigator.Drive.DefaultBlockSize : blockSize;
        toc.CurrentSetTOC.FmksMode = fmksMode;

        return agent.BackupFileListToCurrentSet(
            newSet: newSet,
            fileList,
            ignoreFailures: true,
            fileNotify: notifiable);
    }

    #endregion


    #region *** Single-File Backup ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SingleFile_BackupAndSaveTOC_Succeeds(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("single.dat", 4096);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        bool backupOk = BackupFileList(agent, fixture.TOC, tree.Files, description: "Single File");
        Assert.True(backupOk, "Backup failed");

        Assert.True(agent.BackupTOC(), "TOC save failed");

        // Verify TOC content
        Assert.Single(fixture.TOC);
        Assert.Single(fixture.TOC[1]);
        Assert.Equal("Single File", fixture.TOC[1].Description);

        // File name matches
        Assert.Equal(tree.Files[0], fixture.TOC[1][0].FileDescr.FullName);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SingleFile_TOCReloadPreservesFileEntry(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("roundtrip.dat", 8192);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        BackupFileList(agent, fixture.TOC, tree.Files, description: "TOC Reload");
        Assert.True(agent.BackupTOC(), "TOC save failed");

        // Reload TOC from tape using a fresh agent
        fixture.LoadTOC();

        Assert.Equal(1, fixture.TOC.Count);
        Assert.Single(fixture.TOC[1]);
        Assert.Equal(tree.Files[0], fixture.TOC[1][0].FileDescr.FullName);
        Assert.Equal(8192, fixture.TOC[1][0].FileDescr.Length);
    }

    #endregion


    #region *** Multi-File Backup ***

    [Theory]
    [MemberData(nameof(ProfilesAndHashes))]
    public void MultiFile_BackupWithHash_TOCRecordsAllFiles(DriveProfile profile, TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("batch", count: 8, minSize: 100, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateBackupAgent();

        bool backupOk = BackupFileList(agent, fixture.TOC, tree.Files,
            description: $"Hash={hash}", hash: hash, notifiable: notifiable);
        Assert.True(backupOk, "Backup failed");
        Assert.True(agent.BackupTOC(), "TOC save failed");

        notifiable.AssertAllSucceeded(tree.Files.Count);

        // Verify TOC
        Assert.Equal(1, fixture.TOC.Count);
        Assert.Equal(tree.Files.Count, fixture.TOC[1].Count);
        Assert.Equal(hash, fixture.TOC[1].HashAlgorithm);

        // Check hashes recorded (or not, for None)
        for (int i = 0; i < fixture.TOC[1].Count; i++)
        {
            if (hash == TapeHashAlgorithm.None)
                Assert.Null(fixture.TOC[1][i].Hash);
            else
                Assert.NotNull(fixture.TOC[1][i].Hash);
        }
    }

    [Theory]
    [MemberData(nameof(ProfilesAndFmksModes))]
    public void MultiFile_BackupWithFmksMode_TOCRecordsAllFiles(DriveProfile profile, bool fmksMode)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("fmks", count: 6, minSize: 256, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateBackupAgent();

        bool backupOk = BackupFileList(agent, fixture.TOC, tree.Files,
            description: $"Fmks={fmksMode}", fmksMode: fmksMode, notifiable: notifiable);
        Assert.True(backupOk, "Backup failed");
        Assert.True(agent.BackupTOC(), "TOC save failed");

        notifiable.AssertAllSucceeded(tree.Files.Count);

        Assert.Equal(1, fixture.TOC.Count);
        Assert.Equal(tree.Files.Count, fixture.TOC[1].Count);

        // SeqFilemarks cannot use FmksMode=true — agent falls back to false
        if (profile is DriveProfile.SeqFilemarks or DriveProfile.FilemarksOnly && fmksMode)
            Assert.False(fixture.TOC[1].FmksMode); // should have been overridden
        else
            Assert.Equal(fmksMode, fixture.TOC[1].FmksMode);
    }

    #endregion


    #region *** Multiple Sets — Sequential Backup ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_BackupSequentially_TOCHasBothSets(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        // Backup set 1 (using fixture convenience, which also saves TOC)
        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);

        // Backup set 2 (using fixture convenience, which also saves TOC)
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        Assert.Equal(2, fixture.TOC.Count);
        Assert.Equal(4, fixture.TOC[1].Count);
        Assert.Equal(3, fixture.TOC[2].Count);
        Assert.Equal("Set 1", fixture.TOC[1].Description);
        Assert.Equal("Set 2", fixture.TOC[2].Description);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_TOCReload_PreservesBothSets(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        fixture.BackupFiles(tree1.Files, description: "Set 1", hashAlgorithm: TapeHashAlgorithm.Crc64);
        fixture.BackupFiles(tree2.Files, description: "Set 2", hashAlgorithm: TapeHashAlgorithm.XxHash3);

        // Reload TOC from tape
        fixture.LoadTOC();

        Assert.Equal(2, fixture.TOC.Count);
        Assert.Equal("Set 1", fixture.TOC[1].Description);
        Assert.Equal("Set 2", fixture.TOC[2].Description);
        Assert.Equal(4, fixture.TOC[1].Count);
        Assert.Equal(3, fixture.TOC[2].Count);

        // Verify file names in each set
        for (int i = 0; i < tree1.Files.Count; i++)
            Assert.Equal(tree1.Files[i], fixture.TOC[1][i].FileDescr.FullName);
        for (int i = 0; i < tree2.Files.Count; i++)
            Assert.Equal(tree2.Files[i], fixture.TOC[2][i].FileDescr.FullName);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ThreeSets_DifferentHashAndBlockSize_TOCPreservesAll(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 10);
        tree1.AddFiles("s1", count: 3, minSize: 100, maxSize: 4 * 1024);

        using var tree2 = new TempFileTree(seed: 20);
        tree2.AddFiles("s2", count: 5, minSize: 200, maxSize: 8 * 1024);

        using var tree3 = new TempFileTree(seed: 30);
        tree3.AddFiles("s3", count: 2, minSize: 500, maxSize: 12 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        fixture.BackupFiles(tree1.Files, description: "Set A",
            hashAlgorithm: TapeHashAlgorithm.Crc64, blockSize: 16384);
        fixture.BackupFiles(tree2.Files, description: "Set B",
            hashAlgorithm: TapeHashAlgorithm.XxHash3, blockSize: 32768);
        fixture.BackupFiles(tree3.Files, description: "Set C",
            hashAlgorithm: TapeHashAlgorithm.None, blockSize: 16384);

        // Reload TOC
        fixture.LoadTOC();

        Assert.Equal(3, fixture.TOC.Count);

        Assert.Equal("Set A", fixture.TOC[1].Description);
        Assert.Equal(TapeHashAlgorithm.Crc64, fixture.TOC[1].HashAlgorithm);
        Assert.Equal(3, fixture.TOC[1].Count);

        Assert.Equal("Set B", fixture.TOC[2].Description);
        Assert.Equal(TapeHashAlgorithm.XxHash3, fixture.TOC[2].HashAlgorithm);
        Assert.Equal(5, fixture.TOC[2].Count);

        Assert.Equal("Set C", fixture.TOC[3].Description);
        Assert.Equal(TapeHashAlgorithm.None, fixture.TOC[3].HashAlgorithm);
        Assert.Equal(2, fixture.TOC[3].Count);
    }

    #endregion


    #region *** TOC Integrity After Backup ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_AfterBackup_BlockNumbersAreMonotonicallyIncreasing(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("ordered", count: 10, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Block Order");

        // Block numbers within a set should be monotonically increasing
        var setToc = fixture.TOC[1];
        for (int i = 1; i < setToc.Count; i++)
        {
            Assert.True(setToc[i].Block > setToc[i - 1].Block,
                $"Block numbers not monotonically increasing: " +
                $"file[{i - 1}].Block={setToc[i - 1].Block}, file[{i}].Block={setToc[i].Block}");
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_AfterBackup_UIDsAreUnique(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("uids", count: 10, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "UID Uniqueness");

        var uids = new HashSet<ulong>();
        foreach (var tfi in fixture.TOC[1])
        {
            Assert.True(uids.Add(tfi.UID),
                $"Duplicate UID {tfi.UID} for file {tfi.FileDescr.FullName}");
            Assert.NotEqual(0UL, tfi.UID);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_AfterTwoSets_BlockNumbersAreDistinctPerSet(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("s1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("s2", count: 3, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Set 1");
        fixture.BackupFiles(tree2.Files, description: "Set 2");

        // Set 2's first block should be after set 1's last block
        // (tape wrote set2 content after set1 content)
        long lastBlockSet1 = fixture.TOC[1][^1].Block;
        long firstBlockSet2 = fixture.TOC[2][0].Block;

        Assert.True(firstBlockSet2 > lastBlockSet1,
            $"Set 2 first block ({firstBlockSet2}) should be after set 1 last block ({lastBlockSet1})");
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TOC_AfterBackup_FileLengthsMatchOriginals(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("small.txt", 100);
        tree.AddFile("medium.bin", 10_000);
        tree.AddFile("exact_block.dat", 16384);
        tree.AddFile("zero.dat", 0);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Length Check");

        var setToc = fixture.TOC[1];
        Assert.Equal(4, setToc.Count);

        // Lengths should match the originals
        Assert.Equal(100, setToc[0].FileDescr.Length);
        Assert.Equal(10_000, setToc[1].FileDescr.Length);
        Assert.Equal(16384, setToc[2].FileDescr.Length);
        Assert.Equal(0, setToc[3].FileDescr.Length);
    }

    #endregion


    #region *** Statistics & Callbacks ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_Statistics_MatchFileCount(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("stats", count: 7, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateBackupAgent();

        bool backupOk = BackupFileList(agent, fixture.TOC, tree.Files,
            description: "Stats", notifiable: notifiable);
        Assert.True(backupOk, "Backup failed");

        notifiable.AssertStatsInvariant();

        var finalStats = notifiable.BatchEnds[^1].Stats;
        Assert.Equal(tree.Files.Count, finalStats.FilesTotal);
        Assert.Equal(tree.Files.Count, finalStats.FilesSucceeded);
        Assert.Equal(0, finalStats.FilesFailed);
        Assert.Equal(0, finalStats.FilesSkipped);
        Assert.True(finalStats.BytesProcessed > 0);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_WithSkippedFiles_StatsReflectSkips(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("skip", count: 6, minSize: 100, maxSize: 4 * 1024);

        var notifiable = new TestNotifiable();
        notifiable.FilesToSkip.Add(tree.Files[0]);
        notifiable.FilesToSkip.Add(tree.Files[2]);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        bool backupOk = BackupFileList(agent, fixture.TOC, tree.Files,
            description: "Skip Test", notifiable: notifiable);
        Assert.True(backupOk, "Backup failed");

        notifiable.AssertStatsInvariant();

        var finalStats = notifiable.BatchEnds[^1].Stats;
        Assert.Equal(6, finalStats.FilesTotal);
        Assert.Equal(4, finalStats.FilesSucceeded);
        Assert.Equal(0, finalStats.FilesFailed);
        Assert.Equal(2, finalStats.FilesSkipped);

        // TOC should have only the non-skipped files
        Assert.Equal(4, fixture.TOC[1].Count);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_CallbackOrder_BatchStartBeforeBatchEnd(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("order", count: 3, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateBackupAgent();

        BackupFileList(agent, fixture.TOC, tree.Files,
            description: "Callback Order", notifiable: notifiable);

        Assert.Single(notifiable.BatchStarts);
        Assert.Single(notifiable.BatchEnds);
        Assert.Equal(tree.Files.Count, notifiable.PreProcessed.Count);
        Assert.Equal(tree.Files.Count, notifiable.PostProcessed.Count);
    }

    #endregion


    #region *** Edge-Case Files ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_ZeroByteFile_RecordedInTOC(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("empty.dat", 0);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        bool backupOk = BackupFileList(agent, fixture.TOC, tree.Files, description: "Zero Byte");
        Assert.True(backupOk, "Backup failed");
        Assert.True(agent.BackupTOC(), "TOC save failed");

        Assert.Single(fixture.TOC[1]);
        Assert.Equal(0, fixture.TOC[1][0].FileDescr.Length);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_ExactBlockSizeFile_RecordedCorrectly(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.BlockSize;

        using var tree = new TempFileTree();
        tree.AddFile("exact.dat", blockSize);

        using var agent = fixture.CreateBackupAgent();

        bool backupOk = BackupFileList(agent, fixture.TOC, tree.Files, description: "Exact Block");
        Assert.True(backupOk, "Backup failed");
        Assert.True(agent.BackupTOC(), "TOC save failed");

        Assert.Single(fixture.TOC[1]);
        Assert.Equal(blockSize, fixture.TOC[1][0].FileDescr.Length);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_MixedEdgeCaseFiles_AllRecorded(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        uint blockSize = fixture.Drive.BlockSize;

        using var tree = new TempFileTree();
        tree.AddFile("zero.dat", 0);
        tree.AddFile("tiny.dat", 1);
        tree.AddFile("small.dat", 100);
        tree.AddFile("block_minus_one.dat", blockSize - 1);
        tree.AddFile("exact_block.dat", blockSize);
        tree.AddFile("block_plus_one.dat", blockSize + 1);
        tree.AddFile("large.dat", 128 * 1024);

        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateBackupAgent();

        bool backupOk = BackupFileList(agent, fixture.TOC, tree.Files,
            description: "Edge Cases", notifiable: notifiable);
        Assert.True(backupOk, "Backup failed");
        Assert.True(agent.BackupTOC(), "TOC save failed");

        notifiable.AssertAllSucceeded(tree.Files.Count);
        Assert.Equal(tree.Files.Count, fixture.TOC[1].Count);
    }

    #endregion


    #region *** Multi-Set Backup with Agent Reuse ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoSets_SameAgent_BackupAndSaveTOC(DriveProfile profile)
    {
        // Uses a single agent session for both sets — mirrors real-world usage
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 5, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 4, minSize: 256, maxSize: 12 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        // Set 1
        var notifiable1 = new TestNotifiable();
        bool ok1 = BackupFileList(agent, fixture.TOC, tree1.Files,
            description: "Same Agent Set 1", notifiable: notifiable1);
        Assert.True(ok1, "Set 1 backup failed");
        notifiable1.AssertAllSucceeded(tree1.Files.Count);

        // Set 2
        var notifiable2 = new TestNotifiable();
        bool ok2 = BackupFileList(agent, fixture.TOC, tree2.Files,
            description: "Same Agent Set 2", notifiable: notifiable2);
        Assert.True(ok2, "Set 2 backup failed");
        notifiable2.AssertAllSucceeded(tree2.Files.Count);

        // Save TOC once after both sets
        Assert.True(agent.BackupTOC(), "TOC save failed");

        Assert.Equal(2, fixture.TOC.Count);
        Assert.Equal(5, fixture.TOC[1].Count);
        Assert.Equal(4, fixture.TOC[2].Count);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ThreeSets_FreshAgentPerSet_TOCAccumulates(DriveProfile profile)
    {
        // Uses separate agent sessions per set — mirrors the VirtualTapeFixture.BackupFiles pattern
        using var tree1 = new TempFileTree(seed: 10);
        tree1.AddFiles("a", count: 3, minSize: 100, maxSize: 4 * 1024);

        using var tree2 = new TempFileTree(seed: 20);
        tree2.AddFiles("b", count: 5, minSize: 200, maxSize: 8 * 1024);

        using var tree3 = new TempFileTree(seed: 30);
        tree3.AddFiles("c", count: 2, minSize: 500, maxSize: 12 * 1024);

        using var fixture = new VirtualTapeFixture(profile);

        fixture.BackupFiles(tree1.Files, description: "Set Alpha");
        fixture.BackupFiles(tree2.Files, description: "Set Beta");
        fixture.BackupFiles(tree3.Files, description: "Set Gamma");

        Assert.Equal(3, fixture.TOC.Count);
        Assert.Equal(3, fixture.TOC[1].Count);
        Assert.Equal(5, fixture.TOC[2].Count);
        Assert.Equal(2, fixture.TOC[3].Count);

        // Reload TOC and verify persistence
        fixture.LoadTOC();

        Assert.Equal(3, fixture.TOC.Count);
        Assert.Equal("Set Alpha", fixture.TOC[1].Description);
        Assert.Equal("Set Beta", fixture.TOC[2].Description);
        Assert.Equal("Set Gamma", fixture.TOC[3].Description);
    }

    #endregion


    #region *** BytesBackedup Tracking ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_BytesBackedup_IncrementsCorrectly(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("bytes", count: 5, minSize: 1024, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        Assert.Equal(0L, agent.BytesBackedup);

        BackupFileList(agent, fixture.TOC, tree.Files, description: "Bytes Check");

        // BytesBackedup should reflect the raw tape bytes written (including headers/padding)
        Assert.True(agent.BytesBackedup > 0,
            "BytesBackedup should be positive after backup");
        Assert.True(agent.BytesBackedup >= tree.TotalSize,
            $"BytesBackedup ({agent.BytesBackedup}) should be >= total file size ({tree.TotalSize})");
    }

    #endregion
}
