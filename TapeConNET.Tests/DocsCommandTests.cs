using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;

namespace TapeConNET.Tests;

/// <summary>
/// Phase 8 — verifies that <c>tapecon docs</c> renders the embedded markdown
/// topics, lists topics on demand, and rejects unknown topics with the
/// documented usage exit code.
/// </summary>
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
        Assert.Contains(r.Entries, e => e.Message.Contains(headingFragment, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Docs_NoArgs_RendersConceptsByDefault()
    {
        var r = await TapeConHost.RunAsync("docs");
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains(r.Entries, e => e.Message.Contains("tapecon — Concepts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Docs_List_ShowsAllTopics()
    {
        var r = await TapeConHost.RunAsync("docs", "--list");
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains(r.Entries, e => e.Message.Contains("concepts"));
        Assert.Contains(r.Entries, e => e.Message.Contains("migration"));
        Assert.Contains(r.Entries, e => e.Message.Contains("faq"));
    }

    [Fact]
    public async Task Docs_UnknownTopic_ReturnsUsageError()
    {
        var r = await TapeConHost.RunAsync("docs", "nonsense");
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
    }
}
