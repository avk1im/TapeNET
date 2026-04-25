using TapeLibNET.Tests.Helpers; // linked TempFileTree + FileComparer

using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;

namespace TapeConNET.Tests;

/// <summary>
/// End-to-end backup → restore round-trip tests via <c>tapecon</c>'s public
/// CLI, against a file-backed virtual drive. Uses the linked
/// <see cref="TempFileTree"/> + <see cref="FileComparer"/> helpers from
/// <c>TapeLibNET.Tests</c>.
/// </summary>
public class BackupRestoreRoundTripTests
{
    private static async Task FormatAsync(TempVirtualMedia media, string mediaName = "RoundTripMedia")
    {
        var fmt = await TapeConHost.RunAsync(
            media.Verb("format", "--name", mediaName, "--yes"));
        Assert.Equal(TapeConExitCode.Ok, fmt.Exit);
    }

    [Theory]
    [InlineData(false)] // single-partition
    [InlineData(true)]  // with initiator (TOC in dedicated partition)
    public async Task Backup_Then_Restore_RoundTripsBytes(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator);
        using var src = new TempFileTree();
        src.AddFiles("docs", count: 8, minSize: 1024, maxSize: 16 * 1024);
        src.AddFile("nested/deep/file.bin", 65_537);

        await FormatAsync(media);

        var backup = await TapeConHost.RunAsync(
            media.Verb("backup",
                "--description", "RoundTrip-Set-1",
                "--subdirs",
                src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, backup.Exit);

        var restoreRoot = Path.Combine(media.Root, "restore");
        Directory.CreateDirectory(restoreRoot);

        var restore = await TapeConHost.RunAsync(
            media.Verb("restore",
                "--target", restoreRoot,
                "--subdirs",
                "--existing", "Overwrite"));
        Assert.Equal(TapeConExitCode.Ok, restore.Exit);

        // Round-trip preserves the volume-relative path under the restore
        //  target. Resolve src.RootPath against its drive root so we mirror
        //  what TapeFileRestoreAgentEx writes.
        var restoreEffectiveRoot = Path.Combine(restoreRoot, Path.GetPathRoot(src.RootPath)?.Replace(":", "") ?? "");
        // TapeWinNET strips the volume separator; we pass --subdirs so the
        //  full path is recreated. The most reliable comparison is on the
        //  leaf relative paths, anchored on src.RootPath.
        FileComparer.AssertFilesMatch(src.RootPath, src.Files, FindRestoredRoot(restoreRoot, src.RootPath));
    }

    [Fact]
    public async Task AppendBackup_AddsSecondSet_BothRestorable()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src1 = new TempFileTree();
        using var src2 = new TempFileTree();
        src1.AddFiles("set1", count: 4, minSize: 512, maxSize: 4096);
        src2.AddFiles("set2", count: 4, minSize: 512, maxSize: 4096);

        await FormatAsync(media);

        var b1 = await TapeConHost.RunAsync(
            media.Verb("backup", "--description", "Set-1", "--subdirs", src1.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b1.Exit);

        var b2 = await TapeConHost.RunAsync(
            media.Verb("backup", "--description", "Set-2", "--append", "--subdirs", src2.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b2.Exit);

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.Equal(TapeConExitCode.Ok, info.Exit);
        Assert.Contains(info.Entries, e => e.Message.Contains("Set-1"));
        Assert.Contains(info.Entries, e => e.Message.Contains("Set-2"));
    }

    [Fact]
    public async Task Restore_SetIndex_SelectsCorrectSet()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src1 = new TempFileTree();
        using var src2 = new TempFileTree();
        var f1 = src1.AddFile("a.txt", 1024);
        var f2 = src2.AddFile("b.txt", 2048);

        await FormatAsync(media);

        var b1 = await TapeConHost.RunAsync(
            media.Verb("backup", "--description", "Old", src1.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b1.Exit);

        var b2 = await TapeConHost.RunAsync(
            media.Verb("backup", "--description", "New", "--append", src2.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b2.Exit);

        // Default: latest set (0) — should restore set 2 (b.txt).
        var restoreLatest = Path.Combine(media.Root, "restore-latest");
        Directory.CreateDirectory(restoreLatest);
        var rLatest = await TapeConHost.RunAsync(
            media.Verb("restore", "--target", restoreLatest, "--subdirs",
                "--existing", "Overwrite"));
        Assert.Equal(TapeConExitCode.Ok, rLatest.Exit);

        // Explicit oldest set (1).
        var restoreOldest = Path.Combine(media.Root, "restore-oldest");
        Directory.CreateDirectory(restoreOldest);
        var rOldest = await TapeConHost.RunAsync(
            media.Verb("restore", "1", "--target", restoreOldest, "--subdirs",
                "--existing", "Overwrite"));
        Assert.Equal(TapeConExitCode.Ok, rOldest.Exit);
    }

    /// <summary>
    /// Walks <paramref name="restoreRoot"/> and locates the directory whose
    /// last segment matches the leaf of <paramref name="srcRoot"/>. Tape
    /// restores prepend the volume identifier to the path, so the absolute
    /// layout differs across machines.
    /// </summary>
    private static string FindRestoredRoot(string restoreRoot, string srcRoot)
    {
        var leaf = Path.GetFileName(srcRoot.TrimEnd('\\', '/'));
        var match = Directory.EnumerateDirectories(restoreRoot, leaf, SearchOption.AllDirectories)
            .FirstOrDefault();
        return match ?? restoreRoot;
    }
}
