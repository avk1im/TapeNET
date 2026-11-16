using TapeConNET.Cli;
using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;

namespace TapeConNET.Tests;

/// <summary>
/// <c>tapecon docs</c>: renders the embedded Markdown topics, lists them (Design-CliPresentation §10), and rejects
///  unknown topics with the documented usage exit code.
/// </summary>
/// <remarks>
/// Topics and the list are documents (<c>IConsoleUx.Write</c>), not log entries: the tests read
///  <see cref="TapeConHost.RunResult.Output"/>, the plain form a redirected console shows.
/// </remarks>
public class DocsCommandTests
{
    #region *** Topics ***

    [Theory]
    [InlineData("concepts",           "Concepts")]
    [InlineData("migration-from-1.0", "Migration from 1.x to 2.0")]
    [InlineData("faq",                "FAQ")]
    public async Task Docs_KnownTopic_RendersHeading(string topic, string headingFragment)
    {
        var r = await TapeConHost.RunAsync("docs", topic);

        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains(headingFragment, r.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("concepts")]
    [InlineData("migration-from-1.0")]
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
    public async Task Docs_TopicName_IsCaseInsensitive()
    {
        var r = await TapeConHost.RunAsync("docs", "CONCEPTS");

        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.Contains("Concepts", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTopic_HasATitle()
        => Assert.All(DocTopics.All, t => Assert.False(string.IsNullOrWhiteSpace(t.Title), $"{t.Name} has no # title"));

    #endregion

    #region *** The list ***

    [Fact]
    public async Task Docs_NoArgs_ListsTopics_LikeList()
    {
        var bare = await TapeConHost.RunAsync("docs");
        var list = await TapeConHost.RunAsync("docs", "--list");

        Assert.Equal(TapeConExitCode.Ok, bare.Exit);
        Assert.Equal(TapeConExitCode.Ok, list.Exit);
        Assert.Equal(list.Output, bare.Output);
    }

    [Fact]
    public async Task Docs_List_EveryTopic_Sorted_WithItsTitle()
    {
        var r = await TapeConHost.RunAsync("docs", "--list");

        var lines = r.Output.ReplaceLineEndings("\n").Split('\n').ToList();
        int topics = lines.IndexOf("Topics");
        Assert.True(topics >= 0, "a Topics section");

        // Each topic: a row starting with its name; the title begins on the same row
        var rows = DocTopics.All
            .Select(t => (Topic: t, Row: lines.FindIndex(topics, l => l.StartsWith("  " + t.Name + " ", StringComparison.Ordinal))))
            .ToList();
        Assert.All(rows, x =>
        {
            Assert.True(x.Row > topics, $"no row for {x.Topic.Name}");
            string firstWord = x.Topic.Title!.Split(' ')[0];
            Assert.Contains(firstWord, lines[x.Row], StringComparison.Ordinal);
        });
        Assert.Equal(rows.Select(x => x.Row).Order(), rows.Select(x => x.Row));
        Assert.Equal(DocTopics.All.Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase), DocTopics.All.Select(t => t.Name));
    }

    [Fact]
    public async Task Docs_List_FitsTheWidth()
    {
        var r = await TapeConHost.RunAsync("docs");

        Assert.All(r.Output.ReplaceLineEndings("\n").Split('\n'),
            l => Assert.True(l.Length <= 80, $"{l.Length}: {l}"));
    }

    [Fact]
    public async Task Docs_UnknownTopic_ReturnsUsageError_AndLists()
    {
        var r = await TapeConHost.RunAsync("docs", "nonsense");

        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
        Assert.Contains("nonsense", r.Output, StringComparison.Ordinal);
        Assert.Contains("Topics", r.Output, StringComparison.Ordinal);
    }

    #endregion
}
