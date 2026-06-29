namespace HelpNET.Content;

/// <summary>
/// Parses walkthrough steps from the body of a <c>kind: walkthrough</c> topic.
/// <para>
/// Steps are authored as <c>## [ControlName] Title</c> sections in the topic body.
/// The control name is slugified via <see cref="HelpSlug.From"/> so authors may write
/// the natural display name (e.g. <c>[Backup sets list]</c>) or the pre-slugified form
/// (<c>[backup-sets-list]</c>) interchangeably.
/// </para>
/// <para>
/// Action steps use the grammar <c>## [action:&lt;id&gt;] Title</c>; the action id is
/// stored verbatim in <see cref="WalkthroughStep.ActionId"/>.
/// </para>
/// </summary>
internal static class WalkthroughParser
{
    // Sentinel prefix for action steps inside [brackets].
    private const string ActionPrefix = "action:";

    /// <summary>
    /// Splits <paramref name="body"/> into walkthrough steps by parsing every
    /// <c>## [Target] Title</c> H2 section header, then collecting the body text
    /// that follows until the next H2 or end-of-string.
    /// Returns an empty list when the body contains no recognisable step sections.
    /// </summary>
    public static IReadOnlyList<WalkthroughStep> ParseSteps(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];

        var steps = new List<WalkthroughStep>();

        foreach (var section in SplitH2Sections(body))
        {
            // First line of the section is the ## heading; rest is the body.
            int nl = section.IndexOf('\n');
            var header  = (nl < 0 ? section : section[..nl]).Trim();
            var content = (nl < 0 ? string.Empty : section[(nl + 1)..]).Trim();

            // Must look like  ## [something] rest-of-title
            if (!header.StartsWith("## [", StringComparison.Ordinal)) continue;
            int rb = header.IndexOf(']', 4);
            if (rb < 0) continue;

            var target = header[4..rb].Trim();
            var title  = header[(rb + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(title)) continue; // malformed — skip

            if (target.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Action step: [action:<id>] Title
                var actionId = target[ActionPrefix.Length..].Trim();
                steps.Add(new WalkthroughStep(string.Empty, title, content, ActionId: actionId));
            }
            else
            {
                // Control step: slugify the target name so display names and slug forms both work.
                var slug = HelpSlug.From(target);
                steps.Add(new WalkthroughStep(slug, title, content));
            }
        }

        return steps.AsReadOnly();
    }

    // ── H2 section splitter ───────────────────────────────────────────────────
    //  Splits the text on every "## " line, keeping the heading on the same
    //  section block as its body.  Sections before the first ## are discarded
    //  (front-matter / topic intro text).

    private static IEnumerable<string> SplitH2Sections(string text)
    {
        var lines  = text.Split('\n');
        var buffer = new System.Text.StringBuilder();
        bool inSection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("## ", StringComparison.Ordinal)
                || line.Equals("##", StringComparison.Ordinal))
            {
                if (inSection && buffer.Length > 0)
                {
                    yield return buffer.ToString().TrimEnd();
                    buffer.Clear();
                }
                inSection = true;
                buffer.AppendLine(line);
            }
            else if (inSection)
            {
                buffer.AppendLine(line);
            }
        }

        if (inSection && buffer.Length > 0)
            yield return buffer.ToString().TrimEnd();
    }
}
