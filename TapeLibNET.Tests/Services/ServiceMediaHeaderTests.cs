using System.IO;
using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia, TestTapeServiceHost
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Service-level coverage for media-identity headers (§10): format-time heading, the overwrite /
///  wrong-kind / continuation prompt machinery, MediaId reset on overwrite, the calibration guard,
///  the opt-out flags, and LEGACY (header-less) media round-tripping through the header-aware code.
/// </summary>
/// <remarks>
/// Unlike the agent-level fixtures, the service HEADS every volume it formats / continues, so "headless"
///  media here is genuinely legacy — manufactured with a raw <see cref="TapeFileBackupAgent"/> whose
///  <see cref="TapeFileAgent.WritesMediaHeader"/> is false (<see cref="ManufactureLegacyMedia"/>). The
///  <see cref="TestTapeServiceHost"/> scripts prompt answers via <c>MediaMismatchAnswers</c> and records
///  every prompt in <c>MediaMismatchPrompts</c>.
/// </remarks>
public class ServiceMediaHeaderTests : ServiceTestBase
{
    private const long MB = 1024L * 1024;
    private const long CalibCapacity = 16L * MB;   // small: a full calibration run stays fast

    // ── Local open helpers ────────────────────────────────────────────────────

    /// <summary>Opens file-backed setmarks media WITHOUT formatting or restoring the TOC — just load.</summary>
    private async Task<(TapeServiceBase svc, TestTapeServiceHost host)> OpenLoadOnlyAsync(
        TempVirtualMedia media, FileMode mode, VirtualTapeEwProfile? ew = null)
    {
        var (svc, host) = CreateService();
        var vmd = new VirtualMediaDescriptor(media.ContentPath, media.ContentCapacity, null, 0);

        Assert.True(await svc.OpenVirtualDriveAsync(
                VirtualTapeDriveCapabilities.WithSetmarks, vmd, mode, ewProfile: ew),
            $"OpenVirtualDriveAsync failed: {svc.LastError}");
        Assert.True(await svc.LoadMediaAsync(), $"LoadMediaAsync failed: {svc.LastError}");

        return (svc, host);
    }

    /// <summary>Opens + formats file-backed setmarks media with EW emulation (for calibration tests).</summary>
    private async Task<(TapeServiceBase svc, TestTapeServiceHost host)> OpenFormatWithEwAsync(
        TempVirtualMedia media, FileMode mode = FileMode.Create)
    {
        var (svc, host) = await OpenLoadOnlyAsync(media, mode, VirtualTapeEwProfile.EmulatedOverreport(media.ContentCapacity));
        Assert.True(await svc.FormatMediaAsync(-1L, MediaName), $"FormatMediaAsync failed: {svc.LastError}");
        return (svc, host);
    }

    /// <summary>
    /// Manufactures genuinely LEGACY (header-less) single-partition media via a raw backup agent whose
    ///  <see cref="TapeFileAgent.WritesMediaHeader"/> is false — content starts at block 0, no BOM header.
    /// </summary>
    private static void ManufactureLegacyMedia(TempVirtualMedia media, TempFileTree src, string description = "Legacy Media")
    {
        var backend = VirtualTapeDriveBackend.CreateFileBacked(
            TestLoggerFactory.Default, media.ContentPath, media.ContentCapacity,
            initiatorFilePath: null, initiatorCapacity: 0,
            VirtualTapeDriveCapabilities.WithSetmarks, FileMode.Create);

        using var drive = new TapeDrive(TestLoggerFactory.Default, backend);
        Assert.True(drive.ReopenDrive(0), "legacy: ReopenDrive");
        Assert.True(drive.ReloadMedia(), "legacy: ReloadMedia");
        Assert.True(drive.PrepareMedia(), "legacy: PrepareMedia");
        Assert.True(drive.FormatMedia(-1L), "legacy: FormatMedia");

        using var agent = new TapeFileBackupAgent(drive, new TapeTOC(description))
        {
            WritesMediaHeader = false,   // ← the point: no BOM header, legacy layout
        };

        var toc = agent.TOC;
        toc.AddNewSetTOC(0);
        toc.CurrentSetTOC.Description   = "Legacy Set";
        toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc32;
        toc.CurrentSetTOC.BlockSize     = drive.DefaultBlockSize > 0 ? drive.DefaultBlockSize : FallbackBlockSize;

        Assert.True((bool)agent.BackupFileListToCurrentSet(
            newSet: true, [.. src.Files], ignoreFailures: true), "legacy: backup");
        Assert.True((bool)agent.BackupTOC(), "legacy: BackupTOC");
    }

    // ── Format heads the media ────────────────────────────────────────────────

    [Theory]
    [InlineData(false)] // single-partition → TocPlacement.InSet
    [InlineData(true)]  // initiator partition → TocPlacement.InPartition
    public async Task FormatMedia_WritesMediaHeader_MatchingToc(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);

        var (svc, _) = await OpenAndFormatAsync(media);
        using (svc)
        {
            var header = svc.LoadedMediaHeader;   // populated by FormatMediaAsync's post-reload RefreshLoadedHeader
            Assert.NotNull(header);
            Assert.Equal(svc.TOC!.MediaId, header!.MediaId);           // header ↔ TOC share identity
            Assert.NotEqual(Guid.Empty, header.MediaId);
            Assert.Equal(withInitiator ? TapeTocPlacement.InPartition : TapeTocPlacement.InSet,
                header.TocPlacement);
        }
    }

    // ── Overwrite: prompt, proceed, MediaId reset ─────────────────────────────

    [Fact]
    public async Task Overwrite_MediaWithSets_Prompts_Proceed_MintsFreshMediaId()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity);
        using var src1  = new TempFileTree(); src1.AddFiles("s1", 4, 1_024, 4_096);
        using var src2  = new TempFileTree(); src2.AddFiles("s2", 4, 1_024, 4_096);

        Guid firstId;
        {
            var (svc, _) = await OpenAndFormatAsync(media);
            using (svc)
            {
                Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src1.RootPath, "s1"))).Success);
                firstId = svc.TOC!.MediaId;
            }
        }

        var (svc2, host) = await ReopenAsync(media);   // loads header (firstId) + TOC (1 set)
        using (svc2)
        {
            Assert.Equal(firstId, svc2.LoadedMediaHeader!.MediaId);

            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Proceed);
            var result = await svc2.ExecuteBackupAsync(MakeBackupRequest(svc2, src2.RootPath, "s2")); // append:false ⇒ overwrite
            Assert.True(result.Success, $"overwrite failed: {svc2.LastError}");
            Assert.Equal(src2.Files.Count, result.FilesSucceeded);

            // The overwrite guard fired a prompt (existing sets present) in the OverwriteBackup context …
            AssertMediaPrompts(host, (TapeMediaVerdict.MediaIdMismatch, MediaPromptContext.OverwriteBackup));
            // … and the rewritten media now carries a FRESH series id (collision-safe vs surviving volumes).
            Assert.NotEqual(firstId, svc2.TOC!.MediaId);
        }

        // The tape actually holds only the new series now.
        var (svcR, _) = await ReopenAsync(media);
            // this ReopenAsync host is a fresh, unchecked host → teardown asserts it stayed silent
        using (svcR)
        {
            Assert.Single(svcR.TOC!);
            Assert.NotEqual(firstId, svcR.TOC!.MediaId);
            Assert.Equal(svcR.TOC!.MediaId, svcR.LoadedMediaHeader!.MediaId);
        }
    }

    [Fact]
    public async Task Overwrite_MediaWithSets_Abort_PreservesMedia()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity);
        using var src1  = new TempFileTree(); src1.AddFiles("keep", 4, 1_024, 4_096);
        using var src2  = new TempFileTree(); src2.AddFiles("new", 4, 1_024, 4_096);

        Guid firstId;
        {
            var (svc, _) = await OpenAndFormatAsync(media);
            using (svc)
            {
                Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src1.RootPath, "keep"))).Success);
                firstId = svc.TOC!.MediaId;
            }
        }

        var (svc2, host) = await ReopenAsync(media);
        using (svc2)
        {
            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Abort);

            var result = await svc2.ExecuteBackupAsync(MakeBackupRequest(svc2, src2.RootPath, "new"));
            Assert.True(result.WasAborted, "overwrite should abort on user Abort");
            AssertMediaPrompts(host, (TapeMediaVerdict.MediaIdMismatch, MediaPromptContext.OverwriteBackup)); // check that there was just this exact prompt
        }

        // Original series + set survive untouched.
        var (svcR, _) = await ReopenAsync(media);
        using (svcR)
        {
            Assert.Single(svcR.TOC!);
            Assert.Equal(firstId, svcR.TOC!.MediaId);
        }
    }

    [Fact]
    public async Task Overwrite_ForceVolumeOverwrite_SuppressesPrompt()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity);
        using var src1  = new TempFileTree(); src1.AddFiles("s1", 4, 1_024, 4_096);
        using var src2  = new TempFileTree(); src2.AddFiles("s2", 4, 1_024, 4_096);

        {
            var (svc, _) = await OpenAndFormatAsync(media);
            using (svc)
                Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src1.RootPath, "s1"))).Success);
        }

        var (svc2, host) = await ReopenAsync(media);
        using (svc2)
        {
            // No scripted answer — but the flag suppresses the prompt entirely.
            var req = MakeBackupRequest(svc2, src2.RootPath, "s2") with { ForceVolumeOverwrite = true };
            var result = await svc2.ExecuteBackupAsync(req);

            Assert.True(result.Success, $"forced overwrite failed: {svc2.LastError}");
            AssertNoMediaPrompts(host); // prompt suppressed — never reached the host
        }
    }

    // ── Append verifies identity → no prompt on matching media ────────────────

    [Fact]
    public async Task Append_MatchingMedia_NoPrompt()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity);
        using var src1  = new TempFileTree(); src1.AddFiles("a", 4, 1_024, 4_096);
        using var src2  = new TempFileTree(); src2.AddFiles("b", 4, 1_024, 4_096);

        {
            var (svc, _) = await OpenAndFormatAsync(media);
            using (svc)
                Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src1.RootPath, "a"))).Success);
        }

        var (svc2, host) = await ReopenAsync(media);
        using (svc2)
        {
            var result = await svc2.ExecuteBackupAsync(MakeBackupRequest(svc2, src2.RootPath, "b", append: true));
            Assert.True(result.Success, $"append failed: {svc2.LastError}");
            AssertNoMediaPrompts(host); // same series + volume ⇒ Match ⇒ silent
            Assert.Equal(2, svc2.TOC!.Count);
        }
    }

    // ── Wrong kind: a calibration cartridge met by a backup-overwrite ─────────

    [Fact]
    public async Task Backup_Overwrite_OnCalibrationCartridge_RaisesWrongKind()
    {
        using var media = new TempVirtualMedia(withInitiator: false, CalibCapacity);

        // 1) Lay down a calibration header (calibration overwrites BOM with its own header).
        // Formatting heads the media, so the calibration guard (§10.7) correctly challenges
        //  it — confirm to proceed with the scratch setup.
        {
            var (svc, host1) = await OpenFormatWithEwAsync(media);
            using (svc)
            {
                // Calibration setup over freshly-headed media → CalibrateScratch guard:
                host1.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Proceed);   // proceed to erase and calibrate
                var cal = await svc.ExecuteCalibrateAsync(new CalibrateRequest(
                    EjectWhenDone: false, Options: new TapeCalibrationOptions { SampleCount = 20, NumCheckpoints = 4 }));
                Assert.True(cal.Success, $"calibration setup failed: {svc.LastError}");
                AssertMediaPrompts(host1, (TapeMediaVerdict.WrongKind, MediaPromptContext.CalibrateScratch));
            }
        }

        // 2) Reopen for a backup-overwrite: the cartridge now identifies as calibration media.
        using var src = new TempFileTree(); src.AddFiles("x", 3, 1_024, 4_096);
        // Backup-overwrite over the calibration cartridge → WrongKind/OverwriteBackup:
        var (svc2, host) = await OpenLoadOnlyAsync(media, FileMode.Open,
            VirtualTapeEwProfile.EmulatedOverreport(media.ContentCapacity));   // no RestoreTOC — cal destroyed it
        using (svc2)
        {
            Assert.IsType<TapeCalibrationHeader>(svc2.LoadedHeader);

            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Abort);      // decline the destructive overwrite
            var result = await svc2.ExecuteBackupAsync(MakeBackupRequest(svc2, src.RootPath, "over-cal")); // overwrite
            Assert.True(result.WasAborted);
            AssertMediaPrompts(host, (TapeMediaVerdict.WrongKind, MediaPromptContext.OverwriteBackup));
        }
    }

    // ── Calibration guard: a backup cartridge met by a calibration run ────────

    [Fact]
    public async Task Calibrate_OnBackupMedia_Confirm_DeclineAborts()
    {
        using var media = new TempVirtualMedia(withInitiator: false, CalibCapacity);
        using var src   = new TempFileTree(); src.AddFiles("b", 3, 1_024, 4_096);

        {
            var (svc, _) = await OpenAndFormatAsync(media);   // heads the media
            using (svc)
                Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src.RootPath, "b"))).Success);
        }

        var (svc2, host) = await OpenLoadOnlyAsync(media, FileMode.Open,
            VirtualTapeEwProfile.EmulatedOverreport(media.ContentCapacity));
        using (svc2)
        {
            Assert.IsType<TapeMediaHeader>(svc2.LoadedHeader);

            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Abort);   // respond "abort" to the "holds a backup — erase?" confirm
            var cal = await svc2.ExecuteCalibrateAsync(new CalibrateRequest(
                EjectWhenDone: false, Options: new TapeCalibrationOptions { SampleCount = 8, NumCheckpoints = 4 }));

            Assert.True(cal.WasAborted, "calibration should abort when the backup-media confirm is declined");
            Assert.Contains("holds a backup", cal.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            AssertMediaPrompts(host, (TapeMediaVerdict.WrongKind, MediaPromptContext.CalibrateScratch));
        }
    }

    [Fact]
    public async Task Calibrate_SkipMediaHeaderCheck_ProceedsWithoutConfirm()
    {
        using var media = new TempVirtualMedia(withInitiator: false, CalibCapacity);
        using var src   = new TempFileTree(); src.AddFiles("b", 3, 1_024, 4_096);

        {
            var (svc, _) = await OpenAndFormatAsync(media);
            using (svc)
                Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src.RootPath, "b"))).Success);
        }

        var (svc2, _) = await OpenLoadOnlyAsync(media, FileMode.Open,
            VirtualTapeEwProfile.EmulatedOverreport(media.ContentCapacity));
        using (svc2)
        {
            // No ConfirmAnswers queued — the guard is skipped, so the run is never blocked.
            var cal = await svc2.ExecuteCalibrateAsync(new CalibrateRequest(
                EjectWhenDone: false,
                Options: new TapeCalibrationOptions { SampleCount = 8, NumCheckpoints = 4 })
                { SkipMediaHeaderCheck = true });

            Assert.True(cal.Success, $"calibration with SkipMediaHeaderCheck failed: {svc2.LastError}");
        }
    }

    // ── Legacy (header-less) media round-trips through the header-aware code ───

    [Fact]
    public async Task LegacyHeadlessMedia_Restores_NoMismatchPrompt()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity);
        using var src   = new TempFileTree(); src.AddFiles("legacy", 6, 1_024, 8_192);

        ManufactureLegacyMedia(media, src);   // content at block 0, no BOM header

        var restoreRoot = Path.Combine(media.Root, "restore_legacy");
        Directory.CreateDirectory(restoreRoot);

        var (svc, host) = await ReopenAsync(media);   // loads (no header) + restores the end-of-tape TOC
        using (svc)
        {
            Assert.Null(svc.LoadedMediaHeader);   // Unidentified — legitimate legacy media

            int setIdx = svc.TOC!.SetIndexToStd(svc.TOC.CapSetIndex(0));
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIdx] = null },
                Incremental:           false,
                TargetDirectory:       restoreRoot,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false,
                EjectWhenDone:         false);

            var result = await svc.ExecuteRestoreAsync(req);
            Assert.True(result.Success, $"legacy restore failed: {svc.LastError}");
            Assert.Equal(src.Files.Count, result.FilesSucceeded);
            AssertNoMediaPrompts(host); // Unidentified media never prompts
        }

        FileComparer.AssertFilesMatch(src.RootPath, src.Files, FindRestoredRoot(restoreRoot, src.RootPath));
    }
}
