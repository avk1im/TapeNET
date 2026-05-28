using System.Text;
using HelpNET.Content;
using HelpNET.Indexing;

namespace HelpNET.Assistants;

/// <summary>
/// A pure-lexical assistant that answers queries using BM25 search combined with
/// intent matching.  No AI model is required.
/// </summary>
/// <remarks>
/// The response is formatted as Markdown: a short intro line followed by numbered
/// excerpts, each with a heading-like title and a short citation link.
/// </remarks>
public sealed class LexicalHelpAssistant : IHelpAssistant
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int    DefaultTopK           = 5;
    private const float  IntentBoostWeight     = 0.4f;  // weight when blending intent score into BM25
    private const float  LowConfidenceThreshold = 0.05f;

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly BM25HelpIndex  _bm25;
    private readonly IntentMatcher  _intent;
    private readonly HelpContentStore _store;
    private readonly int            _topK;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <param name="bm25">Pre-built BM25 index over the corpus.</param>
    /// <param name="intent">Intent matcher over the same corpus.</param>
    /// <param name="store">Content store, used for related-topic look-ups.</param>
    /// <param name="topK">Maximum number of excerpts to include in the response.</param>
    public LexicalHelpAssistant(
        BM25HelpIndex   bm25,
        IntentMatcher   intent,
        HelpContentStore store,
        int             topK = DefaultTopK)
    {
        _bm25   = bm25;
        _intent = intent;
        _store  = store;
        _topK   = topK;
    }

    /// <inheritdoc/>
    public HelpAssistantMode Mode => HelpAssistantMode.Lexical;

    // ── IHelpAssistant ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<HelpAssistantResponse> AskAsync(
        HelpAssistantRequest request,
        CancellationToken    ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. BM25 search
        var bm25Hits = _bm25.Search(request.Query, _topK * 2);

        // 2. Intent matching
        var intentHits = _intent.Match(request.Query, _topK * 2);

        // 3. Build a merged score map (topic id → blended score).
        var scores = new Dictionary<string, (HelpTopic Topic, float Score, string Excerpt)>(
            StringComparer.OrdinalIgnoreCase);

        float maxBm25 = bm25Hits.Count > 0 ? bm25Hits[0].Score : 1f;
        foreach (var hit in bm25Hits)
        {
            var normScore = maxBm25 > 0 ? hit.Score / maxBm25 : 0f;
            scores[hit.Topic.Id] = (hit.Topic, normScore, hit.Excerpt);
        }

        foreach (var (topic, intentScore) in intentHits)
        {
            if (scores.TryGetValue(topic.Id, out var existing))
            {
                // Blend: keep BM25 dominant but give intent a boost.
                var blended = existing.Score + IntentBoostWeight * intentScore;
                scores[topic.Id] = (existing.Topic, blended, existing.Excerpt);
            }
            else
            {
                scores[topic.Id] = (topic, IntentBoostWeight * intentScore,
                    topic.PlainText.Length > 200
                        ? topic.PlainText[..200] + "…"
                        : topic.PlainText);
            }
        }

        // 4. Sort merged results and take topK.
        var ranked = scores.Values
            .OrderByDescending(x => x.Score)
            .Take(_topK)
            .ToList();

        // 5. Build the Markdown response.
        float confidence = ranked.Count > 0 ? Math.Min(ranked[0].Score, 1f) : 0f;

        HelpAssistantResponse response;

        if (ranked.Count == 0 || confidence < LowConfidenceThreshold)
        {
            response = new HelpAssistantResponse(
                AnswerMarkdown:   "I couldn't find relevant help topics for your question. " +
                                  "Try different keywords or browse the contents.",
                Citations:        [],
                SuggestedTopics:  [],
                SuggestedActions: [],
                Confidence:       0f,
                Mode:             Mode);
        }
        else
        {
            var md         = BuildMarkdown(ranked);
            var citations  = ranked
                .Select(r => new HelpCitation(r.Topic.Id, r.Topic.Title, r.Excerpt))
                .ToList()
                .AsReadOnly();
            var suggested  = BuildSuggested(ranked);

            response = new HelpAssistantResponse(
                AnswerMarkdown:   md,
                Citations:        citations,
                SuggestedTopics:  suggested,
                SuggestedActions: [],
                Confidence:       confidence,
                Mode:             Mode);
        }

        return Task.FromResult(response);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildMarkdown(
        List<(HelpTopic Topic, float Score, string Excerpt)> ranked)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Here are the most relevant help topics:\n");

        for (int i = 0; i < ranked.Count; i++)
        {
            var (topic, _, excerpt) = ranked[i];
            sb.AppendLine($"**{i + 1}. [{topic.Title}](help://topic/{topic.Id})**");
            sb.AppendLine();
            sb.AppendLine(excerpt);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private IReadOnlyList<HelpTopicRef> BuildSuggested(
        List<(HelpTopic Topic, float Score, string Excerpt)> ranked)
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result  = new List<HelpTopicRef>();

        foreach (var (topic, _, _) in ranked)
            seen.Add(topic.Id);

        foreach (var (topic, _, _) in ranked)
        {
            foreach (var relId in topic.RelatedTopicIds)
            {
                if (seen.Contains(relId)) continue;
                var rel = _store.GetById(relId);
                if (rel is null)  continue;
                result.Add(new HelpTopicRef(rel.Id, rel.Title));
                seen.Add(relId);
                if (result.Count >= 4) goto done;
            }
        }
        done:
        return result.AsReadOnly();
    }
}
