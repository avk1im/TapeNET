using HelpNET.Content;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="HelpContentStore"/> loading, deduplication, and lookup.
/// </summary>
public class HelpContentStoreTests
{
    // ── Loading ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_ParsesAllTopics_FromFixture()
    {
        var store = await TestContentFixture.LoadStoreAsync();

        // The fixture has 10 raw documents, all with valid ids.
        Assert.Equal(10, store.All.Count);
    }

    [Fact]
    public async Task LoadAsync_TopicWithoutId_IsSkipped()
    {
        const string md = "---\ntitle: No Id\n---\nBody.";
        var source = new InMemoryHelpContentSource("t", [("noid.md", md)]);
        var store  = await HelpContentStore.LoadAsync(source);

        Assert.Empty(store.All);
    }

    // ── Duplicate detection ───────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_DuplicateId_FirstWins_SecondReported()
    {
        const string md1 = "---\nid: dup\ntitle: First\n---\nFirst body.";
        const string md2 = "---\nid: dup\ntitle: Second\n---\nSecond body.";

        var source = new InMemoryHelpContentSource("t",
        [
            ("dup1.md", md1),
            ("dup2.md", md2),
        ]);

        var store = await HelpContentStore.LoadAsync(source);

        Assert.Single(store.All);
        Assert.Equal("First", store.GetById("dup")!.Title);
        Assert.Contains("dup", store.DuplicateIds);
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ExistingId_ReturnsTopic()
    {
        var store = await TestContentFixture.LoadStoreAsync();
        var topic = store.GetById("home");
        Assert.NotNull(topic);
        Assert.Equal("TapeWinNET Help", topic.Title);
    }

    [Fact]
    public async Task GetById_MissingId_ReturnsNull()
    {
        var store = await TestContentFixture.LoadStoreAsync();
        Assert.Null(store.GetById("does.not.exist"));
    }

    [Fact]
    public async Task GetById_IsCaseInsensitive()
    {
        var store = await TestContentFixture.LoadStoreAsync();
        Assert.NotNull(store.GetById("HOME"));
        Assert.NotNull(store.GetById("Home"));
    }

    // ── GetByHost ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByHost_ReturnsTopicsForHost()
    {
        var store = await TestContentFixture.LoadStoreAsync();

        var mainWindowTopics = store.GetByHost("MainWindow");
        Assert.Single(mainWindowTopics);
        Assert.Equal("ui.main-window", mainWindowTopics[0].Id);
    }

    [Fact]
    public async Task GetByHost_UnknownHost_ReturnsEmpty()
    {
        var store = await TestContentFixture.LoadStoreAsync();
        Assert.Empty(store.GetByHost("NonExistentWindow"));
    }

    [Fact]
    public async Task GetByHost_IsCaseInsensitive()
    {
        var store = await TestContentFixture.LoadStoreAsync();
        Assert.NotEmpty(store.GetByHost("mainwindow"));
        Assert.NotEmpty(store.GetByHost("MAINWINDOW"));
    }

    // ── GetGlossaryEntry ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetGlossaryEntry_ExistingGlossaryTopic_ReturnsTopic()
    {
        var store = await TestContentFixture.LoadStoreAsync();
        var entry = store.GetGlossaryEntry("glossary.toc");

        Assert.NotNull(entry);
        Assert.Equal(HelpTopicKind.Glossary, entry.Kind);
    }

    [Fact]
    public async Task GetGlossaryEntry_NonGlossaryTopic_ReturnsNull()
    {
        // "home" exists but is not a glossary topic.
        var store = await TestContentFixture.LoadStoreAsync();
        Assert.Null(store.GetGlossaryEntry("home"));
    }

    [Fact]
    public async Task GetGlossaryEntry_MissingId_ReturnsNull()
    {
        var store = await TestContentFixture.LoadStoreAsync();
        Assert.Null(store.GetGlossaryEntry("glossary.does-not-exist"));
    }

    // ── GetRelated ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRelated_IncludesExplicitRelatedTopics()
    {
        var store   = await TestContentFixture.LoadStoreAsync();
        var related = store.GetRelated("home");

        // home lists quickstart.backup and concepts.backup-sets as related.
        var ids = related.Select(r => r.Id).ToList();
        Assert.Contains("quickstart.backup",    ids);
        Assert.Contains("concepts.backup-sets", ids);
    }

    [Fact]
    public async Task GetRelated_IncludesReverseLinks()
    {
        var store   = await TestContentFixture.LoadStoreAsync();
        // concepts.backup-sets lists "quickstart.backup" as related, so
        // GetRelated("quickstart.backup") should include concepts.backup-sets.
        var related = store.GetRelated("quickstart.backup");
        var ids     = related.Select(r => r.Id).ToList();

        Assert.Contains("concepts.backup-sets", ids);
    }

    [Fact]
    public async Task GetRelated_NoDuplicatesInResult()
    {
        var store   = await TestContentFixture.LoadStoreAsync();
        var related = store.GetRelated("concepts.backup-sets");
        var ids     = related.Select(r => r.Id).ToList();

        Assert.Equal(ids.Distinct().Count(), ids.Count);
    }

    // ── PlainText extraction ──────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_PlainTextStripsMarkdownFormatting()
    {
        // The backup-sets topic uses *italic* and code-style text.
        var store = await TestContentFixture.LoadStoreAsync();
        var topic = store.GetById("concepts.backup-sets");

        Assert.NotNull(topic);
        // Plain text should contain words but not raw asterisks.
        Assert.DoesNotContain("*", topic.PlainText);
        Assert.Contains("backup", topic.PlainText, StringComparison.OrdinalIgnoreCase);
    }

    // ── ai_excerpt field ─────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_AiExcerptFalse_IsRespected()
    {
        var store = await TestContentFixture.LoadStoreAsync();
        // reference.keyboard-shortcuts has ai_excerpt: false
        var topic = store.GetById("reference.keyboard-shortcuts");
        Assert.NotNull(topic);
        Assert.False(topic.IncludeInAiCorpus);
    }

    [Fact]
    public async Task LoadAsync_AiExcerptDefaultsToTrue()
    {
        var store = await TestContentFixture.LoadStoreAsync();
        // concepts.backup-sets has no ai_excerpt field — should default to true.
        var topic = store.GetById("concepts.backup-sets");
        Assert.NotNull(topic);
        Assert.True(topic.IncludeInAiCorpus);
    }
}
