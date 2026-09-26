using System.IO;
using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;
using Windows.Win32.Foundation;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Step 6 — the set-level channel reaches the user.
/// <para>
/// Two shipped defects are pinned here, and both are invisible to counter-based assertions.
/// <list type="number">
/// <item><c>ServiceOperationProgressHandler</c> did not implement the Step 4 members, so it inherited
///  the interface's <c>Abort</c> default (SH-18) and aborted every operation that met a repairable
///  drift — reporting it as "aborted per user request" for a decision no user made.</item>
/// <item><c>DeleteBackupSetsAsync</c> passed no notifiable at all, so the agent's no-notifiable policy
///  declined every destructive recovery. The originating scenario of the whole feature — repair a
///  damaged tail by deleting it — could not succeed from the UI.</item>
/// </list>
/// </para>
/// <para>
/// <b>Prompt discipline.</b> <c>ServiceTestBase</c>'s teardown asserts that no prompt went unexamined, so
///  every test here either provokes NO prompt or asserts on it and then clears it. Seeding never counts:
///  it runs with <c>ProceedOnMediaMismatch</c> so it raises none in the first place (§Helpers).
/// </para>
/// </summary>
public class ServiceSetAnomalyTests : ServiceTestBase
{
    #region *** Helpers ***

    /// <summary>
    /// Exposes the <c>protected static</c> advice policy, mirroring <c>VerdictProbe</c> in
    ///  <c>ServiceReportTests</c>. No tape, no fixture, no I/O.
    /// </summary>
    private sealed class AdviceProbe(ITapeServiceHost host)
        : TapeServiceBase(TestLoggerFactory.Default, host)
    {
        public static SetAnomalyAdvice Advise(in TapeSetStatistics sets)
            => AdviseOnSetAnomalies(sets);

        public static (ServiceReportLevel Level, string Headline, string Action) Text(
            SetAnomalyAdvice advice) => AdviseText(advice);
    }

    /// <summary>
    /// Backs up <paramref name="setCount"/> distinct sets onto freshly formatted media.
    /// </summary>
    /// <remarks>
    /// <c>ProceedOnMediaMismatch</c> throughout, and not for convenience: the FIRST backup overwrites, so
    ///  it calls <c>ResetMediaId()</c> and stamps a fresh header, while the service's cached
    ///  <c>_loadedHeader</c> still describes the pre-format cartridge. Every append that follows would
    ///  then evaluate against that stale cache and prompt about a mismatch that exists nowhere on tape.
    ///  Suppressing it here keeps the seeding silent and leaves the prompt ledger clean for the test
    ///  proper — seeding artifacts must never be mistaken for the behaviour under examination.
    /// </remarks>
    private async Task<TempFileTree[]> SeedSetsAsync(TempVirtualMedia media, int setCount)
    {
        var trees = new TempFileTree[setCount];

        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            for (int i = 0; i < setCount; i++)
            {
                trees[i] = new TempFileTree();
                trees[i].AddFiles($"s{i + 1}", count: 3, minSize: 512, maxSize: 4_096);

                var req = MakeBackupRequest(svc, trees[i].RootPath, $"Set-{i + 1}", append: i > 0)
                    with { ProceedOnMediaMismatch = true };

                var result = await svc.ExecuteBackupAsync(req);
                Assert.True(result.Success, $"seeding set {i + 1} failed: {result.Message}");
            }

            // Seeding must leave nothing for the test to explain.
            Assert.Empty(host.SetAnomalyPrompts);
            host.MediaMismatchPrompts.Clear();   // suppressed ones are logged, not prompted; belt-and-braces
        }
        return trees;
    }

    private static void DisposeAll(TempFileTree[] trees)
    {
        foreach (var t in trees)
            t.Dispose();
    }

    private static RestoreRequest MakeValidateRequest(TestTapeService _ /*svc*/, int setIndex) => new(
        Mode:                  RestoreMode.Validate,
        CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
        Incremental:           false,
        TargetDirectory:       null,
        RecurseSubdirectories: true,
        HandleExisting:        TapeHowToHandleExisting.Skip,
        SkipAllErrors:         false,
        EjectWhenDone:         false)
    {
        ProceedOnMediaMismatch = true,   // the volume check is not what these tests examine
    };

    /// <summary>
    /// Asserts a single set-anomaly prompt was raised, returns it, and CLEARS the ledger so the teardown
    ///  sweep stays meaningful.
    /// </summary>
    private static TestTapeServiceHost.SetAnomalyPrompt TakeSingleSetPrompt(TestTapeServiceHost host)
    {
        var prompt = Assert.Single(host.SetAnomalyPrompts);
        host.SetAnomalyPrompts.Clear();
        return prompt;
    }

    #endregion

    #region *** (A) The policy, pure ***

    public static TheoryData<int, int, int, bool, SetAnomalyAdvice> AdviceCases => new()
    {
        // detected, recovered, recoveredFromBom, writeBlocked, expected
        { 0, 0, 0, false, SetAnomalyAdvice.None               },
        { 1, 1, 0, false, SetAnomalyAdvice.CheckDriveOrMedia  },
        { 1, 1, 1, false, SetAnomalyAdvice.RepairTrailingSets },
        { 1, 0, 0, false, SetAnomalyAdvice.CheckDriveOrMedia  }, // detected but unrepaired still advises
        { 1, 0, 0, true,  SetAnomalyAdvice.WriteRefused       },
        { 3, 3, 1, true,  SetAnomalyAdvice.WriteRefused       }, // a refusal outranks every recovery
        { 5, 5, 1, false, SetAnomalyAdvice.RepairTrailingSets }, // one tail recovery outranks four deltas
    };

    /// <summary>
    /// The ordering is the substance: a refusal outranks everything because the user is blocked right
    ///  now, and a single BOM recovery outranks any number of deltas because it indicts the TAIL rather
    ///  than a mark.
    /// </summary>
    [Theory]
    [MemberData(nameof(AdviceCases))]
    public void AdviseOnSetAnomalies_Classifies(
        int detected, int recovered, int fromBom, bool blocked, SetAnomalyAdvice expected)
    {
        var sets = new TapeSetStatistics
        {
            AnomaliesDetected         = detected,
            AnomaliesRecovered        = recovered,
            AnomaliesRecoveredFromBom = fromBom,
            SetWriteBlocked           = blocked,
        };

        Assert.Equal(expected, AdviceProbe.Advise(sets));
    }

    /// <summary>
    /// Every actionable advice must name something the user can actually DO — an advice that only
    ///  describes the problem is a log line, not advice.
    /// </summary>
    [Theory]
    [InlineData(SetAnomalyAdvice.RepairTrailingSets)]
    [InlineData(SetAnomalyAdvice.CheckDriveOrMedia)]
    [InlineData(SetAnomalyAdvice.WriteRefused)]
    public void AdviseText_CarriesAnAction(SetAnomalyAdvice advice)
    {
        var (level, headline, action) = AdviceProbe.Text(advice);

        Assert.False(string.IsNullOrWhiteSpace(headline));
        Assert.False(string.IsNullOrWhiteSpace(action));
        Assert.True(level is ServiceReportLevel.Warning or ServiceReportLevel.Failed,
            "set advice is never merely informational");
    }

    /// <summary>The silent case must be genuinely silent — no headline to accidentally report.</summary>
    [Fact]
    public void AdviseText_None_SaysNothing()
    {
        var (_, headline, action) = AdviceProbe.Text(SetAnomalyAdvice.None);
        Assert.Empty(headline);
        Assert.Empty(action);
    }

    #endregion

    #region *** (B) The read path — the handler no longer aborts ***

#if DEBUG
    /// <summary>
    /// The Step 6 fix, end to end. Before it, the service handler inherited the interface's <c>Abort</c>
    ///  default, so a drift Step 5 can repair instead terminated the operation — and reported it as a
    ///  user abort.
    /// </summary>
    /// <remarks>
    /// Asserts four separable things: the operation SUCCEEDS, it is not mislabelled an abort, the
    ///  recovery is COUNTED, and the user is ADVISED. A fix that merely stopped aborting would pass the
    ///  first two and fail the rest.
    /// </remarks>
    [Fact]
    public async Task Restore_WithDrift_RecoversAndReportsAdvice()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        TempFileTree[] trees = [];

        try
        {
            trees = await SeedSetsAsync(media, setCount: 4);

            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                int setIndex = svc.TOC!.FirstSetOnVolume + 1;   // a middle set, reached by counting

                host.SetAnomalyAnswer = true;                   // authorize the repair
                svc.OnAgentReady = a => a.Navigator.SimulateSetMiscount = +1;

                var result = await svc.ExecuteRestoreAsync(MakeValidateRequest(svc, setIndex));

                Assert.False(result.WasAborted,
                    $"a repairable drift must not abort the operation. {host.DumpReports()}");
                Assert.True(result.Success, $"the drift must be repaired. {host.DumpReports()}");

                // The recovery is counted, and rides into the service result.
                Assert.True(result.Sets.AnomaliesRecovered >= 1,
                    "the recovery must reach ServiceOperationResult.Sets");
                Assert.True(result.HasSetAnomalies);

                // The user was asked exactly once, and on the READ path the stakes are stated as such.
                var prompt = TakeSingleSetPrompt(host);
                Assert.False(prompt.IsDestructive, "a validate is not a destructive operation");

                // And the user is told, with something to act on.
                Assert.True(host.ContainsMessage("recovered"),
                    $"the host must be told the set was recovered. {host.DumpReports()}");
                Assert.True(host.ContainsMessage("miscounted") || host.ContainsMessage("cartridge"),
                    $"the host must be advised what to do. {host.DumpReports()}");
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    /// <summary>
    /// Provable silence. An advice channel that fires on a clean cartridge would train users to ignore
    ///  it — which is worse than not having it at all.
    /// </summary>
    [Fact]
    public async Task Restore_CleanRun_ReportsNoSetAdvice()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        TempFileTree[] trees = [];

        try
        {
            trees = await SeedSetsAsync(media, setCount: 2);

            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                int setIndex = svc.TOC!.LastSetOnVolume;
                var result = await svc.ExecuteRestoreAsync(MakeValidateRequest(svc, setIndex));

                Assert.True(result.Success);
                Assert.False(result.HasSetAnomalies);
                Assert.Equal(0, result.Sets.AnomaliesDetected);
                Assert.Equal(0, result.Sets.AnomaliesRecovered);
                Assert.False(result.Sets.SetWriteBlocked);

                // Nothing from the set channel at all — no log line, no prompt.
                Assert.False(host.ContainsMessage("anomaly"),
                    $"a clean run must not mention set anomalies. {host.DumpReports()}");
                Assert.Empty(host.SetAnomalyPrompts);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (C) The delete path — the verb this feature exists for ***

#if DEBUG
    /// <summary>
    /// The second Step 6 fix: the delete can now authorize its own recovery, so a cartridge whose tail
    ///  no longer matches its TOC can be repaired from the UI.
    /// </summary>
    /// <remarks>
    /// Before Step 6 this could not succeed by construction: with no notifiable, the agent's
    ///  no-notifiable policy declines every destructive recovery (SH-18), and there was no way for a
    ///  user to say otherwise.
    /// </remarks>
    [Fact]
    public async Task Delete_WithDamagedTail_PromptsAndRepairs()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        TempFileTree[] trees = [];

        try
        {
            trees = await SeedSetsAsync(media, setCount: 5);

            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                host.SetAnomalyAnswer = true;   // authorize the recovery

                int deleteFrom = svc.TOC!.LastSetOnVolume - 1;   // drop the last two
                svc.OnAgentReady = a => a.Navigator.SimulateSetMiscount = +1;

                var result = await svc.DeleteBackupSetsExAsync(deleteFrom);

                Assert.True(result.Success, $"the damaged tail must be repairable. {host.DumpReports()}");
                Assert.Equal(2, result.SetsDeleted);
                Assert.False(result.TapeUnchanged);
                Assert.False(result.Sets.SetWriteBlocked, "a recovered delete is not a blocked one");
                Assert.True(result.Sets.AnomaliesRecovered >= 1);

                // The user was asked — and the prompt named a DESTRUCTIVE operation, with both sets.
                var prompt = TakeSingleSetPrompt(host);
                Assert.True(prompt.IsDestructive, "a delete must present its recovery as destructive");
                Assert.False(string.IsNullOrWhiteSpace(prompt.ExpectedSet));
                Assert.False(string.IsNullOrWhiteSpace(prompt.ActualSet));
                Assert.NotEqual(prompt.ExpectedSet, prompt.ActualSet);
            }

            // The RIGHT sets survive — the only assertion that distinguishes a repair from a mess.
            var (checkSvc, checkHost) = await ReopenAsync(media);
            using (checkSvc)
            {
                Assert.Equal(3, checkSvc.TOC!.Count);
                Assert.Empty(checkHost.SetAnomalyPrompts);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Declining the recovery must leave the cartridge untouched and say so — the user's most likely
    ///  next question after "no" is "did it do anything anyway?".
    /// </summary>
    /// <remarks>
    /// A DECLINE is a genuine user abort: something could have been authorized and was not. That is the
    ///  one path where <c>WasAborted</c> is the honest verdict — as opposed to a block, where nobody was
    ///  ever asked (see <see cref="Delete_Blocked_DoesNotReportNoFilesProcessed"/>).
    /// </remarks>
    [Fact]
    public async Task Delete_Declined_ReportsRefusedAndTapeUnchanged()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        TempFileTree[] trees = [];

        try
        {
            trees = await SeedSetsAsync(media, setCount: 5);

            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                host.SetAnomalyAnswer = false;   // decline

                int deleteFrom = svc.TOC!.LastSetOnVolume - 1;
                svc.OnAgentReady = a => a.Navigator.SimulateSetMiscount = +1;

                var result = await svc.DeleteBackupSetsExAsync(deleteFrom);

                Assert.False(result.Success);
                Assert.Equal(0, result.SetsDeleted);
                Assert.True(result.TapeUnchanged);
                Assert.Equal(0, result.Sets.AnomaliesRecovered);

                var prompt = TakeSingleSetPrompt(host);
                Assert.True(prompt.IsDestructive);

                Assert.True(host.ContainsMessage("unchanged") || host.ContainsMessage("declined"),
                    $"the user must be told the tape was not touched. {host.DumpReports()}");
            }

            // Nothing was deleted.
            var (checkSvc, checkHost) = await ReopenAsync(media);
            using (checkSvc)
            {
                Assert.Equal(5, checkSvc.TOC!.Count);
                Assert.Empty(checkHost.SetAnomalyPrompts);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A refused delete must explain itself, never report as a bare nothing-happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CorrectsSetNavigation = false</c> makes the drift UNRECOVERABLE by policy — no delta, no
    ///  renavigation — so the ladder blocks without ever asking. Deliberately NOT a persistent miscount:
    ///  that injector keeps firing on every navigation, including the TOC write that follows, which
    ///  collapses the operation into an emergency-TOC-export failure and never reaches the state under
    ///  test.
    /// </para>
    /// <para>
    /// Nobody was asked, so nobody declined: <c>WasAborted</c> must stay false and the refusal must carry
    ///  its own diagnosis. This is the assertion that keeps the abort verdict from swallowing the block.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Delete_Blocked_DoesNotReportNoFilesProcessed()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        TempFileTree[] trees = [];

        try
        {
            trees = await SeedSetsAsync(media, setCount: 5);

            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                int deleteFrom = svc.TOC!.LastSetOnVolume - 2;
                svc.OnAgentReady = a =>
                {
                    a.Navigator.SimulateSetMiscount = +1;
                    a.CorrectsSetNavigation = false;   // no stage remains — block, do not ask
                };

                var result = await svc.DeleteBackupSetsExAsync(deleteFrom);

                Assert.False(result.Success);
                Assert.True(result.Sets.SetWriteBlocked);
                Assert.True(result.TapeUnchanged);
                Assert.Equal(0, result.Sets.AnomaliesRecovered);

                // A REASON, not a count — and never the old phrasing.
                Assert.False(string.IsNullOrWhiteSpace(result.Message),
                    "a blocked delete must carry the agent's diagnosis");
                Assert.NotEqual((uint)WIN32_ERROR.ERROR_CANCELLED, result.ErrorCode);
                Assert.False(host.ContainsMessage("no files processed"),
                    $"a refused delete must not report as a file operation. {host.DumpReports()}");
                Assert.True(host.ContainsMessage("refused"),
                    $"the refusal must name itself. {host.DumpReports()}");

                // Nothing was asked: CanAttemptRecovery was false throughout.
                Assert.Empty(host.SetAnomalyPrompts);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion

    #region *** (D) The write path — the verdict that outranks NothingProcessed ***

#if DEBUG
    /// <summary>
    /// A refused overwrite processes no files, so every counter-based verdict would describe it as
    ///  "nothing happened". <c>SetVerificationBlocked</c> exists to name the cause instead.
    /// </summary>
    /// <remarks>
    /// Reaches the block the same way the delete test does — by policy rather than by a persistent
    ///  injector — for the same reason: a persistent miscount would take the TOC write down with it and
    ///  the operation would fail for an unrelated cause. See
    ///  <see cref="Delete_Blocked_DoesNotReportNoFilesProcessed"/>.
    /// </remarks>
    [Fact]
    public async Task Backup_OverwriteBlocked_ReportsRefusal()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        TempFileTree[] trees = [];
        using var replacement = new TempFileTree();
        replacement.AddFiles("rep", count: 2, minSize: 512, maxSize: 4_096);

        try
        {
            trees = await SeedSetsAsync(media, setCount: 5);

            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                svc.OnAgentReady = a => a.Navigator.SimulateSetMiscount = +1;

                // Overwrite a MIDDLE set: append-after-set reuses the slot with newSet: false, which is
                //  the one backup shape that verifies (§3 of the design).
                var req = MakeBackupRequest(svc, replacement.RootPath, "Blocked-Overwrite", append: true)
                    with
                    {
                        AppendAfterSetIndex    = svc.TOC!.FirstSetOnVolume + 1,
                        CorrectSetNavigation   = false,   // unrecoverable by policy — block, do not ask
                        ProceedOnMediaMismatch = true,
                    };

                var result = await svc.ExecuteBackupAsync(req);

                Assert.False(result.IsFullSuccess);
                Assert.Equal(0, result.FilesSucceeded);
                Assert.True(result.Sets.SetWriteBlocked,
                    $"the overwrite must be REFUSED, not merely unsuccessful. {host.DumpReports()}");

                // Refused, not aborted: nobody was asked, so nobody declined.
                Assert.False(result.WasAborted);

                // The headline must name the refusal — not "no files processed".
                Assert.True(host.ContainsMessage("refused") || host.ContainsMessage("not the one expected"),
                    $"the refusal must reach the user. {host.DumpReports()}");
                Assert.Empty(host.SetAnomalyPrompts);
            }

            // The tape is untouched: all five sets still there.
            var (checkSvc, checkHost) = await ReopenAsync(media);
            using (checkSvc)
            {
                Assert.Equal(5, checkSvc.TOC!.Count);
                Assert.Empty(checkHost.SetAnomalyPrompts);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The other half of the write path: a drift the agent CAN repair is repaired, the overwrite
    ///  completes, and the user is asked once — with the stakes named as destructive.
    /// </summary>
    /// <remarks>
    /// Guards the inverse regression of the test above: a verification that blocked every drift would
    ///  make overwriting impossible on any cartridge whose drive miscounts a single mark.
    /// </remarks>
    [Fact]
    public async Task Backup_OverwriteWithRecoverableDrift_Succeeds()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        TempFileTree[] trees = [];
        using var replacement = new TempFileTree();
        replacement.AddFiles("rec", count: 2, minSize: 512, maxSize: 4_096);

        try
        {
            trees = await SeedSetsAsync(media, setCount: 5);

            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                host.SetAnomalyAnswer = true;   // authorize the repair
                svc.OnAgentReady = a => a.Navigator.SimulateSetMiscount = +1;

                var req = MakeBackupRequest(svc, replacement.RootPath, "Recovered-Overwrite", append: true)
                    with
                    {
                        AppendAfterSetIndex    = svc.TOC!.FirstSetOnVolume + 1,
                        ProceedOnMediaMismatch = true,
                    };

                var result = await svc.ExecuteBackupAsync(req);

                Assert.True(result.Success, $"a repairable drift must not block. {host.DumpReports()}");
                Assert.False(result.Sets.SetWriteBlocked);
                Assert.True(result.Sets.AnomaliesRecovered >= 1);
                Assert.True(result.FilesSucceeded > 0, "the replacement files were written");

                var prompt = TakeSingleSetPrompt(host);
                Assert.True(prompt.IsDestructive, "an overwrite must present its recovery as destructive");
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion

    #region *** (E) The join ***

#if DEBUG
    /// <summary>
    /// The agent's counters must arrive intact in <see cref="ServiceOperationResult.Sets"/> — the
    ///  boundary where Step 4's statistics become something a UI can render.
    /// </summary>
    /// <remarks>
    /// Guards against the most likely regression: a <c>with</c> expression that rebuilds the result and
    ///  silently drops the struct, leaving every counter at zero on a run that plainly had anomalies.
    /// </remarks>
    [Fact]
    public async Task SetStatistics_ReachTheServiceResult()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        TempFileTree[] trees = [];

        try
        {
            trees = await SeedSetsAsync(media, setCount: 4);

            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                int setIndex = svc.TOC!.FirstSetOnVolume + 1;

                host.SetAnomalyAnswer = true;
                svc.OnAgentReady = a => a.Navigator.SimulateSetMiscount = +1;

                var result = await svc.ExecuteRestoreAsync(MakeValidateRequest(svc, setIndex));

                // The counters survive the hand-off, one for one.
                Assert.True(result.Sets.AnomaliesDetected >= 1);
                Assert.True(result.Sets.AnomaliesRecovered >= 1);
                Assert.Equal(result.Sets.HasAnomalies, result.HasSetAnomalies);

                // The set counters are independent of the file ones — a clean file loop with a repaired
                //  set must show both.
                Assert.True(result.FilesSucceeded > 0, "the files themselves restored fine");
                Assert.Equal(0, result.FilesFailed);

                TakeSingleSetPrompt(host);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Statistics are PER OPERATION at the service boundary too: a second operation on the same service
    ///  must not inherit the first one's anomalies.
    /// </summary>
    [Fact]
    public async Task SetStatistics_DoNotLeakBetweenOperations()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        TempFileTree[] trees = [];

        try
        {
            trees = await SeedSetsAsync(media, setCount: 4);

            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                host.SetAnomalyAnswer = true;

                // First: drifts and recovers.
                int driftSet = svc.TOC!.FirstSetOnVolume + 1;
                svc.OnAgentReady = a => a.Navigator.SimulateSetMiscount = +1;

                var first = await svc.ExecuteRestoreAsync(MakeValidateRequest(svc, driftSet));
                Assert.True(first.Success);
                Assert.True(first.Sets.AnomaliesRecovered >= 1);
                TakeSingleSetPrompt(host);

                // Second: clean, and must report clean.
                svc.OnAgentReady = null;
                int cleanSet = svc.TOC!.LastSetOnVolume;

                var second = await svc.ExecuteRestoreAsync(MakeValidateRequest(svc, cleanSet));
                Assert.True(second.Success);
                Assert.False(second.HasSetAnomalies);
                Assert.Equal(0, second.Sets.AnomaliesDetected);
                Assert.Equal(0, second.Sets.AnomaliesRecovered);
                Assert.Empty(host.SetAnomalyPrompts);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion
}
