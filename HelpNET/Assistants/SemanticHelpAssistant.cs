using System.Text;
using HelpNET.Content;
using HelpNET.Embeddings;
using HelpNET.Retrieval;

namespace HelpNET.Assistants;

/// <summary>
/// An assistant that ranks results using semantic (vector) search but does not
/// synthesise answers with an LLM.  Returns the top-K excerpts as Markdown,
/// identical in shape to <see cref="LexicalHelpAssistant"/> but ranked by
/// cosine similarity rather than BM25.
/// </summary>
public sealed class SemanticHelpAssistant : IHelpAssistant
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int   DefaultTopK            = 5;
    private const float LowConfidenceThreshold = 0.15f;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly IHelpEmbeddingIndex _index;
    private readonly HelpContentStore    _store;
    private readonly int                 _topK;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <param name="index">Embedding-based index for semantic search.</param>
    /// <param name="store">Content store for related-topic lookups.</param>
    /// <param name="topK">Maximum number of excerpts to include.</param>
    public SemanticHelpAssistant(
        IHelpEmbeddingIndex index,
        HelpContentStore    store,
        int                 topK = DefaultTopK)
    {
        _index = index;
        _store = store;
        _topK  = topK;
    }

    /// <inheritdoc/>
    public HelpAssistantMode Mode => HelpAssistantMode.Semantic;

    // ── IHelpAssistant ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<HelpAssistantResponse> AskAsync(
        HelpAssistantRequest request,
        CancellationToken    ct)
    {
        ct.ThrowIfCancellationRequested();

        var excerpts = await _index.SearchAsync(request.Query, _topK, ct)
                                   .ConfigureAwait(false);

        // Low-confidence: nothing semantically close enough.
        if (excerpts.Count == 0 || excerpts[0].Score < LowConfidenceThreshold)
            return NoMatch(request.Query);

        float confidence = Math.Min(excerpts[0].Score, 1f);

        var citations = excerpts
            .Select(e => new HelpCitation(e.Topic.Id, e.Topic.Title, e.Snippet))
            .ToList();

        var suggestedIds = new HashSet<string>(citations.Select(c => c.TopicId));
        var suggested    = BuildSuggested(citations, suggestedIds);
        var markdown     = BuildMarkdown(request.Query, excerpts);

        return new HelpAssistantResponse(
            markdown,
            citations,
            suggested,
            [],
            confidence,
            HelpAssistantMode.Semantic);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HelpAssistantResponse NoMatch(string query) =>
        new($"No closely matching topics found for **{query}**. " +
             "Try rephrasing or browse the topic list.",
            [],
            [],
            [],
            0f,
            HelpAssistantMode.Semantic);

    /// <summary>Builds the Markdown response body from the ranked excerpts.</summary>
    private static string BuildMarkdown(string query, IReadOnlyList<HelpExcerpt> excerpts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Semantic results for:** {query}");
        sb.AppendLine();

        for (int i = 0; i < excerpts.Count; i++)
        {
            var e = excerpts[i];
            sb.AppendLine(
                $"{i + 1}. **[{e.Heading}](help://topic/{e.Topic.Id})** " +
                $"— {e.Topic.Title}");
            if (!string.IsNullOrWhiteSpace(e.Snippet))
                sb.AppendLine($"   > {e.Snippet.Replace("\n", " ")}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Builds the suggested-topics list from related topics of cited items.</summary>
    private List<HelpTopicRef> BuildSuggested(
        IReadOnlyList<HelpCitation> citations,
        HashSet<string>             excludeIds)
    {
        var seen       = new HashSet<string>(excludeIds);
        var suggested  = new List<HelpTopicRef>();

        foreach (var c in citations)
        {
            var topic = _store.GetById(c.TopicId);
            if (topic is null) continue;

            foreach (var relId in topic.RelatedTopicIds)
            {
                if (!seen.Add(relId)) continue;

                var rel = _store.GetById(relId);
                if (rel is not null)
                    suggested.Add(new HelpTopicRef(rel.Id, rel.Title));
            }
        }

        return suggested;
    }
}
