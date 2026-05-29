using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;

using Markdig;

using HelpNET.Content;
using HelpNET.Session;

namespace TapeWinNET.Help;

/// <summary>
/// Converts <see cref="HelpTopic.MarkdownBody"/> to a WPF <see cref="FlowDocument"/>
/// and intercepts <c>help://</c> navigation URIs, routing them to the appropriate
/// <see cref="IHelpSession"/> method.
/// </summary>
public sealed class MarkdownRenderer
{
    // ── Markdig pipeline (shared; pipelines are thread-safe after construction) ──
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly IHelpSession _session;
    private readonly HelpActionRouter _actions;

    public MarkdownRenderer(IHelpSession session, HelpActionRouter actions)
    {
        _session = session;
        _actions = actions;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders a Markdown string to a <see cref="FlowDocument"/> and wires up
    /// <c>help://</c> hyperlink handlers.
    /// </summary>
    public FlowDocument Render(string markdown)
    {
        var document = Markdig.Wpf.Markdown.ToFlowDocument(markdown, _pipeline);

        // Walk all hyperlinks in the document and attach our handler
        foreach (var hyperlink in EnumerateHyperlinks(document))
        {
            // Capture the NavigateUri at the time of wiring
            var uri = hyperlink.NavigateUri;
            if (uri is null) continue;

            hyperlink.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                HandleNavigate(e.Uri ?? uri);
            };
        }

        return document;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a navigation URI:
    /// <list type="bullet">
    ///   <item><c>help://topic/&lt;id&gt;</c> — navigates the help session.</item>
    ///   <item><c>help://glossary/&lt;term&gt;</c> — shows a tooltip (Phase 5 fallback; full popover in Phase 7).</item>
    ///   <item><c>help://action/&lt;id&gt;</c> — invokes a registered <see cref="HelpActionRouter"/> command.</item>
    ///   <item><c>http(s)://…</c> — opens in the default browser.</item>
    /// </list>
    /// </summary>
    private void HandleNavigate(Uri uri)
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
                    // Phase 5: simple MessageBox fallback; Phase 7 will show an inline popover.
                    MessageBox.Show(
                        $"Glossary term: {parsed.Target}\n\n(Full glossary popovers available in a future update.)",
                        "Glossary",
                        MessageBoxButton.OK, MessageBoxImage.Information);
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

    /// <summary>Enumerates all <see cref="Hyperlink"/> inlines in a <see cref="FlowDocument"/>.</summary>
    private static IEnumerable<Hyperlink> EnumerateHyperlinks(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        foreach (var inline in EnumerateInlines(block))
            if (inline is Hyperlink hl)
                yield return hl;
    }

    private static IEnumerable<Inline> EnumerateInlines(TextElement element)
    {
        switch (element)
        {
            case Paragraph p:
                foreach (var i in p.Inlines)
                {
                    yield return i;
                    if (i is Span span)
                        foreach (var sub in EnumerateInlines(span))
                            yield return sub;
                }
                break;

            case Span s:
                foreach (var i in s.Inlines)
                {
                    yield return i;
                    if (i is Span sub)
                        foreach (var subsub in EnumerateInlines(sub))
                            yield return subsub;
                }
                break;

            case List list:
                foreach (var item in list.ListItems)
                    foreach (var block in item.Blocks)
                        foreach (var i in EnumerateInlines(block))
                            yield return i;
                break;

            case Section section:
                foreach (var block in section.Blocks)
                    foreach (var i in EnumerateInlines(block))
                        yield return i;
                break;
        }
    }
}
