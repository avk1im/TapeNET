using TapeLibNET.Agents;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Toc;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Services;

#if TEST_HOOKS
/// <summary>
/// Design-TocLocation, service level: the location ladder end to end, stage by stage, on every drive layout — each test
///  damages the cartridge AT REST (between operations) so that exactly the stage under test succeeds, then asserts what
///  reaches the user: the prompts, the live report, and the closing advice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coverage.</b> S0, S1 and S4 (load, declined, resume, resume declined) here, on all four layouts. S2 and S3 alone
///  stay at agent level (<c>TocLocationLadderTests</c>): their cartridges need junk in front of the copies.
/// </para>
/// <para>
/// <b>The damage that leaves only S4</b> is <see cref="FullWalkDamage"/> — per layout, on the medium alone.
/// </para>
/// <para>
/// <b>Partitions</b>: the ladder is S0, S1, S2 (the whole TOC partition), S4 (the content partition). On a healthy series
///  a TOC never stands in the content partition, so the service-level S4 outcome there is "asked, searched, nothing" —
///  with both findings named. The positive case (a mixed-up cartridge) is <see cref="S4_Load_RecoveredByFullWalk"/>,
///  whose copies the damage writes into the content partition.
/// </para>
/// </remarks>
public class ServiceTocLocationTests : ServiceTestBase
{
    #region *** Data and helpers ***

    private const int MvFileCount = 16;
    private const long MvFileSize = 350L * 1024;
    private const long MvCapacitySet = 36L * 1024 * 1024;
    private const long MvCapacityPartitions = 3L * 1024 * 1024;
    private static readonly TimeSpan NoHang = TimeSpan.FromMinutes(2);

    public static TheoryData<DriveProfile> AllProfiles =>
        [DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly];

    public static TheoryData<DriveProfile> InSetProfiles =>
        [DriveProfile.Setmarks, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly];

    private static TempVirtualMedia NewMedia(DriveProfile profile)
        => new(withInitiator: profile == DriveProfile.Partitions, ContentCapacity, InitiatorCapacity);

    /// <summary>One set, backed up and closed — the cartridge every single-volume test damages.</summary>
    private async Task SeedAsync(TempVirtualMedia media, DriveProfile profile)
    {
        using var src = new TempFileTree();
        src.AddFiles("ladder", count: 4, minSize: 512, maxSize: 4_096);
        var (svc, host) = await OpenAndFormatAsync(media, profile);
        using (svc)
            Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src.RootPath, "Ladder"))).Success,
                host.DumpReports());
    }

    /// <summary>
    /// Opens <paramref name="media"/> alone, applies <paramref name="surgery"/> to its backend, and closes it again — the
    ///  surgery marks the medium dirty, and unloading saves it like any write.
    /// </summary>
    /// <param name="surgery">Gets the backend and the volume's own TOC, read before the damage (for Partitions).</param>
    private async Task DamageAsync(TempVirtualMedia media, DriveProfile profile,
        Action<VirtualTapeDriveBackend, TapeTOC> surgery)
    {
        var (svc, _) = CreateService();
        using (svc)
        {
            Assert.True(await svc.OpenVirtualDriveAsync(CapsFor(media, profile), VmdFor(media), FileMode.Open), svc.LastError);
            Assert.True(await svc.LoadMediaAsync(), svc.LastError);
            Assert.True(await svc.RestoreTOCAsync(), svc.LastError);
            surgery(svc.VirtualBackend!, svc.TOC!);
            await svc.CloseAsync();
        }
    }

    /// <summary>Opens and loads <paramref name="media"/> WITHOUT reading its TOC — the test reads it, answers queued.</summary>
    private async Task<(TestTapeService, TestTapeServiceHost)> OpenUnreadAsync(TempVirtualMedia media, DriveProfile profile)
    {
        var (svc, host) = CreateService();
        Assert.True(await svc.OpenVirtualDriveAsync(CapsFor(media, profile), VmdFor(media), FileMode.Open), svc.LastError);
        Assert.True(await svc.LoadMediaAsync(), svc.LastError);
        return (svc, host);
    }

    /// <summary>The first block of TOC copy 1, corrupted — wherever the layout keeps it.</summary>
    private static void CorruptFirstTocCopy(VirtualTapeDriveBackend backend, DriveProfile profile)
    {
        if (backend.InitiatorMedia is { } toc)
        {
            toc.CorruptBlockAt(0, offset: 0);
            return;
        }
        // Copy 1 is the data block in front of the second-last filemark ([toc1][FM][toc2][FM])
        var media = backend.ContentMedia!;
        var layout = media.Layout.ToList();
        var marks = layout.Where(b => b.IsMark).ToList();
        var toc1 = layout[layout.IndexOf(marks[^2]) - 1];
        Assert.False(toc1.IsMark, media.DescribeLayout());
        media.CorruptBlockAt(toc1.BeginAtBlock, offset: 0);
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

    private static (TempVirtualMedia, TempVirtualMedia) MakeVolumes(DriveProfile profile)
    {
        bool partitions = profile == DriveProfile.Partitions;
        long capacity = partitions ? MvCapacityPartitions : MvCapacitySet;
        return (new TempVirtualMedia(partitions, capacity), new TempVirtualMedia(partitions, capacity));
    }

    private static TempFileTree MakeMvTree()
    {
        var tree = new TempFileTree();
        for (int i = 0; i < MvFileCount; i++)
            tree.AddFile($"mv_loc_{i:D2}.bin", MvFileSize);
        return tree;
    }

    private async Task BackupAcrossTwoVolumesAsync(IReadOnlyList<TempVirtualMedia> volumes, TempFileTree tree,
        DriveProfile profile)
    {
        var (svc, host) = await OpenAndFormatMultiVolumeAsync(volumes, profile);
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
    [MemberData(nameof(AllProfiles))]
    public async Task S0_Sound_ReadsInPlace_Silent(DriveProfile profile)
    {
        using var media = NewMedia(profile);
        await SeedAsync(media, profile);

        var (svc, host) = await OpenUnreadAsync(media, profile);
        using (svc)
        {
            Assert.True(await svc.RestoreTOCAsync(), svc.LastError);
            Assert.Equal(1, svc.TOC!.Count);
            AssertNoMediaPrompts(host);
            Assert.False(host.ContainsMessage("table of contents of"), host.DumpReports());
        }
    }

    #endregion

    #region *** S1: the other copy ***

    /// <summary>Copy 1 damaged at rest: copy 2 reads, no prompt — and the closing advice says to re-save the TOC.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task S1_FirstCopyDamaged_SecondReads_AdvisesResave(DriveProfile profile)
    {
        using var media = NewMedia(profile);
        await SeedAsync(media, profile);
        await DamageAsync(media, profile, (backend, _) => CorruptFirstTocCopy(backend, profile));

        var (svc, host) = await OpenUnreadAsync(media, profile);
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
    /// Damaged so only S4 can find a copy (<see cref="FullWalkDamage"/>; on Partitions the copies stand in the content
    ///  partition): asked once, says Search, the walk recovers the TOC — advised as "found only by searching".
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task S4_Load_RecoveredByFullWalk(DriveProfile profile)
    {
        using var media = NewMedia(profile);
        await SeedAsync(media, profile);
        await DamageAsync(media, profile, (backend, toc) =>
            FullWalkDamage.Apply(profile, backend, damageHeaders: false, ownToc: toc));

        var (svc, host) = await OpenUnreadAsync(media, profile);
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
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task S4_Load_Declined_FailsNamingIt(DriveProfile profile)
    {
        using var media = NewMedia(profile);
        await SeedAsync(media, profile);
        await DamageAsync(media, profile, (backend, toc) =>
            FullWalkDamage.Apply(profile, backend, damageHeaders: false, ownToc: toc));

        var (svc, host) = await OpenUnreadAsync(media, profile);
        using (svc)
        {
            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Abort);
            Assert.False(await svc.RestoreTOCAsync().WaitAsync(NoHang));
            AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
            Assert.Contains("declined", svc.LastError);
        }
    }

    /// <summary>
    /// Partitions, the TOC partition blank and nothing in the content partition (the healthy-series case): asked once,
    ///  searched, nothing — the failure names both findings.
    /// </summary>
    [Fact]
    public async Task S4_Load_Partitions_TocPartitionBlank_AskedOnce_FailsNamingIt()
    {
        const DriveProfile profile = DriveProfile.Partitions;
        using var media = NewMedia(profile);
        await SeedAsync(media, profile);
        await DamageAsync(media, profile, (backend, _) =>
            FullWalkDamage.Apply(profile, backend, damageHeaders: false, ownToc: null));

        var (svc, host) = await OpenUnreadAsync(media, profile);
        using (svc)
        {
            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Proceed);
            Assert.False(await svc.RestoreTOCAsync().WaitAsync(NoHang));
            AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
            Assert.Contains("TOC partition is blank", svc.LastError);
            Assert.Contains("not even by searching the whole medium", svc.LastError);
        }
    }

    #endregion

    #region *** S4: the whole medium, as the resume's last resort ***

    /// <summary>
    /// Volume 1 of a series: its media header, set header and TOC position damaged. Nothing identifies it but its own TOC,
    ///  which only the full walk finds — asked once, for volume #1 — and the whole set restores, byte for byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public async Task S4_Resume_HeadedVolumeDamaged_IdentifiedByFullWalk_RestoresEverything(DriveProfile profile)
    {
        var (vol1, vol2) = MakeVolumes(profile);
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeMvTree();
            await BackupAcrossTwoVolumesAsync(volumes, tree, profile);
            await DamageAsync(vol1, profile, (backend, _) => FullWalkDamage.Apply(profile, backend, damageHeaders: true));

            string restoreDir = Path.Combine(vol1.Root, "restore_full_walk");
            Directory.CreateDirectory(restoreDir);
            var (svc, host) = await ReopenMultiVolumeAsync(volumes, profile);
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

    /// <summary>
    /// The same damage, the search declined (in-set layouts) — or, on Partitions, searched and found nothing: volume 1 is
    ///  rejected, the volume prompt follows (End), naming why.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task S4_Resume_NotIdentified_VolumeRejected(DriveProfile profile)
    {
        bool partitions = profile == DriveProfile.Partitions;
        var (vol1, vol2) = MakeVolumes(profile);
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var tree = MakeMvTree();
            await BackupAcrossTwoVolumesAsync(volumes, tree, profile);
            await DamageAsync(vol1, profile, (backend, _) => FullWalkDamage.Apply(profile, backend, damageHeaders: true));

            string restoreDir = Path.Combine(vol1.Root, "restore_rejected");
            Directory.CreateDirectory(restoreDir);
            var (svc, host) = await ReopenMultiVolumeAsync(volumes, profile);
            using (svc)
            {
                host.MediaMismatchAnswers.Enqueue(partitions ? MediaMismatchChoice.Proceed : MediaMismatchChoice.Abort);
                var result = await svc.ExecuteRestoreAsync(MakeRestoreRequest(svc.TOC!.LastSetOnVolume, restoreDir))
                    .WaitAsync(NoHang);

                Assert.False(result.Success);
                AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
                var prompt = Assert.Single(host.VolumeErrorPrompts);
                host.VolumeErrorPrompts.Clear();
                Assert.Equal(1, prompt.Volume);
                Assert.Contains(partitions ? "not even by searching the whole medium" : "declined", prompt.ErrorMessage);
            }
        }
    }

    #endregion
}
#endif
