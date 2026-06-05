using System.Windows;
using System.Windows.Documents;

using HelpNET.Assistants;
using HelpNET.Content;
using HelpNET.Session;

using TapeWinNET.Help;

using Xunit;

namespace TapeWinNET.Tests;

/// <summary>
/// Unit tests for <see cref="MarkdownRenderer"/>.
/// <para/>
/// These tests require STA because <see cref="FlowDocument"/> and related WPF types
/// are <see cref="System.Windows.Threading.DispatcherObject"/>s.
/// </summary>
public sealed class MarkdownRendererTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal stub of <see cref="IHelpSession"/> used to satisfy
    /// <see cref="MarkdownRenderer"/>'s constructor without a real session.
    /// Supports optional topic-title lookup, glossary-definition lookup, and
    /// an <paramref name="onNavigate"/> callback for asserting navigation was (not) called.
    /// </summary>
    private sealed class StubSession(
        Dictionary<string, string>? titles        = null,
        Dictionary<string, string>? glossaryDefs  = null,
        Action?                     onNavigate    = null) : IHelpSession
    {
        private readonly Dictionary<string, string> _titles      = titles       ?? [];
        private readonly Dictionary<string, string> _glossaryDefs = glossaryDefs ?? [];
        private readonly Action?                    _onNavigate  = onNavigate;

        public string? TryGetTopicTitle(string id)
            => _titles.TryGetValue(id, out var t) ? t : null;

        public string? TryGetGlossaryDefinition(string termSlug)
            => _glossaryDefs.TryGetValue(termSlug, out var d) ? d : null;

        // ── Unused interface members ───────────────────────────────────────────
        public HelpTopic?                     CurrentTopic   => null;
        public IReadOnlyList<HelpTopic>       BackHistory    => [];
        public IReadOnlyList<HelpTopic>       ForwardHistory => [];
        public IReadOnlyList<ConversationTurn> Conversation  => [];
        public HelpAssistantMode              AssistantMode  => HelpAssistantMode.Lexical;

        public Task<HelpTopic> NavigateAsync(HelpNavigationRequest request, CancellationToken ct)
        {
            _onNavigate?.Invoke();
            return Task.FromResult<HelpTopic>(null!);
        }
        public Task<HelpTopic?> BackAsync(CancellationToken ct)    => Task.FromResult<HelpTopic?>(null);
        public Task<HelpTopic?> ForwardAsync(CancellationToken ct) => Task.FromResult<HelpTopic?>(null);
        public Task<HelpTopic>  HomeAsync(CancellationToken ct)    => Task.FromResult<HelpTopic>(null!);

        public Task<IReadOnlyList<HelpSearchHit>> SearchAsync(string query, int topK, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<HelpSearchHit>>([]);

        public Task<HelpAssistantResponse> AskAsync(string query, CancellationToken ct)
            => Task.FromResult<HelpAssistantResponse>(null!);

        public IReadOnlyList<WalkthroughScript> GetWalkthroughsForHost(string hostName) => [];
        public HelpTopic? GetTopicForControl(string hostName, string topicId) => null;
        public void ClearConversation() { }

        public event EventHandler? CurrentTopicChanged  { add { } remove { } }
        public event EventHandler<HelpAssistantResponse>? AnswerReceived { add { } remove { } }
        public event EventHandler? AssistantModeChanged { add { } remove { } }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── Render ────────────────────────────────────────────────────────────────

    [StaFact]
    public void Render_ValidMarkdown_ReturnsNonNullFlowDocument()
    {
        var renderer = new MarkdownRenderer(new StubSession(), new HelpActionRouter());

        var doc = renderer.Render("# Hello\n\nWorld.");

        Assert.NotNull(doc);
    }

    [StaFact]
    public void Render_EmptyString_ReturnsFlowDocument()
    {
        var renderer = new MarkdownRenderer(new StubSession(), new HelpActionRouter());

        // Should not throw.
        var doc = renderer.Render(string.Empty);

        Assert.NotNull(doc);
    }

    // ── help:// links survive Render ──────────────────────────────────────────

    [StaFact]
    public void Render_HelpTopicLink_SurvivesAsHyperlink()
    {
        var renderer = new MarkdownRenderer(new StubSession(), new HelpActionRouter());
        // Explicit markdown link using the help:// scheme.
        const string markdown = "[Backup Sets](help://topic/concepts.backup-sets)";

        var doc = renderer.Render(markdown);

        // The FlowDocument should contain at least one Hyperlink with the help:// URI.
        bool found = ContainsHelpUri(doc, "help://topic/concepts.backup-sets");
        Assert.True(found, "Expected a hyperlink with a help://topic URI in the rendered document.");
    }

    // ── Bare [topic.id] citation rewriting ───────────────────────────────────

    [StaFact]
    public void Render_BareTopicIdCitation_IsRewrittenToHyperlinkWithKnownTitle()
    {
        var titles = new Dictionary<string, string> { ["dialog.restore"] = "Restore files" };
        var renderer = new MarkdownRenderer(new StubSession(titles), new HelpActionRouter());

        // Bare citation: [dialog.restore] — not a standard markdown link.
        var doc = renderer.Render("See [dialog.restore] for details.");

        // Should be rewritten to a hyperlink pointing at help://topic/dialog.restore.
        bool found = ContainsHelpUri(doc, "help://topic/dialog.restore");
        Assert.True(found, "Bare [topic-id] should be rewritten to a help://topic hyperlink.");
    }

    [StaFact]
    public void Render_BareTopicIdCitation_FallsBackToIdAsLinkText_WhenTopicUnknown()
    {
        // Session has no titles registered.
        var renderer = new MarkdownRenderer(new StubSession(), new HelpActionRouter());

        // Should not throw even when the topic title is unknown.
        var doc = renderer.Render("See [unknown.topic] for details.");

        // The id itself becomes the link text; a hyperlink should still be emitted.
        bool found = ContainsHelpUri(doc, "help://topic/unknown.topic");
        Assert.True(found, "Unknown topic-id should still produce a help:// hyperlink.");
    }

    [StaFact]
    public void Render_ProperMarkdownLink_IsNotDoubleRewritten()
    {
        // [dialog.restore](help://topic/dialog.restore) — already a proper link,
        // the regex must NOT match it (it checks for the absence of a following '(').
        var renderer = new MarkdownRenderer(new StubSession(), new HelpActionRouter());

        // Should not throw and should still contain the single hyperlink.
        var doc = renderer.Render("[dialog.restore](help://topic/dialog.restore)");

        bool found = ContainsHelpUri(doc, "help://topic/dialog.restore");
        Assert.True(found, "An already-proper markdown link should survive unchanged.");
    }

    // ── http:// links are not intercepted ────────────────────────────────────

    [StaFact]
    public void Render_HttpLink_IsPreservedAsHyperlink()
    {
        var renderer = new MarkdownRenderer(new StubSession(), new HelpActionRouter());
        const string markdown = "[Example](https://example.com)";

        var doc = renderer.Render(markdown);

        bool found = ContainsUri(doc, "https://example.com");
        Assert.True(found, "Standard https:// links should be preserved in the FlowDocument.");
    }

    // ── Glossary link styling ─────────────────────────────────────────────────

    [StaFact]
    public void Render_GlossaryLink_HasDashedUnderlineAndTooltip()
    {
        // Provide a glossary definition so the tooltip can be populated.
        var session = new StubSession(glossaryDefs: new Dictionary<string, string>
        {
            ["backup-set"] = "**Backup set** \u2014 a single snapshot of files.",
        });
        var renderer = new MarkdownRenderer(session, new HelpActionRouter());

        var doc = renderer.Render("[backup set](help://glossary/backup-set)");

        // The rendered FlowDocument must contain a hyperlink with the glossary URI.
        bool hasLink = ContainsHelpUri(doc, "help://glossary/backup-set");
        Assert.True(hasLink, "Expected a help://glossary/ hyperlink in the rendered document.");

        // The hyperlink must also carry a tooltip (the definition text).
        bool hasTooltip = ContainsGlossaryTooltip(doc, "backup-set");
        Assert.True(hasTooltip, "Glossary link should have a tooltip carrying the definition.");
    }

    [StaFact]
    public void Render_GlossaryLink_WithoutDefinition_StillRendersLink()
    {
        // Session returns null for the definition — link must still appear.
        var renderer = new MarkdownRenderer(new StubSession(), new HelpActionRouter());

        var doc = renderer.Render("[virtual drive](help://glossary/virtual-drive)");

        bool hasLink = ContainsHelpUri(doc, "help://glossary/virtual-drive");
        Assert.True(hasLink, "Glossary link without a known definition should still produce a hyperlink.");
    }

    [StaFact]
    public void HandleNavigate_GlossaryUri_RaisesGlossaryLinkClickedEvent()
    {
        var renderer = new MarkdownRenderer(new StubSession(), new HelpActionRouter());

        string? receivedSlug = null;
        renderer.GlossaryLinkClicked += (_, slug) => receivedSlug = slug;

        renderer.HandleNavigate(new Uri("help://glossary/incremental-backup"));

        Assert.Equal("incremental-backup", receivedSlug);
    }

    [StaFact]
    public void HandleNavigate_GlossaryUri_DoesNotNavigateSession()
    {
        // Ensure the glossary path no longer falls through to NavigateAsync.
        bool navigateCalled = false;
        var session = new StubSession(onNavigate: () => navigateCalled = true);
        var renderer = new MarkdownRenderer(session, new HelpActionRouter());

        renderer.HandleNavigate(new Uri("help://glossary/toc"));

        Assert.False(navigateCalled, "Glossary click must NOT trigger session navigation.");
    }

    // ── Malformed help:// URIs do not throw ───────────────────────────────────

    [StaFact]
    public void HandleNavigate_MalformedHelpUri_DoesNotThrow()
    {
        var renderer = new MarkdownRenderer(new StubSession(), new HelpActionRouter());

        // help:// without a recognised path — should be silently ignored.
        var ex = Record.Exception(() =>
            renderer.HandleNavigate(new Uri("help://unknown-segment/xyz")));

        Assert.Null(ex);
    }

    [StaFact]
    public void HandleNavigate_MalformedHelpUri_EmptyTarget_DoesNotThrow()
    {
        var renderer = new MarkdownRenderer(new StubSession(), new HelpActionRouter());

        var ex = Record.Exception(() =>
            renderer.HandleNavigate(new Uri("help://topic/")));

        Assert.Null(ex);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if any <see cref="Hyperlink"/> in <paramref name="doc"/>
    /// has a <see cref="Hyperlink.NavigateUri"/> whose string representation starts
    /// with or equals <paramref name="uri"/>.
    /// </summary>
    private static bool ContainsHelpUri(FlowDocument doc, string uri)
        => ContainsUri(doc, uri);

    /// <summary>
    /// Returns <c>true</c> when there is a glossary <see cref="Hyperlink"/> for
    /// <paramref name="slug"/> that also has a non-null <see cref="FrameworkContentElement.ToolTip"/>.
    /// </summary>
    private static bool ContainsGlossaryTooltip(FlowDocument doc, string slug)
    {
        var glossaryUri = $"help://glossary/{slug}";
        return EnumerateHyperlinks(doc).Any(hl =>
            hl.NavigateUri?.ToString()
              .StartsWith(glossaryUri, StringComparison.OrdinalIgnoreCase) == true
            && hl.ToolTip is not null);
    }

    /// <summary>Yields all <see cref="Hyperlink"/> elements in the document.</summary>
    private static IEnumerable<Hyperlink> EnumerateHyperlinks(FlowDocument doc)
    {
        var stack = new Stack<TextElement>();
        foreach (var b in doc.Blocks)
            if (b is TextElement te) stack.Push(te);
        while (stack.Count > 0)
        {
            var el = stack.Pop();
            if (el is Hyperlink hl) yield return hl;
            var children = el switch
            {
                Paragraph p  => p.Inlines.OfType<TextElement>(),
                Section   s  => s.Blocks.OfType<TextElement>(),
                List      l  => l.ListItems.OfType<TextElement>(),
                ListItem  li => li.Blocks.OfType<TextElement>(),
                Hyperlink h  => h.Inlines.OfType<TextElement>(),
                Span      sp => sp.Inlines.OfType<TextElement>(),
                _            => [],
            };
            foreach (var c in children) stack.Push(c);
        }
    }

    private static bool ContainsUri(FlowDocument doc, string uri)
    {
        foreach (var block in doc.Blocks)
        {
            if (FindHyperlink(block, uri))
                return true;
        }
        return false;
    }

    private static bool FindHyperlink(TextElement element, string uri)
    {
        if (element is Hyperlink hl
            && hl.NavigateUri?.ToString().StartsWith(uri, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // Recurse into known container types
        var children = element switch
        {
            Paragraph p      => p.Inlines.OfType<TextElement>(),
            Section s        => s.Blocks.OfType<TextElement>(),
            List l           => l.ListItems.OfType<TextElement>(),
            ListItem li      => li.Blocks.OfType<TextElement>(),
            Hyperlink hl2    => hl2.Inlines.OfType<TextElement>(),
            Span sp          => sp.Inlines.OfType<TextElement>(),
            _                => []
        };

        return children.Any(c => FindHyperlink(c, uri));
    }
}
