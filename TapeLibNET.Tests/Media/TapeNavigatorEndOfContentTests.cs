using TapeLibNET.Agents;
using TapeLibNET.Media;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using Windows.Win32.Foundation;

namespace TapeLibNET.Tests.Media;

/// <summary>
/// Design-TocLocation R2 / TL-3: when end-of-content cannot be found by marks, the TOC-in-set navigators settle at
///  begin-of-content ONLY on positive blank evidence. Content without marks is refused — the old fallback read the media
///  header as a TOC and let an append overwrite set 0.
/// </summary>
public class TapeNavigatorEndOfContentTests
{
    public static TheoryData<DriveProfile, bool> InSetProfilesAndHeaders
    {
        get
        {
            TheoryData<DriveProfile, bool> data = [];
            foreach (var profile in new[] { DriveProfile.Setmarks, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly })
            {
                data.Add(profile, false);
                data.Add(profile, true);
            }
            return data;
        }
    }

    /// <summary>Content written with NO marks at all: end-of-content is unknowable — refuse, never "blank".</summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void ContentWithoutMarks_IsRefused(DriveProfile profile, bool headed)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed);
        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        Assert.True(agent.Navigator.MoveToBeginOfContent());

        var block = new byte[fixture.Drive.BlockSize];
        new Random(3).NextBytes(block);
        for (int i = 0; i < 3; i++)
            Assert.Equal(block.Length, fixture.Drive.WriteDirect(block, 0, block.Length));
        agent.Navigator.ResetContentSet();

        Assert.False(agent.Navigator.MoveToEndOfContent(), "content without marks must not be taken for blank media");

        // R2's promise: refused, and settled NOWHERE. The error is either INVALID_DATA, or — when the mark-based locate
        //  failed positionally — that positional error, kept so the agent's ladder can retry from begin-of-content.
        Assert.Equal(TapeNavigator.UnknownSet, agent.Navigator.CurrentContentSet);
        Assert.Contains(agent.Navigator.LastErrorWin32, new[]
        {
            WIN32_ERROR.ERROR_INVALID_DATA,
            WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA,
            WIN32_ERROR.ERROR_NO_DATA_DETECTED,
            WIN32_ERROR.ERROR_END_OF_MEDIA,
        });
    }

    /// <summary>Blank media (headed or not): end-of-content IS begin-of-content, at its exact block.</summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void BlankMedia_SettlesAtBeginOfContent(DriveProfile profile, bool headed)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed);
        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        agent.Navigator.ResetContentSet();

        Assert.True(agent.Navigator.MoveToEndOfContent(), agent.Navigator.LastErrorMessage);
        Assert.Equal(fixture.FirstContentBlock, fixture.Drive.CurrentBlock);
    }

    /// <summary>A healthy tape is unaffected: append after one set, then reload — two sets.</summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void HealthyTape_AppendStillWorks(DriveProfile profile, bool headed)
    {
        using var tree1 = new TempFileTree(seed: 1);
        tree1.AddFiles("h1", count: 3, minSize: 512, maxSize: 4 * 1024);
        using var tree2 = new TempFileTree(seed: 2);
        tree2.AddFiles("h2", count: 2, minSize: 512, maxSize: 4 * 1024);
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed, withSetHeaders: headed);

        fixture.BackupFiles(tree1.Files, description: "One");
        fixture.LoadTOC();
        fixture.BackupFiles(tree2.Files, description: "Two");
        fixture.LoadTOC();

        Assert.Equal(2, fixture.TOC.Count);
        Assert.Equal(tree1.Files.Count, fixture.TOC[1].Count);
    }

    /// <summary>A tape holding only a TOC (zero content sets): end-of-content is begin-of-content, and the TOC reloads.</summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void TocOnly_ZeroSets_EndOfContentIsBeginOfContent(DriveProfile profile, bool headed)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed);
        fixture.SaveAndReloadTOC();

        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        agent.Navigator.ResetContentSet();
        Assert.True(agent.Navigator.MoveToEndOfContent(), agent.Navigator.LastErrorMessage);
        Assert.Equal(fixture.FirstContentBlock, fixture.Drive.CurrentBlock);
    }

    /// <summary>
    /// TOC-mark layout: the initial TOC right after the media header (as Format writes it) — the navigator stands in set 0,
    ///  no mark exists yet. It settles at begin-of-content and writes the mark; the TOC reloads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeqFilemarks_InitialTocAfterMediaHeader_Writes(bool headed)
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.SeqFilemarks, withMediaHeader: false);
        using (var agent = new TapeAgentBase(fixture.Drive, fixture.TOC))
        {
            if (headed)
                Assert.True(agent.WriteMediaHeader(), agent.LastErrorMessage);   // leaves CurrentContentSet = 0
            Assert.True(agent.BackupTOC(), agent.LastErrorMessage);
        }
        Assert.Equal(0, fixture.TOC.Count);
    }

    #region *** The recorded end of content (Design-TocLocation §8.1, TL-3) ***

    /// <summary>One set backed up, TOC saved: returns the block where the set really ends.</summary>
    private static long BackupOneSet(VirtualTapeFixture fixture, TempFileTree tree)
    {
        fixture.BackupFiles(tree.Files, description: "One");
        long end = fixture.TOC[1].FirstBlockAfterSet;
        Assert.True(end > 0, $"the set must record its end ({end})");
        return end;
    }

    private static TempFileTree SmallTree(int seed, string name)
    {
        var tree = new TempFileTree(seed: seed);
        tree.AddFiles(name, count: 3, minSize: 512, maxSize: 4 * 1024);
        return tree;
    }

    /// <summary>The navigator's probe: the true end is confirmed and the head stands there; −1 records nothing to check.</summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void CheckRecordedEnd_TrueEnd_Confirmed_HeadThere(DriveProfile profile, bool headed)
    {
        using var tree = SmallTree(1, "confirm");
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed, withSetHeaders: headed);
        long end = BackupOneSet(fixture, tree);

        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        agent.Navigator.ResetContentSet();

        Assert.Equal(RecordedEndOfContent.NotApplicable, agent.Navigator.CheckRecordedEndOfContent(-1, out _));
        Assert.Equal(RecordedEndOfContent.Confirmed, agent.Navigator.CheckRecordedEndOfContent(end, out TapeResult evidence));
        Assert.True(evidence.Success);
        Assert.Equal(end, fixture.Drive.CurrentBlock);
        Assert.Equal(-1, agent.Navigator.CurrentContentSet);
    }

    /// <summary>The probe at a block inside the set: disputed, with evidence, and NO position believed.</summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void CheckRecordedEnd_InsideTheSet_Disputed_NothingBelieved(DriveProfile profile, bool headed)
    {
        using var tree = SmallTree(2, "dispute");
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed, withSetHeaders: headed);
        BackupOneSet(fixture, tree);
        long inside = fixture.TOC[1][0].Address.Block;

        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        agent.Navigator.ResetContentSet();

        Assert.Equal(RecordedEndOfContent.Disputed, agent.Navigator.CheckRecordedEndOfContent(inside, out TapeResult evidence));
        Assert.False(evidence.Success);
        Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, evidence.ErrorCode);
        Assert.Equal(TapeNavigator.UnknownSet, agent.Navigator.CurrentContentSet);
        Assert.True(agent.Navigator.WentOK, "the probe's own errors are cleared — the verdict carries them");
    }

    /// <summary>TOC in partition records no end to check.</summary>
    [Fact]
    public void CheckRecordedEnd_Partitions_NotApplicable()
    {
        using var tree = SmallTree(3, "partition");
        using var fixture = new VirtualTapeFixture(DriveProfile.Partitions);
        long end = BackupOneSet(fixture, tree);
        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        Assert.Equal(RecordedEndOfContent.NotApplicable, agent.Navigator.CheckRecordedEndOfContent(end, out _));
    }

    /// <summary>
    /// The refusal end to end: the TOC records an end inside the set, the marks lead to the true end. The append is
    ///  refused before writing anything — the diagnosis states the facts (no advice), the dispute is a TOC anomaly with
    ///  the recorded block, and the tape still holds exactly the one set, ending where it did.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void Append_RecordedEndDisputed_Refused_TapeUnchanged_AnomalyRecorded(DriveProfile profile, bool headed)
    {
        using var tree1 = SmallTree(4, "first");
        using var tree2 = SmallTree(5, "second");
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed, withSetHeaders: headed);
        long trueEnd = BackupOneSet(fixture, tree1);
        int filesInSet = fixture.TOC[1].Count;

        // The TOC's record goes wrong — in memory only; the tape is consistent.
        long recorded = fixture.TOC[1][0].Address.Block;
        Assert.NotEqual(trueEnd, recorded);
        fixture.TOC[1].FirstBlockAfterSet = recorded;

        fixture.TOC.AddNewSetTOC(0);
        fixture.TOC.CurrentSetTOC.Description = "Refused";
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        var notify = new TestNotifiable();
        using (var agent = fixture.CreateBackupAgent())
        {
            TapeResult result = agent.BackupFileListToCurrentSet(newSet: true, tree2.Files, ignoreFailures: false,
                fileNotify: notify);

            Assert.False(result.Success);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, result.ErrorCode);
            Assert.Contains("End of content disputed", result.ErrorMessage);
            Assert.Contains($"block {recorded}", result.ErrorMessage);
            Assert.Contains($"block {trueEnd}", result.ErrorMessage);
            Assert.DoesNotContain("Scan the media", result.ErrorMessage);   // advice is the service's
            Assert.False(agent.CanResumeToNextVolume);
            Assert.Equal(0, agent.Statistics.FilesSucceeded);

            var a = Assert.Single(agent.TocAnomalies);
            Assert.Equal((TapeTocAnomalyKind.RecordedEndDisputed, TapeTocAnomalyStage.Detected), (a.Kind, a.Stage));
            Assert.Equal(recorded, a.Block);
            Assert.Equal(fixture.TOC.Volume, a.Volume);
            Assert.False(a.Diagnosis.Success);
            Assert.Equal(1, agent.Statistics.TocStats.AnomaliesDetected);
            Assert.Equal(a, Assert.Single(notify.TocAnomalies).Anomaly);
        }

        // Nothing was written: the tape's TOC still describes the one set, and content still ends where it did.
        fixture.LoadTOC();
        Assert.Equal(1, fixture.TOC.Count);
        Assert.Equal(filesInSet, fixture.TOC[1].Count);
        Assert.Equal(trueEnd, fixture.TOC[1].FirstBlockAfterSet);
        using var probe = new TapeAgentBase(fixture.Drive, fixture.TOC);
        probe.EnsureMediaHeaderResolved();
        probe.Navigator.ResetContentSet();
        Assert.True(probe.Navigator.MoveToEndOfContent(), probe.Navigator.LastErrorMessage);
        Assert.Equal(trueEnd, fixture.Drive.CurrentBlock);
    }

    /// <summary>
    /// Corrupts the first block of TOC copy 1 on the content partition — the data run in front of the second-last
    ///  filemark (<c>[toc1][FM][toc2][FM]</c>), wherever the layout put it. Returns that block.
    /// </summary>
    private static long CorruptFirstTocCopy(VirtualTapeFixture fixture)
    {
        var media = fixture.Backend.ContentMedia!;
        var layout = media.Layout.ToList();
        var marks = layout.Where(b => b.IsMark).ToList();
        var toc1 = layout[layout.IndexOf(marks[^2]) - 1];
        Assert.False(toc1.IsMark, media.DescribeLayout());
        media.CorruptBlockAt(toc1.BeginAtBlock, offset: 0);
        return toc1.BeginAtBlock;
    }

    /// <summary>
    /// The other half of TL-3: the recorded end is RIGHT, but the tape cannot confirm it — TOC copy 1 there is damaged.
    ///  The marks lead to the same block, so the two independent sources agree: the append proceeds, overwriting the
    ///  damaged copy; the unreadable copy is on record (CopyUnreadable, copy 1, at the recorded block); the operation's
    ///  own TOC write replaces it, so nothing is left to advise. Both sets stand.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void Append_RecordedEndUnconfirmed_MarksAgree_Appends_CopyUnreadableRecorded(DriveProfile profile, bool headed)
    {
        using var tree1 = SmallTree(6, "first");
        using var tree2 = SmallTree(7, "second");
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed, withSetHeaders: headed);
        long trueEnd = BackupOneSet(fixture, tree1);

        long copy1 = CorruptFirstTocCopy(fixture);
        if (profile is DriveProfile.Setmarks or DriveProfile.FilemarksOnly)
            Assert.Equal(trueEnd, copy1);               // the TOC starts right where content ends
        else
            Assert.True(copy1 > trueEnd, "past the gap file and the TOC mark");

        // The probe alone: the recorded end cannot be confirmed — the TOC area there does not read as written.
        using (var probe = new TapeAgentBase(fixture.Drive, fixture.TOC))
        {
            probe.EnsureMediaHeaderResolved();
            probe.Navigator.ResetContentSet();
            Assert.Equal(RecordedEndOfContent.Disputed, probe.Navigator.CheckRecordedEndOfContent(trueEnd, out _));
        }

        fixture.TOC.AddNewSetTOC(0);
        fixture.TOC.CurrentSetTOC.Description = "Agreed";
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        var notify = new TestNotifiable();
        using (var agent = fixture.CreateBackupAgent())
        {
            TapeResult result = agent.BackupFileListToCurrentSet(newSet: true, tree2.Files, ignoreFailures: false,
                fileNotify: notify);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(tree2.Files.Count, agent.Statistics.FilesSucceeded);

            var detected = Assert.Single(agent.TocAnomalies);
            Assert.Equal((TapeTocAnomalyKind.CopyUnreadable, TapeTocAnomalyStage.Detected), (detected.Kind, detected.Stage));
            Assert.Equal((byte)1, detected.CopyIndex);
            Assert.Equal(trueEnd, detected.Block);
            Assert.Equal(fixture.TOC.Volume, detected.Volume);
            Assert.Equal(detected, Assert.Single(notify.TocAnomalies).Anomaly);

            // The operation's TOC write replaces the damaged copy: settled, nothing left to advise.
            Assert.True(agent.BackupTOC(tocNotify: notify), agent.LastErrorMessage);
            Assert.Contains(agent.TocAnomalies, a => a is
            { Kind: TapeTocAnomalyKind.CopyUnreadable, Stage: TapeTocAnomalyStage.Retried });
            Assert.Equal(TocAnomalyAdvice.None,
                TapeServiceBase.AdviseOnTocAnomalies(agent.TocAnomalies, agent.TOCPlacement).Advice);
        }

        // Both sets on tape; the second starts exactly where the first ended.
        fixture.LoadTOC();
        Assert.Equal(2, fixture.TOC.Count);
        Assert.Equal(trueEnd + (headed ? 1 : 0), fixture.TOC[2][0].Address.Block);
        Assert.True(fixture.TOC[2].FirstBlockAfterSet > trueEnd);
        TocInvariants.AssertWellFormed(fixture.TOC);
    }

    #endregion

}
