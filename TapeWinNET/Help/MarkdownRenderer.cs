using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

using Markdig;

using HelpNET.Content;
using HelpNET.Session;

namespace TapeWinNET.Help;

/// <summary>
/// Converts <see cref="HelpTopic.MarkdownBody"/> to a WPF <see cref="FlowDocument"/>
/// and intercepts <c>help://</c> navigation URIs, routing them to the appropriate
/// <see cref="IHelpSession"/> method.
/// </summary>
public sealed partial class MarkdownRenderer(IHelpSession session, HelpActionRouter actions)
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

    private readonly IHelpSession _session = session;
    private readonly HelpActionRouter _actions = actions;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders a Markdown string to a <see cref="FlowDocument"/>.
    /// Bare topic-id citations emitted by the AI assistant (e.g. <c>[concepts.backup-sets]</c>)
    /// are rewritten to proper markdown links before Markdig processes the text, so they
    /// appear as clickable hyperlinks in the chat pane.
    /// Hyperlink clicks are handled in <c>HelpPane.xaml.cs</c> via
    /// <c>PreviewMouseLeftButtonDown</c> + <see cref="HandleNavigate"/>.
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
        return doc;
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

    // Single-word ids like [home] are valid (no dot/hyphen required); the pattern
    //  also continues to match compound ids like [concepts.backup-sets].
    [GeneratedRegex(@"\[([a-z][a-z0-9]*(?:[.\-][a-z0-9]+)*)\](?!\()", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
