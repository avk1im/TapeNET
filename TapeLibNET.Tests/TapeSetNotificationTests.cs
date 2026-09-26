using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;
using Windows.Win32.Foundation;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 4 — the set-level notification channel. A set that does not verify reaches the host with enough
///  detail to prompt, the host's answer is honoured, and the operation's set statistics are accurate.
/// <para>
/// Two things under test, deliberately separated: the CHANNEL (payload, answer, abort convergence,
///  silence) and the STATISTICS (<c>Statistics.Sets</c>). The statistics are what Step 6 will surface, so
///  a wrong count here becomes a wrong summary there — and the summary is the only place the person
///  holding the cartridge learns that it is drifting.
/// </para>
/// </summary>
public class TapeSetNotificationTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
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
        Path.Combine(Path.GetTempPath(), $"TapeNET_SNT_{Guid.NewGuid():N}");

    /// <summary>
    /// A tape of <paramref name="setCount"/> sets with DISTINCT descriptions, so the payload's
    ///  expected/actual names are genuinely distinguishable rather than coincidentally equal.
    /// </summary>
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

    /// <summary>
    /// A minimal implementer that overrides NOTHING new — the DIM contract's only honest witness.
    ///  <see cref="TestNotifiable"/> cannot serve here: it implements both new members.
    /// </summary>
    private sealed class BareNotifiable : ITapeFileNotifiable
    {
        public void SetStart(int setIndex, in TapeFileStatistics stats) { }
        public void SetEnd(int setIndex, in TapeFileStatistics stats) { }
        public bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats) => true;
        public bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats) => true;
        public FileFailedAction OnFileFailed(TapeFileInfo fileInfo, TapeResult result, in TapeFileStatistics stats)
            => FileFailedAction.Skip;
        public void OnFileSkipped(TapeFileInfo fileInfo, in TapeFileStatistics stats) { }
        // OnSetAnomaly / OnSetAnomalyRecovered deliberately NOT overridden.
    }

    #endregion

    #region *** (A) The payload ***

#if DEBUG
    /// <summary>
    /// A prompt that cannot name the sets is not a prompt. The payload must carry BOTH descriptions —
    ///  what the TOC believes stands here, and what the tape actually says — plus the indices that make
    ///  the drift intelligible.
    /// </summary>
    /// <remarks>
    /// The sets carry distinct descriptions, so an implementation that filled both fields from the same
    ///  source would be caught. FIVE sets, not four: on the setmark and partition layouts the miscount
    ///  injector is consumed by the <c>-1</c> settle inside <c>MoveToEndOfContentCore</c>, so the tape
    ///  must be long enough that the cancelled settle still lands inside content.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BlockedSet_RaisesOnSetAnomaly_WithBothDescriptions(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "pl");

            fixture.TOC.CurrentSetIndex = 3;
            using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
            {
                agent.Navigator.SimulateSetMiscount = +1;
                agent.Navigator.SimulateSetMiscountPersistent = true; // to prevent agent autorecovery

                Assert.False(agent.DeleteSetsFromCurrentSetUp(fileNotify: notify));
            }

            var a = Assert.Single(notify.SetAnomalies).Anomaly;

            Assert.Equal(TapeSetHeaderVerdict.SetIndexDrift, a.Verdict);
            Assert.Equal(TapeSetAnomalyStage.Detected, a.Stage);
            Assert.Equal(3, a.SetIndex);

            // The two sides must DIFFER — that is the whole content of the message.
            Assert.NotEqual(a.ExpectedVolumeSetIndex, a.ActualVolumeSetIndex);
            Assert.NotEqual(a.ExpectedDescription, a.ActualDescription);
            Assert.Contains("Set 3", a.ExpectedDescription);
            Assert.Contains("Set 4", a.ActualDescription);   // the drift of +1 landed one set on

            // Destructive, and unrecoverable on this path until Step 5.
            Assert.True(a.IsDestructive); // destructive
            Assert.True(a.CanAttemptRecovery); // Step 5: now can attempt recovery!
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// An unreadable header still names the set the TOC expected, and says plainly that the tape's side
    ///  is unavailable. A payload that omitted the expected side would leave the host nothing to show.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void UnreadableSet_RaisesOnSetAnomaly_NamingTheExpectedSet(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "ur");

            fixture.TOC.CurrentSetIndex = 3;
            using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
            {
                agent.ReadBomHeader();   // resolve BOM first, so the injector targets the SET read
                fixture.Backend.ContentReadFaults.CorruptAlways(bits: 2, offset: 48); // keep corrupting to prevent agent autorecovery
                Assert.False(agent.DeleteSetsFromCurrentSetUp(fileNotify: notify));
            }

            var a = Assert.Single(notify.SetAnomalies).Anomaly;
            Assert.Equal(TapeSetHeaderVerdict.Unreadable, a.Verdict);
            Assert.Equal(3, a.SetIndex);
            Assert.Contains("Set 3", a.ExpectedDescription);
            Assert.False(string.IsNullOrWhiteSpace(a.ActualDescription));

            // Raised AFTER SetError, so the diagnosis is real rather than a placeholder.
            Assert.False(a.Diagnosis.Success);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, a.Diagnosis.ErrorCode);
            Assert.NotEmpty(a.Diagnosis.ErrorMessage);
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion

    #region *** (B) The answer, and the abort channels ***

#if DEBUG
    /// <summary>
    /// A blocked set reports the ANOMALY, not the cancellation. The user pressed abort and knows it;
    ///  what they need from the result is what provoked the prompt.
    /// </summary>
    /// <remarks>
    /// The same rule the file path follows: <c>NotifyFileFailed</c> → <c>Abort</c> still latches the
    ///  original fault, and <c>ERROR_CANCELLED</c> is reserved for an abort with NO underlying fault
    ///  (<c>ThrowIfAbortRequested</c> firing on a clean file). <see cref="TapeAgentBase.IsAbortRequested"/>
    ///  is the channel that says "aborted"; the error code says "why".
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnSetAnomaly_ReturningAbort_ReportsTheAnomalyNotTheCancellation(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable { SetAnomalyAction = SetAnomalyAction.Abort };

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "ab");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = new TapeSetAgent(fixture.Drive, fixture.TOC);
            agent.Navigator.SimulateSetMiscount = +1;

            var result = agent.DeleteSetsFromCurrentSetUp(fileNotify: notify);

            Assert.False(result);
            Assert.True(agent.IsAbortRequested);                 // the abort IS recorded…
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, result.ErrorCode);   // …and the reason survives
            Assert.Contains("drift", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Single(notify.SetAnomalies);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The exception channel must be indistinguishable from the enum: a callback that THROWS and one
    ///  that returns <see cref="SetAnomalyAction.Abort"/> produce the same code, the same message and
    ///  the same flag. Run BOTH in one test, so the assertion is convergence rather than two guesses.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnSetAnomaly_Throwing_ConvergesWithTheEnum(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "th");

            TapeResult RunWith(TestNotifiable notify)
            {
                fixture.TOC.CurrentSetIndex = 3;
                using var agent = new TapeSetAgent(fixture.Drive, fixture.TOC);
                agent.Navigator.SimulateSetMiscount = +1;

                // The exception must NOT escape: the public API is contractually TapeResult-based.
                var r = agent.DeleteSetsFromCurrentSetUp(fileNotify: notify);
                Assert.False(r);
                Assert.True(agent.IsAbortRequested);
                return r;
            }

            var byEnum = RunWith(new TestNotifiable { SetAnomalyAction = SetAnomalyAction.Abort });
            var byThrow = RunWith(new TestNotifiable { ThrowOnSetAnomaly = true });

            Assert.Equal(byEnum.ErrorCode, byThrow.ErrorCode);
            Assert.Equal(byEnum.ErrorMessage, byThrow.ErrorMessage);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, byThrow.ErrorCode);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// SH-18's default-interface-implementation contract: a notifiable that has not been taught about
    ///  destructive recovery never authorizes it — and the refusal still reports the anomaly.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void NotifiableWithoutOverrides_DefaultsToAbort(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "dm");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = new TapeSetAgent(fixture.Drive, fixture.TOC);
            agent.Navigator.SimulateSetMiscount = +1;

            var result = agent.DeleteSetsFromCurrentSetUp(fileNotify: new BareNotifiable());

            Assert.False(result);
            Assert.True(agent.IsAbortRequested);   // the DIM default declined…
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, result.ErrorCode);   // …reporting why
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// No notifiable at all is NOT an abort — it defers to the path's own policy. The write path still
    ///  refuses, but with its own diagnosis rather than <c>ERROR_CANCELLED</c>: nobody asked to cancel.
    /// </summary>
    /// <remarks>
    /// This is the branch that keeps every legacy caller working. A blanket Abort default here would
    ///  have turned every <c>fileNotify: null</c> call into a user-cancelled operation.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void NoNotifiable_BlocksWithoutClaimingCancellation(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "nn");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = new TapeSetAgent(fixture.Drive, fixture.TOC);
            agent.Navigator.SimulateSetMiscount = +1;

            var result = agent.DeleteSetsFromCurrentSetUp();   // no notifiable

            Assert.False(result);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, result.ErrorCode);
            Assert.True(agent.Statistics.Sets.SetWriteBlocked);
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion

    #region *** (C) Silence on the happy path ***

    /// <summary>
    /// Provable silence. A channel that fired spuriously would train users to dismiss it, which is worse
    ///  than not having it — so the clean paths must raise NOTHING, and must count every set as a success.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CleanOperation_RaisesNothing_AndCountsSetsSucceeded(DriveProfile profile)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "cl");

            // A clean restore of one set…
            fixture.TOC.CurrentSetIndex = 2;
            using (var restore = fixture.CreateRestoreAgent(restoreDir))
            {
                Assert.True(restore.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify));

                var sets = restore.Statistics.Sets;
                Assert.False(sets.HasAnomalies);
                Assert.Equal(0, sets.AnomaliesRecovered);
                Assert.False(sets.SetWriteBlocked);
                Assert.Equal(sets.SetsProcessed, sets.SetsSucceeded);   // every set entered was clean
                Assert.True(sets.SetsProcessed >= 1);
                Assert.Empty(restore.SetAnomalies);
            }

            notify.AssertNoSetAnomalies();

            // …and a clean delete.
            notify.Clear();
            fixture.TOC.CurrentSetIndex = 3;
            using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
            {
                Assert.True(agent.DeleteSetsFromCurrentSetUp(fileNotify: notify));
                Assert.False(agent.Statistics.Sets.HasAnomalies);
                Assert.Empty(agent.SetAnomalies);
            }

            Assert.Empty(notify.SetAnomalies);
            Assert.Empty(notify.SetAnomaliesRecovered);
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// The set counters track real sets, not callbacks: restoring several sets in one operation counts
    ///  each exactly once. Guards the counter against the multi-set loop double-counting.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSetRestore_CountsEachSetOnce(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "ms");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = fixture.CreateValidateAgent();

            // Restore set 3 and everything below it — three sets in one operation.
            Assert.True(agent.RestoreFilesFromCurrentSetDown([null, null, null],
                ignoreFailures: false, fileNotify: notify));

            var sets = agent.Statistics.Sets;
            Assert.Equal(3, sets.SetsProcessed);
            Assert.Equal(3, sets.SetsSucceeded);
            Assert.False(sets.HasAnomalies);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (D) Set statistics ***

    //  What Step 6 will put in front of the user. A wrong count here becomes a wrong summary there.

#if DEBUG
    /// <summary>
    /// A blocked destructive write records exactly one anomaly, sets the blocked flag, and does NOT
    ///  count the set as a success.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BlockedWrite_CountsOneAnomaly_AndSetsTheBlockedFlag(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "ac");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = new TapeSetAgent(fixture.Drive, fixture.TOC);
            agent.Navigator.SimulateSetMiscount = +1;
            agent.Navigator.SimulateSetMiscountPersistent = true; // to prevent agent autorecovery

            Assert.False(agent.DeleteSetsFromCurrentSetUp(fileNotify: notify));

            var sets = agent.Statistics.Sets;
            Assert.Equal(1, sets.AnomaliesDetected);
            Assert.Equal(0, sets.AnomaliesRecovered);      // nothing was repaired
            Assert.True(sets.SetWriteBlocked);

            // The agent's records and the host's must agree — they are the same events.
            Assert.Equal(2, agent.SetAnomalies.Count); // failure + failed retry
            Assert.True(agent.SetAnomalies.Count >= notify.SetAnomalies.Count); // the agent shouldn't call notify on retry for the same failure
            Assert.Equal(agent.SetAnomalies[0].Verdict, notify.SetAnomalies[0].Anomaly.Verdict);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A drift the READ path repairs is counted as RECOVERED, not as a failure: the restore succeeds,
    ///  <c>AnomaliesRecovered</c> counts it, and the blocked flag stays clear.
    /// </summary>
    /// <remarks>
    /// This is the asymmetry SH-16 rests on. A recovered drift still means the drive or the medium
    ///  miscounted marks — a tape that corrects on every set is a tape to retire — so it must be COUNTED
    ///  even though nothing failed. <c>AnomaliesRecoveredFromBom</c> stays zero: Step 5 introduces the
    ///  second stage, and this one was settled by the delta.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RecoveredDrift_IsCountedAsRecovered_NotAsFailure(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "rc");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = fixture.CreateValidateAgent();
            agent.Navigator.SimulateSetMiscount = +1;

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify),
                "the read path repairs the drift");

            var sets = agent.Statistics.Sets;
            Assert.Equal(1, sets.AnomaliesDetected);
            Assert.Equal(1, sets.AnomaliesRecovered);
            Assert.Equal(0, sets.AnomaliesRecoveredFromBom);   // the delta settled it (Step 5 adds the other)
            Assert.False(sets.SetWriteBlocked);

            // The set hit an anomaly, so it is NOT counted among the clean ones.
            Assert.True(sets.SetsProcessed >= 1);
            Assert.Equal(sets.SetsProcessed - 1, sets.SetsSucceeded);

            var recovered = Assert.Single(notify.SetAnomaliesRecovered).Anomaly;
            Assert.Equal(TapeSetHeaderVerdict.SetIndexDrift, recovered.Verdict);
            Assert.Equal(TapeSetAnomalyStage.Delta, recovered.Stage);   // which stage settled it (SH-16)
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The statistics are PER OPERATION. A second verb on the same agent must start from zero, or the
    ///  closing summary would attribute an earlier cartridge's drift to this one.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SetStatistics_ResetBetweenOperations(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "rs");

            using var agent = fixture.CreateValidateAgent();

            // First operation: drifts and recovers.
            fixture.TOC.CurrentSetIndex = 3;
            agent.Navigator.SimulateSetMiscount = +1;
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
            Assert.Equal(1, agent.Statistics.Sets.AnomaliesRecovered);
            Assert.Single(agent.SetAnomalies);

            // Second operation on the SAME agent: clean, and must report clean.
            fixture.TOC.CurrentSetIndex = 2;
            Assert.Equal(0, agent.Navigator.SimulateSetMiscount);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));

            var sets = agent.Statistics.Sets;
            Assert.Equal(0, sets.AnomaliesDetected);
            Assert.Equal(0, sets.AnomaliesRecovered);
            Assert.False(sets.SetWriteBlocked);
            Assert.Empty(agent.SetAnomalies);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// SH-18's once-per-set rule. The ladder may consult the anomaly several times while working through
    ///  a single set, but the USER is asked exactly once.
    /// </summary>
    /// <remarks>
    /// Exercised through the correction path, which re-enters the ladder with a re-read header: the
    ///  DETECTED count may legitimately exceed one, but <c>notify.SetAnomalies</c> must not. The guard is
    ///  keyed on the set INDEX rather than a bool precisely so Step 5's re-navigation — which re-enters
    ///  the ladder for the same set — cannot re-arm it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnSetAnomaly_IsRaisedAtMostOncePerSet(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "on");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = fixture.CreateValidateAgent();

            // Persistent: the correction's own re-verify drifts again, so the ladder runs twice.
            agent.Navigator.SimulateSetMiscount = +1;
            agent.Navigator.SimulateSetMiscountPersistent = true;

            Assert.False(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: notify),
                "a drift that survives one correction must fail the set");

            agent.Navigator.SimulateSetMiscountPersistent = false;
            agent.Navigator.SimulateSetMiscount = 0;

            Assert.Single(notify.SetAnomalies);                              // asked ONCE…
            Assert.True(agent.Statistics.Sets.AnomaliesDetected >= 1);       // …however often the ladder looked
            Assert.Empty(notify.SetAnomaliesRecovered);                      // and nothing was repaired
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The statistics reach the CALLBACK, not merely the agent — which is the point of nesting them in
    ///  <see cref="TapeFileStatistics"/>. A host deciding whether to abort needs the running count at the
    ///  moment it is asked.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SetStatistics_ReachTheCallback(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "cb");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = new TapeSetAgent(fixture.Drive, fixture.TOC);
            agent.Navigator.SimulateSetMiscount = +1;
            agent.Navigator.SimulateSetMiscountPersistent = true; // to prevent agent autorecovery

            Assert.False(agent.DeleteSetsFromCurrentSetUp(fileNotify: notify));

            // The SNAPSHOT is a by-value copy taken when OnSetAnomaly fired — at DETECTION, before
            //  either recovery stage ran. It can only carry what was known then: the anomaly exists,
            //  it is destructive, and nothing has been repaired yet.
            var incident = Assert.Single(notify.SetAnomalies);
            Assert.Equal(1, incident.Stats.Sets.AnomaliesDetected);
            Assert.Equal(0, incident.Stats.Sets.AnomaliesRecovered);
            Assert.True(incident.Anomaly.IsDestructive);        // a destructive write is GATED on this…
            Assert.True(incident.Anomaly.CanAttemptRecovery);   // …and a stage was still untried

            // SetWriteBlocked belongs to the TERMINAL, two stages later, so it is only observable on
            //  the agent's live statistics — never on a snapshot handed to the prompt.
            var sets = agent.Statistics.Sets;
            Assert.True(sets.SetWriteBlocked);
            Assert.Equal(0, sets.AnomaliesRecovered);
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion
}