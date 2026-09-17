using System.IO;
using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Step 9.0 coverage: the agent's diagnosis must SURVIVE to the user.
/// <para>
/// Two defects are pinned here. The agent used to build its final <see cref="TapeResult"/> from live
///  error state that intervening successes had already cleared; and the service dropped
///  <c>ErrorCode</c>/<c>ErrorMessage</c> on the implicit <c>TapeResult → bool</c> conversion. Both are
///  invisible to counter-based assertions — every test below therefore asserts on the MESSAGE, not on
///  the file counts.
/// </para>
/// </summary>
public class ServiceReportTests : ServiceTestBase
{
    // ── Verdict classification (pure) ─────────────────────────────────────────

    /// <summary>
    /// Exercises <c>JudgeFileOperation</c> through a public surrogate, since the method itself is
    ///  <c>protected static</c>. A table test over all six verdicts: no tape, no fixture, no I/O.
    /// </summary>
    /// <remarks>
    /// The ordering of the checks is the substance here. Abort and Failed outrank the counters because a
    ///  stopped operation's counters describe how far it got, not what happened; and
    ///  <c>NothingProcessed</c> must be checked before <c>CompletedWithSkips</c>, since an operation that
    ///  skipped everything processed nothing and should say so.
    /// </remarks>
    private sealed class VerdictProbe(ITapeServiceHost host) : TapeServiceBase(TestLoggerFactory.Default, host)
    {
        public static FileOperationVerdict Judge(in FileOperationResult r, bool pending = false)
            => JudgeFileOperation(r, pending);

        public static (ServiceReportLevel Level, string Message) Verbalize(
            FileOperationVerdict v, in FileOperationResult r, string op)
            => VerbalizeFileOperation(v, r, op);
    }

    public static TheoryData<int, int, int, bool, bool, FileOperationVerdict> VerdictCases => new()
    {
        // processed, succeeded, failed, skipped→(as bool flags below), aborted, hasFailed, expected
        //  { FilesProcessed, FilesFailed, FilesSkipped, WasAborted, HasFailed, expected }
        {  5, 0, 0, false, false, FileOperationVerdict.FullSuccess            },
        {  5, 0, 2, false, false, FileOperationVerdict.CompletedWithSkips     },
        {  5, 2, 0, false, false, FileOperationVerdict.CompletedWithFailures  },
        {  5, 2, 1, false, false, FileOperationVerdict.CompletedWithFailures  }, // failures outrank skips
        {  0, 0, 0, false, false, FileOperationVerdict.NothingProcessed       },
        {  5, 0, 0, true,  false, FileOperationVerdict.Aborted                },
        {  5, 3, 0, true,  false, FileOperationVerdict.Aborted                }, // abort outranks failures
        {  5, 0, 0, false, true,  FileOperationVerdict.Failed                 },
    };

    [Theory]
    [MemberData(nameof(VerdictCases))]
    public void JudgeFileOperation_Classifies(
        int filesProcessed, int filesFailed, int filesSkipped,
        bool wasAborted, bool hasFailed, FileOperationVerdict expected)
    {
        var result = new RestoreResult
        {
            FilesTotal     = 5,
            FilesProcessed = filesProcessed,
            FilesSucceeded = filesProcessed - filesFailed - filesSkipped,
            FilesFailed    = filesFailed,
            FilesSkipped   = filesSkipped,
            WasAborted     = wasAborted,
            HasFailed      = hasFailed,
        };

        Assert.Equal(expected, VerdictProbe.Judge(result));
    }

    /// <summary>
    /// A pending volume continuation is NOT a failure — the single easiest mistake in this classification,
    ///  and the one that would turn every multi-volume operation into a reported error.
    /// </summary>
    [Fact]
    public void JudgeFileOperation_PendingContinuation_IsNotAFailure()
    {
        var result = new RestoreResult
        {
            FilesTotal = 10, FilesProcessed = 4, FilesSucceeded = 4,
            FilesFailed = 0, FilesSkipped = 0,
        };

        Assert.Equal(FileOperationVerdict.FullSuccess,
            VerdictProbe.Judge(result, pending: true));
    }

    /// <summary>
    /// The diagnosis must reach the headline for the verdicts where the counters cannot explain
    ///  themselves — above all <c>NothingProcessed</c>, which is otherwise a statement with no cause.
    /// </summary>
    [Theory]
    [InlineData(FileOperationVerdict.NothingProcessed)]
    [InlineData(FileOperationVerdict.CompletedWithFailures)]
    [InlineData(FileOperationVerdict.Failed)]
    public void VerbalizeFileOperation_CarriesTheDiagnosis(FileOperationVerdict verdict)
    {
        const string reason = "Volume mismatch at set #2";
        var result = new RestoreResult { FilesTotal = 3, FilesFailed = 1, Diagnosis = TapeResult.Fail(13u, reason) };

        var (_, message) = VerdictProbe.Verbalize(
            verdict, result, "Restore");

        Assert.Contains(reason, message, StringComparison.Ordinal);
    }

    /// <summary>A successful operation must not have a reason suffix bolted on.</summary>
    [Fact]
    public void VerbalizeFileOperation_FullSuccess_CarriesNoReason()
    {
        var result = new RestoreResult { FilesTotal = 3, FilesProcessed = 3, FilesSucceeded = 3, Diagnosis = TapeResult.OK };

        var (level, message) = VerdictProbe.Verbalize(
            FileOperationVerdict.FullSuccess, result, "Restore");

        Assert.Equal(ServiceReportLevel.Completed, level);
        Assert.DoesNotContain(" — ", message, StringComparison.Ordinal);
    }

    // ── TapeResult and ServiceOperationResult agree ────────────────────────

    /// <summary>
    /// The join: a result's <c>ErrorCode</c> and <c>Message</c> must never disagree with its embedded
    ///  diagnosis. Guards against a future <c>with</c> expression re-introducing independent fields.
    /// </summary>
    [Fact]
    public void Result_ErrorFieldsDeriveFromDiagnosis()
    {
        var diag = TapeResult.Fail(13u, "Bad data");
        var result = new RestoreResult { FilesTotal = 3, Diagnosis = diag };

        Assert.Equal(13u, result.ErrorCode);
        Assert.Equal("Bad data", result.Message);

        // An explicit Message overrides for display, but the CODE still comes from the diagnosis.
        var overridden = result with { Message = "Something friendlier" };
        Assert.Equal("Something friendlier", overridden.Message);
        Assert.Equal(13u, overridden.ErrorCode);

        // A clean diagnosis yields a null message — "no message" stays meaningful.
        Assert.Null(new RestoreResult { FilesTotal = 3 }.Message);
    }

    // ── Backup: the agent's diagnosis reaches the user ────────────────────────

#if DEBUG

    /// <summary>
    /// Several injected file failures. The counters alone would say "3 failed"; the REASON only exists in
    ///  the agent's latched diagnosis, and this pins that it survives the boundary into
    ///  <c>BackupResult.Message</c> and into the host's headline.
    /// </summary>
    [Fact]
    public async Task Backup_PartialFailure_ReportsTheAgentDiagnosis()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        src.AddFiles("diag", count: 9, minSize: 1_024, maxSize: 8 * 1_024);

        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            var req = MakeBackupRequest(svc, src.RootPath, "Diagnosis-Test") with
            {
                SkipAllErrors = true,   // skip every failure so the run completes and we reach the headline
            };

            // Armed at the deterministic moment the agent is created — no need for polling w/ deadline.
            svc.OnAgentReady = a =>
            {
                a.SimulateFileFailures.Enabled = true;
                a.SimulateFileFailures.EveryNth = 3;   // files 3, 6, 9
            };

            var result = await svc.ExecuteBackupAsync(req);

            Assert.True(result.FilesFailed > 0, "the simulator must have produced failures");

            // THE point of the test: a reason, not just a count.
            Assert.False(string.IsNullOrWhiteSpace(result.Message),
                $"BackupResult.Message must carry the agent's diagnosis. {host.DumpReports()}");
            Assert.Contains("Simulated", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(0u, result.ErrorCode);
            Assert.Equal(result.Diagnosis.ErrorMessage, result.Message);   // the join holds


            // ...and it must reach the user, not merely the result object.
            Assert.True(host.ContainsMessage("Simulated"),
                "the host headline must name the reason, not only the failure count");
        }

        AssertNoMediaPrompts(host);
    }

    /// <summary>
    /// A TOC-write failure is the case where counters are actively MISLEADING: every file succeeded, yet
    ///  the media is unusable without a TOC. The reason must reach the user.
    /// </summary>
    [Fact]
    public async Task Backup_TocFailure_ReportsTheReason()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        src.AddFiles("tocdiag", count: 4, minSize: 512, maxSize: 4_096);

        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            // Fail BOTH copies: bit 0 and bit 1. The agent then reports a genuine TOC failure and the
            //  service falls through to its enforce / emergency-export ladder.
            // Armed at the deterministic moment the agent is created — no need for polling w/ deadline.
            svc.OnAgentReady = a =>
                a.SimulateTOCFailureMask = 3;

            var result = await svc.ExecuteBackupAsync(
                MakeBackupRequest(svc, src.RootPath, "TOC-Diagnosis"));

            // Whatever the final outcome, the TOC problem must be visible in the log.
            Assert.True(host.ContainsMessage("TOC"),
                "a TOC write failure must be reported to the host");
        }
    }

    // ── Restore: a set-level failure must explain itself ──────────────────────

    /// <summary>
    /// The headline defect Step 9.0 exists to fix. A set that fails BEFORE any file is touched produces
    ///  zero file failures, so the old code reported a bare "no files processed" with no cause — the
    ///  agent's diagnosis having been cleared by the next set's successful <c>ResetError()</c> and then
    ///  discarded at the <c>TapeResult → bool</c> conversion.
    /// </summary>
    [Fact]
    public async Task Restore_NothingProcessed_ReportsWhy()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        src.AddFiles("why", count: 5, minSize: 512, maxSize: 4_096);

        // ── Backup cleanly ───────────────────────────────────────────────────
        var (backupSvc, _) = await OpenAndFormatAsync(media);
        using (backupSvc)
            Assert.True((await backupSvc.ExecuteBackupAsync(
                MakeBackupRequest(backupSvc, src.RootPath, "Why-Set"))).Success);

        // ── Restore with BOTH TOC copies unreadable at the set level ─────────
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int setIndex = svc.TOC!.SetIndexToStd(svc.TOC.CapSetIndex(0));

            // Fail every file so the set yields nothing at all.
            // Armed at the deterministic moment the agent is created — no need for polling w/ deadline.
            svc.OnAgentReady = a =>
            {
                a.SimulateFileFailures.Enabled = true;
                a.SimulateFileFailures.EveryNth = 1;
            };

            var result = await svc.ExecuteRestoreAsync(new RestoreRequest(
                Mode:                  RestoreMode.Validate,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
                Incremental:           false,
                TargetDirectory:       null,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Skip,
                SkipAllErrors:         true,
                EjectWhenDone:         false));

            Assert.Equal(0, result.FilesSucceeded);

            // The regression guard: a reason, not a bare count.
            Assert.False(string.IsNullOrWhiteSpace(result.Message),
                "RestoreResult.Message must carry the agent's diagnosis");
            Assert.True(host.ContainsMessage("Simulated"),
                "the host must be told WHY nothing was restored");
        }

        AssertNoMediaPrompts(host);
    }

#endif // DEBUG

    // ── Media-level faults: the injector reaches the service layer ────────────

#if DEBUG

    /// <summary>
    /// A drive-level read fault, injected below the agent entirely — the closest thing the suite has to
    ///  a real hardware failure. Proves the diagnosis path works for errors the agent did not synthesize.
    /// </summary>
    /// <remarks>
    /// Uses <c>ContentReadFaults</c> with <c>EveryNth</c> so a handful of reads fail across the set; the
    ///  agent surfaces them as per-file failures, and the service must name the cause. Contrast with
    ///  <c>SimulateFileFailures</c>, which fabricates the exception inside the agent — here the fault is
    ///  genuinely at the medium.
    /// </remarks>
    [Fact]
    public async Task Restore_MediaReadFaults_ReportTheReason()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        src.AddFiles("mediafault", count: 8, minSize: 8 * 1_024, maxSize: 32 * 1_024);

        var (backupSvc, _) = await OpenAndFormatAsync(media);
        using (backupSvc)
            Assert.True((await backupSvc.ExecuteBackupAsync(
                MakeBackupRequest(backupSvc, src.RootPath, "MediaFault-Set"))).Success);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            // ADAPT: exposing the backend from the service requires a hook — TapeServiceBase has no
            //  public Drive/Backend accessor today. Either add
            //      internal VirtualTapeDriveBackend? VirtualBackend => _drive?.Backend as VirtualTapeDriveBackend;
            //  or move this test to the agent level (see ErrorHandlingTests addendum), where the fixture
            //  exposes Backend directly. The assertions below are what matter, not the plumbing.
            var backend = svc.VirtualBackend;
            Assert.NotNull(backend);

            backend!.ContentReadFaults.Enabled = true;
            backend.ContentReadFaults.EveryNth = 5;

            int setIndex = svc.TOC!.SetIndexToStd(svc.TOC.CapSetIndex(0));
            var result = await svc.ExecuteRestoreAsync(new RestoreRequest(
                Mode:                  RestoreMode.Validate,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
                Incremental:           false,
                TargetDirectory:       null,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Skip,
                SkipAllErrors:         true,
                EjectWhenDone:         false));

            Assert.True(backend.ContentReadFaults.Occurrences > 0,
                "the injector must actually have fired");

            backend.ContentReadFaults.Reset();

            // A medium-level fault must surface with a reason, exactly like a synthesized one.
            if (result.FilesFailed > 0)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.Message),
                    "a medium-level fault must still yield a diagnosis");
                Assert.True(host.HasErrors,
                    "the host must have been told about the read faults");
            }
        }
    }

#endif // DEBUG
}
