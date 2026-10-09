using TapeLibNET.Scan;
using TapeLibNET.Services;
using TapeLibNET.Toc;
using TapeLibNET.Agents;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Services;


/// <summary>
/// Phase 7 (Design-Format-v2 §6, §11.5), service side, on a GENUINE legacy cartridge: identify, upgrade on append with one
///  warning, and Scan Media before and after — <c>Format_ScanMedia_BothFamilies</c>.
/// </summary>
/// <remarks>
/// The Phase 0 image is copied into a file-backed <see cref="TempVirtualMedia"/> with the capacities it was written with
///  (<see cref="VirtualTapeFixture.DefaultContentCapacity"/> / <see cref="VirtualTapeFixture.DefaultInitiatorCapacity"/>),
///  then opened by the service like any cartridge.
/// </remarks>
public class ServiceLegacyMediaTests : ServiceTestBase
{
    private const string UpgradeMessage = "upgraded to format 2.1";

    /// <summary>Profile → whether its image carries an initiator partition.</summary>
    public static TheoryData<DriveProfile, bool> Images =>
        new() { { DriveProfile.Setmarks, false }, { DriveProfile.Partitions, true } };

    private static TempVirtualMedia LegacyMedia(DriveProfile profile, bool withInitiator)
    {
        var media = new TempVirtualMedia(withInitiator,
            VirtualTapeFixture.DefaultContentCapacity, VirtualTapeFixture.DefaultInitiatorCapacity);
        LegacyVirtualImages.CopyTo(profile, media);
        return media;
    }

    private static int CountUpgradeMessages(TestTapeServiceHost host)
        => host.FindMessages(UpgradeMessage).Count();

    #region *** Identify ***

    /// <summary>The service reads the legacy TOC and says so — before anything is written.</summary>
    [Theory]
    [MemberData(nameof(Images))]
    public async Task LegacyCartridge_Identifies_AndReportsLegacyToc(DriveProfile profile, bool withInitiator)
    {
        using var media = LegacyMedia(profile, withInitiator);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            Assert.NotNull(svc.TOC);
            Assert.True(svc.TOC!.LoadedFromLegacy);
            Assert.Equal(LegacyVirtualImages.SetDescription, svc.TOC[1].Description);
            Assert.Equal(TapeDataFormat.Legacy, svc.TOC[1].DataFormat);

            Assert.Contains(svc.DescribeMedia(), p => p.Label == "TOC Format" && p.Value.StartsWith("legacy"));
            Assert.Equal(0, CountUpgradeMessages(host));                     // reading upgrades nothing
        }
    }

    #endregion

    #region *** Append: Format_LegacyTape_AppendUpgradesToc (service) ***

    /// <summary>
    /// Appending through the service: one upgrade warning, the TOC on tape is 2.1 afterwards, and both the legacy set and
    ///  the new set restore.
    /// </summary>
    [Theory]
    [MemberData(nameof(Images))]
    public async Task LegacyCartridge_Append_WarnsOnce_AndBothSetsRestore(DriveProfile profile, bool withInitiator)
    {
        using var media = LegacyMedia(profile, withInitiator);
        using var tree = new TempFileTree(seed: 90);
        tree.AddFiles("svcappend", count: 4, minSize: 512, maxSize: 8 * 1024);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "Appended", append: true));
            Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
            Assert.Equal(1, CountUpgradeMessages(host));
        }

        var (check, _) = await ReopenAsync(media);
        using (check)
        {
            Assert.False(check.TOC!.LoadedFromLegacy);
            Assert.Equal(2, check.TOC.Count);
        }

        // The legacy set, through its 12-byte headers
        string oldDir = Path.Combine(media.Root, "restore_legacy");
        Directory.CreateDirectory(oldDir);
        await RestoreSetAsync(media, 1, oldDir);
        LegacyVirtualImages.AssertRestored(oldDir);

        // The new set, through its 2.1 headers
        string newDir = Path.Combine(media.Root, "restore_new");
        Directory.CreateDirectory(newDir);
        await RestoreSetAsync(media, 2, newDir);
        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(newDir, tree.RootPath));
    }

    private async Task RestoreSetAsync(TempVirtualMedia media, int setIndex, string targetDir)
    {
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
                Incremental:           false,
                TargetDirectory:       targetDir,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false,
                EjectWhenDone:         false);
            var result = await svc.ExecuteRestoreAsync(req);
            Assert.True(result.Success, $"restore of set {setIndex} failed: {result.Message}\n{host.DumpReports()}");
            Assert.Equal(0, result.FilesFailed);
        }
    }

    #endregion

    #region *** Format_ScanMedia_BothFamilies ***

    /// <summary>
    /// Scan Media on the untouched legacy cartridge: its TOC copies are recovered and flagged legacy, which makes the
    ///  cartridge positively ours. The legacy set carries no header and no 2.1 frame, so the scan does not count it —
    ///  the recovered TOC lists it. Nothing reads as damaged.
    /// </summary>
    [Theory]
    [MemberData(nameof(Images))]
    public async Task Scan_LegacyCartridge_RecoversLegacyToc_NoDamage(DriveProfile profile, bool withInitiator)
    {
        using var media = LegacyMedia(profile, withInitiator);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var result = await svc.ScanMediaAsync(new ScanMediaRequest());
            Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
            MediaScanMap map = result.Map!;

            Assert.Equal(ScannedMediaKind.Backup, map.Kind);                  // a TOC copy of ours proves it
            Assert.Equal(0, result.SetsFound);                                // legacy content is not identifiable
            var tocs = map.Fragments.Where(f => f.Kind == FragmentKind.TOC).ToList();
            Assert.NotEmpty(tocs);
            Assert.All(tocs, f =>
            {
                Assert.True(f.HarvestedToc?.LoadedFromLegacy, $"TOC copy at {f.StartBlock} not legacy");
                Assert.Equal(1, f.HarvestedToc!.Count);
            });
            Assert.DoesNotContain(map.Fragments, f => f.Kind == FragmentKind.Unknown && !f.Diagnosis.Success);
            Assert.Contains("per the recovered table of contents", result.Summary);
        }
    }

    /// <summary>
    /// Scan Media after a 2.1 append — one tape, both families: the 2.1 set is identified by its first file's header
    ///  frame (with the SetId the TOC records), the legacy set is not, the TOC copies are 2.1 now and list both.
    /// </summary>
    [Theory]
    [MemberData(nameof(Images))]
    public async Task Scan_MixedCartridge_FindsBothSets_Toc21_NoDamage(DriveProfile profile, bool withInitiator)
    {
        using var media = LegacyMedia(profile, withInitiator);
        using var tree = new TempFileTree(seed: 91);
        tree.AddFiles("mix", count: 3, minSize: 512, maxSize: 4 * 1024);

        Guid newSetId;
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "2.1 set", append: true))).Success,
                host.DumpReports());
            newSetId = svc.TOC![2].SetId;
        }

        var (scan, scanHost) = await ReopenAsync(media);
        using (scan)
        {
            var result = await scan.ScanMediaAsync(new ScanMediaRequest());
            Assert.True(result.Success, $"{result.Message}\n{scanHost.DumpReports()}");
            MediaScanMap map = result.Map!;

            Assert.Equal(ScannedMediaKind.Backup, map.Kind);
            var content = Assert.Single(map.Fragments, f => f.Kind == FragmentKind.SetContent);
            Assert.Equal(newSetId, content.Id);
            Assert.Equal(1, result.SetsFound);

            var tocs = map.Fragments.Where(f => f.Kind == FragmentKind.TOC).ToList();
            Assert.NotEmpty(tocs);
            Assert.All(tocs, f =>
            {
                Assert.False(f.HarvestedToc?.LoadedFromLegacy ?? true, $"TOC copy at {f.StartBlock} should be 2.1");
                Assert.Equal(2, f.HarvestedToc!.Count);
            });
            Assert.DoesNotContain(map.Fragments, f => f.Kind == FragmentKind.Unknown && !f.Diagnosis.Success);
        }
    }

    #endregion
}
