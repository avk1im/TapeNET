using TapeLibNET.Tests.Helpers;

using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;

namespace TapeConNET.Tests;

/// <summary>
/// End-to-end tests for Phase 5 — FCL (File Conditions Language) integration:
/// <list type="bullet">
///   <item>Explicit <c>--filter</c> / <c>--filter-file</c> options on
///         <c>backup</c>, <c>restore</c>, <c>validate</c>, <c>verify</c>,
///         and <c>list</c>.</item>
///   <item>Auto-detection of bare positional arguments per the
///         architecture order (FCL file → inline FCL → path → wildcard).</item>
/// </list>
/// </summary>
public class FclFilterTests
{
    private static async Task FormatAsync(TempVirtualMedia media, string mediaName = "FclMedia")
    {
        var fmt = await TapeConHost.RunAsync(
            media.Verb("format", "--name", mediaName, "--yes"));
        Assert.Equal(TapeConExitCode.Ok, fmt.Exit);
    }

    /// <summary>
    /// Sets up media with one backup set containing a mix of <c>.txt</c>,
    /// <c>.bin</c>, and <c>.log</c> files used by the read-side filter tests.
    /// </summary>
    private static async Task<(TempVirtualMedia media, TempFileTree src, string txtPath, string binPath, string logPath)>
        SetupMixedSetAsync()
    {
        var src = new TempFileTree();
        var txt = src.AddFile("docs/keep.txt", 512);
        var bin = src.AddFile("docs/skip.bin", 4096);
        var log = src.AddFile("logs/today.log", 1024);

        var media = new TempVirtualMedia(withInitiator: true);
        try
        {
            await FormatAsync(media);
            var b = await TapeConHost.RunAsync(
                media.Verb("backup", "--description", "FCL-Set", "--subdirs", src.RootPath));
            Assert.Equal(TapeConExitCode.Ok, b.Exit);
            return (media, src, txt, bin, log);
        }
        catch
        {
            media.Dispose();
            src.Dispose();
            throw;
        }
    }

    // ─── List ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_WithExplicitFilterOption_AppliesFcl()
    {
        var (media, src, _, _, _) = await SetupMixedSetAsync();
        using (media) using (src)
        {
            var r = await TapeConHost.RunAsync(
                media.Verb("list", "--filter", "extension == \".txt\""));
            Assert.Equal(TapeConExitCode.Ok, r.Exit);
            Assert.Contains(r.Entries, e => e.Message.Contains("keep.txt"));
            Assert.DoesNotContain(r.Entries, e => e.Message.Contains("skip.bin"));
            Assert.DoesNotContain(r.Entries, e => e.Message.Contains("today.log"));
        }
    }

    [Fact]
    public async Task List_WithBareInlineFcl_IsAutoDetected()
    {
        var (media, src, _, _, _) = await SetupMixedSetAsync();
        using (media) using (src)
        {
            // Bare positional FCL string — recognized by the leading `name`
            //  keyword (architecture §4 auto-detect order #2).
            var r = await TapeConHost.RunAsync(
                media.Verb("list", "0", "name matches \"*.log\""));
            Assert.Equal(TapeConExitCode.Ok, r.Exit);
            Assert.Contains(r.Entries, e => e.Message.Contains("today.log"));
            Assert.DoesNotContain(r.Entries, e => e.Message.Contains("keep.txt"));
            Assert.DoesNotContain(r.Entries, e => e.Message.Contains("skip.bin"));
        }
    }

    [Fact]
    public async Task List_WithFilterFile_LoadsFromDisk()
    {
        var (media, src, _, _, _) = await SetupMixedSetAsync();
        using (media) using (src)
        {
            var fclPath = Path.Combine(media.Root, "only-bin.fcl");
            File.WriteAllText(fclPath, "extension == \".bin\"");

            var r = await TapeConHost.RunAsync(
                media.Verb("list", "--filter-file", fclPath));
            Assert.Equal(TapeConExitCode.Ok, r.Exit);
            Assert.Contains(r.Entries, e => e.Message.Contains("skip.bin"));
            Assert.DoesNotContain(r.Entries, e => e.Message.Contains("keep.txt"));
        }
    }

    [Fact]
    public async Task List_WithBareFclFilePath_IsAutoDetected()
    {
        var (media, src, _, _, _) = await SetupMixedSetAsync();
        using (media) using (src)
        {
            // Architecture §4 auto-detect order #1: existing .fcl file path
            //  passed as a bare positional argument is loaded as FCL.
            var fclPath = Path.Combine(media.Root, "only-txt.fcl");
            File.WriteAllText(fclPath, "extension == \".txt\"");

            var r = await TapeConHost.RunAsync(media.Verb("list", "0", fclPath));
            Assert.Equal(TapeConExitCode.Ok, r.Exit);
            Assert.Contains(r.Entries, e => e.Message.Contains("keep.txt"));
            Assert.DoesNotContain(r.Entries, e => e.Message.Contains("skip.bin"));
        }
    }

    [Fact]
    public async Task List_WithSizeCondition_FiltersBySizeBytes()
    {
        var (media, src, _, _, _) = await SetupMixedSetAsync();
        using (media) using (src)
        {
            // Files: keep.txt=512, today.log=1024, skip.bin=4096.
            //  Filter expects only those above 1024 bytes ⇒ skip.bin only.
            var r = await TapeConHost.RunAsync(
                media.Verb("list", "--filter", "size > 2KB"));
            Assert.Equal(TapeConExitCode.Ok, r.Exit);
            Assert.Contains(r.Entries, e => e.Message.Contains("skip.bin"));
            Assert.DoesNotContain(r.Entries, e => e.Message.Contains("keep.txt"));
            Assert.DoesNotContain(r.Entries, e => e.Message.Contains("today.log"));
        }
    }

    // ─── Validate / Verify ──────────────────────────────────────────────

    [Fact]
    public async Task Validate_WithFilter_OnlyChecksMatchingFiles()
    {
        var (media, src, _, _, _) = await SetupMixedSetAsync();
        using (media) using (src)
        {
            var r = await TapeConHost.RunAsync(
                media.Verb("validate", "--filter", "extension == \".txt\""));
            Assert.Equal(TapeConExitCode.Ok, r.Exit);
            // Validate's per-set summary line announces the file count being
            //  processed; with a single .txt file we expect exactly 1.
            Assert.Contains(r.Entries, e =>
                e.Message.Contains("1 file", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Verify_WithBareFclFilter_PassesAgainstUnchangedFiles()
    {
        var (media, src, _, _, _) = await SetupMixedSetAsync();
        using (media) using (src)
        {
            var r = await TapeConHost.RunAsync(
                media.Verb("verify", "0", "name matches \"*.txt\""));
            Assert.Equal(TapeConExitCode.Ok, r.Exit);
        }
    }

    // ─── Restore ────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_WithFilter_RestoresOnlyMatchingFiles()
    {
        var (media, src, _, _, _) = await SetupMixedSetAsync();
        using (media) using (src)
        {
            var target = Path.Combine(media.Root, "restore-txt-only");
            Directory.CreateDirectory(target);

            var r = await TapeConHost.RunAsync(
                media.Verb("restore", "0",
                    "--target", target,
                    "--subdirs",
                    "--existing", "Overwrite",
                    "--filter", "extension == \".txt\""));
            Assert.Equal(TapeConExitCode.Ok, r.Exit);

            var restored = Directory
                .EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();
            Assert.Contains("keep.txt", restored);
            Assert.DoesNotContain("skip.bin", restored);
            Assert.DoesNotContain("today.log", restored);
        }
    }

    [Fact]
    public async Task Restore_WithBareWildcardArg_IsAutoDetected()
    {
        var (media, src, _, _, _) = await SetupMixedSetAsync();
        using (media) using (src)
        {
            var target = Path.Combine(media.Root, "restore-bin-only");
            Directory.CreateDirectory(target);

            // Bare positional after `set` — wildcard pattern path
            //  (architecture §4 auto-detect order #4).
            var r = await TapeConHost.RunAsync(
                media.Verb("restore", "0", "*.bin",
                    "--target", target,
                    "--subdirs",
                    "--existing", "Overwrite"));
            Assert.Equal(TapeConExitCode.Ok, r.Exit);

            var restored = Directory
                .EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();
            Assert.Contains("skip.bin", restored);
            Assert.DoesNotContain("keep.txt", restored);
        }
    }

    // ─── Backup ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Backup_WithFilter_OnlyMatchingFilesAreWritten()
    {
        using var src = new TempFileTree();
        src.AddFile("docs/included.txt", 512);
        src.AddFile("docs/excluded.bin", 4096);
        src.AddFile("docs/note.log", 256);

        using var media = new TempVirtualMedia(withInitiator: true);
        await FormatAsync(media);

        var b = await TapeConHost.RunAsync(
            media.Verb("backup",
                "--description", "FCL-Backup",
                "--subdirs",
                "--filter", "extension == \".txt\"",
                src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b.Exit);

        var list = await TapeConHost.RunAsync(media.Verb("list"));
        Assert.Equal(TapeConExitCode.Ok, list.Exit);
        Assert.Contains(list.Entries, e => e.Message.Contains("included.txt"));
        Assert.DoesNotContain(list.Entries, e => e.Message.Contains("excluded.bin"));
        Assert.DoesNotContain(list.Entries, e => e.Message.Contains("note.log"));
    }

    [Fact]
    public async Task Backup_BarePositionalsSplitIntoSourcesAndFilters()
    {
        using var src = new TempFileTree();
        src.AddFile("a/keep1.txt", 256);
        src.AddFile("a/keep2.txt", 256);
        src.AddFile("a/skip.bin", 256);

        using var media = new TempVirtualMedia(withInitiator: true);
        await FormatAsync(media);

        // Bare args: a path (existing folder ⇒ source) and an inline FCL
        //  expression (starts with the `name` keyword ⇒ filter).
        var b = await TapeConHost.RunAsync(
            media.Verb("backup",
                "--description", "FCL-Bare",
                "--subdirs",
                src.RootPath,
                "name matches \"*.txt\""));
        Assert.Equal(TapeConExitCode.Ok, b.Exit);

        var list = await TapeConHost.RunAsync(media.Verb("list"));
        Assert.Equal(TapeConExitCode.Ok, list.Exit);
        Assert.Contains(list.Entries, e => e.Message.Contains("keep1.txt"));
        Assert.Contains(list.Entries, e => e.Message.Contains("keep2.txt"));
        Assert.DoesNotContain(list.Entries, e => e.Message.Contains("skip.bin"));
    }

    [Fact]
    public async Task Backup_WithoutSources_ReturnsUsageError()
    {
        // Only an FCL filter is given — no path/folder/wildcard ⇒ nothing to
        //  back up. The verb must reject this with a usage error rather than
        //  silently producing an empty set.
        using var media = new TempVirtualMedia(withInitiator: true);
        await FormatAsync(media);

        var r = await TapeConHost.RunAsync(
            media.Verb("backup", "--description", "Empty", "name matches \"*.txt\""));
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
    }

    // ─── Error paths ────────────────────────────────────────────────────

    [Fact]
    public async Task List_WithMalformedFilter_ReturnsUsageError()
    {
        var (media, src, _, _, _) = await SetupMixedSetAsync();
        using (media) using (src)
        {
            // Recognized as inline FCL by the leading `size` keyword, then
            //  rejected by the parser ("nope" isn't a valid size literal).
            var r = await TapeConHost.RunAsync(
                media.Verb("list", "--filter", "size >= nope"));
            Assert.Equal(TapeConExitCode.UsageError, r.Exit);
        }
    }

    [Fact]
    public async Task List_WithMissingFilterFile_ReturnsUsageError()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        await FormatAsync(media);

        var nope = Path.Combine(media.Root, "does-not-exist.fcl");
        var r = await TapeConHost.RunAsync(
            media.Verb("list", "--filter-file", nope));
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
    }
}
