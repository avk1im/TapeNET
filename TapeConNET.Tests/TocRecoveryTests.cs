using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;
using TapeLibNET.Scan;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;     // linked TempVirtualMedia, TempFileTree, FileComparer

namespace TapeConNET.Tests;

/// <summary>
/// The TOC recovery cycle through the CLI (tapecon docs media-recovery): a TOC travels between verbs as a file.
///  <c>export-toc</c>, <c>scan-media</c> and <c>recover-toc</c> write one; <c>--import-toc</c> reads it on <c>list</c>,
///  <c>restore</c>, <c>validate</c>, <c>verify</c>, <c>delete-sets</c> and <c>rename-media</c>.
/// </summary>
/// <remarks>
/// The media is healthy here: the cycle must work end to end on any media. Damaged copies, and a later volume's TOC used
///  for an earlier volume, are the service suites' business — the CLI cannot damage a tape nor swap volumes mid-run.
/// </remarks>
public class TocRecoveryTests
{
    #region *** Helpers ***

    private static async Task FormatAsync(TempVirtualMedia media, string mediaDesc = "RecoveryMedia")
        => Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync(media.Verb("format", "--mdesc", mediaDesc, "--yes"))).Exit);

    private static async Task BackupAsync(TempVirtualMedia media, TempFileTree src, string description)
        => Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync(media.Verb("backup", "--description", description, "--subdirs",
                src.RootPath))).Exit);

    /// <summary>Formatted media with one set; the source tree stays for comparison.</summary>
    private static async Task<TempFileTree> MediaWithOneSetAsync(TempVirtualMedia media)
    {
        var src = new TempFileTree();
        src.AddFiles("docs", count: 5, minSize: 512, maxSize: 8 * 1024);
        await FormatAsync(media);
        await BackupAsync(media, src, "Recovery-Set-1");
        return src;
    }

    private static string Folder(TempVirtualMedia media, string name)
    {
        var path = Path.Combine(media.Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> ExportTocAsync(TempVirtualMedia media)
    {
        var file = Path.Combine(media.Root, "first" + TapeServiceBase.TOCFileExtension);
        var r = await TapeConHost.RunAsync(media.Verb("export-toc", file));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.True(File.Exists(file), "export-toc writes the file");
        return file;
    }

    private static string FindRestoredRoot(string restoreRoot, string srcRoot)
    {
        var leaf = Path.GetFileName(srcRoot.TrimEnd('\\', '/'));
        return Directory.EnumerateDirectories(restoreRoot, leaf, SearchOption.AllDirectories).FirstOrDefault()
            ?? restoreRoot;
    }

    #endregion

    #region *** export-toc and --import-toc ***

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportToc_ThenRestoreWithImportToc_RoundTripsBytes(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator);
        using var src = await MediaWithOneSetAsync(media);
        var toc = await ExportTocAsync(media);

        var restoreRoot = Folder(media, "restore");
        var r = await TapeConHost.RunAsync(media.Verb("restore", "--target", restoreRoot, "--subdirs",
            "--existing", "Overwrite", "--import-toc", toc));

        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.True(r.ContainsMessage("TOC imported from file"));
        FileComparer.AssertFilesMatch(src.RootPath, src.Files, FindRestoredRoot(restoreRoot, src.RootPath));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("validate")]
    [InlineData("verify")]
    public async Task ReadingVerbs_TakeImportToc(string verb)
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = await MediaWithOneSetAsync(media);
        var toc = await ExportTocAsync(media);

        var r = await TapeConHost.RunAsync(media.Verb(verb, "--import-toc", toc));

        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.True(r.ContainsMessage("TOC imported from file"));
    }

    [Fact]
    public async Task List_ImportToc_ShowsTheFilesSets()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = await MediaWithOneSetAsync(media);
        var toc = await ExportTocAsync(media);

        var r = await TapeConHost.RunAsync(media.Verb("list", "--sets-only", "--import-toc", toc));

        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.True(r.ContainsMessage("Recovery-Set-1"));
    }

    [Fact]
    public async Task ImportToc_MissingFile_Fails()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = await MediaWithOneSetAsync(media);

        var r = await TapeConHost.RunAsync(media.Verb("list", "--import-toc",
            Path.Combine(media.Root, "nowhere.tapetoc")));

        Assert.Equal(TapeConExitCode.OperationFailed, r.Exit);
    }

    [Fact]
    public async Task Backup_DoesNotTakeImportToc()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = await MediaWithOneSetAsync(media);
        var toc = await ExportTocAsync(media);

        var r = await TapeConHost.RunAsync(media.Verb("backup", "--import-toc", toc, src.RootPath));
        Assert.NotEqual(TapeConExitCode.Ok, r.Exit);
    }

    [Fact]
    public async Task TocVerb_IsGone()
    {
        var r = await TapeConHost.RunAsync("toc", "export", "x.tapetoc");
        Assert.NotEqual(TapeConExitCode.Ok, r.Exit);
    }

    #endregion

    #region *** Repair: writing an imported TOC back ***

    [Fact]
    public async Task RenameMedia_ImportTocWithoutName_ResavesUnderItsName()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = await MediaWithOneSetAsync(media);
        var toc = await ExportTocAsync(media);

        var r = await TapeConHost.RunAsync(media.Verb("rename-media", "--import-toc", toc));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.True(info.ContainsMessage("RecoveryMedia"));
        Assert.True(info.ContainsMessage("Recovery-Set-1"));
    }

    [Fact]
    public async Task RenameMedia_ImportTocWithName_Renames()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = await MediaWithOneSetAsync(media);
        var toc = await ExportTocAsync(media);

        var r = await TapeConHost.RunAsync(media.Verb("rename-media", "--import-toc", toc, "--name", "Repaired"));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.True(info.ContainsMessage("Repaired"));
    }

    [Fact]
    public async Task DeleteSets_ImportToc_DeletesAndSaves()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = await MediaWithOneSetAsync(media);
        await BackupAsync(media, src, "Recovery-Set-2");
        var toc = await ExportTocAsync(media);

        var r = await TapeConHost.RunAsync(media.Verb("delete-sets", "0", "--import-toc", toc));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.True(info.ContainsMessage("Recovery-Set-1"));
        Assert.False(info.ContainsMessage("Recovery-Set-2"));
    }

    #endregion

    #region *** scan-media and recover-toc ***

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanMedia_Export_SavesMapAndRecoveredTocs_UsableByImportToc(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator);
        using var src = await MediaWithOneSetAsync(media);
        var scans = Folder(media, "scans");

        var scan = await TapeConHost.RunAsync(media.Verb("scan-media", "--export", scans));
        Assert.Equal(TapeConExitCode.Ok, scan.Exit);

        Assert.Single(Directory.GetFiles(scans, "*" + MediaScanMap.MapFileExtension));
        var tocs = Directory.GetFiles(scans, "*" + TapeServiceBase.TOCFileExtension);
        Assert.NotEmpty(tocs);                                  // both copies, normally

        foreach (var toc in tocs)
        {
            var list = await TapeConHost.RunAsync(media.Verb("list", "--sets-only", "--import-toc", toc));
            Assert.Equal(TapeConExitCode.Ok, list.Exit);
            Assert.True(list.ContainsMessage("Recovery-Set-1"));
        }
    }

    [Fact]
    public async Task ScanMedia_NoRecoverToc_SavesMapOnly()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = await MediaWithOneSetAsync(media);
        var scans = Folder(media, "scans");

        var scan = await TapeConHost.RunAsync(media.Verb("scan-media", "--no-recover-toc", "--export", scans));

        Assert.Equal(TapeConExitCode.Ok, scan.Exit);
        Assert.Single(Directory.GetFiles(scans, "*" + MediaScanMap.MapFileExtension));
        Assert.Empty(Directory.GetFiles(scans, "*" + TapeServiceBase.TOCFileExtension));
    }

    [Fact]
    public async Task RecoverToc_FromMap_SavesToc_ThenRestoreRoundTrips()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = await MediaWithOneSetAsync(media);
        var scans = Folder(media, "scans");
        Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync(media.Verb("scan-media", "--no-recover-toc", "--export", scans))).Exit);

        var mapPath = Directory.GetFiles(scans, "*" + MediaScanMap.MapFileExtension).Single();
        var map = MediaScanMap.FromJson(File.ReadAllText(mapPath))!;
        var copy = map.Fragments.First(f => f.Kind == FragmentKind.TOC);
        var toc = Path.Combine(media.Root, "recovered" + TapeServiceBase.TOCFileExtension);

        var recover = await TapeConHost.RunAsync(media.Verb("recover-toc",
            "--map", mapPath, "--fragment", copy.Ordinal.ToString(), "--save", toc));
        Assert.Equal(TapeConExitCode.Ok, recover.Exit);
        Assert.True(File.Exists(toc));

        var restoreRoot = Folder(media, "restore");
        var r = await TapeConHost.RunAsync(media.Verb("restore", "--target", restoreRoot, "--subdirs",
            "--existing", "Overwrite", "--import-toc", toc));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        FileComparer.AssertFilesMatch(src.RootPath, src.Files, FindRestoredRoot(restoreRoot, src.RootPath));
    }

    [Fact]
    public async Task RecoverToc_NotATocFragment_Fails()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = await MediaWithOneSetAsync(media);
        var scans = Folder(media, "scans");
        Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync(media.Verb("scan-media", "--no-recover-toc", "--export", scans))).Exit);
        var mapPath = Directory.GetFiles(scans, "*" + MediaScanMap.MapFileExtension).Single();
        var map = MediaScanMap.FromJson(File.ReadAllText(mapPath))!;
        var notToc = map.Fragments.First(f => f.Kind != FragmentKind.TOC);

        var r = await TapeConHost.RunAsync(media.Verb("recover-toc", "--map", mapPath,
            "--fragment", notToc.Ordinal.ToString(), "--save", Path.Combine(media.Root, "x.tapetoc")));

        Assert.Equal(TapeConExitCode.OperationFailed, r.Exit);
    }

    [Theory]
    [InlineData("--map")]
    [InlineData("--fragment")]
    [InlineData("--save")]
    public async Task RecoverToc_EveryOptionIsRequired(string omitted)
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        string[] all = ["--map", "m.tapescan", "--fragment", "1", "--save", "t.tapetoc"];
        int at = Array.IndexOf(all, omitted);
        string[] args = [.. all[..at], .. all[(at + 2)..]];

        var r = await TapeConHost.RunAsync(media.Verb("recover-toc", args));
        Assert.NotEqual(TapeConExitCode.Ok, r.Exit);
    }

    [Fact]
    public async Task RecoverToc_AdoptIsGone()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        var r = await TapeConHost.RunAsync(media.Verb("recover-toc", "--map", "m.tapescan", "--fragment", "1",
            "--save", "t.tapetoc", "--adopt"));
        Assert.NotEqual(TapeConExitCode.Ok, r.Exit);
    }

    #endregion
}
