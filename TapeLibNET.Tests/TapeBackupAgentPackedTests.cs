using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Focused tests for the packed (shared-block) backup path on
/// <see cref="TapeFileBackupAgent"/>:
/// <see cref="TapeFileBackupAgent.BackupFileListToCurrentSet"/> and
/// <see cref="TapeFileBackupAgent.BackupFilesToCurrentSet(bool, System.Collections.Generic.List{string}, bool, bool, ITapeFileNotifiable?)"/>.
/// <para>
/// These tests exercise the <c>TapeFilePacker</c>-backed write pipeline introduced
/// in Phase 2: file slots are opened on the packer, addresses are surfaced
/// asynchronously through <c>FilesCommitted</c>, and post-process notifications
/// are deferred until commit time. They do NOT exercise restore, since the
/// read-side packer (Step E) is not yet wired in -- restore still assumes
/// per-file block alignment.
/// </para>
/// </summary>
public class TapeBackupAgentPackedTests
{
    #region *** Test Data ***

#pragma warning disable CA1825
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];
#pragma warning restore CA1825

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
    /// Configures a new content set on the fixture's TOC and runs the packed
    /// entry point. Returns the agent's result. Does NOT save the TOC.
    /// </summary>
    private static TapeResult BackupPacked(
        TapeFileBackupAgent agent,
        TapeTOC toc,
        List<string> fileList,
        string description = "Packed Set",
        bool newSet = true,
        TapeHashAlgorithm hash = TapeHashAlgorithm.Crc64,
        uint blockSize = 0,
        bool incremental = false,
        bool ignoreFailures = true,
        ITapeFileNotifiable? notifiable = null)
    {
        toc.AddNewSetTOC(0, incremental);
        toc.CurrentSetTOC.Description = description;
        toc.CurrentSetTOC.HashAlgorithm = hash;
        toc.CurrentSetTOC.BlockSize = blockSize == 0
            ? agent.Manager.Navigator.Drive.DefaultBlockSize
            : blockSize;

        return agent.BackupFileListToCurrentSet(
            newSet: newSet,
            fileList,
            ignoreFailures: ignoreFailures,
            fileNotify: notifiable);
    }

    #endregion


    #region *** Single-File / Smoke ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_SingleFile_BackupAndSaveTOC_Succeeds(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("single.dat", 4096);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        var result = BackupPacked(agent, fixture.TOC, tree.Files, description: "Packed Single");
        Assert.True((bool)result, $"Packed backup failed: {result}");
        Assert.True(agent.BackupTOC(), "TOC save failed");

        Assert.Single(fixture.TOC);
        Assert.Single(fixture.TOC[1]);
        Assert.Equal("Packed Single", fixture.TOC[1].Description);
        Assert.Equal(tree.Files[0], fixture.TOC[1][0].FileDescr.FullName);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_EmptyFileList_TreatedAsSuccess(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        var result = agent.BackupFileListToCurrentSet(
            newSet: true, fileList: [], ignoreFailures: true, fileNotify: null);
        Assert.True((bool)result, $"Empty list packed backup failed: {result}");
    }

    #endregion


    #region *** Multi-File Packed Backup ***

    [Theory]
    [MemberData(nameof(ProfilesAndHashes))]
    public void Packed_MultiFile_TOCRecordsAllFiles(DriveProfile profile, TapeHashAlgorithm hash)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("packed", count: 12, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateBackupAgent();

        var result = BackupPacked(agent, fixture.TOC, tree.Files,
            description: $"Packed Hash={hash}", hash: hash, notifiable: notifiable);
        Assert.True((bool)result, $"Packed backup failed: {result}");
        Assert.True(agent.BackupTOC(), "TOC save failed");

        notifiable.AssertAllSucceeded(tree.Files.Count);

        Assert.Equal(1, fixture.TOC.Count);
        Assert.Equal(tree.Files.Count, fixture.TOC[1].Count);
        Assert.Equal(hash, fixture.TOC[1].HashAlgorithm);

        // Hashes recorded according to algorithm
        for (int i = 0; i < fixture.TOC[1].Count; i++)
        {
            if (hash == TapeHashAlgorithm.None)
                Assert.Null(fixture.TOC[1][i].Hash);
            else
                Assert.NotNull(fixture.TOC[1][i].Hash);
        }

        // File names preserved in original order
        for (int i = 0; i < tree.Files.Count; i++)
            Assert.Equal(tree.Files[i], fixture.TOC[1][i].FileDescr.FullName);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_ManySmallFiles_ShareBlocks(DriveProfile profile)
    {
        // 64 tiny files, each well under one block: with packing, multiple files
        //  must share blocks, so the address sequence cannot be one-block-per-file.
        const int count = 64;
        const long size = 256;

        using var tree = new TempFileTree();
        for (int i = 0; i < count; i++)
            tree.AddFile($"tiny_{i:D3}.dat", size);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        var result = BackupPacked(agent, fixture.TOC, tree.Files, description: "Packed Tiny");
        Assert.True((bool)result, $"Packed backup failed: {result}");

        var setToc = fixture.TOC[1];
        Assert.Equal(count, setToc.Count);

        // Find at least one pair of consecutive files sharing a tape block.
        bool sharedAny = false;
        for (int i = 1; i < setToc.Count; i++)
        {
            if (setToc[i].Address.Block == setToc[i - 1].Address.Block)
            {
                sharedAny = true;
                Assert.True(setToc[i].Address.Offset > setToc[i - 1].Address.Offset,
                    "Files sharing a block must have strictly increasing offsets");
            }
        }
        Assert.True(sharedAny, "Expected at least one pair of files to share a tape block");
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_TOC_AddressesAreMonotonicallyIncreasing(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("ordered", count: 20, minSize: 100, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        var result = BackupPacked(agent, fixture.TOC, tree.Files, description: "Packed Order");
        Assert.True((bool)result, $"Packed backup failed: {result}");

        var setToc = fixture.TOC[1];
        for (int i = 1; i < setToc.Count; i++)
        {
            var prev = setToc[i - 1].Address;
            var cur = setToc[i].Address;
            bool monotonic = (cur.Block > prev.Block) ||
                             (cur.Block == prev.Block && cur.Offset > prev.Offset);
            Assert.True(monotonic,
                $"Addresses not monotonically increasing: " +
                $"file[{i - 1}]={prev}, file[{i}]={cur}");
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_TOCReload_PreservesAddressesWithOffsets(DriveProfile profile)
    {
        // Mix small files (will pack) and verify addresses survive a TOC round-trip.
        using var tree = new TempFileTree();
        tree.AddFile("a.dat", 200);
        tree.AddFile("b.dat", 300);
        tree.AddFile("c.dat", 400);
        tree.AddFile("d.dat", 500);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        var result = BackupPacked(agent, fixture.TOC, tree.Files, description: "Packed Reload");
        Assert.True((bool)result, $"Packed backup failed: {result}");
        Assert.True(agent.BackupTOC(), "TOC save failed");

        // Snapshot the addresses before reload
        var before = fixture.TOC[1].Select(t => t.Address).ToList();

        fixture.LoadTOC();

        var after = fixture.TOC[1].Select(t => t.Address).ToList();
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Block, after[i].Block);
            Assert.Equal(before[i].Offset, after[i].Offset);
        }
    }

    #endregion


    #region *** Multiple Sets — Sequential Packed Backup ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_TwoSets_BackupSequentially_TOCHasBothSets(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 5, minSize: 100, maxSize: 4 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 4, minSize: 200, maxSize: 6 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        Assert.True((bool)BackupPacked(agent, fixture.TOC, tree1.Files,
            description: "Packed Set 1", hash: TapeHashAlgorithm.Crc64));
        Assert.True(agent.BackupTOC());

        var r2 = BackupPacked(agent, fixture.TOC, tree2.Files,
            description: "Packed Set 2", hash: TapeHashAlgorithm.XxHash3);
        Assert.True((bool)r2, $"Packed Set 2 failed: {r2}");
        Assert.True(agent.BackupTOC());

        Assert.Equal(2, fixture.TOC.Count);
        Assert.Equal(5, fixture.TOC[1].Count);
        Assert.Equal(4, fixture.TOC[2].Count);
        Assert.Equal("Packed Set 1", fixture.TOC[1].Description);
        Assert.Equal("Packed Set 2", fixture.TOC[2].Description);
    }

    #endregion


    #region *** Statistics & Notifications ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_Statistics_MatchFileCount(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("stats", count: 7, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateBackupAgent();

        var result = BackupPacked(agent, fixture.TOC, tree.Files,
            description: "Packed Stats", notifiable: notifiable);
        Assert.True((bool)result, $"Packed backup failed: {result}");

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
    public void Packed_PostProcess_FiresOnceForEverySuccessfulFile(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("notify", count: 10, minSize: 100, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        var notifiable = new TestNotifiable();
        using var agent = fixture.CreateBackupAgent();

        var result = BackupPacked(agent, fixture.TOC, tree.Files,
            description: "Packed Notify", notifiable: notifiable);
        Assert.True((bool)result, $"Packed backup failed: {result}");

        // PreProcess fires per-file at BeginFile time -- exactly once each here.
        Assert.Equal(tree.Files.Count, notifiable.PreProcessed.Count);
        // PostProcess fires only on commit -- exactly once for each succeeded file.
        Assert.Equal(tree.Files.Count, notifiable.PostProcessed.Count);
        Assert.Empty(notifiable.FilesFailed);
        Assert.Empty(notifiable.FilesSkipped);

        // PostProcess order must match the file order on the packed path.
        for (int i = 0; i < tree.Files.Count; i++)
            Assert.Equal(tree.Files[i], notifiable.PostProcessed[i].FileInfo.FileDescr.FullName);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_PreProcessSkip_FilesSkippedAndNotInTOC(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("skip", count: 6, minSize: 100, maxSize: 2 * 1024);

        var notifiable = new TestNotifiable();
        // Skip files at indices 1 and 3
        notifiable.FilesToSkip.Add(tree.Files[1]);
        notifiable.FilesToSkip.Add(tree.Files[3]);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        var result = BackupPacked(agent, fixture.TOC, tree.Files,
            description: "Packed Skip", notifiable: notifiable);
        Assert.True((bool)result, $"Packed backup failed: {result}");

        notifiable.AssertStatsInvariant();
        Assert.Equal(2, notifiable.FilesSkipped.Count);
        Assert.Equal(tree.Files.Count - 2, fixture.TOC[1].Count);

        // Skipped files must not appear in TOC.
        var tocNames = fixture.TOC[1].Select(t => t.FileDescr.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(tree.Files[1], tocNames);
        Assert.DoesNotContain(tree.Files[3], tocNames);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_MissingFile_SkipAction_BackupContinues(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("present", count: 4, minSize: 100, maxSize: 2 * 1024);
        // Inject a non-existent path in the middle
        var fileList = new List<string>(tree.Files);
        fileList.Insert(2, Path.Combine(tree.RootPath, "does_not_exist.dat"));

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        // Mirrors the legacy semantic: a per-file failure -- even when Skip is
        //  chosen -- flips bc.overallSuccess to false, so the overall TapeResult
        //  reports failure. The loop, however, still continues and the remaining
        //  files DO get backed up. The post-conditions below verify exactly that.
        var result = BackupPacked(agent, fixture.TOC, fileList,
            description: "Packed MissingSkip", notifiable: notifiable);
        Assert.False((bool)result, "Skipping a per-file failure must still report overall failure");

        Assert.Single(notifiable.FilesFailed);
        Assert.Equal(tree.Files.Count, fixture.TOC[1].Count);
        notifiable.AssertStatsInvariant();
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_MissingFile_AbortAction_StopsBackup(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("present", count: 5, minSize: 100, maxSize: 2 * 1024);
        var fileList = new List<string>(tree.Files);
        fileList.Insert(2, Path.Combine(tree.RootPath, "does_not_exist.dat"));

        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Abort };

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        // Configure set BEFORE the call, mirroring BackupPacked() helper but with
        //  ignoreFailures: false so that Abort actually halts the loop.
        fixture.TOC.AddNewSetTOC(0);
        fixture.TOC.CurrentSetTOC.Description = "Packed MissingAbort";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        var result = agent.BackupFileListToCurrentSet(
            newSet: true, fileList, ignoreFailures: false, fileNotify: notifiable);

        Assert.False((bool)result, "Abort action should produce a failed result");
        Assert.Single(notifiable.FilesFailed);
        // Files before the missing one were committed; files after were not attempted.
        Assert.Equal(2, fixture.TOC[1].Count);
        notifiable.AssertStatsInvariant();
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_AbortInPreProcess_CleanShutdown(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("abort", count: 10, minSize: 100, maxSize: 2 * 1024);

        // Abort proactively after 3 PreProcess invocations. We deliberately do NOT
        //  use AbortAfterNSucceeded here: on the packed path FilesSucceeded only
        //  advances when the packer surfaces a commit (typically at flush time),
        //  so a "after N succeeded" trigger would never fire mid-loop for small
        //  files. AbortAfterNPreProcessed is commit-timing-independent.
        var notifiable = new TestNotifiable { AbortAfterNPreProcessed = 3 };

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        var result = BackupPacked(agent, fixture.TOC, tree.Files,
            description: "Packed Abort", notifiable: notifiable, ignoreFailures: false);

        // Result should be failure due to abort.
        Assert.False((bool)result, "Aborted packed backup should not report success");
        notifiable.AssertStatsInvariant();

        // PreProcess fired exactly 4 times: the first three returned true, the
        //  fourth threw TapeAbortRequestedException and broke the loop.
        Assert.Equal(4, notifiable.PreProcessed.Count);
        // The loop must have stopped well before draining all files.
        Assert.True(fixture.TOC[1].Count < tree.Files.Count,
            "Abort should have stopped the loop before all files were processed");
    }

    #endregion


    #region *** Coexistence with Legacy Path ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Packed_ThenLegacy_BothSetsLandInTOC(DriveProfile profile)
    {
        using var tree1 = new TempFileTree(seed: 11);
        tree1.AddFiles("packed", count: 5, minSize: 100, maxSize: 2 * 1024);

        using var tree2 = new TempFileTree(seed: 22);
        tree2.AddFiles("legacy", count: 4, minSize: 200, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        // Set 1 via packed
        Assert.True((bool)BackupPacked(agent, fixture.TOC, tree1.Files, description: "Packed"));
        Assert.True(agent.BackupTOC());

        // Set 2 via legacy
        fixture.TOC.AddNewSetTOC(0);
        fixture.TOC.CurrentSetTOC.Description = "Legacy";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        // FIXME transition: this test deliberately exercises the legacy aligned path
        //  alongside the packed path to verify both can coexist within the same TOC.
        //  Once the aligned API is removed, this test should be removed as well.
#pragma warning disable CS0618 // Aligned API is intentionally used for coexistence coverage
        Assert.True((bool)agent.BackupFileListToCurrentSetAligned(
            newSet: true, tree2.Files, ignoreFailures: true, fileNotify: null));
#pragma warning restore CS0618
        Assert.True(agent.BackupTOC());

        Assert.Equal(2, fixture.TOC.Count);
        Assert.Equal(tree1.Files.Count, fixture.TOC[1].Count);
        Assert.Equal(tree2.Files.Count, fixture.TOC[2].Count);
    }

    #endregion
}
