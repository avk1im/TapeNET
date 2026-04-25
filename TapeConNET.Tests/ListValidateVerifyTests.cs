using TapeLibNET.Tests.Helpers;

using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;

namespace TapeConNET.Tests;

/// <summary>
/// Tests for the read-side verbs that need an existing backup set:
/// <c>list</c>, <c>validate</c>, and <c>verify</c>.
/// </summary>
public class ListValidateVerifyTests
{
    private static async Task<TempVirtualMedia> SetupSingleSetMediaAsync(TempFileTree src)
    {
        var media = new TempVirtualMedia(withInitiator: true);
        try
        {
            var fmt = await TapeConHost.RunAsync(
                media.Verb("format", "--name", "RVMedia", "--yes"));
            Assert.Equal(TapeConExitCode.Ok, fmt.Exit);

            var b = await TapeConHost.RunAsync(
                media.Verb("backup", "--description", "RV-Set", "--subdirs",
                    "--hash", "Crc32",
                    src.RootPath));
            Assert.Equal(TapeConExitCode.Ok, b.Exit);
            return media;
        }
        catch
        {
            media.Dispose();
            throw;
        }
    }

    [Fact]
    public async Task List_ShowsAllFiles()
    {
        using var src = new TempFileTree();
        var f1 = src.AddFile("hello.txt", 512);
        var f2 = src.AddFile("sub/world.bin", 4096);

        using var media = await SetupSingleSetMediaAsync(src);

        var list = await TapeConHost.RunAsync(media.Verb("list"));
        Assert.Equal(TapeConExitCode.Ok, list.Exit);
        Assert.Contains(list.Entries, e => e.Message.Contains("hello.txt"));
        Assert.Contains(list.Entries, e => e.Message.Contains("world.bin"));
    }

    [Fact]
    public async Task List_WithPattern_FiltersFiles()
    {
        using var src = new TempFileTree();
        src.AddFile("docs/keep.txt", 256);
        src.AddFile("docs/skip.bin", 256);

        using var media = await SetupSingleSetMediaAsync(src);

        // Set index '0' = latest, then a *.txt pattern.
        var list = await TapeConHost.RunAsync(media.Verb("list", "0", "*.txt"));
        Assert.Equal(TapeConExitCode.Ok, list.Exit);
        Assert.Contains(list.Entries, e => e.Message.Contains("keep.txt"));
        Assert.DoesNotContain(list.Entries, e => e.Message.Contains("skip.bin"));
    }

    [Fact]
    public async Task Validate_PassesAfterCleanBackup()
    {
        using var src = new TempFileTree();
        src.AddFiles("payload", count: 6, minSize: 1024, maxSize: 8192);

        using var media = await SetupSingleSetMediaAsync(src);

        var v = await TapeConHost.RunAsync(media.Verb("validate"));
        Assert.Equal(TapeConExitCode.Ok, v.Exit);
    }

    [Fact]
    public async Task Verify_PassesAgainstUnchangedFiles()
    {
        using var src = new TempFileTree();
        src.AddFiles("payload", count: 4, minSize: 1024, maxSize: 4096);

        using var media = await SetupSingleSetMediaAsync(src);

        var v = await TapeConHost.RunAsync(media.Verb("verify"));
        Assert.Equal(TapeConExitCode.Ok, v.Exit);
    }
}
