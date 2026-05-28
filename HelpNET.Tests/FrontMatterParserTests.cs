using HelpNET.Content;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for the YAML front-matter parser embedded in <see cref="FrontMatterParser"/>.
/// </summary>
public class FrontMatterParserTests
{
    // ── Basic parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NoFrontMatter_ReturnsEmptyFieldsAndFullBody()
    {
        const string md = "# Hello\n\nSome body text.";
        var (fields, body) = FrontMatterParser.Parse(md);

        Assert.Empty(fields);
        Assert.Contains("Hello", body);
        Assert.Contains("Some body text.", body);
    }

    [Fact]
    public void Parse_ValidFrontMatter_ExtractsScalars()
    {
        const string md =
            "---\nid: my.topic\ntitle: My Topic\nkind: concept\n---\nBody here.";

        var (fields, body) = FrontMatterParser.Parse(md);

        Assert.Equal("my.topic", FrontMatterParser.GetString(fields, "id"));
        Assert.Equal("My Topic", FrontMatterParser.GetString(fields, "title"));
        Assert.Equal("concept",  FrontMatterParser.GetString(fields, "kind"));
        Assert.Contains("Body here", body);
    }

    [Fact]
    public void Parse_BlockSequence_ReturnsListField()
    {
        const string md =
            "---\nid: t1\nkeywords:\n  - backup\n  - tape\n  - restore\n---\nBody.";

        var (fields, _) = FrontMatterParser.Parse(md);
        var kw = FrontMatterParser.GetList(fields, "keywords");

        Assert.Equal(3, kw.Count);
        Assert.Contains("backup",  kw);
        Assert.Contains("tape",    kw);
        Assert.Contains("restore", kw);
    }

    [Fact]
    public void Parse_FlowSequence_ReturnsListField()
    {
        const string md = "---\nid: t2\nkeywords: [alpha, beta, gamma]\n---\nBody.";

        var (fields, _) = FrontMatterParser.Parse(md);
        var kw = FrontMatterParser.GetList(fields, "keywords");

        Assert.Equal(3, kw.Count);
        Assert.Contains("alpha", kw);
        Assert.Contains("beta",  kw);
        Assert.Contains("gamma", kw);
    }

    [Fact]
    public void Parse_BooleanField_True()
    {
        const string md = "---\nid: t3\nai_excerpt: true\n---\nBody.";
        var (fields, _) = FrontMatterParser.Parse(md);
        Assert.True(FrontMatterParser.GetBool(fields, "ai_excerpt", false));
    }

    [Fact]
    public void Parse_BooleanField_False()
    {
        const string md = "---\nid: t4\nai_excerpt: false\n---\nBody.";
        var (fields, _) = FrontMatterParser.Parse(md);
        Assert.False(FrontMatterParser.GetBool(fields, "ai_excerpt", true));
    }

    [Fact]
    public void Parse_MissingOptionalField_GetString_ReturnsNull()
    {
        const string md = "---\nid: t5\n---\nBody.";
        var (fields, _) = FrontMatterParser.Parse(md);
        Assert.Null(FrontMatterParser.GetString(fields, "host"));
    }

    [Fact]
    public void Parse_MissingListField_ReturnsEmpty()
    {
        const string md = "---\nid: t6\n---\nBody.";
        var (fields, _) = FrontMatterParser.Parse(md);
        Assert.Empty(FrontMatterParser.GetList(fields, "keywords"));
    }

    [Fact]
    public void Parse_MissingBoolField_ReturnsDefault()
    {
        const string md = "---\nid: t7\n---\nBody.";
        var (fields, _) = FrontMatterParser.Parse(md);
        Assert.True(FrontMatterParser.GetBool(fields, "ai_excerpt", true));
        Assert.False(FrontMatterParser.GetBool(fields, "ai_excerpt", false));
    }

    [Fact]
    public void Parse_QuotedStringValue_UnquotesCorrectly()
    {
        const string md = "---\nid: t8\ntitle: \"My Quoted Title\"\n---\nBody.";
        var (fields, _) = FrontMatterParser.Parse(md);
        Assert.Equal("My Quoted Title", FrontMatterParser.GetString(fields, "title"));
    }

    [Fact]
    public void Parse_SingleQuotedValue_UnquotesCorrectly()
    {
        const string md = "---\nid: t9\ntitle: 'Single Quoted'\n---\nBody.";
        var (fields, _) = FrontMatterParser.Parse(md);
        Assert.Equal("Single Quoted", FrontMatterParser.GetString(fields, "title"));
    }

    [Fact]
    public void Parse_MultipleListsInOneFrontMatter_BothParsedCorrectly()
    {
        const string md =
            "---\nid: t10\nkeywords:\n  - kw1\n  - kw2\nintents:\n  - do something\n  - another intent\n---\nBody.";

        var (fields, _) = FrontMatterParser.Parse(md);

        var kw      = FrontMatterParser.GetList(fields, "keywords");
        var intents = FrontMatterParser.GetList(fields, "intents");

        Assert.Equal(2, kw.Count);
        Assert.Equal(2, intents.Count);
        Assert.Contains("do something", intents);
    }

    // ── All kind values ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("concept",     HelpTopicKind.Concept)]
    [InlineData("walkthrough", HelpTopicKind.Walkthrough)]
    [InlineData("reference",   HelpTopicKind.Reference)]
    [InlineData("ui-map",      HelpTopicKind.UiMap)]
    [InlineData("quickstart",  HelpTopicKind.QuickStart)]
    [InlineData("feature",     HelpTopicKind.Feature)]
    [InlineData("dialog",      HelpTopicKind.Dialog)]
    [InlineData("home",        HelpTopicKind.Home)]
    [InlineData("glossary",    HelpTopicKind.Glossary)]
    public async Task LoadStore_ParsesAllKindValues(string kindStr, HelpTopicKind expected)
    {
        var md     = $"---\nid: kind-test\ntitle: T\nkind: {kindStr}\n---\nBody.";
        var source = new InMemoryHelpContentSource("t", [("kind-test.md", md)]);
        var store  = await HelpContentStore.LoadAsync(source);

        var topic = store.GetById("kind-test");
        Assert.NotNull(topic);
        Assert.Equal(expected, topic.Kind);
    }

    [Fact]
    public async Task LoadStore_UnknownKind_DefaultsToConcept()
    {
        const string md = "---\nid: unk\ntitle: T\nkind: something-weird\n---\nBody.";
        var source = new InMemoryHelpContentSource("t", [("unk.md", md)]);
        var store  = await HelpContentStore.LoadAsync(source);

        var topic = store.GetById("unk");
        Assert.NotNull(topic);
        Assert.Equal(HelpTopicKind.Concept, topic.Kind);
    }
}
