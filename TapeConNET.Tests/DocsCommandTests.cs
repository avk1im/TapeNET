using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;

namespace TapeConNET.Tests;

/// <summary>
/// Phase 8 — verifies that <c>tapecon docs</c> renders the embedded markdown
/// topics, lists topics on demand, and rejects unknown topics with the
/// documented usage exit code.
/// </summary>
/// <remarks>
/// Since Design-CliPresentation Step 2 the topics are written as documents (<c>IConsoleUx.Write</c>), not as log
///  entries: the tests read <see cref="TapeConHost.RunResult.Output"/> — the plain form a redirected console shows.
/// </remarks>
public class DocsCommandTests
{
    [Theory]
    [InlineData("concepts",  "tapecon — Concepts")]
    [InlineData("migration", "Migration from 1.x to 2.0")]
    [InlineData("faq",       "FAQ")]
    public async Task Docs_KnownTopic_RendersHeading(string topic, string headingFragment)
    {
        var r = await TapeConHost.RunAsync("docs", topic);
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains(headingFragment, r.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("concepts")]
    [InlineData("migration")]
    [InlineData("faq")]
    public async Task Docs_KnownTopic_RendersMarkdown_NotItsSyntax(string topic)
    {
        var r = await TapeConHost.RunAsync("docs", topic);
        Assert.Equal(TapeConExitCode.Ok, r.Exit);

        var lines = r.Output.ReplaceLineEndings("\n").Split('\n');
        Assert.DoesNotContain(lines, l => l.StartsWith("# ", StringComparison.Ordinal) || l.StartsWith("## ", StringComparison.Ordinal));
        Assert.DoesNotContain("|---", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docs_NoArgs_RendersConceptsByDefault()
    {
        var r = await TapeConHost.RunAsync("docs");
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains("tapecon — Concepts", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docs_List_ShowsAllTopics()
    {
        var r = await TapeConHost.RunAsync("docs", "--list");
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains("concepts", r.Output);
        Assert.Contains("migration", r.Output);
        Assert.Contains("faq", r.Output);
    }

    [Fact]
    public async Task Docs_UnknownTopic_ReturnsUsageError()
    {
        var r = await TapeConHost.RunAsync("docs", "nonsense");
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
    }
}
