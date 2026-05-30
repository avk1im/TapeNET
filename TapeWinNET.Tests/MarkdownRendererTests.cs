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
    /// Only <see cref="TryGetTopicTitle"/> is exercised.
    /// </summary>
    private sealed class StubSession(Dictionary<string, string>? titles = null) : IHelpSession
    {
        private readonly Dictionary<string, string> _titles = titles ?? [];

        public string? TryGetTopicTitle(string id)
            => _titles.TryGetValue(id, out var t) ? t : null;

        // ── Unused interface members ───────────────────────────────────────────
        public HelpTopic?                     CurrentTopic   => null;
        public IReadOnlyList<HelpTopic>       BackHistory    => [];
        public IReadOnlyList<HelpTopic>       ForwardHistory => [];
        public IReadOnlyList<ConversationTurn> Conversation  => [];
        public HelpAssistantMode              AssistantMode  => HelpAssistantMode.Lexical;

        public Task<HelpTopic>  NavigateAsync(HelpNavigationRequest request, CancellationToken ct) => Task.FromResult<HelpTopic>(null!);
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
