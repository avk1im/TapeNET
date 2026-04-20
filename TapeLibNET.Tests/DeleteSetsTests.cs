using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Tests for <see cref="TapeFileAgent.DeleteSetsFromCurrentSetUp"/> — verifies that
/// deleting trailing backup sets correctly overwrites the tape, updates the TOC,
/// and leaves remaining sets intact and restorable.
/// All profiles are tested to surface profile-specific positioning bugs.
/// </summary>
public class DeleteSetsTests
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

    /// <summary>Non-partition profiles only (TOC in set).</summary>
    public static TheoryData<DriveProfile> TOCInSetProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];
#pragma warning restore CA1825 // Avoid zero-length array allocations

    #endregion


    #region *** Delete Last Set ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DeleteLastSet_TOCUpdated_RemainingSetIntact(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("set1", count: 3, minSize: 512, maxSize: 4096);
        var set1Files = new List<string>(tree.Files);

        tree.AddFiles("set2", count: 2, minSize: 256, maxSize: 2048);
        var set2Files = tree.Files.GetRange(set1Files.Count, tree.Files.Count - set1Files.Count);

        using var fixture = new VirtualTapeFixture(profile);

        // Backup two sets
        fixture.BackupFiles(set1Files, description: "Set 1");
        fixture.BackupFiles(set2Files, description: "Set 2");
        fixture.LoadTOC();
        Assert.Equal(2, fixture.TOC.Count);

        // Delete the last set (set 2)
        fixture.TOC.CurrentSetIndex = fixture.TOC.LastSetOnVolume; // = 2
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        var result = agent.DeleteSetsFromCurrentSetUp();
        Assert.True(result, $"Delete failed: {result.ErrorMessage}");

        // Reload TOC from tape and verify
        fixture.LoadTOC();
        Assert.Equal(1, fixture.TOC.Count);
        Assert.Equal("Set 1", fixture.TOC[1].Description);
        Assert.Equal(set1Files.Count, fixture.TOC[1].Count);

        // Verify remaining set is restorable
        using var restoreDir = new TempFileTree();
        using var restoreAgent = fixture.CreateRestoreAgent(restoreDir.RootPath);
        fixture.TOC.CurrentSetIndex = 1;
        var restoreResult = restoreAgent.RestoreFilesFromCurrentSetDown(
            [null], // all files from set 1
            fileNotify: null);
        Assert.True(restoreResult, "Restore of retained set failed");
    }

    #endregion


    #region *** Delete Multiple Trailing Sets ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DeleteTwoOfThreeSets_FirstSetSurvives(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("set1", count: 4, minSize: 512, maxSize: 4096);
        var set1Files = new List<string>(tree.Files);

        tree.AddFiles("set2", count: 3, minSize: 256, maxSize: 2048);
        tree.AddFiles("set3", count: 2, minSize: 128, maxSize: 1024);

        using var fixture = new VirtualTapeFixture(profile);

        fixture.BackupFiles(set1Files, description: "Set 1");
        fixture.BackupFiles(
            tree.Files.GetRange(set1Files.Count, 3), description: "Set 2");
        fixture.BackupFiles(
            tree.Files.GetRange(set1Files.Count + 3, 2), description: "Set 3");
        fixture.LoadTOC();
        Assert.Equal(3, fixture.TOC.Count);

        // Delete from set 2 onwards
        fixture.TOC.CurrentSetIndex = 2; // set 2
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        var result = agent.DeleteSetsFromCurrentSetUp();
        Assert.True(result, $"Delete failed: {result.ErrorMessage}");

        // Reload and verify
        fixture.LoadTOC();
        Assert.Equal(1, fixture.TOC.Count);
        Assert.Equal("Set 1", fixture.TOC[1].Description);
        Assert.Equal(set1Files.Count, fixture.TOC[1].Count);
    }

    #endregion


    #region *** Delete All Sets (TOC in set only) ***

    [Theory]
    [MemberData(nameof(TOCInSetProfiles))]
    public void DeleteAllSets_MediaBecomesEmpty(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 5, minSize: 256, maxSize: 4096);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Only Set");
        fixture.LoadTOC();
        Assert.Equal(1, fixture.TOC.Count);

        // Delete all sets
        fixture.TOC.CurrentSetIndex = fixture.TOC.FirstSetOnVolume;
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        var result = agent.DeleteSetsFromCurrentSetUp();
        Assert.True(result, $"Delete failed: {result.ErrorMessage}");

        // Reload and verify empty
        fixture.LoadTOC();
        Assert.True(fixture.TOC.IsEmpty);
    }

    [Theory]
    [MemberData(nameof(TOCInSetProfiles))]
    public void DeleteAllSets_ThenBackupNewSet_Succeeds(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("old", count: 3, minSize: 512, maxSize: 4096);
        var oldFiles = new List<string>(tree.Files);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(oldFiles, description: "Old Set");
        fixture.LoadTOC();

        // Delete all
        fixture.TOC.CurrentSetIndex = fixture.TOC.FirstSetOnVolume;
        using (var agent = new TapeFileAgent(fixture.Drive, fixture.TOC))
        {
            Assert.True(agent.DeleteSetsFromCurrentSetUp());
        }

        // Now backup new files
        tree.AddFiles("new", count: 2, minSize: 128, maxSize: 2048);
        var newFiles = tree.Files.GetRange(oldFiles.Count, tree.Files.Count - oldFiles.Count);

        fixture.LoadTOC(); // reload the empty TOC
        fixture.BackupFiles(newFiles, description: "New Set");
        fixture.LoadTOC();

        Assert.Equal(1, fixture.TOC.Count);
        Assert.Equal("New Set", fixture.TOC[1].Description);
        Assert.Equal(newFiles.Count, fixture.TOC[1].Count);
    }

    #endregion


    #region *** Delete All Sets — Partition Drive Rejects ***

    [Fact]
    public void DeleteAllSets_PartitionDrive_Fails()
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 3, minSize: 256, maxSize: 2048);

        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        fixture.BackupFiles(tree.Files, description: "Test Set");
        fixture.LoadTOC();

        fixture.TOC.CurrentSetIndex = fixture.TOC.FirstSetOnVolume;
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        var result = agent.DeleteSetsFromCurrentSetUp();
        Assert.False(result.Success, "Should have failed for partition drive delete-all");
    }

    [Fact]
    public void DeleteLastSet_PartitionDrive_Succeeds()
    {
        // Deleting non-all sets on partition drive should work fine
        using var tree = new TempFileTree();
        tree.AddFiles("set1", count: 3, minSize: 512, maxSize: 4096);
        var set1Files = new List<string>(tree.Files);

        tree.AddFiles("set2", count: 2, minSize: 256, maxSize: 2048);
        var set2Files = tree.Files.GetRange(set1Files.Count, tree.Files.Count - set1Files.Count);

        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        fixture.BackupFiles(set1Files, description: "Set 1");
        fixture.BackupFiles(set2Files, description: "Set 2");
        fixture.LoadTOC();

        fixture.TOC.CurrentSetIndex = fixture.TOC.LastSetOnVolume;
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        var result = agent.DeleteSetsFromCurrentSetUp();
        Assert.True(result, $"Delete failed: {result.ErrorMessage}");

        fixture.LoadTOC();
        Assert.Equal(1, fixture.TOC.Count);
        Assert.Equal("Set 1", fixture.TOC[1].Description);
    }

    #endregion


    #region *** Precondition Checks ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DeleteSet_NotOnVolume_Fails(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("data", count: 2, minSize: 256, maxSize: 1024);

        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Test Set");
        fixture.LoadTOC();

        // Artificially set volume to 2 so current set is not on volume
        fixture.TOC.Volume = 2;
        fixture.TOC.CurrentSetIndex = 1;

        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        var result = agent.DeleteSetsFromCurrentSetUp();
        Assert.False(result.Success, "Should have failed — set not on volume");
    }

    #endregion
}
