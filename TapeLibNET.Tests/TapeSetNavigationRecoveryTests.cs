using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;
using Windows.Win32.Foundation;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 5 — the unified two-stage recovery (SH-14). A cartridge whose TAIL no longer matches its TOC is
///  brought back to a sound state instead of staying stuck.
/// <para>
/// The centrepiece is <see cref="FailedBackupTail_DeleteRecovers_AndTocIsStored"/>: it manufactures the
///  real fault — a set header on tape whose closing setmark never made it — and then repairs the
///  cartridge. Everything else in this file exists to prove that the repair happened for the RIGHT
///  reason, since a recovery that lands on the correct set by luck is indistinguishable from one that
///  works, right up until the day it isn't.
/// </para>
/// </summary>
public class TapeSetNavigationRecoveryTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    /// <summary>Non-partition profiles: the TOC-in-set layouts, where a damaged tail is reachable.</summary>
    public static TheoryData<DriveProfile> TOCInSetProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #endregion

    #region *** Helpers ***

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
        catch { /* best-effort */ }
    }

    private static string NewRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_SNR_{Guid.NewGuid():N}");

    private static void AssertRestoredMatches(TempFileTree tree, string restoreDir)
    {
        string equivalent = Path.Combine(
            restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath));
        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, equivalent);
    }

    /// <summary>
    /// A tape of <paramref name="setCount"/> sets with distinctly-named files, so a recovery that landed
    ///  on the wrong set cannot masquerade as success.
    /// </summary>
    /// <remarks>
    /// FIVE sets is the working minimum for the miscount injector on the setmark and partition layouts:
    ///  the <c>-1</c> settle inside <c>MoveToEndOfContentCore</c> consumes the injection, so the tape must
    ///  be long enough that the cancelled settle still lands inside content.
    /// </remarks>
    private static TempFileTree[] BuildMultiSetTape(VirtualTapeFixture fixture, int setCount, string prefix)
    {
        var trees = new TempFileTree[setCount];
        for (int i = 0; i < setCount; i++)
        {
            trees[i] = new TempFileTree();
            trees[i].AddFiles($"{prefix}{i + 1}", count: 3, minSize: 512, maxSize: 8 * 1024);
            fixture.BackupFiles(trees[i].Files, description: $"Set {i + 1}");
        }
        return trees;
    }

    private static void DisposeAll(TempFileTree[] trees)
    {
        foreach (var t in trees)
            t.Dispose();
    }

    private static void AssertSetStillRestores(VirtualTapeFixture fixture, int setIndex, TempFileTree tree)
    {
        string restoreDir = NewRestoreDir();
        try
        {
            fixture.TOC.CurrentSetIndex = setIndex;
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                $"set #{setIndex} must restore");
            AssertRestoredMatches(tree, restoreDir);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

#if DEBUG
    /// <summary>
    /// Layout-aware tail damage. Produces a cartridge whose own end-anchored counting primitive lands on
    ///  the WRONG set — which is a different physical fault on each layout, though the same logical one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The filemark layouts</b> are damaged by an aborted mid-set backup: the set header reaches tape,
    ///  the closing mark does not, and <c>MoveToNextFilemark(-3)</c> — which assumes a fixed number of
    ///  filemarks between EOD and the TOC — is thrown off by the orphan.
    /// </para>
    /// <para>
    /// <b>The setmark layout shrugs that off</b>, and rightly so: that is the whole value of a dedicated
    ///  mark type. Its count is of SETMARKS, and the aborted set never wrote one, so the last setmark on
    ///  tape is still the one that legitimately closed the previous set. To make it suffer we must
    ///  destroy a setmark that DOES exist — position before the last one and write content over it.
    /// </para>
    /// </remarks>
    private static void DamageTheTail(VirtualTapeFixture fixture, TempFileTree doomed)
    {
        var notify = new TestNotifiable { AbortAfterNPreProcessed = 1 };

        using var agent = fixture.CreateBackupAgent();
        fixture.TOC.AddNewSetTOC();
        fixture.TOC.CurrentSetTOC.Description = "Doomed Set";

        // Expected to fail: the abort fires mid-set, after the set header reached tape.
        agent.BackupFileListToCurrentSet(newSet: true, doomed.Files,
            ignoreFailures: false, fileNotify: notify);

        // Drop the half-written set from the in-memory TOC — the failed operation never recorded it.
        fixture.TOC.RemoveLastEmptySet();

        if (fixture.Drive.SupportsSetmarks)
        {
            EraseLastSetmark(fixture);
            return;
        }
    }

    /// <summary>
    /// Overwrites the setmark that closes the LAST set, so a backward setmark count runs one set too far.
    /// </summary>
    /// <remarks>
    /// Writing a block AT the setmark's position destroys it (a tape write truncates everything beyond),
    ///  which is exactly what a partial write during a power loss does. The TOC is deliberately left
    ///  describing the pre-damage tape, mirroring an operation that never reached its TOC write.
    /// </remarks>
    private static void EraseLastSetmark(VirtualTapeFixture fixture)
    {
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();

        // End of content, then back over the setmark that closes the last set.
        Assert.True(agent.Navigator.MoveToEndOfContent(), "failed to reach end-of-content");
        Assert.True(agent.Navigator.MoveToNextContentSetmark(-1), "failed to step back over the last setmark");

        // Write here: the setmark is gone, and with it the anchor a backward count depends on.
        Assert.True(fixture.Drive.WriteGapFile(), "failed to overwrite the trailing setmark");

        agent.Navigator.ResetContentSet();   // nobody knows where anything is now — which is the point
    }
#endif
    
    #endregion

    #region *** (A) The originating scenario ***

#if DEBUG
    /// <summary>
    /// The feature's reason for existing, end to end: a cartridge whose tail was left mis-shapen by a
    ///  failed backup is brought back to a sound state by a delete that would previously have refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserts FOUR things, because the return value alone would pass for a version that deleted one set
    ///  too many: the right sets survive and restore BYTE-FOR-BYTE, the wrong ones are gone, the TOC was
    ///  written, and the recovery was REPORTED — so the user learns the tail is unreliable.
    /// </para>
    /// <para>
    /// Before Step 5 this test fails at the delete: the backward count crosses the damaged region, the
    ///  set header disagrees, and SH-13 refuses. That refusal was safe but left the user with no verb
    ///  that could repair the cartridge — which is the gap §2.1 of the design argues is not acceptable.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FailedBackupTail_DeleteRecovers_AndTocIsStored(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        using var doomed = new TempFileTree();
        doomed.AddFiles("doomed", count: 3, minSize: 512, maxSize: 4 * 1024);
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "tail");

            DamageTheTail(fixture, doomed);

            // Now repair: delete from set 4 up, keeping 1..3. The navigation counts BACKWARD across the
            //  damage, so stage 1 sees a drift and stage 2 renavigates from begin-of-content.
            fixture.TOC.CurrentSetIndex = 4;
            using (var agent = new TapeFileAgent(fixture.Drive, fixture.TOC))
            {
                var result = agent.DeleteSetsFromCurrentSetUp(fileNotify: notify);
                Assert.True(result, $"the damaged tail must be recoverable: {result.ErrorMessage}");

                var sets = agent.Statistics.Sets;
                Assert.True(sets.AnomaliesRecovered >= 1, "the drift must be reported as RECOVERED");
                Assert.False(sets.SetWriteBlocked, "a recovered delete is not a blocked one");
            }

            // The TOC reached the tape, and describes what actually survives.
            fixture.LoadTOC();
            Assert.Equal(3, fixture.TOC.Count);
            Assert.Equal("Set 3", fixture.TOC[3].Description);

            // The decisive assertion: the RIGHT sets survive, byte-for-byte.
            for (int i = 0; i < 3; i++)
                AssertSetStillRestores(fixture, i + 1, trees[i]);

            // And the user was told the tail is unreliable.
            Assert.NotEmpty(notify.SetAnomaliesRecovered);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// §2.2 of the design, and the reason the ladder is shared: the SAME damaged cartridge now restores.
    ///  A restore's failure costs time rather than data, but that is an argument about consequences, not
    ///  about whether the repair works.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RestoreOnDamagedTail_AlsoRecovers(DriveProfile profile)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];
        using var doomed = new TempFileTree();
        doomed.AddFiles("rdoom", count: 3, minSize: 512, maxSize: 4 * 1024);
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "rt");

            DamageTheTail(fixture, doomed);

            // Restore the LAST intact set — reached by counting backward across the damage.
            fixture.TOC.CurrentSetIndex = 4;
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify),
                "the read path gets the same repair");

            AssertRestoredMatches(trees[3], restoreDir);
            Assert.True(agent.Statistics.Sets.AnomaliesRecovered >= 1);
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }
#endif // DEBUG

    #endregion

    #region *** (B) The recovery works for the RIGHT reason ***

#if DEBUG
    /// <summary>
    /// SH-19, directly. Drives stage 1 to the case that does NOT self-reset — the delta MOVED
    ///  successfully but the re-verify still disagreed — then asserts that stage 2 performed real
    ///  transport rather than tripping the SH-4 idempotence check.
    /// </summary>
    /// <remarks>
    /// Without <c>Navigator.ResetContentSet()</c> the re-target equals what the navigator already
    ///  believes, <c>MoveToTargetContentSet</c> returns true with no move at all, and the second
    ///  verification re-reads the SAME wrong block. Every other test in this file would still pass — by
    ///  landing on the right set for the wrong reason — which is exactly why this one asserts the
    ///  mechanism instead of the outcome.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Renavigation_ActuallyMovesTheHead(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "mv");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = fixture.CreateValidateAgent();

            // SkipN so the injection lands on the NAVIGATION rather than on the end-of-content settle,
            //  and persists just long enough for stage 1's re-verify to disagree too.
            agent.Navigator.SimulateSetMiscount = +1;
            agent.Navigator.SimulateSetMiscountSkipN = 1;

            long blockBefore = fixture.Drive.CurrentBlock;
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                "stage 2 must rescue what stage 1 could not settle");

            // Real transport happened: the head is not where the pre-recovery belief left it.
            Assert.NotEqual(blockBefore, fixture.Drive.CurrentBlock);

            // And the set we ended on is the one the TOC named — verified, not assumed.
            Assert.Equal(TOC_CurrentOnVolume(fixture), agent.Navigator.CurrentContentSet);
        }
        finally
        {
            DisposeAll(trees);
        }

        static int TOC_CurrentOnVolume(VirtualTapeFixture f) => f.TOC.CurrentSetIndexOnVolume;
    }

    /// <summary>
    /// The bound. A miscount that defeats BOTH stages must fail cleanly — no recursion, no third
    ///  attempt — and must ask the user exactly once (SH-18), however many times the ladder looked.
    /// </summary>
    /// <remarks>
    /// Both injection points are live on the renavigation (<c>MoveToNextContentSetmark</c> and the
    ///  <c>TapeNavigatorTOCInSet</c> merged-filemark fast path), so a persistent miscount genuinely
    ///  defeats stage 2 rather than being bypassed by it. That is what makes this a test of the GUARD
    ///  rather than an accident of layout.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Renavigation_IsBoundedStructurally(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "bd");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = fixture.CreateValidateAgent();

            agent.Navigator.SimulateSetMiscount = +1;
            agent.Navigator.SimulateSetMiscountPersistent = true;

            var result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify);

            agent.Navigator.SimulateSetMiscountPersistent = false;
            agent.Navigator.SimulateSetMiscount = 0;

            Assert.False(result, "a drift that survives both stages must fail the set, not loop");
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, result.ErrorCode);

            Assert.Single(notify.SetAnomalies);                          // asked ONCE…
            Assert.True(agent.Statistics.Sets.AnomaliesDetected >= 1);   // …however often the ladder looked
            Assert.Empty(notify.SetAnomaliesRecovered);                  // and nothing was repaired
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The structural precondition. A navigation that already counted FORWARD has no other direction to
    ///  try, so it gets stage 1 and then terminates — no wasted rewind, and no second prompt.
    /// </summary>
    /// <remarks>
    /// Forced via <c>navigateFromBegin: true</c>, which is the user-facing form of exactly the move
    ///  stage 2 makes automatically. Reaching for it here means the target is non-negative from the
    ///  outset, so <c>endAnchored</c> is false.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ForwardNavigation_DoesNotRenavigate(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "fw");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

            agent.Navigator.SimulateSetMiscount = +1;
            agent.Navigator.SimulateSetMiscountPersistent = true;   // defeat stage 1 as well

            var result = agent.DeleteSetsFromCurrentSetUp(navigateFromBegin: true, fileNotify: notify);

            agent.Navigator.SimulateSetMiscountPersistent = false;
            agent.Navigator.SimulateSetMiscount = 0;

            Assert.False(result);
            // Recovered nothing, and — the point — reported no BOM-stage recovery, because none was tried.
            Assert.Equal(0, agent.Statistics.Sets.AnomaliesRecoveredFromBom);
            Assert.Empty(notify.SetAnomaliesRecovered);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Identity verdicts skip every stage (§5.4). Renavigating a cartridge that is not the expected
    ///  cartridge cannot help, and moving the head on a foreign tape is the last thing anyone wants.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WrongVolume_NeverRenavigates(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "wv");

            fixture.TOC.CurrentSetIndex = 3;
            int realVolume = fixture.TOC.Volume;
            fixture.TOC.Volume += 5;

            using (var agent = fixture.CreateValidateAgent())
            {
                var result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify);
                Assert.False(result, "a volume mismatch fails without attempting anything positional");
                Assert.Equal(0, agent.Statistics.Sets.AnomaliesRecovered);
                Assert.Equal(0, agent.Statistics.Sets.AnomaliesRecoveredFromBom);
            }

            fixture.TOC.Volume = realVolume;
            AssertSetStillRestores(fixture, 3, trees[2]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion

    #region *** (C) The terminal split, and the decision ***

#if DEBUG
    /// <summary>
    /// <c>Unreadable</c> renavigates on BOTH paths — it is that verdict's only recovery — and only then
    ///  splits: the read proceeds unverified, the destructive write blocks.
    /// </summary>
    /// <remarks>
    /// The corruption is one-shot, so the block read AFTER the renavigation is clean. That makes this a
    ///  test of the stage rather than of the split: both paths reach a healthy header the second time,
    ///  which is precisely the "the medium was fine, the count was not" confirmation §5.4 describes.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Unreadable_Renavigates_ThenSplitsByPath(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable { SetAnomalyAction = SetAnomalyAction.Proceed };

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "ur");

            // READ path: corrupt the first set-header read; the renavigation re-reads it cleanly.
            fixture.TOC.CurrentSetIndex = 3;
            using (var agent = fixture.CreateValidateAgent())
            {
                agent.ReadBomHeader();
                fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48);

                Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                    "the read path recovers — or, failing that, proceeds unverified");
                Assert.Equal(1, fixture.Backend.ContentReadFaults.Occurrences);
            }

            // WRITE path: same injection, opposite terminal — but only if the renavigation did not settle
            //  it. With a one-shot corruption it DOES settle, so the delete succeeds and the sets that
            //  should survive do.
            fixture.TOC.CurrentSetIndex = 4;
            using (var agent = new TapeFileAgent(fixture.Drive, fixture.TOC))
            {
                agent.ReadBomHeader();
                fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48);

                // Without notify agent will abort for write/delete by default! (vs. proceed for read)
                var result = agent.DeleteSetsFromCurrentSetUp(fileNotify: notify);
                Assert.True(result, $"a positional fault is recoverable on the write path too: {result.ErrorMessage}");
            }

            fixture.LoadTOC();
            Assert.Equal(3, fixture.TOC.Count);
            for (int i = 0; i < 3; i++)
                AssertSetStillRestores(fixture, i + 1, trees[i]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The user's veto is honoured before any transport. Answering <c>Abort</c> at the prompt must leave
    ///  the head exactly where it was — the whole point of asking BEFORE acting.
    /// </summary>
    /// <remarks>
    /// Also pins the error-reporting rule settled for the file paths: the result carries the ANOMALY, not
    ///  the cancellation. The user knows they pressed stop; what they need is what provoked the prompt.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Recovery_Declined_BlocksWithoutMoving(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable { SetAnomalyAction = SetAnomalyAction.Abort };

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "dc");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
            agent.Navigator.SimulateSetMiscount = +1;

            var result = agent.DeleteSetsFromCurrentSetUp(fileNotify: notify);

            Assert.False(result);
            Assert.True(agent.IsAbortRequested);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, result.ErrorCode);   // the REASON, not the veto
            Assert.Equal(0, agent.Statistics.Sets.AnomaliesRecovered);
            Assert.Equal(0, agent.Statistics.Sets.AnomaliesRecoveredFromBom);
            Assert.Single(notify.SetAnomalies);

            // Nothing was destroyed, including the sets the delete was aiming at.
            for (int i = 0; i < trees.Length; i++)
                AssertSetStillRestores(fixture, i + 1, trees[i]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Stage attribution (SH-16). A drift the DELTA settles indicts a single mark; one that needed the
    ///  renavigation indicts the TAIL. The summary must be able to tell them apart, because only the
    ///  second argues for retiring the cartridge.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DeltaRecovery_ReportsDeltaStage(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "dl");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = fixture.CreateValidateAgent();
            agent.Navigator.SimulateSetMiscount = +1;   // one-shot: stage 1 settles it

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify));

            var recovered = Assert.Single(notify.SetAnomaliesRecovered).Anomaly;
            Assert.Equal(TapeSetAnomalyStage.Delta, recovered.Stage);

            // A RECOVERED payload describes the set's state NOW: sound. The fault it repaired lives in
            //  the DETECTION entry of the forensic trail, which is what the prompt carried.
            Assert.True(recovered.Diagnosis.Success);

            var detected = Assert.Single(agent.SetAnomalies);
            Assert.Equal(TapeSetAnomalyStage.Detected, detected.Stage);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, detected.Diagnosis.ErrorCode);
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion

    #region *** (D) No regression on the clean path ***

    /// <summary>
    /// Provable silence. A recovery that fired spuriously would double the transport cost of every
    ///  operation and cry wolf in every summary.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void NoDrift_NoRecovery(DriveProfile profile)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "cl");

            fixture.TOC.CurrentSetIndex = 2;
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.Equal(0, agent.Navigator.SimulateSetMiscount);

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify));
            AssertRestoredMatches(trees[1], restoreDir);

            var sets = agent.Statistics.Sets;
            Assert.False(sets.HasAnomalies);
            Assert.Equal(0, sets.AnomaliesRecovered);
            Assert.Equal(0, sets.AnomaliesRecoveredFromBom);
            notify.AssertNoSetAnomalies();
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }

#if DEBUG
    /// <summary>
    /// A recovered operation leaves the agent sound: the next set, with no fault injected, proceeds
    ///  normally and reports clean.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void AfterRecovery_SubsequentSetIsClean(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "af");

            using var agent = fixture.CreateValidateAgent();

            fixture.TOC.CurrentSetIndex = 3;
            agent.Navigator.SimulateSetMiscount = +1;
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false), "the recovered pass");
            Assert.Equal(1, agent.Statistics.Sets.AnomaliesRecovered);

            fixture.TOC.CurrentSetIndex = 5;
            Assert.Equal(0, agent.Navigator.SimulateSetMiscount);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false), "the clean pass after it");

            // Per-operation statistics: the previous cartridge's drift is not attributed to this one.
            Assert.False(agent.Statistics.Sets.HasAnomalies);
            Assert.Equal(0, agent.Statistics.Sets.AnomaliesRecovered);
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion

    // ============================================================================================
    //  TapeSetNavigationRecoveryTests.cs — Step 5A region
    //  Requires the existing helpers in that class: AllProfiles, BuildMultiSetTape, DisposeAll,
    //   AssertSetStillRestores, NewRestoreDir, AssertRestoredMatches, TryDeleteDirectory.
    // ============================================================================================

    #region *** (E) Step 5A — recovering a FAILED navigation (SH-20) ***

    //  The inconvenient half of the same fault. Step 5 repairs a navigation that COMPLETED and landed
    //   wrong; these repair one that never completed — a backward count that ran out of marks or medium
    //   because the tail is SHORTER than the TOC describes.
    //
    //  The injector is a LARGE negative miscount rather than physical damage: it makes the backward count
    //   demand more marks than exist, which is precisely what a missing trailing mark does, and it works
    //   identically on every layout.

#if DEBUG
    /// <summary>
    /// The feature: a backward count that fails outright is retried forward from begin-of-content, and
    ///  the set is reached after all.
    /// </summary>
    /// <remarks>
    /// Before Step 5A this failed with a bare transport error from a cartridge whose healthy front half
    ///  was sitting there, perfectly reachable — the gap §5.7 was written to close.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FailedBackwardNavigation_RecoversFromBom(DriveProfile profile)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();   // permissive: authorizes the retry

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "fn");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = fixture.CreateRestoreAgent(restoreDir);

            // Demand far more marks than the volume holds: the backward count cannot complete.
            agent.Navigator.SimulateSetMiscount = -10;

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify),
                "an unreachable-by-backward-count set must still be reached from begin-of-content");

            // The decisive assertion: we reached the RIGHT set, not merely a set.
            AssertRestoredMatches(trees[2], restoreDir);

            Assert.Equal(1, agent.Statistics.Sets.AnomaliesRecoveredFromBom);
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Attribution (SH-16). A set reached only by renavigating indicts the volume's TAIL, so it must
    ///  report as <see cref="TapeSetAnomalyStage.Renavigated"/> — and the OPERATION must still succeed.
    /// </summary>
    /// <remarks>
    /// The detection payload carries the real positional error (<c>ERROR_NO_DATA_DETECTED</c> or a
    ///  sibling), never a synthesized stand-in: the host is being asked to authorize a repositioning and
    ///  deserves to know what actually went wrong. The RECOVERY payload reads OK, because by then the set
    ///  is reachable.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FailedNavigation_ReportsRenavigatedStage(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "st");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = fixture.CreateValidateAgent();

            //agent.Navigator.SimulateSetMiscount = +10;
            // A POSITIONAL navigation failure, layout-independent. SimulateSetMiscount cannot serve
            //  here: on the setmark layouts the offset is consumed by the -1 settle inside
            //  MoveToEndOfContentInternal, whose "no setmarks ⇒ assume blank" fallback SWALLOWS the
            //  failure and returns a successful-but-wrong landing — which is a Step 5 drift, not a
            //  Step 5A failed navigation.
            agent.Navigator.SimulateNavigationFailures.EnableOnce();
            agent.Navigator.SimulateNavigationFailureError = WIN32_ERROR.ERROR_NO_DATA_DETECTED;

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify));

            // Asked once, with a real diagnosis naming the transport fault.
            var detected = Assert.Single(notify.SetAnomalies).Anomaly;
            Assert.Equal(TapeSetAnomalyStage.Detected, detected.Stage);
            Assert.False(detected.Diagnosis.Success);
            Assert.NotEqual(0u, detected.Diagnosis.ErrorCode);
            Assert.True(detected.CanAttemptRecovery);

            // Reported as recovered VIA THE TAIL, and the payload says the set is sound now.
            var recovered = Assert.Single(notify.SetAnomaliesRecovered).Anomaly;
            Assert.Equal(TapeSetAnomalyStage.Renavigated, recovered.Stage);
            Assert.True(recovered.Diagnosis.Success);

            var sets = agent.Statistics.Sets;
            Assert.Equal(1, sets.AnomaliesDetected);
            Assert.Equal(1, sets.AnomaliesRecovered);
            Assert.Equal(1, sets.AnomaliesRecoveredFromBom);
            Assert.False(sets.SetWriteBlocked);

            // The repair is not the caller's problem.
            Assert.True(agent.LastResult.Success);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The SH-20 gate. Only a POSITIONAL error earns a retry: a drive that went offline fails to move
    ///  too, and a full-length rewind can neither revive it nor produce a better diagnosis.
    /// </summary>
    /// <remarks>
    /// Asserts the ABSENCE of the recovery, not merely the failure — a version that retried pointlessly
    ///  would still end up failing, just slower and with the wrong error on top.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void NonPositionalNavigationFailure_DoesNotRetry(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "np");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = fixture.CreateValidateAgent();

            // A transport fault that has nothing to do with the mark structure.
            agent.Navigator.SimulateNavigationFailures.EnableOnce();
            agent.Navigator.SimulateNavigationFailureError = WIN32_ERROR.ERROR_NOT_READY;

            var result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify);

            Assert.False(result, "a drive fault must not be papered over by a rewind");
            Assert.Equal((uint)WIN32_ERROR.ERROR_NOT_READY, result.ErrorCode);   // ITS OWN error, unmasked

            // Nothing was asked, nothing was attempted, nothing was recovered.
            Assert.Empty(notify.SetAnomalies);
            Assert.Empty(notify.SetAnomaliesRecovered);
            Assert.Equal(0, agent.Statistics.Sets.AnomaliesRecoveredFromBom);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The structural precondition: a navigation that already counted FORWARD has no other direction to
    ///  try, so a positional failure there is terminal.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FailedForwardNavigation_DoesNotRetry(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "ff");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

            // navigateFromBegin forces the FORWARD count, so endAnchored is false from the outset.
            agent.Navigator.SimulateSetMiscount = +10;   // overshoot past EOD going forward

            var result = agent.DeleteSetsFromCurrentSetUp(navigateFromBegin: true, fileNotify: notify);

            Assert.False(result);
            Assert.Equal(0, agent.Statistics.Sets.AnomaliesRecoveredFromBom);   // no rewind was wasted
            Assert.Empty(notify.SetAnomaliesRecovered);

            // And nothing was destroyed on the way.
            for (int i = 0; i < trees.Length; i++)
                AssertSetStillRestores(fixture, i + 1, trees[i]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// When BOTH directions fail, the SECOND error surfaces — it describes the state we actually ended
    ///  in, which is "unreachable from either direction".
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FailedNavigation_BothDirectionsFail_ReportsSecondError(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "bd");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = fixture.CreateValidateAgent();

            // Persistent: the forward retry demands too many marks as well.
            //agent.Navigator.SimulateSetMiscount = -10;
            //agent.Navigator.SimulateSetMiscountPersistent = true;
            // A POSITIONAL navigation failure, layout-independent. SimulateSetMiscount cannot serve
            //  here: on the setmark layouts the offset is consumed by the -1 settle inside
            //  MoveToEndOfContentInternal, whose "no setmarks ⇒ assume blank" fallback SWALLOWS the
            //  failure and returns a successful-but-wrong landing — which is a Step 5 drift, not a
            //  Step 5A failed navigation.
            agent.Navigator.SimulateNavigationFailures.EnableAlways();
            agent.Navigator.SimulateNavigationFailureError = WIN32_ERROR.ERROR_NO_DATA_DETECTED;


            var result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify);

            agent.Navigator.SimulateSetMiscountPersistent = false;
            agent.Navigator.SimulateSetMiscount = 0;

            Assert.False(result, "unreachable from either direction must fail cleanly");
            Assert.NotEmpty(result.ErrorMessage);

            // It was ATTEMPTED — the prompt fired — but nothing was recovered.
            Assert.Single(notify.SetAnomalies);
            Assert.Empty(notify.SetAnomaliesRecovered);
            Assert.Equal(0, agent.Statistics.Sets.AnomaliesRecoveredFromBom);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The write path gets the same cure — and the cartridge comes back. A delete whose backward count
    ///  cannot complete now reaches its target from begin-of-content, verifies there, and deletes the
    ///  right sets.
    /// </summary>
    /// <remarks>
    /// The recovery only changes WHERE the head is; SH-13 is untouched, so the verification at the
    ///  renavigated position still had to return <c>Match</c> before a single byte was destroyed.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DeleteOnUnreachableTail_RecoversAndDeletes(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();   // permissive — the write path REQUIRES authorization

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "du");

            fixture.TOC.CurrentSetIndex = 3;        // delete 3..4, keep 1..2
            using (var agent = new TapeFileAgent(fixture.Drive, fixture.TOC))
            {
                agent.Navigator.SimulateSetMiscount = -10;

                var result = agent.DeleteSetsFromCurrentSetUp(fileNotify: notify);
                Assert.True(result, $"the unreachable tail must be recoverable: {result.ErrorMessage}");

                Assert.Equal(1, agent.Statistics.Sets.AnomaliesRecoveredFromBom);
                Assert.False(agent.Statistics.Sets.SetWriteBlocked);
            }

            fixture.LoadTOC();
            Assert.Equal(2, fixture.TOC.Count);

            // The RIGHT sets survive, byte-for-byte.
            AssertSetStillRestores(fixture, 1, trees[0]);
            AssertSetStillRestores(fixture, 2, trees[1]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The authorization contract, on the path where it bites. With NO notifiable the write path declines
    ///  the renavigation — nobody can authorize a destructive repositioning — while the read path proceeds.
    /// </summary>
    /// <remarks>
    /// This is the asymmetry SH-18 exists for, and the reason the legacy overwrite tests behave as they
    ///  always did: they pass <c>fileNotify: null</c>, so the retry never runs and the refusal stands.
    ///  Hand them a permissive notifiable and the very same tape recovers — which is the feature, not a
    ///  regression.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FailedNavigation_WithoutNotifiable_SplitsByPath(DriveProfile profile)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "au");

            // WRITE path, no notifiable ⇒ the retry is declined and the delete refuses.
            fixture.TOC.CurrentSetIndex = 3;
            using (var agent = new TapeFileAgent(fixture.Drive, fixture.TOC))
            {
                agent.Navigator.SimulateSetMiscount = +10;

                Assert.False(agent.DeleteSetsFromCurrentSetUp(),
                    "nobody can authorize repositioning a destructive write");
                Assert.True(agent.IsAbortRequested);
                Assert.Equal(0, agent.Statistics.Sets.AnomaliesRecoveredFromBom);
            }

            // Nothing was touched.
            for (int i = 0; i < trees.Length; i++)
                AssertSetStillRestores(fixture, i + 1, trees[i]);

            // READ path, no notifiable ⇒ proceeds and recovers: repositioning a restore costs only time.
            fixture.TOC.CurrentSetIndex = 3;
            using (var agent = fixture.CreateRestoreAgent(restoreDir))
            {
                // A POSITIONAL navigation failure, layout-independent. SimulateSetMiscount cannot serve
                //  here: on the setmark layouts the offset is consumed by the -1 settle inside
                //  MoveToEndOfContentInternal, whose "no setmarks ⇒ assume blank" fallback SWALLOWS the
                //  failure and returns a successful-but-wrong landing — which is a Step 5 drift, not a
                //  Step 5A failed navigation.
                agent.Navigator.SimulateNavigationFailures.EnableOnce();
                agent.Navigator.SimulateNavigationFailureError = WIN32_ERROR.ERROR_NO_DATA_DETECTED;

                Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
                Assert.Equal(1, agent.Statistics.Sets.AnomaliesRecoveredFromBom);
            }

            AssertRestoredMatches(trees[2], restoreDir);
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }
#endif // DEBUG

    #endregion

}
