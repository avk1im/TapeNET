using HelpNET.Content;
using HelpNET.Indexing;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="IntentMatcher"/>.
/// </summary>
public class IntentMatcherTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HelpTopic MakeTopic(string id, string[] intents)
        => new(id, id, HelpTopicKind.Concept, null,
            [], intents, [],
            "body", "body", null, true);

    // ── Basic matching ────────────────────────────────────────────────────────

    [Fact]
    public void Match_ExactIntentPhrase_ReturnsHighScore()
    {
        var topic   = MakeTopic("backup", ["how do I create a backup"]);
        var matcher = new IntentMatcher([topic]);

        var results = matcher.Match("how do I create a backup");

        Assert.Single(results);
        Assert.Equal("backup", results[0].Topic.Id);
        Assert.True(results[0].Score > 0.5f);
    }

    [Fact]
    public void Match_PartialIntentOverlap_StillMatches()
    {
        var topic   = MakeTopic("restore", ["how do I restore files"]);
        var matcher = new IntentMatcher([topic]);

        // "restore files" overlaps with the intent phrase enough.
        var results = matcher.Match("restore files");
        Assert.NotEmpty(results);
        Assert.Equal("restore", results[0].Topic.Id);
    }

    [Fact]
    public void Match_NoOverlap_ReturnsEmpty()
    {
        var topic   = MakeTopic("tape", ["how do I format a tape"]);
        var matcher = new IntentMatcher([topic]);

        var results = matcher.Match("connect remote host service");
        Assert.Empty(results);
    }

    // ── Multiple topics ───────────────────────────────────────────────────────

    [Fact]
    public void Match_MultipleCandidates_BestMatchFirst()
    {
        var topics = new[]
        {
            MakeTopic("backup",  ["start backup", "create backup", "make new backup"]),
            MakeTopic("restore", ["restore files", "recover data"]),
            MakeTopic("format",  ["format tape", "erase media"]),
        };
        var matcher = new IntentMatcher(topics);

        var results = matcher.Match("create backup files tape");

        Assert.NotEmpty(results);
        Assert.Equal("backup", results[0].Topic.Id);
    }

    [Fact]
    public void Match_TopKLimitsResults()
    {
        var topics = Enumerable.Range(1, 10)
            .Select(i => MakeTopic($"t{i}", [$"backup restore topic {i}"]))
            .ToArray();
        var matcher = new IntentMatcher(topics);

        var results = matcher.Match("backup restore", topK: 3);
        Assert.True(results.Count <= 3);
    }

    // ── Threshold ─────────────────────────────────────────────────────────────

    [Fact]
    public void Match_BelowThreshold_Excluded()
    {
        var topic   = MakeTopic("niche", ["very specific niche term here"]);
        var matcher = new IntentMatcher([topic]);

        // Query shares almost nothing with the intent.
        var results = matcher.Match("backup tape drive", threshold: 0.5f);
        Assert.Empty(results);
    }

    // ── Phrase normalisation ──────────────────────────────────────────────────

    [Fact]
    public void Match_StopWordsIgnored_MatchStillWorks()
    {
        var topic   = MakeTopic("restore", ["how do I restore my files"]);
        var matcher = new IntentMatcher([topic]);

        // "restore" and "files" survive stop-word removal on both sides.
        var results = matcher.Match("restore the files");
        Assert.NotEmpty(results);
    }

    // ── Tokenizer unit tests ──────────────────────────────────────────────────

    [Fact]
    public void TokenizeIntent_RemovesStopWords()
    {
        var tokens = IntentMatcher.TokenizeIntent("how do I get my files back").ToList();

        Assert.DoesNotContain("how", tokens);
        Assert.DoesNotContain("do",  tokens);
        Assert.DoesNotContain("my",  tokens);
        Assert.Contains("files",     tokens);
        Assert.Contains("back",      tokens);
    }

    [Fact]
    public void TokenizeIntent_LowercasesInput()
    {
        var tokens = IntentMatcher.TokenizeIntent("RESTORE Files").ToList();
        Assert.Contains("restore", tokens);
        Assert.Contains("files",   tokens);
    }

    // ── Fixture corpus table-driven tests ────────────────────────────────────

    [Theory]
    [InlineData("how do I create a backup",    "quickstart.backup")]
    [InlineData("get my files back",           "quickstart.restore")]
    [InlineData("what is an incremental backup", "concepts.incremental-backup")]
    public async Task Match_FixtureCorpus_ExpectedTopicTopRanked(
        string query, string expectedId)
    {
        var store   = await TestContentFixture.LoadStoreAsync();
        var matcher = new IntentMatcher(store.All);

        var results = matcher.Match(query);

        Assert.NotEmpty(results);
        Assert.Equal(expectedId, results[0].Topic.Id);
    }
}
