using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;
using TapeConNET.Ux;

namespace TapeConNET.Tests;

/// <summary>
/// Smoke tests for the System.CommandLine verb tree built by
/// <c>RootCommandFactory</c>. These tests don't touch a drive — they only
/// exercise parsing, help, and usage validation.
/// </summary>
public class CliParsingTests
{
    [Fact]
    public async Task NoArgs_PrintsBannerAndHint_ReturnsOk()
    {
        var r = await TapeConHost.RunAsync();
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains(r.Entries, e => e.Message.Contains("No verb specified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Help_TopLevel_ReturnsOk()
    {
        var r = await TapeConHost.RunAsync("--help");
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("backup")]
    [InlineData("restore")]
    [InlineData("validate")]
    [InlineData("verify")]
    [InlineData("list")]
    [InlineData("format")]
    [InlineData("eject")]
    [InlineData("toc")]
    [InlineData("docs")]
    [InlineData("demo")]
    public async Task Help_ForVerb_ReturnsOk(string verb)
    {
        var r = await TapeConHost.RunAsync(verb, "--help");
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
    }

    [Fact]
    public async Task UnknownVerb_ReturnsUsageError()
    {
        var r = await TapeConHost.RunAsync("not-a-verb");
        Assert.NotEqual(TapeConExitCode.Ok, r.Exit);
    }

    [Fact]
    public async Task Backup_WithoutDriveSelection_AutoOpensWin32DriveZero()
    {
        // Per architecture §4 shortcut: when no drive option is supplied, the
        //  verb auto-opens Win32 drive 0. On a CI machine with no tape drive
        //  installed that opens-then-fails — but the failure is an
        //  operational one (no such drive), NOT a usage error.
        var r = await TapeConHost.RunAsync("backup", "C:\\");
        Assert.NotEqual(TapeConExitCode.UsageError, r.Exit);
        Assert.Contains(r.Entries, e =>
            e.Message.Contains("Win32 drive 0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Backup_MutuallyExclusiveDriveOptions_ReturnsUsageError()
    {
        var r = await TapeConHost.RunAsync(
            "backup", "--in-memory", "--virtual", "ignored.vtape", "C:\\");
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
    }

    [Fact]
    public async Task Backup_MissingFilesArgument_ReturnsNonZero()
    {
        // 'backup' requires at least one positional argument
        var r = await TapeConHost.RunAsync("backup", "--in-memory");
        Assert.NotEqual(TapeConExitCode.Ok, r.Exit);
    }
}
