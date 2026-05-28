using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Roundtrip;

namespace HelpNET.Content;

/// <summary>
/// Parses Markdown documents with YAML front-matter into <see cref="HelpTopic"/> records.
/// </summary>
/// <remarks>
/// The front-matter block must be delimited by <c>---</c> lines at the very start of the
/// document.  Supported field types are strings, boolean values, and YAML sequence lists
/// (either block style <c>- item</c> or flow style <c>[a, b, c]</c>).
/// Unrecognised fields are silently ignored so the format can evolve without breaking older
/// parsers.
/// </remarks>
internal static class FrontMatterParser
{
    // ── Regex helpers ────────────────────────────────────────────────────────

    // Matches the opening and closing --- delimiters.
    private static readonly Regex s_frontMatterBlock =
        new(@"^\s*---\s*\r?\n(?<fm>[\s\S]*?)\r?\n\s*---\s*(\r?\n|$)",
            RegexOptions.Compiled | RegexOptions.Multiline);

    // A single "key: value" line.
    private static readonly Regex s_scalarLine =
        new(@"^(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<val>.+)$",
            RegexOptions.Compiled);

    // A mapping key with no inline value (marks start of a block sequence or
    // an empty value).
    private static readonly Regex s_keyOnly =
        new(@"^(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*$",
            RegexOptions.Compiled);

    // A block-sequence item "- value".
    private static readonly Regex s_seqItem =
        new(@"^\s+-\s+(?<val>.+)$", RegexOptions.Compiled);

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the YAML front-matter from <paramref name="markdown"/> and returns a
    /// dictionary of fields plus the body text (everything after the closing <c>---</c>).
    /// </summary>
    /// <returns>
    /// <c>(fields, body)</c> where <c>fields</c> is a case-insensitive
    /// <c>string → object</c> map and <c>body</c> is the Markdown body without front-matter.
    /// Returns empty fields and the full input if no front-matter block is found.
    /// </returns>
    public static (Dictionary<string, object> Fields, string Body) Parse(string markdown)
    {
        var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var m = s_frontMatterBlock.Match(markdown);
        if (!m.Success)
            return (fields, markdown);

        var fmText = m.Groups["fm"].Value;
        var body   = markdown.Substring(m.Index + m.Length);

        ParseFields(fmText, fields);
        return (fields, body!);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void ParseFields(string fmText, Dictionary<string, object> fields)
    {
        var lines = fmText.Split('\n');
        string? currentListKey = null;
        var     currentList    = new List<string>();

        void FlushList()
        {
            if (currentListKey is not null)
            {
                fields[currentListKey] = (IReadOnlyList<string>)currentList.AsReadOnly();
                currentListKey = null;
                currentList    = [];
            }
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Skip blank lines — they can appear between YAML fields
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Block sequence continuation
            var seqMatch = s_seqItem.Match(line);
            if (seqMatch.Success && currentListKey is not null)
            {
                currentList.Add(Unquote(seqMatch.Groups["val"].Value.Trim()));
                continue;
            }

            // Starting a new field — flush any pending list first
            FlushList();

            // "key: value" (scalar or flow-list)
            var scalarMatch = s_scalarLine.Match(line);
            if (scalarMatch.Success)
            {
                var key = scalarMatch.Groups["key"].Value;
                var val = scalarMatch.Groups["val"].Value.Trim();

                if (val.StartsWith('[') && val.EndsWith(']'))
                {
                    // Inline / flow sequence: [a, b, c]
                    var items = val[1..^1]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => Unquote(s.Trim()))
                        .ToList();
                    fields[key] = (IReadOnlyList<string>)items.AsReadOnly();
                }
                else
                {
                    fields[key] = ParseScalarValue(val);
                }
                continue;
            }

            // "key:" — start of a block sequence (or empty scalar)
            var keyMatch = s_keyOnly.Match(line);
            if (keyMatch.Success)
            {
                var key = keyMatch.Groups["key"].Value;
                currentListKey = key;
                // Do NOT flush yet — next lines may be sequence items
                currentList = [];
                // Register the key with an empty list for now;
                // FlushList() will overwrite it.
            }
        }

        FlushList();
    }

    private static object ParseScalarValue(string val)
    {
        if (bool.TryParse(val, out var b))
            return b;
        return Unquote(val);
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 &&
            ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1];
        return s;
    }

    // ── Typed field accessors used by HelpContentStore ───────────────────────

    internal static string? GetString(Dictionary<string, object> fields, string key)
        => fields.TryGetValue(key, out var v) && v is string s ? s : null;

    internal static bool GetBool(Dictionary<string, object> fields, string key, bool defaultValue)
    {
        if (!fields.TryGetValue(key, out var v)) return defaultValue;
        return v switch
        {
            bool b    => b,
            string s  => bool.TryParse(s, out var bv) ? bv : defaultValue,
            _         => defaultValue,
        };
    }

    internal static IReadOnlyList<string> GetList(Dictionary<string, object> fields, string key)
        => fields.TryGetValue(key, out var v) && v is IReadOnlyList<string> list
            ? list
            : [];
}

/// <summary>
/// Converts a Markdig AST to plain text for indexing purposes.
/// </summary>
internal static class PlainTextRenderer
{
    private static readonly MarkdownPipeline s_pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>
    /// Strips all Markdown formatting and returns plain text suitable for indexing.
    /// Code fences are replaced by their content; headings keep their text.
    /// </summary>
    public static string Render(string markdown)
    {
        // Strip common Markdown syntax characters via regex after converting
        // the document to its leaf text nodes via Markdig.
        var doc = Markdig.Markdown.Parse(markdown, s_pipeline);
        var sb  = new System.Text.StringBuilder();

        // Walk all block and inline nodes manually.
        foreach (var block in doc)
        {
            WalkBlock(block, sb);
        }

        // Collapse whitespace
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static void WalkBlock(Markdig.Syntax.MarkdownObject node, System.Text.StringBuilder sb)
    {
        if (node is Markdig.Syntax.Inlines.LiteralInline lit)
        {
            sb.Append(lit.Content).Append(' ');
            return;
        }
        if (node is Markdig.Syntax.Inlines.CodeInline code)
        {
            sb.Append(code.Content).Append(' ');
            return;
        }
        if (node is Markdig.Syntax.LeafBlock leaf && leaf.Inline is not null)
        {
            foreach (var inline in leaf.Inline)
                WalkBlock(inline, sb);
            return;
        }
        if (node is Markdig.Syntax.ContainerBlock container)
        {
            foreach (var child in container)
                WalkBlock(child, sb);
            return;
        }
        if (node is Markdig.Syntax.Inlines.ContainerInline containerInline)
        {
            foreach (var child in containerInline)
                WalkBlock(child, sb);
        }
    }
}
