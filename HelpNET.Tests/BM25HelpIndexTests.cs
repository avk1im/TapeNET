using HelpNET.Content;
using HelpNET.Indexing;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="BM25HelpIndex"/>.
/// </summary>
public class BM25HelpIndexTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HelpTopic MakeTopic(string id, string title, string plain,
        string[]? keywords = null)
        => new(id, title, HelpTopicKind.Concept, null,
            keywords ?? [],
            [],
            [],
            plain, plain, null, true);

    // ── Empty corpus ──────────────────────────────────────────────────────────

    [Fact]
    public void Search_EmptyCorpus_ReturnsEmpty()
    {
        var index = BM25HelpIndex.Build([]);
        var hits  = index.Search("anything", 5);
        Assert.Empty(hits);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        var index = BM25HelpIndex.Build([MakeTopic("t1", "T", "some text")]);
        Assert.Empty(index.Search("",   5));
        Assert.Empty(index.Search("  ", 5));
    }

    // ── Basic relevance ───────────────────────────────────────────────────────

    [Fact]
    public void Search_ExactTermMatch_ReturnsExpectedTopic()
    {
        var topics = new[]
        {
            MakeTopic("tape",    "Tape basics",  "tape drive cartridge rewind"),
            MakeTopic("restore", "Restore data", "restore recover files disk"),
        };
        var index = BM25HelpIndex.Build(topics);

        var hits = index.Search("restore", 5);
        Assert.NotEmpty(hits);
        Assert.Equal("restore", hits[0].Topic.Id);
    }

    [Fact]
    public void Search_MultiTermQuery_BestMatchTopRanked()
    {
        var topics = new[]
        {
            MakeTopic("a", "Incremental backup",  "incremental backup delta changed files tape"),
            MakeTopic("b", "Restore files",       "restore files recover destination tape"),
            MakeTopic("c", "Format media",        "format erase tape blank initialise"),
        };
        var index = BM25HelpIndex.Build(topics);

        var hits = index.Search("incremental backup changed files", 3);
        Assert.Equal("a", hits[0].Topic.Id);
    }

    [Fact]
    public void Search_TopKLimitsResults()
    {
        var topics = Enumerable.Range(1, 10)
            .Select(i => MakeTopic($"t{i}", $"Topic {i}", "backup tape restore data"))
            .ToArray();
        var index = BM25HelpIndex.Build(topics);

        var hits = index.Search("backup", 3);
        Assert.True(hits.Count <= 3);
    }

    [Fact]
    public void Search_ScoresDescending()
    {
        var topics = new[]
        {
            MakeTopic("rich",  "Rich", "backup tape backup tape backup"), // 3× backup
            MakeTopic("poor",  "Poor", "backup disk storage"),            // 1× backup
        };
        var index = BM25HelpIndex.Build(topics);

        var hits = index.Search("backup", 5);
        Assert.True(hits.Count >= 2);
        Assert.True(hits[0].Score >= hits[1].Score);
    }

    // ── Keyword boost ─────────────────────────────────────────────────────────

    [Fact]
    public void Search_KeywordsBoostScore()
    {
        // One topic has "restore" in keywords, the other only in body text.
        var withKeyword    = MakeTopic("kw", "KW",   "tape backup",  ["restore"]);
        var withoutKeyword = MakeTopic("nk", "NoKW", "restore files backup tape");

        var index = BM25HelpIndex.Build([withKeyword, withoutKeyword]);
        var hits  = index.Search("restore", 5);

        // The keyword-boosted topic should rank first.
        Assert.Equal("kw", hits[0].Topic.Id);
    }

    // ── Excerpt ───────────────────────────────────────────────────────────────

    [Fact]
    public void Search_ExcerptContainsQueryTerm()
    {
        var topic = MakeTopic("t", "T",
            "This is a long document about incremental backup. " +
            "It explains how incremental backups work and why they are useful.");
        var index = BM25HelpIndex.Build([topic]);

        var hits = index.Search("incremental", 1);
        Assert.Single(hits);
        Assert.Contains("incremental", hits[0].Excerpt, StringComparison.OrdinalIgnoreCase);
    }

    // ── Tokenizer unit tests ──────────────────────────────────────────────────

    [Fact]
    public void Tokenize_StopWordsExcluded()
    {
        var tokens = BM25HelpIndex.Tokenize("the quick brown fox").ToList();
        Assert.DoesNotContain("the", tokens);
        Assert.Contains("quick", tokens);
        Assert.Contains("fox",   tokens);
    }

    [Fact]
    public void Tokenize_LowercasesInput()
    {
        var tokens = BM25HelpIndex.Tokenize("BACKUP Tape").ToList();
        Assert.Contains("backup", tokens);
        Assert.Contains("tape",   tokens);
    }

    [Fact]
    public void Tokenize_SingleCharTokensExcluded()
    {
        var tokens = BM25HelpIndex.Tokenize("a b c backup").ToList();
        Assert.DoesNotContain("a", tokens);
        Assert.DoesNotContain("b", tokens);
        Assert.Contains("backup", tokens);
    }

    // ── Known-corpus table-driven tests ──────────────────────────────────────

    [Theory]
    [InlineData("tape drive",         "quickstart.backup")]        // "tape" + "drive" hit keywords/body of backup topics
    [InlineData("recover files",      "quickstart.restore")]       // restore quickstart is most term-dense for recover/files
    [InlineData("incremental delta",  "concepts.incremental-backup")] // delta + incremental are in incremental-backup body/keywords
    public async Task Search_FixtureCorpus_ExpectedTopicTopRanked(
        string query, string expectedTopicId)
    {
        var store = await TestContentFixture.LoadStoreAsync();
        var index = BM25HelpIndex.Build(store.All);

        var hits = index.Search(query, 5);

        Assert.NotEmpty(hits);
        Assert.Equal(expectedTopicId, hits[0].Topic.Id);
    }
}
