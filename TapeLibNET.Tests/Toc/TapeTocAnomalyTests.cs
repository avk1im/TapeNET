using TapeLibNET.Agents;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Toc;

/// <summary>
/// Design-TocLocation Phase 2, agent level: every TOC copy that does not reach the tape, or does not read, is a TOC
///  anomaly — recorded on the agent, counted in <see cref="TapeTocStatistics"/>, reported through
///  <see cref="ITapeTocNotifiable"/>, once per volume, and settled by a later success.
/// </summary>
/// <remarks>
/// <see cref="TapeAgentBase.SimulateTOCFailureMask"/> fails a copy INSIDE the agent: bit 0 the 1st attempt, bit 1 the
///  2nd, bit 2 the 3rd (the read's direct retry). The counter restarts with every BackupTOC / RestoreTOC call.
/// </remarks>
public class TapeTocAnomalyTests
{
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #region *** Helpers ***

    /// <summary>A set backed up on the fixture's tape, its TOC NOT yet written — the agent is the caller's.</summary>
    private static TapeFileBackupAgent BackupWithoutToc(VirtualTapeFixture fixture, TempFileTree tree)
    {
        fixture.TOC.AddNewSetTOC(0);
        fixture.TOC.CurrentSetTOC.Description = "TOC anomalies";
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        var agent = fixture.CreateBackupAgent();
        Assert.True(agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: false),
            agent.LastErrorMessage);
        return agent;
    }

    private static TempFileTree SmallTree(string name)
    {
        var tree = new TempFileTree();
        tree.AddFiles(name, count: 3, minSize: 512, maxSize: 4 * 1024);
        return tree;
    }

    #endregion

    #region *** Write: the clean path is silent ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupToc_Clean_RecordsNothing_CountsTwoCopies(DriveProfile profile)
    {
        using var tree = SmallTree("clean");
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = BackupWithoutToc(fixture, tree);
        var notify = new TestNotifiable();

        Assert.True(agent.BackupTOC(tocNotify: notify), agent.LastErrorMessage);

        Assert.Empty(agent.TocAnomalies);
        notify.AssertNoTocAnomalies();
        var toc = agent.Statistics.TocStats;
        Assert.Equal(2, toc.CopiesWritten);
        Assert.Equal(0, toc.CopyWriteFailures);
        Assert.False(toc.HasAnomalies);
    }

    #endregion

#if TEST_HOOKS

    #region *** Write: a missing copy is a success with an anomaly ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupToc_SecondCopyFails_SuccessWithCopyWriteFailed(DriveProfile profile)
    {
        using var tree = SmallTree("second");
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = BackupWithoutToc(fixture, tree);
        var notify = new TestNotifiable();
        agent.SimulateTOCFailureMask = 2;

        Assert.True(agent.BackupTOC(tocNotify: notify), "one copy on tape is a success");

        var a = Assert.Single(agent.TocAnomalies);
        Assert.Equal(TapeTocAnomalyKind.CopyWriteFailed, a.Kind);
        Assert.Equal(TapeTocAnomalyStage.Detected, a.Stage);
        Assert.Equal(2, a.CopyIndex);
        Assert.Equal(fixture.TOC.Volume, a.Volume);
        Assert.False(a.Diagnosis.Success, "the anomaly carries the cause");

        var toc = agent.Statistics.TocStats;
        Assert.Equal(1, toc.CopiesWritten);
        Assert.Equal(1, toc.CopyWriteFailures);
        Assert.Equal(1, toc.AnomaliesDetected);
        Assert.Equal(0, toc.VolumesWithoutToc);

        var reported = Assert.Single(notify.TocAnomalies);
        Assert.Equal(a, reported.Anomaly);
        Assert.Empty(notify.TocAnomaliesRecovered);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupToc_FirstCopyFails_SuccessNamesCopyOne(DriveProfile profile)
    {
        using var tree = SmallTree("first");
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = BackupWithoutToc(fixture, tree);
        agent.SimulateTOCFailureMask = 1;

        Assert.True(agent.BackupTOC(), "one copy on tape is a success");

        var a = Assert.Single(agent.TocAnomalies);
        Assert.Equal(TapeTocAnomalyKind.CopyWriteFailed, a.Kind);
        Assert.Equal(1, a.CopyIndex);
        Assert.True(agent.LastResult.Success, "a missing copy is no failure of the operation");
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupToc_BothCopiesFail_FailureWithNoCopyWritten(DriveProfile profile)
    {
        using var tree = SmallTree("none");
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = BackupWithoutToc(fixture, tree);
        var notify = new TestNotifiable();
        agent.SimulateTOCFailureMask = 3;

        Assert.False(agent.BackupTOC(tocNotify: notify));

        var a = Assert.Single(agent.TocAnomalies);
        Assert.Equal(TapeTocAnomalyKind.NoCopyWritten, a.Kind);
        Assert.Equal(0, a.CopyIndex);
        Assert.False(a.Diagnosis.Success);

        var toc = agent.Statistics.TocStats;
        Assert.Equal(0, toc.CopiesWritten);
        Assert.Equal(2, toc.CopyWriteFailures);
        Assert.Equal(1, toc.VolumesWithoutToc);
        Assert.Single(notify.TocAnomalies);
    }

    #endregion

    #region *** Once per volume; settled by a later success ***

    /// <summary>The trail keeps every finding; the callback and the counter speak once per volume.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupToc_FailsTwiceOnOneVolume_RaisedOnce_TrailKeepsBoth(DriveProfile profile)
    {
        using var tree = SmallTree("twice");
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = BackupWithoutToc(fixture, tree);
        var notify = new TestNotifiable();
        agent.SimulateTOCFailureMask = 3;

        Assert.False(agent.BackupTOC(tocNotify: notify));
        Assert.False(agent.BackupTOC(tocNotify: notify));

        Assert.Equal(2, agent.TocAnomalies.Count);
        Assert.All(agent.TocAnomalies, a => Assert.Equal(TapeTocAnomalyKind.NoCopyWritten, a.Kind));
        Assert.Single(notify.TocAnomalies);
        Assert.Equal(1, agent.Statistics.TocStats.AnomaliesDetected);
        Assert.Equal(1, agent.Statistics.TocStats.VolumesWithoutToc);
    }

    /// <summary>
    /// A later TOC write in the same operation (the service's retry, at agent level) that succeeds settles the lost TOC — a recovery
    ///  with <see cref="TapeTocAnomalyStage.Retried"/>, and the volume is no longer counted as without TOC.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupToc_LostThenWritten_SettledAsRetried(DriveProfile profile)
    {
        using var tree = SmallTree("retried");
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = BackupWithoutToc(fixture, tree);
        var notify = new TestNotifiable();

        agent.SimulateTOCFailureMask = 3;
        Assert.False(agent.BackupTOC(tocNotify: notify));
        agent.SimulateTOCFailureMask = 0;
        Assert.True(agent.BackupTOC(tocNotify: notify), agent.LastErrorMessage);

        Assert.Collection(agent.TocAnomalies,
            a => Assert.Equal((TapeTocAnomalyKind.NoCopyWritten, TapeTocAnomalyStage.Detected), (a.Kind, a.Stage)),
            a => Assert.Equal((TapeTocAnomalyKind.NoCopyWritten, TapeTocAnomalyStage.Retried), (a.Kind, a.Stage)));

        var recovered = Assert.Single(notify.TocAnomaliesRecovered).Anomaly;
        Assert.True(recovered.Diagnosis.Success, "a recovery reports the TOC's state NOW");

        var toc = agent.Statistics.TocStats;
        Assert.Equal(1, toc.AnomaliesDetected);
        Assert.Equal(1, toc.AnomaliesRecovered);
        Assert.Equal(0, toc.VolumesWithoutToc);
        Assert.Equal(2, toc.CopiesWritten);

        fixture.LoadTOC();     // and the TOC on tape is good
        Assert.Equal(1, fixture.TOC.Count);
    }

    /// <summary>The records belong to the operation: a new backup starts with none.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Anomalies_AreClearedAtOperationStart(DriveProfile profile)
    {
        using var tree = SmallTree("clear");
        using var fixture = new VirtualTapeFixture(profile);
        using var agent = fixture.CreateBackupAgent();

        agent.SimulateTOCFailureMask = 2;
        Assert.True(agent.BackupTOC());
        Assert.NotEmpty(agent.TocAnomalies);
        agent.SimulateTOCFailureMask = 0;

        fixture.TOC.AddNewSetTOC(0);
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        Assert.True(agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: false),
            agent.LastErrorMessage);

        Assert.Empty(agent.TocAnomalies);
        Assert.False(agent.Statistics.TocStats.HasAnomalies);
        Assert.Equal(0, agent.Statistics.TocStats.CopyWriteFailures);
    }

    #endregion

    #region *** Read ***

    /// <summary>The 1st copy does not read, the 2nd does: detected AND recovered, both on the volume the TOC names.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RestoreToc_FirstCopyUnreadable_RecoveredBySibling(DriveProfile profile)
    {
        using var tree = SmallTree("sibling");
        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Sibling");

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC()) { SimulateTOCFailureMask = 1 };
        var notify = new TestNotifiable();
        Assert.True(reader.RestoreTOC(notify), reader.LastErrorMessage);
        Assert.Equal(1, reader.TOC.Count);

        Assert.Collection(reader.TocAnomalies,
            a =>
            {
                Assert.Equal((TapeTocAnomalyKind.CopyUnreadable, TapeTocAnomalyStage.Detected), (a.Kind, a.Stage));
                Assert.Equal(1, a.CopyIndex);
                Assert.Equal(fixture.TOC.Volume, a.Volume);
                Assert.False(a.Diagnosis.Success);
            },
            a => Assert.Equal((TapeTocAnomalyKind.CopyUnreadable, TapeTocAnomalyStage.Sibling), (a.Kind, a.Stage)));

        Assert.Single(notify.TocAnomalies);
        Assert.Single(notify.TocAnomaliesRecovered);
        Assert.Equal(1, reader.Statistics.TocStats.AnomaliesDetected);
        Assert.Equal(1, reader.Statistics.TocStats.AnomaliesRecovered);
    }

    /// <summary>No copy reads: one anomaly, on the unknown volume (0) — nothing on tape could name it.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RestoreToc_NoCopyReads_NoReadableCopy(DriveProfile profile)
    {
        using var tree = SmallTree("unreadable");
        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Unreadable");

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC()) { SimulateTOCFailureMask = 7 };
        var notify = new TestNotifiable();
        Assert.False(reader.RestoreTOC(notify));

        var a = Assert.Single(reader.TocAnomalies);
        Assert.Equal(TapeTocAnomalyKind.NoReadableCopy, a.Kind);
        Assert.Equal(0, a.Volume);
        Assert.False(a.Diagnosis.Success);
        Assert.Single(notify.TocAnomalies);
    }

    /// <summary>A clean read records nothing.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RestoreToc_Clean_RecordsNothing(DriveProfile profile)
    {
        using var tree = SmallTree("readclean");
        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Clean");

        using var reader = new TapeAgentBase(fixture.Drive, new TapeTOC());
        var notify = new TestNotifiable();
        Assert.True(reader.RestoreTOC(notify), reader.LastErrorMessage);

        Assert.Empty(reader.TocAnomalies);
        notify.AssertNoTocAnomalies();
    }

    #endregion

    #region *** The set verbs report the TOC write too ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Delete_SecondTocCopyFails_ReachesTheSetNotifiable(DriveProfile profile)
    {
        using var tree1 = SmallTree("del1");
        using var tree2 = SmallTree("del2");
        using var fixture = new VirtualTapeFixture(profile);
        fixture.BackupFiles(tree1.Files, description: "Keep");
        fixture.BackupFiles(tree2.Files, description: "Delete");

        using var agent = new TapeSetAgent(fixture.Drive, fixture.TOC) { SimulateTOCFailureMask = 2 };
        fixture.TOC.CurrentSetIndex = fixture.TOC.LastSetOnVolume;
        ITapeSetNotifiable notify = new TestNotifiable();

        Assert.True(agent.DeleteSetsFromCurrentSetUp(setNotify: notify), agent.LastErrorMessage);

        var a = Assert.Single(((TestNotifiable)notify).TocAnomalies).Anomaly;
        Assert.Equal(TapeTocAnomalyKind.CopyWriteFailed, a.Kind);
        Assert.Equal(1, agent.Statistics.TocStats.CopyWriteFailures);
    }

    #endregion

#endif // TEST_HOOKS

    #region *** A full volume: only one copy fits ***

    /// <summary>
    /// The TOC-mark layout needs the gap plus two copies — 64 B more than a 32 KiB reserve holds (Design-TocLocation F4).
    ///  On a full volume copy 1 fits and copy 2 does not: a success, with <see cref="TapeTocAnomalyKind.CopyWriteFailed"/>
    ///  for copy 2 and END_OF_MEDIA as its cause — the input of the "calibrate the drive" advice.
    /// </summary>
    /// <remarks>Geometry as in <c>TapeTOCWriteIntegrityTests</c>: 256 KiB content, a media header, a 32 KiB reserve.</remarks>
    [Fact]
    public void FullVolume_TocMarkLayout_OnlyOneCopyFits_CopyWriteFailedWithEom()
    {
        const long capacity = 256L * 1024;
        using var tree = new TempFileTree();
        tree.AddFiles("full", count: 24, minSize: 12 * 1024, maxSize: 16 * 1024);
        using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks, withMediaHeader: true,
            contentCapacity: capacity);

        fixture.TOC.AddNewSetTOC(0);
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        using var agent = fixture.CreateBackupAgent();
        agent.Navigator.TOCCapacity = 32 * 1024;

        Assert.False(agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: false));
        Assert.True(agent.CanResumeToNextVolume, "the volume must have run full");

        Assert.True(agent.BackupTOC(), $"one copy on tape is a success: {agent.LastErrorMessage}");

        var a = Assert.Single(agent.TocAnomalies);
        Assert.Equal(TapeTocAnomalyKind.CopyWriteFailed, a.Kind);
        Assert.Equal(2, a.CopyIndex);
        Assert.True(ErrorManageableBase.IsEOM(a.Diagnosis.ErrorCode),
            $"the cause is end-of-media, not {a.Diagnosis.ErrorMessage}");
    }

    #endregion
}
