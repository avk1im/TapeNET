using TapeLibNET.Agents;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Toc;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Services;

#if TEST_HOOKS
/// <summary>
/// Design-TocLocation, service level: the location ladder end to end, stage by stage — each test damages the cartridge AT
///  REST (tape surgery between operations) so that exactly one stage succeeds, then asserts what reaches the user: the
///  prompts, the live report, and the closing advice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coverage by stage.</b> S0, S1 and S4 (load, declined, resume) here. S2 and S3 stay at agent level
///  (<c>TocLocationLadderTests</c>): reproducing them needs junk WRITTEN behind or in front of the copies, which tape surgery
///  (cut, remove a mark, corrupt a block) cannot do on the service's Setmarks / Partitions layouts, and the service exposes
///  no raw writes.
/// </para>
/// <para>
/// <b>Damage that leaves only S4.</b> On the Setmarks layout, removing the volume's last setmark defeats S0 (one setmark back
///  from end-of-data), S3 (everything behind the mark moves one block earlier) and S2 (no setmark anchor); the walk from BOM
///  still meets TOC copy 2.
/// </para>
/// </remarks>
public class ServiceTocLocationTests : ServiceTestBase
{
    #region *** Data and helpers ***

    private const int MvFileCount = 16;
    private const long MvFileSize = 350L * 1024;
    private const long MvCapacity = 36L * 1024 * 1024;
    private static readonly TimeSpan NoHang = TimeSpan.FromMinutes(2);

    private static VirtualTapeDriveCapabilities CapsOf(TempVirtualMedia media) => media.HasInitiator
        ? VirtualTapeDriveCapabilities.WithPartitions
        : VirtualTapeDriveCapabilities.WithSetmarks;

    private static VirtualMediaDescriptor VmdOf(TempVirtualMedia media) => new(media.ContentPath, media.ContentCapacity,
        media.InitiatorPath, media.HasInitiator ? media.InitiatorCapacity : 0);

    /// <summary>One set, backed up and closed — the cartridge every single-volume test damages.</summary>
    private async Task SeedAsync(TempVirtualMedia media)
    {
        using var src = new TempFileTree();
        src.AddFiles("ladder", count: 4, minSize: 512, maxSize: 4_096);
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
            Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src.RootPath, "Ladder"))).Success,
                host.DumpReports());
    }

    /// <summary>Opens <paramref name="media"/> alone, applies <paramref name="surgery"/> to its backend, and closes it again.</summary>
    /// <remarks>The surgery marks the medium dirty; closing the drive saves it like any write (assumed — see the instructions).</remarks>
    private async Task DamageAsync(TempVirtualMedia media, Action<VirtualTapeDriveBackend> surgery)
    {
        var (svc, _) = CreateService();
        using (svc)
        {
            Assert.True(await svc.OpenVirtualDriveAsync(CapsOf(media), VmdOf(media), FileMode.Open), svc.LastError);
            Assert.True(await svc.LoadMediaAsync(), svc.LastError);
            surgery(svc.VirtualBackend!);
            await svc.CloseAsync();
        }
    }

    /// <summary>Opens and loads <paramref name="media"/> WITHOUT reading its TOC — the test reads it, answers queued.</summary>
    private async Task<(TestTapeService, TestTapeServiceHost)> OpenUnreadAsync(TempVirtualMedia media)
    {
        var (svc, host) = CreateService();
        Assert.True(await svc.OpenVirtualDriveAsync(CapsOf(media), VmdOf(media), FileMode.Open), svc.LastError);
        Assert.True(await svc.LoadMediaAsync(), svc.LastError);
        return (svc, host);
    }

    /// <summary>The first block of TOC copy 1: just past the last setmark (Setmarks), or block 0 of the TOC partition.</summary>
    private static void CorruptFirstTocCopy(VirtualTapeDriveBackend backend)
    {
        if (backend.InitiatorMedia is { } toc)
        {
            toc.CorruptBlockAt(0, offset: 0);
            return;
        }
        var media = backend.ContentMedia!;
        var lastSetmark = media.Layout.Last(b => b.IsMark && b.MarkType == TapeMarkType.Setmark);
        media.CorruptBlockAt(lastSetmark.BeginAtBlock + 1, offset: 0);
    }

    /// <summary>Removes the last setmark (Setmarks layout): only the full-media walk still finds a copy.</summary>
    private static void RemoveLastSetmark(VirtualTapeDriveBackend backend)
    {
        var media = backend.ContentMedia!;
        media.RemoveMarkAt(media.Layout.Last(b => b.IsMark && b.MarkType == TapeMarkType.Setmark).BeginAtBlock);
    }

    /// <summary>The media header and the set header corrupted, the last setmark removed: identifiable by S4 alone.</summary>
    private static void DamageAllButFullWalk(VirtualTapeDriveBackend backend)
    {
        var media = backend.ContentMedia!;
        var layout = media.Layout;
        int firstMark = layout.ToList().FindIndex(b => b.IsMark);
        Assert.True(firstMark >= 0 && !layout[firstMark + 1].IsMark, media.DescribeLayout());
        media.CorruptBlockAt(0, offset: 0);
        media.CorruptBlockAt(layout[firstMark + 1].BeginAtBlock, offset: 0);
        RemoveLastSetmark(backend);
    }

    private static RestoreRequest MakeRestoreRequest(int setIndex, string targetDir) => new(
        Mode: RestoreMode.Restore,
        CheckedFilesBySet: new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
        Incremental: false,
        TargetDirectory: targetDir,
        RecurseSubdirectories: true,
        HandleExisting: TapeHowToHandleExisting.Overwrite,
        SkipAllErrors: false,
        EjectWhenDone: false);

    private static (TempVirtualMedia, TempVirtualMedia) MakeVolumes()
        => (new TempVirtualMedia(withInitiator: false, MvCapacity), new TempVirtualMedia(withInitiator: false, MvCapacity));

    private static TempFileTree MakeMvTree()
    {
        var tree = new TempFileTree();
        for (int i = 0; i < MvFileCount; i++)
            tree.AddFile($"mv_loc_{i:D2}.bin", MvFileSize);
        return tree;
    }

    private async Task BackupAcrossTwoVolumesAsync(IReadOnlyList<TempVirtualMedia> volumes, TempFileTree tree)
    {
        var (svc, host) = await OpenAndFormatMultiVolumeAsync(volumes);
        using (svc)
        {
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "MV-Ladder")).WaitAsync(NoHang);
            Assert.True(result.Success, host.DumpReports());
            Assert.True(host.VolumesInserted >= 1, "the backup must span two volumes");
        }
    }

    #endregion

    #region *** S0: where the layout keeps it ***

    /// <summary>A sound cartridge: the TOC reads in place — no prompt, no anomaly, no advice.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S0_Sound_ReadsInPlace_Silent(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        await SeedAsync(media);

        var (svc, host) = await OpenUnreadAsync(media);
        using (svc)
        {
            Assert.True(await svc.RestoreTOCAsync(), svc.LastError);
            Assert.Equal(1, svc.TOC!.Count);
            AssertNoMediaPrompts(host);
            Assert.False(host.ContainsMessage("table of contents of"), host.DumpReports());
            Assert.False(host.ContainsMessage("TOC copy"), host.DumpReports());
        }
    }

    #endregion

    #region *** S1: the other copy ***

    /// <summary>Copy 1 damaged at rest: copy 2 reads, no prompt — and the closing advice says to re-save the TOC.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S1_FirstCopyDamaged_SecondReads_AdvisesResave(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        await SeedAsync(media);
        await DamageAsync(media, CorruptFirstTocCopy);

        var (svc, host) = await OpenUnreadAsync(media);
        using (svc)
        {
            Assert.True(await svc.RestoreTOCAsync(), svc.LastError);
            Assert.Equal(1, svc.TOC!.Count);
            AssertNoMediaPrompts(host);
            Assert.True(host.ContainsMessage("is damaged"), host.DumpReports());
        }
    }

    #endregion

    #region *** S4: the whole medium, on load ***

    /// <summary>
    /// The last setmark gone: S0–S2 find nothing; the user is asked once (<c>FullWalkForTOC</c>), says Search, and the
    ///  walk recovers the TOC — advised as "found only by searching".
    /// </summary>
    [Fact]
    public async Task S4_Load_TocBehindLostSetmark_AskedOnce_Recovered()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        await SeedAsync(media);
        await DamageAsync(media, RemoveLastSetmark);

        var (svc, host) = await OpenUnreadAsync(media);
        using (svc)
        {
            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Proceed);
            Assert.True(await svc.RestoreTOCAsync().WaitAsync(NoHang), $"{svc.LastError}\n{host.DumpReports()}");
            Assert.Equal(1, svc.TOC!.Count);
            AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
            Assert.True(host.ContainsMessage("found only by searching"), host.DumpReports());
        }
    }

    /// <summary>The same cartridge, the search declined: no TOC, and the failure says why.</summary>
    [Fact]
    public async Task S4_Load_Declined_FailsNamingIt()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        await SeedAsync(media);
        await DamageAsync(media, RemoveLastSetmark);

        var (svc, host) = await OpenUnreadAsync(media);
        using (svc)
        {
            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Abort);
            Assert.False(await svc.RestoreTOCAsync().WaitAsync(NoHang));
            AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
            Assert.Contains("declined", svc.LastError);
        }
    }

    #endregion

    #region *** S4: the whole medium, as the resume's last resort ***

    /// <summary>
    /// Volume 1 of a series: its media header, set header and TOC position damaged. Nothing identifies it but its own TOC,
    ///  which only the full walk finds — asked once, for volume #1 — and the whole set restores, byte for byte.
    /// </summary>
    [Fact]
    public async Task S4_Resume_HeadedVolumeDamaged_IdentifiedByFullWalk_RestoresEverything()
    {
        var (vol1, vol2) = MakeVolumes();
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeMvTree();
            await BackupAcrossTwoVolumesAsync(volumes, tree);
            await DamageAsync(vol1, DamageAllButFullWalk);

            string restoreDir = Path.Combine(vol1.Root, "restore_full_walk");
            Directory.CreateDirectory(restoreDir);
            var (svc, host) = await ReopenMultiVolumeAsync(volumes);
            using (svc)
            {
                var result = await svc.ExecuteRestoreAsync(MakeRestoreRequest(svc.TOC!.LastSetOnVolume, restoreDir))
                    .WaitAsync(NoHang);

                Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
                Assert.Equal(MvFileCount, result.FilesSucceeded);
                Assert.Empty(host.VolumeErrorPrompts);
                AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
                Assert.Contains(result.TocAnomalies, a => a is { Volume: 1, Stage: TapeTocAnomalyStage.FullWalk });
                Assert.True(host.ContainsMessage("found only by searching"), host.DumpReports());
                FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(restoreDir, tree.RootPath));
            }
        }
    }

    /// <summary>The same series, the search declined: volume 1 is rejected — the volume prompt follows (End by default).</summary>
    [Fact]
    public async Task S4_Resume_Declined_VolumeRejected()
    {
        var (vol1, vol2) = MakeVolumes();
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeMvTree();
            await BackupAcrossTwoVolumesAsync(volumes, tree);
            await DamageAsync(vol1, DamageAllButFullWalk);

            string restoreDir = Path.Combine(vol1.Root, "restore_declined");
            Directory.CreateDirectory(restoreDir);
            var (svc, host) = await ReopenMultiVolumeAsync(volumes);
            using (svc)
            {
                host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Abort);
                var result = await svc.ExecuteRestoreAsync(MakeRestoreRequest(svc.TOC!.LastSetOnVolume, restoreDir))
                    .WaitAsync(NoHang);

                Assert.False(result.Success);
                AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
                var prompt = Assert.Single(host.VolumeErrorPrompts);
                host.VolumeErrorPrompts.Clear();
                Assert.Equal(1, prompt.Volume);
                Assert.Contains("declined", prompt.ErrorMessage);
            }
        }
    }

    #endregion
}
#endif
