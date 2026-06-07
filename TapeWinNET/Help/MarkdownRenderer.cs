using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

using Markdig;

using HelpNET.Content;
using HelpNET.Session;

namespace TapeWinNET.Help;

/// <summary>
/// Converts <see cref="HelpTopic.MarkdownBody"/> to a WPF <see cref="FlowDocument"/>
/// and intercepts <c>help://</c> navigation URIs, routing them to the appropriate
/// <see cref="IHelpSession"/> method.
/// </summary>
public sealed partial class MarkdownRenderer(IHelpSession session, IHelpActionRouter actions)
{
    // ── Markdig pipeline (shared; pipelines are thread-safe after construction) ──
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    // Matches bare topic-id citations the AI places in answers, e.g. [concepts.backup-sets],
    // [dialog.restore], or single-word ids like [home] and [backup].
    // NOT already followed by '(' (which would make it a proper markdown link).
    // Group 1 captures the id; we rewrite it to [Display Title](help://topic/id).
    private static readonly Regex _topicRefPattern =
        MyRegex();

    // Info-blue brush matching WarningFg.Info / WarningBr.Info from App.xaml.
    // Used to tint glossary hyperlinks so they are visually distinct from topic links.
    private static readonly SolidColorBrush _glossaryFg =
        new(Color.FromRgb(0x00, 0x78, 0xD4));   // #0078D4 — Windows accent blue

    private readonly IHelpSession     _session = session;
    private readonly IHelpActionRouter _actions = actions;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the user clicks a <c>help://glossary/&lt;slug&gt;</c> link.
    /// The event argument is the slug string.  The host (HelpPane) handles display.
    /// </summary>
    public event EventHandler<string>? GlossaryLinkClicked;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders a Markdown string to a <see cref="FlowDocument"/>.
    /// Bare topic-id citations emitted by the AI assistant (e.g. <c>[concepts.backup-sets]</c>)
    /// are rewritten to proper markdown links before Markdig processes the text, so they
    /// appear as clickable hyperlinks in the chat pane.
    /// Hyperlink clicks are handled in <c>HelpPane.xaml.cs</c> via
    /// <c>PreviewMouseLeftButtonDown</c> + <see cref="HandleNavigate"/>.
    /// Glossary hyperlinks (<c>help://glossary/…</c>) receive a distinctive dashed underline
    /// and info-blue foreground so users can tell them apart from topic navigation links.
    /// </summary>
    public FlowDocument Render(string markdown)
    {
        // Rewrite bare [topic.id] citations → [Display Title](help://topic/topic.id)
        // Fall back to the raw id as link text when the topic is not found in the store.
        var linked = _topicRefPattern.Replace(markdown, m =>
        {
            var id    = m.Groups[1].Value;
            var title = _session.TryGetTopicTitle(id) ?? id;
            return $"[{title}](help://topic/{id})";
        });

        var doc = Markdig.Wpf.Markdown.ToFlowDocument(linked, _pipeline);
        // Markdig.Wpf bakes in a fixed PageWidth; clear it so the document reflows
        //  to the host control's actual width (works for both RichTextBox panes).
        doc.PageWidth = double.NaN;

        // Post-process: style every help://glossary/ hyperlink distinctly.
        StyleGlossaryLinks(doc);

        return doc;
    }

    // ── Glossary link styling ─────────────────────────────────────────────────

    /// <summary>
    /// Walks all <see cref="Hyperlink"/>s in <paramref name="doc"/> and, for those whose
    /// <see cref="Hyperlink.NavigateUri"/> points to <c>help://glossary/…</c>:
    /// <list type="bullet">
    ///   <item>Sets foreground to info-blue.</item>
    ///   <item>Sets text decoration to dashed underline (visually distinct from topic links).</item>
    ///   <item>Attaches a <see cref="System.Windows.Controls.ToolTip"/> with the definition text,
    ///    so hover reveals the definition without a click.</item>
    /// </list>
    /// </summary>
    private void StyleGlossaryLinks(FlowDocument doc)
    {
        foreach (var hl in CollectHyperlinks(doc))
        {
            var uri = hl.NavigateUri?.OriginalString;
            if (uri is null || !uri.StartsWith("help://glossary/", StringComparison.OrdinalIgnoreCase))
                continue;

            // Dashed underline
            var decoration = new TextDecoration
            {
                Location   = TextDecorationLocation.Underline,
                Pen        = new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)), 1)
                {
                    DashStyle = DashStyles.Dash,
                },
                PenOffset  = 1.5,
            };
            hl.TextDecorations = [decoration];
            hl.Foreground = _glossaryFg;

            // Tooltip: resolve the definition eagerly; fall back to the slug itself.
            var slug = uri["help://glossary/".Length..];
            var def  = _session.TryGetGlossaryDefinition(slug);
            if (def is not null)
            {
                // Strip remaining markdown bold markers from the definition for tooltip text.
                var tooltipText = def.Replace("**", string.Empty);
                hl.ToolTip = tooltipText;
            }
        }
    }

    /// <summary>Yields every <see cref="Hyperlink"/> in the document, depth-first.</summary>
    private static IEnumerable<Hyperlink> CollectHyperlinks(FlowDocument doc)
    {
        var stack = new Stack<TextElement>();
        foreach (var b in doc.Blocks)
            if (b is TextElement te) stack.Push(te);

        while (stack.Count > 0)
        {
            var el = stack.Pop();
            if (el is Hyperlink hl)
                yield return hl;

            IEnumerable<TextElement> children = el switch
            {
                Paragraph p   => p.Inlines.OfType<TextElement>(),
                Section   s   => s.Blocks.OfType<TextElement>(),
                List      l   => l.ListItems.OfType<TextElement>(),
                ListItem  li  => li.Blocks.OfType<TextElement>(),
                Hyperlink h   => h.Inlines.OfType<TextElement>(),
                Span      sp  => sp.Inlines.OfType<TextElement>(),
                _             => [],
            };
            foreach (var c in children)
                stack.Push(c);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a navigation URI:
    /// <list type="bullet">
    ///   <item><c>help://topic/&lt;id&gt;</c> — navigates the help session.</item>
    ///   <item><c>help://glossary/&lt;slug&gt;</c> — raises <see cref="GlossaryLinkClicked"/>
    ///    so the host (HelpPane) can show an inline popup.</item>
    ///   <item><c>help://action/&lt;id&gt;</c> — invokes a registered <see cref="IHelpActionRouter"/> command.</item>
    ///   <item><c>http(s)://…</c> — opens in the default browser.</item>
    /// </list>
    /// </summary>
    internal void HandleNavigate(Uri uri)
    {
        if (uri.Scheme.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = HelpUri.TryParse(uri.OriginalString);
            if (parsed is null) return;

            switch (parsed.Kind)
            {
                case HelpUriKind.Topic:
                    // Fire-and-forget navigation on the UI thread — OK because
                    //  HelpSession marshals to the dispatcher internally.
                    _ = _session.NavigateAsync(
                        new HelpNavigationRequest(parsed.Target),
                        CancellationToken.None);
                    break;

                case HelpUriKind.Glossary:
                    // Raise the event; HelpPane shows an inline Popup with the definition.
                    GlossaryLinkClicked?.Invoke(this, parsed.Target);
                    break;

                case HelpUriKind.Action:
                    _actions.Invoke(parsed.Target);
                    break;
            }
        }
        else if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
              || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            // Open external links in the default browser
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* best effort */ }
        }
    }

    // Single-word ids like [home] are valid (no dot/hyphen required); the pattern
    //  also continues to match compound ids like [concepts.backup-sets].
    [GeneratedRegex(@"\[([a-z][a-z0-9]*(?:[.\-][a-z0-9]+)*)\](?!\()", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
