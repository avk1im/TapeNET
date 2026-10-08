using System.Text.RegularExpressions;

using TapeLibNET.Agents;
using TapeLibNET.Scan;
using TapeLibNET.Services;
using TapeLibNET.Toc;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Services;


/// <summary>
/// Phase 7 (Design-Format-v2 §6, §8.6): the service side of mixed media — every TOC-writing verb upgrades a legacy TOC
///  to 2.1 and says so exactly once; overwrites mint fresh set identities; a new tape carries only 2.1 records.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a legacy TOC gets onto the service:</b> the cartridge's own TOC is written as a LEGACY <c>.tapetoc</c> by the
///  test-only <see cref="LegacyTocWriter"/> and imported. The service then holds a legacy TOC that describes this very
///  cartridge — same MediaId, same volume — so the import's identity check matches and stays silent.
/// </para>
/// <para>
/// Such a TOC marks the sets it describes as <see cref="TapeDataFormat.Legacy"/>, while their content on tape carries
///  2.1 file headers. Tests here therefore never RESTORE an imported set; they restore only sets written after the
///  import. Restoring genuine legacy content needs the Phase 0 legacy tape images (see the Phase 7 guide).
/// </para>
/// </remarks>
public class ServiceMixedMediaTests : ServiceTestBase
{
    private const string UpgradeMessage = "upgraded to format 2.1";

    #region *** Helpers ***

    /// <summary>Writes the service's current TOC as a LEGACY <c>.tapetoc</c> and imports it.</summary>
    private static async Task ImportAsLegacyAsync(TestTapeService svc, TempVirtualMedia media)
    {
        string path = Path.Combine(media.Root, $"legacy_{Guid.NewGuid():N}{TapeAgentBase.TOCFileExtension}");
        File.WriteAllBytes(path, LegacyTocWriter.TocBytes(svc.TOC!));

        Assert.True(await svc.ImportTOCFromFileAsync(path), $"legacy import failed: {svc.LastError}");
        Assert.True(svc.TOC!.LoadedFromLegacy, "the imported TOC must be flagged legacy");
    }

    /// <summary>How often the upgrade warning was reported.</summary>
    private static int CountUpgradeMessages(TestTapeServiceHost host)
        => Regex.Matches(host.DumpReports(), Regex.Escape(UpgradeMessage)).Count;

    /// <summary>Formats the cartridge and backs up <paramref name="setCount"/> sets, prompt-free.</summary>
    private async Task<List<TempFileTree>> SeedSetsAsync(TempVirtualMedia media, int setCount)
    {
        var trees = new List<TempFileTree>();
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            for (int i = 1; i <= setCount; i++)
            {
                var tree = new TempFileTree(seed: 100 + i);
                tree.AddFiles($"mix{i}", count: 3, minSize: 512, maxSize: 4_096);
                trees.Add(tree);

                var req = MakeBackupRequest(svc, tree.RootPath, $"Set-{i}", append: i > 1)
                    with { ProceedOnMediaMismatch = true };
                var result = await svc.ExecuteBackupAsync(req);
                Assert.True(result.Success, $"seeding set {i} failed: {result.Message}");
            }
            host.MediaMismatchPrompts.Clear();   // suppressed ones are logged, not prompted; belt-and-braces
        }
        return trees;
    }

    private static void DisposeAll(List<TempFileTree> trees)
    {
        foreach (var t in trees)
            t.Dispose();
    }

    /// <summary>Restores one whole set into a fresh folder and compares it with <paramref name="tree"/>.</summary>
    private async Task RestoreSetAndCompareAsync(TempVirtualMedia media, int setIndex, TempFileTree tree)
    {
        string restoreRoot = Path.Combine(media.Root, $"restore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(restoreRoot);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var req = new RestoreRequest(
                Mode:                  RestoreMode.Restore,
                CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
                Incremental:           false,
                TargetDirectory:       restoreRoot,
                RecurseSubdirectories: true,
                HandleExisting:        TapeHowToHandleExisting.Overwrite,
                SkipAllErrors:         false,
                EjectWhenDone:         false);

            var result = await svc.ExecuteRestoreAsync(req);
            Assert.True(result.Success, $"restore of set {setIndex} failed: {result.Message}\n{host.DumpReports()}");
            Assert.Equal(0, result.FilesFailed);
        }

        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(restoreRoot, tree.RootPath));
    }

    #endregion

    #region *** (A) Legacy TOC → 2.1 on the first write, reported once ***

    /// <summary>
    /// Renaming the media writes the TOC — the cheapest upgrade path. One warning on the first write, none on the
    ///  second; the tape reads back as 2.1.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyToc_RenameMedia_UpgradesOnce(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            await ImportAsLegacyAsync(svc, media);

            Assert.True(await svc.RenameMediaAsync("Renamed once"), svc.LastError);
            Assert.False(svc.TOC!.LoadedFromLegacy, "the TOC on tape is 2.1 now");
            Assert.Equal(1, CountUpgradeMessages(host));

            Assert.True(await svc.RenameMediaAsync("Renamed twice"), svc.LastError);
            Assert.Equal(1, CountUpgradeMessages(host));            // only the first write upgrades
        }

        var (check, _) = await ReopenAsync(media);
        using (check)
        {
            Assert.False(check.TOC!.LoadedFromLegacy);
            Assert.Equal("Renamed twice", check.TOC.Description);
        }
    }

    /// <summary>Renaming a set writes the TOC too.</summary>
    [Fact]
    public async Task LegacyToc_RenameSet_Upgrades()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        List<TempFileTree> trees = [];
        try
        {
            trees = await SeedSetsAsync(media, setCount: 1);
            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                await ImportAsLegacyAsync(svc, media);
                Assert.True(await svc.RenameBackupSetAsync(svc.TOC!.LastSetOnVolume, "Renamed set"), svc.LastError);
                Assert.False(svc.TOC.LoadedFromLegacy);
                Assert.Equal(1, CountUpgradeMessages(host));
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Format_LegacyTape_AppendUpgradesToc, service edition: the new set is written in 2.1, the TOC is upgraded with
    ///  one warning, and the new set restores byte for byte.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyToc_AppendBackup_UpgradesAndRestores(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        using var tree = new TempFileTree();
        tree.AddFiles("append", count: 5, minSize: 512, maxSize: 8 * 1024);

        int newSet;
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            await ImportAsLegacyAsync(svc, media);

            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "New set", append: true));
            Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");

            Assert.False(svc.TOC!.LoadedFromLegacy);
            Assert.Equal(1, CountUpgradeMessages(host));
            newSet = svc.TOC.LastSetOnVolume;
            Assert.Equal(TapeDataFormat.V2, svc.TOC[newSet].DataFormat);
            Assert.NotEqual(Guid.Empty, svc.TOC[newSet].SetId);
        }

        await RestoreSetAndCompareAsync(media, newSet, tree);
    }

    /// <summary>
    /// Delete writes the TOC inside the agent, not through <c>SaveTocCore</c> — and must still report the upgrade.
    /// </summary>
    [Fact]
    public async Task LegacyToc_DeleteSets_Upgrades()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        List<TempFileTree> trees = [];
        try
        {
            trees = await SeedSetsAsync(media, setCount: 2);
            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                await ImportAsLegacyAsync(svc, media);

                var result = await svc.DeleteBackupSetsExAsync(svc.TOC!.LastSetOnVolume);
                Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
                Assert.False(svc.TOC.LoadedFromLegacy);
                Assert.Equal(1, CountUpgradeMessages(host));
            }

            var (check, _) = await ReopenAsync(media);
            using (check)
            {
                Assert.Equal(1, check.TOC!.Count);
                Assert.False(check.TOC.LoadedFromLegacy);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A 2.1 TOC never reports an upgrade — the warning means something only if it is silent on the normal path.
    /// </summary>
    [Fact]
    public async Task Toc21_Writes_NeverReportAnUpgrade()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = new TempFileTree();
        tree.AddFiles("plain", count: 3, minSize: 512, maxSize: 2_048);

        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "Plain"))).Success);
            Assert.True(await svc.RenameMediaAsync("Renamed"), svc.LastError);
            Assert.Equal(0, CountUpgradeMessages(host));
        }
    }

    /// <summary>The TOC's format is visible in the TOC summary and in the media listing.</summary>
    [Fact]
    public async Task TocFormat_IsReported_BeforeAndAfterTheUpgrade()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            await ImportAsLegacyAsync(svc, media);
            Assert.True(host.ContainsMessage("legacy (pre-2.1)"), host.DumpReports());

            Assert.True(await svc.RenameMediaAsync("Upgraded"), svc.LastError);
            var list = await svc.ListContentsAsync(new ListRequest(Depth: ListDepth.DriveAndMedia));
            Assert.True(list.Success, list.Message);
            Assert.True(host.ContainsMessage("TOC format: 2.1"), host.DumpReports());
        }
    }

    #endregion

    #region *** (B) Set identity on overwrite (Format_SetId_ContinuationShared_OverwriteFresh, second half) ***

    /// <summary>
    /// Overwriting a set mints a fresh <c>SetId</c>: whatever of the old set remains on tape beyond the new end keeps
    ///  the old id and can never be mistaken for the new set. Earlier sets keep theirs.
    /// </summary>
    [Fact]
    public async Task Overwrite_ReplacedSet_GetsFreshSetId()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var replacement = new TempFileTree(seed: 900);
        replacement.AddFiles("replacement", count: 3, minSize: 512, maxSize: 4_096);
        List<TempFileTree> trees = [];
        try
        {
            trees = await SeedSetsAsync(media, setCount: 3);

            Guid id1, id2, id3;
            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                var toc = svc.TOC!;
                (id1, id2, id3) = (toc[1].SetId, toc[2].SetId, toc[3].SetId);

                var req = MakeBackupRequest(svc, replacement.RootPath, "Replacement", append: true)
                    with
                    {
                        AppendAfterSetIndex    = 1,       // replace set 2, drop set 3
                        ProceedOnMediaMismatch = true,
                    };
                var result = await svc.ExecuteBackupAsync(req);
                Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
            }

            var (check, _) = await ReopenAsync(media);
            using (check)
            {
                var toc = check.TOC!;
                Assert.Equal(2, toc.Count);
                Assert.Equal(id1, toc[1].SetId);
                Assert.NotEqual(Guid.Empty, toc[2].SetId);
                Assert.DoesNotContain(toc[2].SetId, new[] { id1, id2, id3 });
            }

            await RestoreSetAndCompareAsync(media, 2, replacement);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (C) A new tape carries only 2.1 records (Format_NewTape_WritesOnly21) ***

    /// <summary>
    /// A scan of a tape written by this build finds a media header, its sets, and TOC copies that all recover as 2.1 —
    ///  and not a single damaged or unreadable record. A legacy record, or one this build cannot read, would show up
    ///  as a damaged fragment or a legacy-flagged TOC.
    /// </summary>
    [Fact]
    public async Task NewTape_Scan_FindsOnly21Records()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        List<TempFileTree> trees = [];
        try
        {
            trees = await SeedSetsAsync(media, setCount: 2);
            var (svc, host) = await ReopenAsync(media);
            using (svc)
            {
                var result = await svc.ScanMediaAsync(new ScanMediaRequest());
                Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
                MediaScanMap map = result.Map!;

                Assert.Equal(ScannedMediaKind.Backup, map.Kind);
                Assert.Contains(map.Fragments, f => f.Kind == FragmentKind.MediaHeader);
                Assert.Equal(2, result.SetsFound);

                var tocCopies = map.Fragments.Where(f => f.Kind == FragmentKind.TOC).ToList();
                Assert.NotEmpty(tocCopies);
                Assert.All(tocCopies, f =>
                {
                    Assert.NotNull(f.HarvestedToc);
                    Assert.False(f.HarvestedToc!.LoadedFromLegacy, $"TOC copy at block {f.StartBlock} is legacy");
                });

                Assert.DoesNotContain(map.Fragments, f => f.Kind == FragmentKind.Unknown && !f.Diagnosis.Success);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion
}
