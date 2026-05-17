using System.IO;

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia

namespace TapeLibNET.Tests.Services.Remote;

/// <summary>
/// Remote-service baseline tests: single-volume round-trip bytes, append sets,
///  and CRC validation over a gRPC backend.
/// <para>
/// Mirrors <see cref="ServiceBaselineTests"/> but routes all drive I/O through
///  the in-process <see cref="LocalHostTapeServiceFixture"/> gRPC server.
///  Covers the same plan items (I-1, E-1, E-2) as the local baseline suite.
/// </para>
/// <para>
/// <b>Not included:</b> the mid-run abort test (<c>Backup_Abort_SetEntriesAreIntact</c>)
///  is omitted for remote drives: the agent object lives inside the server process and
///  is not directly reachable from the test client, so the abort-polling idiom used by
///  the local suite cannot be reproduced faithfully over gRPC.
/// </para>
/// </summary>
[Collection(LocalHostTapeServiceCollection.Name)]
public class RemoteServiceBaselineTests(LocalHostTapeServiceFixture fixture)
    : RemoteServiceTestBase(fixture)
{
    /// <summary>
    /// Single-volume backup → restore round-trip over gRPC; verifies every byte
    ///  is recovered intact and that the exact file count is round-tripped.
    ///  Parameterised over both drive profiles.
    /// </summary>
    [Theory]
    [InlineData(false)] // single-partition (setmarks only)
    [InlineData(true)]  // with initiator partition
    public async Task Remote_Backup_Then_Restore_RoundTripsBytes(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        AddRichContent(src);

        // ── Backup ────────────────────────────────────────────────────────────
        var (backupSvc, backupHost) = await OpenAndFormatRemoteAsync(media);
        using (backupSvc)
        {
            var req    = MakeBackupRequest(backupSvc, src.RootPath, "RoundTrip-Set-1");
            var result = await backupSvc.ExecuteBackupAsync(req);

            Assert.False(result.WasAborted,   "Backup was unexpectedly aborted");
            Assert.False(result.HasFailed,    "Backup reported failure");
            Assert.True (result.Success,      "Backup did not succeed");
            Assert.Equal(0, result.FilesFailed);
            Assert.Equal(RichContentFileCount, result.FilesSucceeded);
            Assert.False(backupHost.HasErrors, "Backup host received unexpected error reports");
            Assert.True (backupHost.StateChanges.Contains(ServiceStateChange.OperationEnded),
                "Backup did not emit OperationEnded state change");
        }

        // ── Restore ───────────────────────────────────────────────────────────
        var restoreRoot = Path.Combine(media.Root, "restore");
        Directory.CreateDirectory(restoreRoot);

        var (restoreSvc, restoreHost) = await ReopenRemoteAsync(media);
        using (restoreSvc)
        {
            Assert.NotNull(restoreSvc.TOC);
            Assert.True(restoreSvc.TOC!.Count > 0, "TOC has no sets after restore");

            int setIndex = restoreSvc.TOC.SetIndexToStd(restoreSvc.TOC.CapSetIndex(0));
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
                Incremental:           true,
                TargetDirectory:       restoreRoot,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false);

            var result = await restoreSvc.ExecuteRestoreAsync(req);

            Assert.False(result.WasAborted,   "Restore was unexpectedly aborted");
            Assert.False(result.HasFailed,    "Restore reported failure");
            Assert.True (result.Success,      "Restore did not succeed");
            Assert.Equal(0, result.FilesFailed);
            Assert.Equal(RichContentFileCount, result.FilesSucceeded);
            Assert.False(restoreHost.HasErrors, "Restore host received unexpected error reports");
            Assert.True (restoreHost.StateChanges.Contains(ServiceStateChange.OperationEnded),
                "Restore did not emit OperationEnded state change");
        }

        // ── Byte-level comparison ─────────────────────────────────────────────
        FileComparer.AssertFilesMatch(
            src.RootPath,
            src.Files,
            FindRestoredRoot(restoreRoot, src.RootPath));
    }

    /// <summary>
    /// Append a second backup set after the first over gRPC; verifies that the TOC
    ///  reports two sets and that both are restorable independently.
    /// </summary>
    [Fact]
    public async Task Remote_AppendBackup_AddsSecondSet_BothRestorable()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src1  = new TempFileTree();
        using var src2  = new TempFileTree();
        src1.AddFiles("set1", count: 4, minSize: 512, maxSize: 4_096);
        src2.AddFiles("set2", count: 4, minSize: 512, maxSize: 4_096);

        // ── Backup set 1 (new) ────────────────────────────────────────────────
        var (svc1, host1) = await OpenAndFormatRemoteAsync(media);
        using (svc1)
        {
            var result = await svc1.ExecuteBackupAsync(
                MakeBackupRequest(svc1, src1.RootPath, "Set-1"));
            Assert.True(result.Success, "Backup of Set-1 failed");
            Assert.Equal(0, result.FilesFailed);
            Assert.False(host1.HasErrors, "Set-1 backup host received unexpected errors");
        }

        // ── Backup set 2 (append) ─────────────────────────────────────────────
        var (svc2, host2) = await ReopenRemoteAsync(media);
        using (svc2)
        {
            var result = await svc2.ExecuteBackupAsync(
                MakeBackupRequest(svc2, src2.RootPath, "Set-2", append: true));
            Assert.True(result.Success, "Backup of Set-2 failed");
            Assert.Equal(0, result.FilesFailed);
            Assert.False(host2.HasErrors, "Set-2 backup host received unexpected errors");
        }

        // ── TOC should list two sets ───────────────────────────────────────────
        var (svcRead, _) = await ReopenRemoteAsync(media);
        using (svcRead)
        {
            Assert.NotNull(svcRead.TOC);
            Assert.Equal(2, svcRead.TOC!.Count);

            var descriptions = Enumerable
                .Range(svcRead.TOC.FirstSetOnVolume, svcRead.TOC.Count)
                .Select(i => svcRead.TOC[i].Description)
                .ToList();

            Assert.Contains(descriptions, d => d?.Contains("Set-1") ?? false);
            Assert.Contains(descriptions, d => d?.Contains("Set-2") ?? false);
        }

        // ── Restore set 1 (oldest) ────────────────────────────────────────────
        var restore1Root = Path.Combine(media.Root, "restore1");
        Directory.CreateDirectory(restore1Root);

        var (svcR1, _) = await ReopenRemoteAsync(media);
        using (svcR1)
        {
            int stdIndex1 = svcR1.TOC!.FirstSetOnVolume;
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [stdIndex1] = null },
                Incremental:           false,
                TargetDirectory:       restore1Root,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false);

            var result = await svcR1.ExecuteRestoreAsync(req);
            Assert.True(result.Success, "Restore of Set-1 failed");
            Assert.Equal(0, result.FilesFailed);
        }

        FileComparer.AssertFilesMatch(
            src1.RootPath, src1.Files, FindRestoredRoot(restore1Root, src1.RootPath));

        // ── Restore set 2 (latest) ────────────────────────────────────────────
        var restore2Root = Path.Combine(media.Root, "restore2");
        Directory.CreateDirectory(restore2Root);

        var (svcR2, hostR2) = await ReopenRemoteAsync(media);
        using (svcR2)
        {
            int stdIndex2 = svcR2.TOC!.LastSetOnVolume;
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [stdIndex2] = null },
                Incremental:           false,
                TargetDirectory:       restore2Root,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false);

            var result = await svcR2.ExecuteRestoreAsync(req);
            Assert.True(result.Success, "Restore of Set-2 failed");
            Assert.Equal(0, result.FilesFailed);
            Assert.False(hostR2.HasErrors, "Set-2 restore host received unexpected errors");
        }

        FileComparer.AssertFilesMatch(
            src2.RootPath, src2.Files, FindRestoredRoot(restore2Root, src2.RootPath));
    }

    /// <summary>
    /// Validates that CRC integrity check (<see cref="RestoreMode.Validate"/>) passes
    ///  immediately after a successful remote backup.
    /// </summary>
    [Fact]
    public async Task Remote_Validate_AfterBackup_Passes()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        AddRichContent(src);

        // ── Backup ────────────────────────────────────────────────────────────
        var (svcB, _) = await OpenAndFormatRemoteAsync(media);
        using (svcB)
        {
            var result = await svcB.ExecuteBackupAsync(
                MakeBackupRequest(svcB, src.RootPath, "Validate-Test"));
            Assert.True(result.Success, "Backup failed before validate");
        }

        // ── Validate ─────────────────────────────────────────────────────────
        var (svcV, validateHost) = await ReopenRemoteAsync(media);
        using (svcV)
        {
            int setIndex = svcV.TOC!.SetIndexToStd(svcV.TOC.CapSetIndex(0));
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Validate,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
                Incremental:           true,
                TargetDirectory:       null,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Skip,
                SkipAllErrors:         false);

            var result = await svcV.ExecuteRestoreAsync(req);

            Assert.True(result.Success, "Validate reported failure");
            Assert.Equal(0, result.FilesFailed);
            Assert.False(validateHost.HasErrors, "Validate host received unexpected error reports");
        }
    }
}
