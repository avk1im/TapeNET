using TapeLibNET.Tests.Helpers; // TempVirtualMedia (linked)
using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;

namespace TapeConNET.Tests;

/// <summary>
/// Drive lifecycle tests against a file-backed virtual drive: format, info,
/// TOC export/import, eject. Each test creates its own
/// <see cref="TempVirtualMedia"/> so test order does not matter.
/// </summary>
public class LifecycleTests
{
    [Theory]
    [InlineData(false)] // single-partition (TOC in content)
    [InlineData(true)]  // initiator partition (TOC in initiator)
    public async Task Format_ThenInfo_Succeeds(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator);

        var fmt = await TapeConHost.RunAsync(
            media.Verb("format", "--name", "FixtureMedia", "--yes"));
        Assert.Equal(TapeConExitCode.Ok, fmt.Exit);

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.Equal(TapeConExitCode.Ok, info.Exit);
        Assert.Contains(info.Entries, e => e.Message.Contains("FixtureMedia"));
    }

    [Fact]
    public async Task Format_WithoutYes_NonInteractive_AutoCancels()
    {
        // SilentConsoleUx.Confirm always returns its default (false) → should
        //  exit Cancelled without writing anything to the media file.
        using var media = new TempVirtualMedia();
        var r = await TapeConHost.RunAsync(media.Verb("format"));
        Assert.Equal(TapeConExitCode.Cancelled, r.Exit);
        Assert.False(File.Exists(media.ContentPath));
    }

    [Fact]
    public async Task TocExportImport_RoundTrips()
    {
        using var media = new TempVirtualMedia(withInitiator: true);

        var fmt = await TapeConHost.RunAsync(
            media.Verb("format", "--name", "ExportMe", "--yes"));
        Assert.Equal(TapeConExitCode.Ok, fmt.Exit);

        var tocFile = Path.Combine(media.Root, "exported.toc");
        var export = await TapeConHost.RunAsync(
            "toc", "export", "--virtual", media.ContentPath,
            "--initiator", media.InitiatorPath!,
            tocFile);
        Assert.Equal(TapeConExitCode.Ok, export.Exit);
        Assert.True(File.Exists(tocFile), "TOC file should exist after export");

        var import = await TapeConHost.RunAsync(
            "toc", "import", "--virtual", media.ContentPath,
            "--initiator", media.InitiatorPath!,
            tocFile);
        Assert.Equal(TapeConExitCode.Ok, import.Exit);
    }

    [Fact]
    public async Task Eject_AfterFormat_Succeeds()
    {
        using var media = new TempVirtualMedia();

        var fmt = await TapeConHost.RunAsync(
            media.Verb("format", "--yes"));
        Assert.Equal(TapeConExitCode.Ok, fmt.Exit);

        var eject = await TapeConHost.RunAsync(media.Verb("eject"));
        Assert.Equal(TapeConExitCode.Ok, eject.Exit);
    }
}
