using TapeLibNET.Tests.Helpers; // linked TempFileTree, FileComparer, TempVirtualMedia

using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;

namespace TapeConNET.Tests;

/// <summary>
/// Phase 7A — CLI-level stress and end-to-end coverage of the
/// <c>tapecon</c> verb tree against a file-backed virtual drive. Exercises
/// scenarios beyond the basic single-file round-trip:
/// <list type="bullet">
///   <item>Large, deeply-nested file trees (hundreds of files).</item>
///   <item>Edge-case file names, sizes, and attributes.</item>
///   <item>Three-wave incremental chain (full → incremental → incremental)
///         with full-restore-from-latest verification.</item>
///   <item>Selective restore with FCL filters at restore time.</item>
///   <item>Validate / verify on an incremental chain.</item>
/// </list>
/// <para>
/// <b>Multi-volume coverage</b> intentionally lives in
/// <c>TapeLibNET.Tests.Services.ServiceMultiVolumeTests</c>, which uses
/// <c>MultiVolumeTapeServiceHost</c> to drive in-process volume swaps.
/// The CLI host (<see cref="TapeConNET.Ux.ConsoleUxServiceHost"/>) only
/// asks <c>OnInsertNewMediaConfirm</c> to confirm/abort and cannot supply
/// a fresh virtual media path mid-run, so that scenario is not testable
/// through the CLI surface alone.
/// </para>
/// </summary>
public class StressRoundTripTests
{
    private static async Task FormatAsync(TempVirtualMedia media, string mediaName = "StressMedia")
    {
        var fmt = await TapeConHost.RunAsync(
            media.Verb("format", "--name", mediaName, "--yes"));
        Assert.Equal(TapeConExitCode.Ok, fmt.Exit);
    }

    private static string MakeRestoreDir(TempVirtualMedia media, string subDir)
    {
        var root = Path.Combine(media.Root, subDir);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Walks <paramref name="restoreRoot"/> and locates the directory whose
    /// last segment matches the leaf of <paramref name="srcRoot"/>. Tape
    /// restores prepend the volume identifier to the path.
    /// </summary>
    private static string FindRestoredRoot(string restoreRoot, string srcRoot)
    {
        var leaf = Path.GetFileName(srcRoot.TrimEnd('\\', '/'));
        var match = Directory.EnumerateDirectories(restoreRoot, leaf, SearchOption.AllDirectories)
            .FirstOrDefault();
        return match ?? restoreRoot;
    }

    // ─── Large / nested tree ─────────────────────────────────────────────────

    /// <summary>
    /// Backs up ~250 files spread across nested subdirectories plus the
    /// edge-case set (zero-byte, block-boundary, special names, attributes),
    /// then restores them and compares byte-for-byte.
    /// </summary>
    [Fact]
    public async Task LargeNestedTree_RoundTrip_PreservesAllFilesByteForByte()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src   = new TempFileTree();

        // ~250 small/medium files across several subdirs
        src.AddFiles("docs",       count: 80, minSize: 256,  maxSize: 8 * 1024);
        src.AddFiles("src/lib",    count: 60, minSize: 64,   maxSize: 16 * 1024);
        src.AddFiles("src/app",    count: 40, minSize: 128,  maxSize: 32 * 1024);
        src.AddFiles("data/raw",   count: 50, minSize: 1024, maxSize: 64 * 1024);
        // A few deeply-nested files to verify path reconstruction
        src.AddFile("a/b/c/d/e/f/g/deep.txt", 4096);
        src.AddFile("a/b/c/d/e/f/g/deeper.bin", 8192);
        // Edge cases (zero-byte, block boundaries, special names, attributes)
        src.AddEdgeCases(blockSize: 16 * 1024);

        await FormatAsync(media);

        var b = await TapeConHost.RunAsync(
            media.Verb("backup",
                "--description", "LargeTree",
                "--subdirs",
                src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b.Exit);

        var restoreRoot = MakeRestoreDir(media, "restore");
        var r = await TapeConHost.RunAsync(
            media.Verb("restore",
                "--target", restoreRoot,
                "--subdirs",
                "--existing", "Overwrite",
                "--skip-errors"));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);

        FileComparer.AssertFilesMatch(
            src.RootPath, src.Files, FindRestoredRoot(restoreRoot, src.RootPath));
    }

    // ─── Incremental chain ───────────────────────────────────────────────────

    /// <summary>
    /// Three-wave incremental chain via the CLI:
    /// wave 0 = full backup of 15 files; wave 1 = incremental after 5 file
    /// modifications; wave 2 = incremental after 3 modifications + 2 new files.
    /// Then performs an incremental restore from the latest set and verifies
    /// that all 17 files come back at their final-version content.
    /// </summary>
    [Fact]
    public async Task IncrementalChain_ThreeWaves_RestoreFromLatest_RoundTripsLatestVersions()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src   = new TempFileTree();

        // ── Wave 0: 15 files in three subdirs ───────────────────────────────
        var wave0Files = new List<string>();
        wave0Files.AddRange(src.AddFiles("docs",     count: 5, minSize: 256, maxSize: 4096));
        wave0Files.AddRange(src.AddFiles("src/code", count: 5, minSize: 256, maxSize: 4096));
        wave0Files.AddRange(src.AddFiles("data",     count: 5, minSize: 256, maxSize: 4096));

        await FormatAsync(media);

        var bw0 = await TapeConHost.RunAsync(
            media.Verb("backup",
                "--description", "Wave0-Full",
                "--subdirs",
                src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, bw0.Exit);

        // ── Wave 1: modify 5 files, then incremental ────────────────────────
        for (int i = 0; i < 5; i++)
            src.ModifyFile(wave0Files[i], version: 1);

        var bw1 = await TapeConHost.RunAsync(
            media.Verb("backup",
                "--description", "Wave1-Incr",
                "--incremental",
                "--subdirs",
                src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, bw1.Exit);

        // ── Wave 2: modify 3 different files + add 2 new files ──────────────
        for (int i = 5; i < 8; i++)
            src.ModifyFile(wave0Files[i], version: 2);
        src.AddFile("docs/new_extra_1.txt", 1500);
        src.AddFile("data/new_extra_2.bin", 2500);

        var bw2 = await TapeConHost.RunAsync(
            media.Verb("backup",
                "--description", "Wave2-Incr",
                "--incremental",
                "--subdirs",
                src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, bw2.Exit);

        // ── Restore (incremental, latest set) ───────────────────────────────
        var restoreRoot = MakeRestoreDir(media, "restore-latest");
        var r = await TapeConHost.RunAsync(
            media.Verb("restore",
                "--target", restoreRoot,
                "--subdirs",
                "--existing", "Overwrite"));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);

        // 17 files should come back at their latest-version content
        FileComparer.AssertFilesMatch(
            src.RootPath, src.Files, FindRestoredRoot(restoreRoot, src.RootPath));
    }

    /// <summary>
    /// Restoring an *intermediate* set in the incremental chain without the
    /// <c>--incremental</c> roll-up should restore only the files actually
    /// written to that one set (i.e. exactly the wave-1 deltas).
    /// </summary>
    [Fact]
    public async Task IncrementalChain_RestoreSingleSet_NonIncremental_RestoresOnlyDelta()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src   = new TempFileTree();

        var allFiles = new List<string>();
        allFiles.AddRange(src.AddFiles("a", count: 6, minSize: 256, maxSize: 1024));
        allFiles.AddRange(src.AddFiles("b", count: 6, minSize: 256, maxSize: 1024));

        await FormatAsync(media);

        // Full
        var b0 = await TapeConHost.RunAsync(
            media.Verb("backup", "--description", "Full", "--subdirs", src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b0.Exit);

        // Incremental — change 3 files
        for (int i = 0; i < 3; i++)
            src.ModifyFile(allFiles[i], version: 1);

        var b1 = await TapeConHost.RunAsync(
            media.Verb("backup", "--description", "Delta",
                "--incremental", "--subdirs", src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b1.Exit);

        // Restore set 0 (latest = the incremental) but force-disable
        //  the incremental roll-up so only delta files come back.
        var restoreRoot = MakeRestoreDir(media, "restore-delta-only");
        var r = await TapeConHost.RunAsync(
            media.Verb("restore",
                "0",
                "--target", restoreRoot,
                "--subdirs",
                "--existing", "Overwrite",
                "--incremental", "false"));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);

        var restored = Directory.EnumerateFiles(restoreRoot, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(3, restored.Count);
    }

    // ─── Selective restore with FCL ──────────────────────────────────────────

    /// <summary>
    /// After a single backup of mixed extensions, restoring with an FCL
    /// extension filter should produce only files matching that extension.
    /// </summary>
    [Fact]
    public async Task SelectiveRestore_WithFclExtensionFilter_RestoresMatchingOnly()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src   = new TempFileTree();

        src.AddFiles("mix", count: 12, minSize: 64, maxSize: 1024,
            extensions: [".txt", ".bin", ".log"]);

        await FormatAsync(media);

        var b = await TapeConHost.RunAsync(
            media.Verb("backup", "--description", "Mixed", "--subdirs", src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b.Exit);

        var restoreRoot = MakeRestoreDir(media, "restore-txt");
        var r = await TapeConHost.RunAsync(
            media.Verb("restore",
                "--target", restoreRoot,
                "--subdirs",
                "--existing", "Overwrite",
                "--filter", "extension == \".txt\""));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);

        var restored = Directory.EnumerateFiles(restoreRoot, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(restored);
        Assert.All(restored, p => Assert.EndsWith(".txt", p, StringComparison.OrdinalIgnoreCase));
    }

    // ─── Validate / verify on incremental chain ──────────────────────────────

    [Fact]
    public async Task ValidateAndVerify_OnIncrementalChain_Pass()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src   = new TempFileTree();

        var files = src.AddFiles("vv", count: 8, minSize: 256, maxSize: 4096);

        await FormatAsync(media);

        var b0 = await TapeConHost.RunAsync(
            media.Verb("backup", "--description", "Full", "--subdirs", src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b0.Exit);

        for (int i = 0; i < 3; i++)
            src.ModifyFile(files[i], version: 1);

        var b1 = await TapeConHost.RunAsync(
            media.Verb("backup", "--description", "Incr",
                "--incremental", "--subdirs", src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b1.Exit);

        var v = await TapeConHost.RunAsync(media.Verb("validate"));
        Assert.Equal(TapeConExitCode.Ok, v.Exit);

        var w = await TapeConHost.RunAsync(media.Verb("verify"));
        Assert.Equal(TapeConExitCode.Ok, w.Exit);
    }
}
