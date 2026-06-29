namespace HelpNET.Content;

/// <summary>
/// Loads <see cref="HelpTopic"/> records from an <see cref="IHelpContentSource"/>,
/// builds lookup indexes, and exposes retrieval operations used by the rest of the
/// HelpNET engine.
/// </summary>
/// <remarks>
/// Use <see cref="LoadAsync"/> to create an instance; the constructor is intentionally
/// not public so callers cannot construct a partially-initialised store.
/// </remarks>
public sealed class HelpContentStore
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, HelpTopic>              _byId;
    private readonly Dictionary<string, List<HelpTopic>>        _byHost;
    private readonly IReadOnlyList<HelpTopic>                   _all;

    // ── Construction ─────────────────────────────────────────────────────────

    private HelpContentStore(
        Dictionary<string, HelpTopic>       byId,
        Dictionary<string, List<HelpTopic>> byHost,
        IReadOnlyList<string>               duplicateIds)
    {
        _byId       = byId;
        _byHost     = byHost;
        _all        = [.. byId.Values];
        DuplicateIds = duplicateIds;
    }

    /// <summary>
    /// Loads and parses all documents from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The content source to load from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A fully populated <see cref="HelpContentStore"/>.
    /// Topics with duplicate ids are reported in <see cref="DuplicateIds"/>;
    /// the first occurrence wins.
    /// </returns>
    public static async Task<HelpContentStore> LoadAsync(
        IHelpContentSource source,
        CancellationToken  ct = default)
    {
        var byId       = new Dictionary<string, HelpTopic>(StringComparer.OrdinalIgnoreCase);
        var byHost     = new Dictionary<string, List<HelpTopic>>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<string>();

        await foreach (var raw in source.EnumerateAsync(ct))
        {
            var topic = ParseDocument(raw);
            if (topic is null)
                continue;

            if (!byId.TryAdd(topic.Id, topic))
            {
                duplicates.Add(topic.Id);
                continue; // first occurrence wins
            }

            if (topic.Host is not null)
            {
                if (!byHost.TryGetValue(topic.Host, out var list))
                {
                    list           = [];
                    byHost[topic.Host] = list;
                }
                list.Add(topic);
            }
        }

        return new HelpContentStore(byId, byHost, duplicates.AsReadOnly());
    }

    // ── Public properties ─────────────────────────────────────────────────────

    /// <summary>All topics in the store (order is load order).</summary>
    public IReadOnlyList<HelpTopic> All => _all;

    /// <summary>
    /// Topic ids that appeared more than once in the source.
    /// The first occurrence is kept; subsequent ones are skipped.
    /// </summary>
    public IReadOnlyList<string> DuplicateIds { get; }

    // ── Lookup API ────────────────────────────────────────────────────────────

    /// <summary>Returns the topic with the given id, or <c>null</c> if not found.</summary>
    public HelpTopic? GetById(string id)
        => _byId.TryGetValue(id, out var t) ? t : null;

    /// <summary>Returns all topics whose <c>Host</c> matches <paramref name="hostName"/>.</summary>
    public IReadOnlyList<HelpTopic> GetByHost(string hostName)
        => _byHost.TryGetValue(hostName, out var list)
            ? list.AsReadOnly()
            : [];

    /// <summary>
    /// Looks up a glossary entry by term id.
    /// Returns <c>null</c> if no topic with <c>Kind == Glossary</c> and the given id exists.
    /// </summary>
    public HelpTopic? GetGlossaryEntry(string termId)
    {
        var t = GetById(termId);
        return t?.Kind == HelpTopicKind.Glossary ? t : null;
    }

    // ── Definition-entry caches ───────────────────────────────────────────────

    // Lazily-built cache of glossary definitions (from the reference.glossary topic body).
    // Key: slug (via HelpSlug.From). Value: formatted "**Term** — definition" string.
    private Dictionary<string, string>? _glossaryCache;

    // Per-topic cache of control-help definitions (from each topic's ## Controls chapter).
    // Outer key: topic id (OrdinalIgnoreCase). Inner key: control-name slug.
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>>
        _controlCacheByTopic = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the plain-text definition for the given glossary term slug, or <c>null</c>
    /// when not found.
    /// <para>
    /// Terms are extracted from the <c>reference.glossary</c> topic body, where each entry
    /// is a paragraph of the form <c>**Term name** — definition text.</c>
    /// The <paramref name="termSlug"/> is the term's display name lowercased with spaces and
    /// slashes replaced by hyphens (e.g. <c>"backup-set"</c>, <c>"toc"</c>, <c>"fcl"</c>).
    /// </para>
    /// </summary>
    public string? GetGlossaryDefinition(string termSlug)
    {
        _glossaryCache ??= BuildGlossaryCache();
        return _glossaryCache.TryGetValue(
            termSlug.Trim().ToLowerInvariant(), out var def) ? def : null;
    }

    /// <summary>
    /// Returns a slug → definition map for the <c>## Controls</c> chapter of the
    /// topic with the given <paramref name="topicId"/>, or an empty map when the
    /// topic does not exist or has no <c>## Controls</c> chapter.
    /// The result is cached so repeated calls within the same session are free.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetControlDefinitions(string topicId)
    {
        if (_controlCacheByTopic.TryGetValue(topicId, out var cached))
            return cached;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (GetById(topicId) is { } topic)
            ParseDefinitionEntries(topic.MarkdownBody, map, sectionHeading: "Controls");

        IReadOnlyDictionary<string, string> result = map;
        _controlCacheByTopic[topicId] = result;
        return result;
    }

    // ── Shared definition-entry parser ────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="markdownBody"/> for bold-term definition entries of the
    /// form <c>**Term name** — definition text.</c> and inserts them into
    /// <paramref name="into"/> keyed by their <see cref="HelpSlug"/> value.
    /// <para>
    /// When <paramref name="sectionHeading"/> is <c>null</c>, the entire body is
    /// scanned (used for the flat glossary page).  When it is set (e.g.
    /// <c>"Controls"</c>), only the lines under that <c>## Heading</c> are scanned;
    /// scanning stops at the next <c>## </c> heading or the end of the body.
    /// </para>
    /// </summary>
    private static void ParseDefinitionEntries(
        string                      markdownBody,
        Dictionary<string, string>  into,
        string?                     sectionHeading = null)
    {
        bool   inSection       = sectionHeading is null; // whole-body scan starts immediately
        string sectionMarker   = sectionHeading is null
            ? string.Empty
            : $"## {sectionHeading}";

        foreach (var line in markdownBody.Split('\n'))
        {
            var trimmed = line.Trim();

            if (sectionHeading is not null)
            {
                // Enter the target section
                if (!inSection)
                {
                    if (trimmed.Equals(sectionMarker, StringComparison.OrdinalIgnoreCase))
                        inSection = true;
                    continue;
                }

                // Stop scanning when a new ## heading begins (but allow ### sub-headings)
                if (trimmed.StartsWith("## ", StringComparison.Ordinal)
                    && !trimmed.Equals(sectionMarker, StringComparison.OrdinalIgnoreCase))
                    break;
            }

            // Expected pattern: **Term name** — definition text.
            if (!trimmed.StartsWith("**", StringComparison.Ordinal))
                continue;

            var closeStars = trimmed.IndexOf("**", 2, StringComparison.Ordinal);
            if (closeStars < 0)
                continue;

            var term = trimmed[2..closeStars].Trim();
            if (string.IsNullOrEmpty(term))
                continue;

            // Everything after the closing ** and optional " — " is the definition.
            var rest = trimmed[(closeStars + 2)..].TrimStart();
            if (rest.StartsWith('—'))
                rest = rest[1..].TrimStart();
            else if (rest.StartsWith("--", StringComparison.Ordinal))
                rest = rest[2..].TrimStart();

            // Strip embedded help:// link syntax to produce clean plain text
            //  e.g. "[Foo](help://topic/foo)" → "Foo"
            rest = System.Text.RegularExpressions.Regex.Replace(
                rest, @"\[([^\]]+)\]\(help://[^\)]+\)", "$1");

            var slug = HelpSlug.From(term);

            if (!string.IsNullOrEmpty(slug) && !string.IsNullOrEmpty(rest))
                into[slug] = $"**{term}** — {rest}";
        }
    }

    /// <summary>Parses the <c>reference.glossary</c> topic body into a slug → definition map.</summary>
    private Dictionary<string, string> BuildGlossaryCache()
    {
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var glossaryTopic = GetById("reference.glossary");
        if (glossaryTopic is not null)
            ParseDefinitionEntries(glossaryTopic.MarkdownBody, cache, sectionHeading: null);
        return cache;
    }

    /// <summary>
    /// Returns all topics that include the given topic id in their
    /// <c>RelatedTopicIds</c> list, plus the topic's own related list — a
    /// simple bidirectional related-topics view.
    /// </summary>
    public IReadOnlyList<HelpTopicRef> GetRelated(string topicId)
    {
        var result = new List<HelpTopicRef>();

        // Topics the given topic explicitly links to
        if (_byId.TryGetValue(topicId, out var topic))
        {
            foreach (var relId in topic.RelatedTopicIds)
            {
                if (_byId.TryGetValue(relId, out var rel))
                    result.Add(new HelpTopicRef(rel.Id, rel.Title));
            }
        }

        // Topics that link back to the given topic
        foreach (var other in _all)
        {
            if (other.Id.Equals(topicId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (other.RelatedTopicIds.Any(r =>
                    r.Equals(topicId, StringComparison.OrdinalIgnoreCase)))
            {
                if (!result.Any(r => r.Id.Equals(other.Id, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new HelpTopicRef(other.Id, other.Title));
            }
        }

        return result.AsReadOnly();
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static HelpTopic? ParseDocument(HelpRawDocument raw)
    {
        var (fields, body) = FrontMatterParser.Parse(raw.Markdown);

        var id = FrontMatterParser.GetString(fields, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null; // id is required

        var title = FrontMatterParser.GetString(fields, "title") ?? id;
        var kind  = ParseKind(FrontMatterParser.GetString(fields, "kind"));
        var host  = FrontMatterParser.GetString(fields, "host");

        var keywords  = FrontMatterParser.GetList(fields, "keywords");
        var intents   = FrontMatterParser.GetList(fields, "intents");
        var related   = FrontMatterParser.GetList(fields, "related");

        var includeInAiCorpus = FrontMatterParser.GetBool(fields, "ai_excerpt", defaultValue: true);

        var plainText  = PlainTextRenderer.Render(body);
        var walkthrough = kind == HelpTopicKind.Walkthrough
            ? ParseWalkthrough(body)
            : null;

        return new HelpTopic(
            Id:               id,
            Title:            title,
            Kind:             kind,
            Host:             host,
            Keywords:         keywords,
            Intents:          intents,
            RelatedTopicIds:  related,
            MarkdownBody:     body,
            PlainText:        plainText,
            Walkthrough:      walkthrough,
            IncludeInAiCorpus: includeInAiCorpus);
    }

    private static HelpTopicKind ParseKind(string? s)
        => s?.ToLowerInvariant() switch
        {
            "concept"    => HelpTopicKind.Concept,
            "walkthrough"=> HelpTopicKind.Walkthrough,
            "reference"  => HelpTopicKind.Reference,
            "ui-map"     => HelpTopicKind.UiMap,
            "quickstart" => HelpTopicKind.QuickStart,
            "feature"    => HelpTopicKind.Feature,
            "dialog"     => HelpTopicKind.Dialog,
            "home"       => HelpTopicKind.Home,
            "glossary"   => HelpTopicKind.Glossary,
            _            => HelpTopicKind.Concept,
        };

    private static WalkthroughScript? ParseWalkthrough(string body)
    {
        // Steps are sourced from the topic body via WalkthroughParser,
        //  using ## [Target] Title section headers (see §12.2 / §12.4).
        var steps = WalkthroughParser.ParseSteps(body);
        return steps.Count > 0 ? new WalkthroughScript(steps) : null;
    }
}
