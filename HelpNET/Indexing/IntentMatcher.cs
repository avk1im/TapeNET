using System.Text.RegularExpressions;
using HelpNET.Content;

namespace HelpNET.Indexing;

/// <summary>
/// Matches a natural-language query against the <c>intents</c> phrases defined in
/// help topics, using normalised Jaccard-like overlap scoring.
/// </summary>
/// <remarks>
/// Intent matching complements BM25: BM25 finds topics whose body text overlaps the
/// query; intent matching finds topics whose authors explicitly described the user's
/// goal.  Scores from both sources are blended by the assistant.
/// </remarks>
public sealed class IntentMatcher
{
    private readonly IReadOnlyList<HelpTopic> _topics;

    /// <param name="topics">The topic corpus to match against.</param>
    public IntentMatcher(IReadOnlyList<HelpTopic> topics)
        => _topics = topics;

    /// <summary>
    /// Scores every topic against <paramref name="query"/> and returns those whose
    /// best-intent score exceeds <paramref name="threshold"/>, ordered descending.
    /// </summary>
    /// <param name="query">The user's natural-language query.</param>
    /// <param name="topK">Maximum number of results.</param>
    /// <param name="threshold">
    /// Minimum score (0–1) to include a topic.  Defaults to 0.25.
    /// </param>
    public IReadOnlyList<(HelpTopic Topic, float Score)> Match(
        string query,
        int    topK      = 5,
        float  threshold = 0.25f)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var queryTokens = TokenizeIntent(query).ToHashSet(StringComparer.Ordinal);
        if (queryTokens.Count == 0)
            return [];

        var scored = new List<(HelpTopic Topic, float Score)>();

        foreach (var topic in _topics)
        {
            float best = 0f;
            foreach (var intent in topic.Intents)
            {
                var intentTokens = TokenizeIntent(intent).ToHashSet(StringComparer.Ordinal);
                if (intentTokens.Count == 0) continue;

                // Normalised intersection / union (Jaccard) but skewed toward recall:
                // use query-token overlap to reward intents that cover the query.
                int intersection = queryTokens.Count(t => intentTokens.Contains(t));
                if (intersection == 0) continue;

                float recall    = (float)intersection / queryTokens.Count;
                float precision = (float)intersection / intentTokens.Count;
                float f1        = 2f * recall * precision / (recall + precision);

                if (f1 > best) best = f1;
            }

            if (best >= threshold)
                scored.Add((topic, best));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored.Take(topK).ToList().AsReadOnly();
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    private static readonly Regex s_intentTokenizer =
        new(@"[a-z0-9]+", RegexOptions.Compiled);

    // A broader stop-word set for intent matching — short function words that
    // add noise when matching short intent phrases.
    private static readonly HashSet<string> s_intentStop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "in", "to", "for", "with",
        "on", "at", "by", "from", "is", "it", "this", "that", "be", "are",
        "was", "were", "has", "have", "had", "not", "no", "can", "will",
        "you", "your", "we", "our", "as", "if", "do", "does", "did",
        "how", "what", "when", "where", "why", "which", "who", "i",
        "me", "my", "get", "set", "use", "want",
    };

    internal static IEnumerable<string> TokenizeIntent(string text)
    {
        foreach (Match m in s_intentTokenizer.Matches(text.ToLowerInvariant()))
        {
            var t = m.Value;
            if (t.Length > 1 && !s_intentStop.Contains(t))
                yield return t;
        }
    }
}
