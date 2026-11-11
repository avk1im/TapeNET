using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;

namespace TapeConNET.Tests;

/// <summary>
/// Smoke tests for the System.CommandLine verb tree built by
/// <c>RootCommandFactory</c>. These tests don't touch a drive — they only
/// exercise parsing, help, and usage validation.
/// </summary>
/// <remarks>
/// Help output is a document (<c>IConsoleUx.Write</c>): the tests read <see cref="TapeConHost.RunResult.Output"/> — the
///  plain form at 80 columns, as a redirected console shows it (Design-CliPresentation §5).
/// </remarks>
public class CliParsingTests
{
    #region *** Helpers ***

    private static string[] Lines(TapeConHost.RunResult r) => r.Output.ReplaceLineEndings("\n").Split('\n');

    /// <summary>The index of the line that IS <paramref name="title"/> — a section title stands alone on its line.</summary>
    private static int TitleLine(string[] lines, string title) => Array.IndexOf(lines, title);

    #endregion

    #region *** Parsing ***

    [Fact]
    public async Task NoArgs_PrintsBannerAndHint_ReturnsOk()
    {
        var r = await TapeConHost.RunAsync();
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains(r.Entries, e => e.Message.Contains("No verb specified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnknownVerb_ReturnsUsageError()
    {
        var r = await TapeConHost.RunAsync("not-a-verb");
        Assert.NotEqual(TapeConExitCode.Ok, r.Exit);
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

    #endregion

    #region *** Recursive root options (§4.6) ***

    [Theory]
    [InlineData("--quiet")]
    [InlineData("-q")]
    [InlineData("--no-color")]
    public async Task RootOption_ParsesOnVerb(string option)
    {
        // Before §4.6 the verb rejected these as unknown tokens. docs ignores --quiet: the user asked for the text.
        var r = await TapeConHost.RunAsync("docs", option, "concepts");
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains("tapecon — Concepts", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootOption_ParsesOnVerb_WithHelp()
    {
        var r = await TapeConHost.RunAsync("backup", "--quiet", "--no-color", "--help");
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains("--incremental", r.Output, StringComparison.Ordinal);
    }

    #endregion

    #region *** Help ***

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
    [InlineData("rename-set")]
    [InlineData("create-virtual")]
    public async Task Help_ForVerb_ReturnsOk(string verb)
    {
        var r = await TapeConHost.RunAsync(verb, "--help");
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.StartsWith($"tapecon {verb}", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_Root_ListsCommands_AndFooter()
    {
        var r = await TapeConHost.RunAsync("--help");
        var lines = Lines(r);

        Assert.True(TitleLine(lines, "Commands") >= 0, "Commands section missing");
        Assert.Contains(lines, l => l.StartsWith("  backup ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  create-virtual, new-virtual", StringComparison.Ordinal));
        Assert.Contains("Docs: tapecon docs concepts", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_Backup_GroupsInOrder_OwnOptionsFirst()
    {
        var r = await TapeConHost.RunAsync("backup", "--help");
        var lines = Lines(r);

        string[] titles = ["Arguments", "Options", "File selection", "Encryption", "Drive selection",
                           "New virtual media", "Output and diagnostics"];
        var positions = titles.Select(t => TitleLine(lines, t)).ToArray();

        Assert.All(positions, p => Assert.True(p >= 0, $"missing section: {string.Join(", ", titles.Where((_, i) => positions[i] < 0))}"));
        Assert.Equal(positions.Order(), positions);

        // The verb's own options sit under "Options", before any shared group
        int incremental = Array.FindIndex(lines, l => l.StartsWith("  --incremental", StringComparison.Ordinal));
        Assert.InRange(incremental, positions[1], positions[2]);

        // Encryption opens with the switch, then the password sources
        int encrypt = Array.FindIndex(lines, l => l.StartsWith("  --encrypt,", StringComparison.Ordinal));
        int passwordFile = Array.FindIndex(lines, l => l.StartsWith("  --password-file PATH", StringComparison.Ordinal));
        Assert.InRange(encrypt, positions[3], passwordFile);
    }

    [Fact]
    public async Task Help_Backup_NewVirtualMedia_IsSummary()
    {
        var r = await TapeConHost.RunAsync("backup", "--help");
        var lines = Lines(r);

        // Names only, --initiator (a reference) next to the capacities; the pointer closes the section
        Assert.Contains(lines, l => l.StartsWith("  --capacity  --init-capacity  --initiator", StringComparison.Ordinal));
        Assert.Contains("  Details: tapecon create-virtual --help", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("  --min-block-size", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Help_Backup_RecursiveOptions_InDiagnostics_HelpLast()
    {
        var r = await TapeConHost.RunAsync("backup", "--help");
        var lines = Lines(r);

        int diagnostics = TitleLine(lines, "Output and diagnostics");
        int quiet = Array.FindIndex(lines, l => l.StartsWith("  --quiet, -q", StringComparison.Ordinal));
        int help = Array.FindIndex(lines, l => l.StartsWith("  --help, ", StringComparison.Ordinal));

        Assert.True(diagnostics < quiet && quiet < help, $"diagnostics {diagnostics}, quiet {quiet}, help {help}");
        Assert.Contains("[default: None]", r.Output, StringComparison.Ordinal);       // --log-level
    }

    [Fact]
    public async Task Help_CreateVirtual_PromotesNewVirtualMedia()
    {
        var r = await TapeConHost.RunAsync("create-virtual", "--help");
        var lines = Lines(r);

        // Promoted: in full, as "Options"; no summary, no drive selection
        Assert.True(TitleLine(lines, "Options") >= 0);
        Assert.Equal(-1, TitleLine(lines, "New virtual media"));
        Assert.Equal(-1, TitleLine(lines, "Drive selection"));
        Assert.DoesNotContain("Details: tapecon create-virtual --help", r.Output, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("  --min-block-size, --minimal-block-size SIZE", StringComparison.Ordinal));

        // --initiator once, described for creating media
        Assert.Single(lines, l => l.StartsWith("  --initiator, -I PATH", StringComparison.Ordinal));
        Assert.Contains("partition file to create", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_LongName_PushesDescriptionToNextLine()
    {
        var r = await TapeConHost.RunAsync("backup", "--help");
        var lines = Lines(r);

        // "--virtual-create, --virtual-new PATH" (36) exceeds the 34-column cap
        int row = Array.IndexOf(lines, "  --virtual-create, --virtual-new PATH");
        Assert.True(row >= 0, "the long name stands alone on its line");
        Assert.StartsWith(new string(' ', 2 + 34 + 2) + "Create NEW", lines[row + 1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_Output_FitsTheWidth_ExceptLongNames()
    {
        var r = await TapeConHost.RunAsync("backup", "--help");

        // Only an option name with its placeholder may overrun (e.g. --log-level with its choices) — it never wraps
        Assert.All(Lines(r).Where(l => !l.TrimStart().StartsWith('-')),
            l => Assert.True(l.Length <= 80, $"{l.Length}: {l}"));
    }

    #endregion
}
