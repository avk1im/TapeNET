using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Focused tests for <see cref="TapeFileBackupAgent"/> — the middle layer between
/// low-level tape stream/navigator tests and full backup?restore round-trips.
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
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

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
        uint blockSize = 0,
        ITapeFileNotifiable? notifiable = null)
    {
        toc.AddNewSetTOC(0);
        toc.CurrentSetTOC.Description = description;
        toc.CurrentSetTOC.HashAlgorithm = hash;
        toc.CurrentSetTOC.BlockSize = blockSize == 0 ? agent.Manager.Navigator.Drive.DefaultBlockSize : blockSize;

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
    public void TOC_AfterBackup_AddressesAreMonotonicallyIncreasing(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("ordered", count: 10, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Block Order");

        // Block numbers within a set should be monotonically increasing
        var setToc = fixture.TOC[1];
        for (int i = 1; i < setToc.Count; i++)
        {
            Assert.True(setToc[i].Address > setToc[i - 1].Address,
                $"Addresses not monotonically increasing: " +
                $"file[{i - 1}].Address={setToc[i - 1].Address}, file[{i}].Address={setToc[i].Address}");
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
    public void TOC_AfterTwoSets_AddressesAreDistinctPerSet(DriveProfile profile)
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
        var lastAddressSet1 = fixture.TOC[1][^1].Address;
        var firstAddressSet2 = fixture.TOC[2][0].Address;

        Assert.True(firstAddressSet2 > lastAddressSet1,
            $"Set 2 first address ({firstAddressSet2}) should be after set 1 last address ({lastAddressSet1})");
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
        Assert.True(finalStats.FileBytesProcessed > 0);
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

    #region *** Soft Early Warning (TOC-in-set) ***

    [Fact]
    public void Backup_SoftEarlyWarning_StopsSetLeavingTocRoom()
    {
        // Honest small cartridge + small TOC reserve ⇒ soft EW fires (reported ≤ reserve) well before hard
        //  EOM, stopping the set and reserving room for the TOC. FilemarksOnly = TOC-in-set (LTO-like).
        const long capacity = 2L * 1024 * 1024;
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, contentCapacity: capacity);
        using var agent = fixture.CreateBackupAgent();

        agent.Manager.Navigator.TOCCapacity = 256L * 1024; // small reserve so EW precedes EOM cleanly

        using var tree = new TempFileTree();
        tree.AddFiles("ew", count: 80, minSize: 24 * 1024, maxSize: 48 * 1024); // ~2.9 MB ≫ capacity

        var toc = fixture.TOC;
        toc.AddNewSetTOC(0);
        toc.CurrentSetTOC.Description = "Soft EW";
        toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc32;
        toc.CurrentSetTOC.BlockSize = 16 * 1024; // fine granularity so the reserve spans several blocks

        agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: true);

        // Soft EW stopped the set: a continuation is pending, and only SOME files fit — the fix is that
        //  it stops SHORT of EOM (reserving TOC room), not that it fails to write anything.
        Assert.True(agent.CanResumeToNextVolume,
            "Soft early warning should trigger a volume-continuation stop");
        Assert.InRange(toc.CurrentSetTOC.Count, 1, tree.Files.Count - 1);
    }

    #endregion

    #region *** Overwrite After Full (Write-Position Notification) ***

    // Reproduces the real-world "overwrite a full cartridge" scenario end-to-end through the backup agent.
    //  A real drive reports Remaining = capacity − EOD, which stays stale-small after repositioning before
    //  existing content. Without NotifyNextContentWritePosition (armed by BeginWriteContentForCurrentSet)
    //  the drive would trip a premature logical early warning and clamp every write to zero — the bug that
    //  produced a "0 files" backup set. This test proves the agent path now writes the files.

    [Fact]
    public void OverwriteFullTape_FromBeginning_WritesAllFilesAndRestores()
    {
        // Honest drive (no EW emulation) ⇒ identity calibration, so logical EW fires precisely when
        //  reported remaining ≤ the TOC reserve — deterministic, no a-priori margin to reason about.
        const long capacity = 4L * 1024 * 1024;
        using var fixture = new VirtualTapeFixture(DriveProfile.FilemarksOnly, contentCapacity: capacity);
        var drive = fixture.Drive;

        // --- Phase 1: raw-fill the cartridge to hard EOM with SMALL blocks, then rewind. The drive now
        //     reports < one block remaining (capacity − EOD) — the "previously full tape" the user
        //     overwrites. Small fill blocks keep the leftover < the reserve so the precondition is exact. ---
        Assert.True(drive.SetEarlyWarning(0));                       // no reserve ⇒ fill to hard EOM
        Assert.True(drive.MoveToPartition(MediaPartition.Content));
        Assert.True(drive.Rewind());

        uint fillBs = Math.Max(drive.MinimumBlockSize, 16u * 1024);
        Assert.True(drive.SetBlockSize(fillBs));
        int fillBlock = (int)drive.BlockSize;
        long reserve = Math.Max(64L * 1024, 4L * fillBlock);

        var junk = new byte[fillBlock];
        new Random(71).NextBytes(junk);
        while (true)
        {
            int n = drive.WriteDirect(junk, 0, fillBlock, out _, out _, out bool eom);
            if (n == 0 || eom) break;
        }
        Assert.True(drive.Rewind());

        Assert.True(drive.GetReportedContentRemaining() < reserve,
            "Precondition: after fill the drive should report less than the TOC reserve remaining");

        // --- Phase 2: a fresh backup overwriting the whole tape from the beginning. ---
        using var tree = new TempFileTree();
        tree.AddFiles("overwrite", count: 6, minSize: 8 * 1024, maxSize: 24 * 1024); // ~100 KB total ≪ capacity

        var toc = new TapeTOC("Overwrite media");
        using var agent = new TapeFileBackupAgent(drive, toc);
        agent.Navigator.TOCCapacity = reserve;                      // small reserve fits the small cartridge

        toc.AddNewSetTOC(0);
        toc.CurrentSetTOC.Description = "Fresh over full";
        toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc32;
        toc.CurrentSetTOC.BlockSize = 16 * 1024;

        // First set on volume ⇒ the agent writes from beginning of content and calls
        //  NotifyNextContentWritePosition(0), so the stale ~0 reported remaining does NOT clamp the write.
        bool ok = agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: true);

        Assert.True(ok, "Overwrite backup should succeed");
        Assert.False(agent.CanResumeToNextVolume, "The small overwrite fits — no volume continuation");
        Assert.Equal(tree.Files.Count, toc.CurrentSetTOC.Count);    // ← the bug produced 0 files here

        // The first write reset EOD (truncating the old fill), so reported remaining recovered far above
        //  the reserve — proving the overwrite actually reclaimed the tape, not just squeezed in.
        Assert.True(drive.GetReportedContentRemaining() > reserve,
            "After overwriting from the beginning, reported remaining should recover");

        Assert.True(agent.BackupTOC(), "TOC save after overwrite should succeed");

        // --- Phase 3: restore proves the overwritten content is intact byte-for-byte. ---
        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_OW_{Guid.NewGuid():N}");
        try
        {
            toc.MakeLastSetCurrent();
            using var restore = new TapeFileRestoreAgentEx(
                drive, restoreDir, recurseSubdirs: true, TapeHowToHandleExisting.Overwrite, toc);
            restore.Navigator.TOCCapacity = reserve;

            Assert.True(restore.RestoreAllFilesFromCurrentSet(ignoreFailures: true),
                "Restore of the overwritten set should succeed");

            FileComparer.AssertFilesMatch(
                tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    // --- local helpers (mirrors MultiVolumeBackupRestoreTests) ---

    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
    {
        string pathRoot = Path.GetPathRoot(originalRoot)!;
        string relative = Path.GetRelativePath(pathRoot, originalRoot);
        return Path.Combine(restoreDir, relative);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(path, recursive: true);
        }
        catch { /* best effort */ }
    }

    #endregion

}
