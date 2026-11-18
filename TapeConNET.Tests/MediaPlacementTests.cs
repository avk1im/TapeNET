using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;
using TapeLibNET.Tests.Helpers;     // linked TempVirtualMedia + TempFileTree

namespace TapeConNET.Tests;

/// <summary>
/// Where a backup puts its set — after the last one by default, or over the entire media — and the media description,
///  accepted wherever media is created or renamed.
/// </summary>
public class MediaPlacementTests
{
    private static async Task FormatAsync(TempVirtualMedia media, string mediaDesc = "PlacementMedia")
        => Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync(media.Verb("format", "--media-description", mediaDesc, "--yes"))).Exit);

    private static async Task BackupAsync(TempVirtualMedia media, TempFileTree src, params string[] extra)
        => Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync(media.Verb("backup", [.. extra, "--subdirs", src.RootPath]))).Exit);

    // ── Append is the default ──────────────────────────────────────────────────

    [Fact]
    public async Task Backup_Default_AppendsAfterLastSet_KeepsMediaDescription()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        src.AddFiles("a", count: 3, minSize: 256, maxSize: 2048);
        await FormatAsync(media, "Keep-This-Media");

        await BackupAsync(media, src, "--description", "Set-1");
        await BackupAsync(media, src, "--description", "Set-2");

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.True(info.ContainsMessage("Set-1"));
        Assert.True(info.ContainsMessage("Set-2"));
        Assert.True(info.ContainsMessage("Keep-This-Media"));
    }

    [Fact]
    public async Task Backup_ExplicitAppend_SameAsDefault()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        src.AddFile("a.txt", 1024);
        await FormatAsync(media);

        await BackupAsync(media, src, "--description", "Set-1");
        await BackupAsync(media, src, "--description", "Set-2", "--append");

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.True(info.ContainsMessage("Set-1"));
        Assert.True(info.ContainsMessage("Set-2"));
    }

    // ── Overwrite the entire media ─────────────────────────────────────────────

    [Fact]
    public async Task Backup_OverwriteMedia_ReplacesSetsAndDescription()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        src.AddFile("a.txt", 1024);
        await FormatAsync(media, "Old-Media");
        await BackupAsync(media, src, "--description", "Old-Set");

        await BackupAsync(media, src, "--description", "New-Set", "--overwrite-media", "--mdesc", "New-Media");

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.True(info.ContainsMessage("New-Set"));
        Assert.True(info.ContainsMessage("New-Media"));
        Assert.False(info.ContainsMessage("Old-Set"));
        Assert.False(info.ContainsMessage("Old-Media"));
    }

    [Theory]
    [InlineData("--append")]
    [InlineData("--incremental")]
    public async Task Backup_OverwriteMedia_WithAppendingOption_IsUsageError(string appending)
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        src.AddFile("a.txt", 1024);

        var r = await TapeConHost.RunAsync(media.Verb("backup", "--overwrite-media", appending, src.RootPath));
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
    }

    [Fact]
    public async Task Backup_OverwriteMedia_AppendAfter_IsUsageError()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        src.AddFile("a.txt", 1024);

        var r = await TapeConHost.RunAsync(media.Verb("backup", "--overwrite-media", "--append-after", "1", src.RootPath));
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
    }

    [Fact]
    public async Task Backup_MediaDescription_WhenAppending_WarnsAndKeepsDescription()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        src.AddFile("a.txt", 1024);
        await FormatAsync(media, "Original");

        var r = await TapeConHost.RunAsync(media.Verb("backup", "--mdesc", "Ignored", "--subdirs", src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains(r.AtLevel(WarningLevel.Warning), e => e.Message.Contains("--media-description"));

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.True(info.ContainsMessage("Original"));
        Assert.False(info.ContainsMessage("Ignored"));
    }

    [Fact]
    public async Task Backup_NameAlias_IsNotAccepted()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        src.AddFile("a.txt", 1024);

        var r = await TapeConHost.RunAsync(media.Verb("backup", "--name", "Which", src.RootPath));
        Assert.NotEqual(TapeConExitCode.Ok, r.Exit);       // ambiguous in backup: set or media
    }

    // ── Media description on the other verbs ───────────────────────────────────

    [Theory]
    [InlineData("--media-description")]
    [InlineData("--mdesc")]
    [InlineData("--name")]
    [InlineData("-n")]
    public async Task Format_AcceptsEveryMediaDescriptionAlias(string alias)
    {
        using var media = new TempVirtualMedia(withInitiator: true);

        var fmt = await TapeConHost.RunAsync(media.Verb("format", alias, "Alias-Media", "--yes"));
        Assert.Equal(TapeConExitCode.Ok, fmt.Exit);

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.True(info.ContainsMessage("Alias-Media"));
    }

    [Fact]
    public async Task RenameMedia_WithDescription_DoesNotAsk()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        await FormatAsync(media, "Before");

        var r = await TapeConHost.RunAsync(media.Verb("rename-media", "--name", "After"));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.True(info.ContainsMessage("After"));
    }

    [Theory]
    [InlineData("--description")]
    [InlineData("-D")]
    [InlineData("--name")]
    public async Task RenameSet_WithDescription_DoesNotAsk(string alias)
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        src.AddFile("a.txt", 1024);
        await FormatAsync(media);
        await BackupAsync(media, src, "--description", "Before-Set");

        var r = await TapeConHost.RunAsync(media.Verb("rename-set", "1", alias, "After-Set"));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.True(info.ContainsMessage("After-Set"));
    }
}
