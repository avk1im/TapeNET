using System.IO;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Virtual;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TestTapeServiceHost

using TapeConNET.Services;
using TapeConNET.Tests.Helpers; // TempVirtualMedia
using TapeConNET.Ux;

namespace TapeConNET.Tests.Services;

/// <summary>
/// Sanity-baseline round-trip tests for <see cref="TapeService"/> driven
///  directly (not via the CLI) against a file-backed virtual drive.
/// <para>
/// This is Phase B, step 5: verifying that the service-layer plumbing —
///  progress handlers, <see cref="ITapeServiceHost"/> callbacks, lifecycle
///  methods — works end-to-end before any logic is extracted into
///  <c>TapeServiceBase</c> (Phase C).
/// </para>
/// <para>
/// <see cref="TestTapeServiceHost"/> is not yet wired into <see cref="TapeService"/>
///  (which still takes an <see cref="IConsoleUx"/>); instead, the
///  <see cref="SilentConsoleUx"/> captures all log output, which is the
///  same data that will flow through <see cref="TestTapeServiceHost.Report"/>
///  once Phase C centralises the log channel into <c>TapeServiceBase</c>.
/// </para>
/// </summary>
public class ServiceRoundTripTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Default content capacity: 64 MiB — enough for the test file trees.</summary>
    private const long ContentCapacity = 64L * 1024 * 1024;

    /// <summary>Default initiator partition capacity: 4 MiB.</summary>
    private const long InitiatorCapacity = 4L * 1024 * 1024;

    private const string MediaName = "ServiceRoundTripMedia";

    /// <summary>
    /// Default block size used when the virtual drive reports 0 (memory-backed
    ///  drives have no hardware preference). Matches the value used by
    ///  <c>BackupCommand</c> as its own fallback.
    /// </summary>
    private const uint FallbackBlockSize = 64 * 1024;

    // ── Factory helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="TapeService"/> wired to a <see cref="SilentConsoleUx"/>
    ///  that captures every log entry for post-hoc assertions.
    /// </summary>
    private static (TapeService service, SilentConsoleUx ux) CreateService(
        CancellationToken ct = default)
    {
        var ux = new SilentConsoleUx();
        var service = new TapeService(ux, NullLoggerFactory.Instance, ct);
        return (service, ux);
    }

    /// <summary>
    /// Opens a file-backed virtual drive, formats it, and leaves the service
    ///  in the post-format state (media loaded, TOC available).
    /// </summary>
    private static async Task<(TapeService service, SilentConsoleUx ux)> OpenAndFormatAsync(
        TempVirtualMedia media,
        CancellationToken ct = default)
    {
        var (service, ux) = CreateService(ct);

        var caps = media.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        var vmd = new VirtualMediaDescriptor(
            media.ContentPath,
            media.ContentCapacity,
            media.InitiatorPath,
            media.HasInitiator ? media.InitiatorCapacity : 0);

        Assert.True(await service.OpenVirtualDriveAsync(caps, vmd, FileMode.Create),
            $"OpenVirtualDriveAsync failed: {service.LastError}");
        Assert.True(await service.LoadMediaAsync(),
            $"LoadMediaAsync (post-create) failed: {service.LastError}");

        long initSize = media.HasInitiator ? TapeNavigator.DefaultTOCCapacity : -1L;
        Assert.True(await service.FormatMediaAsync(initSize, MediaName),
            $"FormatMediaAsync failed: {service.LastError}");

        return (service, ux);
    }

    /// <summary>
    /// Re-opens the same virtual media files for reading (e.g. post-backup).
    ///  Loads media and restores the TOC.
    /// </summary>
    private static async Task<(TapeService service, SilentConsoleUx ux)> ReopenAsync(
        TempVirtualMedia media,
        CancellationToken ct = default)
    {
        var (service, ux) = CreateService(ct);

        var caps = media.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        var vmd = new VirtualMediaDescriptor(
            media.ContentPath,
            media.ContentCapacity,
            media.InitiatorPath,
            media.HasInitiator ? media.InitiatorCapacity : 0);

        Assert.True(await service.OpenVirtualDriveAsync(caps, vmd, FileMode.Open),
            $"OpenVirtualDriveAsync (reopen) failed: {service.LastError}");
        Assert.True(await service.LoadMediaAsync(),
            $"LoadMediaAsync (reopen) failed: {service.LastError}");
        Assert.True(await service.RestoreTOCAsync(),
            $"RestoreTOCAsync failed: {service.LastError}");

        return (service, ux);
    }

    /// <summary>
    /// Builds a minimal <see cref="BackupRequest"/> for a file-pattern backup.
    /// </summary>
    private BackupRequest MakeBackupRequest(
        TapeService service,
        string sourceRoot,
        string description,
        bool subdirs = true,
        bool append = false)
    {
        uint blockSize = service.DefaultBlockSize > 0 ? service.DefaultBlockSize : FallbackBlockSize;
        return new BackupRequest(
            FileList:              [sourceRoot],
            ListContainsPatterns:  true,
            Description:           description,
            IncludeSubdirectories: subdirs,
            Incremental:           false,
            BlockSize:             blockSize,
            HashAlgorithm:         TapeHashAlgorithm.Crc32,
            AppendMode:            append,
            AppendAfterSetIndex:   0,
            UseFilemarks:          false,
            SkipAllErrors:         false);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Single-volume backup → restore round-trip; verifies every byte is
    ///  recovered intact. Parameterised over both drive profiles
    ///  (single-partition and with initiator partition).
    /// </summary>
    [Theory]
    [InlineData(false)] // single-partition (setmarks only)
    [InlineData(true)]  // with initiator partition
    public async Task Backup_Then_Restore_RoundTripsBytes(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        src.AddFiles("docs",         count: 8, minSize: 1_024,  maxSize: 16 * 1_024);
        src.AddFile ("nested/deep/file.bin",   65_537);

        // ── Backup ────────────────────────────────────────────────────────────
        var (backupSvc, backupUx) = await OpenAndFormatAsync(media);
        using (backupSvc)
        {
            var req    = MakeBackupRequest(backupSvc, src.RootPath, "RoundTrip-Set-1");
            var result = await backupSvc.ExecuteBackupAsync(req);

            Assert.False(result.WasAborted,  "Backup was unexpectedly aborted");
            Assert.False(result.HasFailed,    "Backup reported failure");
            Assert.True (result.Success,      "Backup did not succeed");
            Assert.Equal(0, result.FilesFailed);
            Assert.True (result.FilesSucceeded > 0, "No files were backed up");
            Assert.False(backupUx.Entries.Any(e =>
                e.Level is WarningLevel.Error or WarningLevel.Failed),
                "Backup log contains unexpected errors");
        }

        // ── Restore ───────────────────────────────────────────────────────────
        var restoreRoot = Path.Combine(media.Root, "restore");
        Directory.CreateDirectory(restoreRoot);

        var (restoreSvc, restoreUx) = await ReopenAsync(media);
        using (restoreSvc)
        {
            Assert.NotNull(restoreSvc.TOC);
            Assert.True(restoreSvc.TOC!.Count > 0, "TOC has no sets after restore");

            // Restore all files from the latest set (index 0 → normalised by RestoreCommand logic)
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

            Assert.False(result.WasAborted,  "Restore was unexpectedly aborted");
            Assert.False(result.HasFailed,    "Restore reported failure");
            Assert.True (result.Success,      "Restore did not succeed");
            Assert.Equal(0, result.FilesFailed);
            Assert.False(restoreUx.Entries.Any(e =>
                e.Level is WarningLevel.Error or WarningLevel.Failed),
                "Restore log contains unexpected errors");
        }

        // ── Byte-level comparison ─────────────────────────────────────────────
        FileComparer.AssertFilesMatch(
            src.RootPath,
            src.Files,
            FindRestoredRoot(restoreRoot, src.RootPath));
    }

    /// <summary>
    /// Append a second backup set after the first; verifies that the TOC
    ///  reports two sets and that both are restorable independently.
    /// </summary>
    [Fact]
    public async Task AppendBackup_AddsSecondSet_BothRestorable()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src1  = new TempFileTree();
        using var src2  = new TempFileTree();
        src1.AddFiles("set1", count: 4, minSize: 512, maxSize: 4_096);
        src2.AddFiles("set2", count: 4, minSize: 512, maxSize: 4_096);

        // ── Backup set 1 (new) ────────────────────────────────────────────────
        var (svc1, _) = await OpenAndFormatAsync(media);
        using (svc1)
        {
            var result = await svc1.ExecuteBackupAsync(
                MakeBackupRequest(svc1, src1.RootPath, "Set-1"));
            Assert.True(result.Success, "Backup of Set-1 failed");
            Assert.Equal(0, result.FilesFailed);
        }

        // ── Backup set 2 (append) ─────────────────────────────────────────────
        var (svc2, _) = await ReopenAsync(media);
        using (svc2)
        {
            var result = await svc2.ExecuteBackupAsync(
                MakeBackupRequest(svc2, src2.RootPath, "Set-2", append: true));
            Assert.True(result.Success, "Backup of Set-2 failed");
            Assert.Equal(0, result.FilesFailed);
        }

        // ── TOC should list two sets ───────────────────────────────────────────
        var (svcRead, _) = await ReopenAsync(media);
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

        // ── Restore set 1 (oldest, standard index 1) ─────────────────────────
        var restore1Root = Path.Combine(media.Root, "restore1");
        Directory.CreateDirectory(restore1Root);

        var (svcR1, _) = await ReopenAsync(media);
        using (svcR1)
        {
            int stdIndex1 = svcR1.TOC!.FirstSetOnVolume; // = 1 (oldest)
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

        // ── Restore set 2 (latest, standard index 2) ─────────────────────────
        var restore2Root = Path.Combine(media.Root, "restore2");
        Directory.CreateDirectory(restore2Root);

        var (svcR2, _) = await ReopenAsync(media);
        using (svcR2)
        {
            int stdIndex2 = svcR2.TOC!.LastSetOnVolume; // = 2 (latest)
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
        }

        FileComparer.AssertFilesMatch(
            src2.RootPath, src2.Files, FindRestoredRoot(restore2Root, src2.RootPath));
    }

    /// <summary>
    /// Verify that aborting a backup mid-run (via the agent's
    ///  <see cref="TapeFileAgent.IsAbortRequested"/> flag, which is the same
    ///  mechanism the <see cref="CancellationToken"/> bridge maps to) returns
    ///  <see cref="BackupResult.WasAborted"/> = <see langword="true"/> and
    ///  does not throw.
    /// </summary>
    [Fact]
    public async Task Backup_Abort_ReportsWasAborted()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        // Add enough files that we can signal abort before the last one
        src.AddFiles("abort", count: 200, minSize: 64 * 1_024, maxSize: 256 * 1_024);

        // Format with a separate service so setup is clean
        var (setupSvc, _) = await OpenAndFormatAsync(media);
        setupSvc.Dispose();

        var (svc, _) = await ReopenAsync(media);
        using (svc)
        {
            uint blockSize = svc.DefaultBlockSize > 0 ? svc.DefaultBlockSize : FallbackBlockSize;
            var req = new BackupRequest(
                FileList:              [src.RootPath],
                ListContainsPatterns:  true,
                Description:           "Abort-Test",
                IncludeSubdirectories: true,
                Incremental:           false,
                BlockSize:             blockSize,
                HashAlgorithm:         TapeHashAlgorithm.None,
                AppendMode:            false,
                AppendAfterSetIndex:   0,
                UseFilemarks:          false,
                SkipAllErrors:         true);

            // Start the backup (it runs on a background thread inside TapeService)
            var backupTask = svc.ExecuteBackupAsync(req);

            // Poll until the agent is assigned (i.e. the backup loop has started),
            //  then signal abort via the same flag the CancellationToken bridge uses.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (svc.Agent is null && DateTime.UtcNow < deadline)
                await Task.Delay(1);

            if (svc.Agent is not null)
                svc.Agent.IsAbortRequested = true;

            var result = await backupTask;

            Assert.True(result.WasAborted, "Expected WasAborted = true after abort signal");
        }
    }

    /// <summary>
    /// Validate that CRC integrity check (Validate mode) passes immediately
    ///  after a successful backup.
    /// </summary>
    [Fact]
    public async Task Validate_AfterBackup_Passes()
    {
        using var media = new TempVirtualMedia(withInitiator: true, ContentCapacity, InitiatorCapacity);
        using var src   = new TempFileTree();
        src.AddFiles("validate", count: 6, minSize: 1_024, maxSize: 8 * 1_024);

        // Backup
        var (svcB, _) = await OpenAndFormatAsync(media);
        using (svcB)
        {
            var result = await svcB.ExecuteBackupAsync(
                MakeBackupRequest(svcB, src.RootPath, "Validate-Test"));
            Assert.True(result.Success, "Backup failed before validate");
        }

        // Validate
        var (svcV, validateUx) = await ReopenAsync(media);
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
            Assert.False(validateUx.Entries.Any(e =>
                e.Level is WarningLevel.Error or WarningLevel.Failed),
                "Validate log contains unexpected errors");
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Walks <paramref name="restoreRoot"/> and locates the directory whose
    ///  last segment matches the leaf of <paramref name="srcRoot"/>. Tape
    ///  restores prepend the volume identifier to the path, so the absolute
    ///  layout differs across machines.
    ///  Mirrors <c>TapeConNET.Tests.BackupRestoreRoundTripTests.FindRestoredRoot</c>.
    /// </summary>
    private static string FindRestoredRoot(string restoreRoot, string srcRoot)
    {
        var leaf = Path.GetFileName(srcRoot.TrimEnd('\\', '/'));
        var match = Directory.EnumerateDirectories(restoreRoot, leaf, SearchOption.AllDirectories)
            .FirstOrDefault();
        return match ?? restoreRoot;
    }
}
