using System.Text.RegularExpressions;
using HelpNET.Content;

namespace HelpNET.Indexing;

/// <summary>
/// BM25 (Okapi BM25) lexical index over help topic plain text.
/// </summary>
/// <remarks>
/// Standard BM25 parameters: k1 = 1.5, b = 0.75.
/// The index tokenizes text into lowercase alphabetic+digit terms, removes
/// single-character tokens and a small English stop-word list.
/// Keyword fields receive a 2× term-frequency boost.
/// </remarks>
public sealed class BM25HelpIndex : IHelpIndex
{
    // ── BM25 hyper-parameters ─────────────────────────────────────────────────

    private const float K1 = 1.5f;
    private const float B  = 0.75f;

    // ── Pre-built index ───────────────────────────────────────────────────────

    private readonly IReadOnlyList<HelpTopic>             _topics;
    private readonly int[]                                 _docLengths;      // term count per doc
    private readonly float                                 _avgDocLength;
    private readonly Dictionary<string, List<(int DocIdx, int Freq)>> _invertedIndex;

    // ── Construction ─────────────────────────────────────────────────────────

    private BM25HelpIndex(
        IReadOnlyList<HelpTopic>                          topics,
        int[]                                              docLengths,
        float                                              avgDocLength,
        Dictionary<string, List<(int DocIdx, int Freq)>> invertedIndex)
    {
        _topics        = topics;
        _docLengths    = docLengths;
        _avgDocLength  = avgDocLength;
        _invertedIndex = invertedIndex;
    }

    /// <summary>Builds the index from the given topic list.</summary>
    public static BM25HelpIndex Build(IReadOnlyList<HelpTopic> topics)
    {
        var docLengths    = new int[topics.Count];
        var invertedIndex = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        long totalTerms   = 0;

        for (int i = 0; i < topics.Count; i++)
        {
            var topic = topics[i];

            // Build the combined token bag: plain text + 2× keyword boost.
            var tokens = new List<string>(Tokenize(topic.PlainText));
            foreach (var kw in topic.Keywords)
                for (int k = 0; k < 2; k++) // 2× boost for keywords
                    tokens.AddRange(Tokenize(kw));

            // Count term frequencies.
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in tokens)
                tf[t] = tf.GetValueOrDefault(t) + 1;

            docLengths[i] = tf.Values.Sum();
            totalTerms   += docLengths[i];

            foreach (var (term, freq) in tf)
            {
                if (!invertedIndex.TryGetValue(term, out var postings))
                {
                    postings          = [];
                    invertedIndex[term] = postings;
                }
                postings.Add((i, freq));
            }
        }

        float avgLen = topics.Count > 0 ? (float)totalTerms / topics.Count : 1f;
        return new BM25HelpIndex(topics, docLengths, avgLen, invertedIndex);
    }

    // ── IHelpIndex ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public IReadOnlyList<HelpSearchHit> Search(string query, int topK)
    {
        if (string.IsNullOrWhiteSpace(query) || _topics.Count == 0)
            return [];

        var queryTerms = Tokenize(query).Distinct().ToArray();
        if (queryTerms.Length == 0)
            return [];

        var scores    = new float[_topics.Count];
        int n         = _topics.Count;

        foreach (var term in queryTerms)
        {
            if (!_invertedIndex.TryGetValue(term, out var postings))
                continue;

            int df = postings.Count;
            // IDF (standard BM25)
            float idf = MathF.Log((n - df + 0.5f) / (df + 0.5f) + 1f);

            foreach (var (docIdx, freq) in postings)
            {
                float dl        = _docLengths[docIdx];
                float tf        = (freq * (K1 + 1f))
                                / (freq + K1 * (1f - B + B * dl / _avgDocLength));
                scores[docIdx] += idf * tf;
            }
        }

        // Collect non-zero results, sort descending.
        var hits = new List<(int Idx, float Score)>();
        for (int i = 0; i < scores.Length; i++)
            if (scores[i] > 0f)
                hits.Add((i, scores[i]));

        hits.Sort((a, b) => b.Score.CompareTo(a.Score));

        var result = new List<HelpSearchHit>(Math.Min(topK, hits.Count));
        foreach (var (idx, score) in hits.Take(topK))
        {
            var topic   = _topics[idx];
            var excerpt = BuildExcerpt(topic, queryTerms);
            result.Add(new HelpSearchHit(topic, score, excerpt));
        }

        return result.AsReadOnly();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts a short excerpt from the topic's plain text that contains
    /// at least one query term.
    /// </summary>
    private static string BuildExcerpt(HelpTopic topic, string[] queryTerms)
    {
        const int ExcerptMaxChars = 200;

        var text  = topic.PlainText;
        if (text.Length == 0)
            return topic.Title;

        // Find the earliest position of any query term.
        int bestPos = text.Length;
        foreach (var term in queryTerms)
        {
            int idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < bestPos)
                bestPos = idx;
        }

        if (bestPos == text.Length)
            bestPos = 0; // no hit — start from the beginning

        int start = Math.Max(0, bestPos - 40);

        // If we're not at the very beginning, advance to the next word boundary
        //  so the excerpt never starts mid-word (leading truncation is jarring to read).
        if (start > 0)
        {
            int ws = start;
            while (ws < text.Length && !char.IsWhiteSpace(text[ws]))
                ws++;
            // Skip the whitespace itself; if we ran off the end just use original start.
            start = ws < text.Length ? ws + 1 : start;
        }

        int len   = Math.Min(ExcerptMaxChars, text.Length - start);
        var excerpt = text.Substring(start, len).Trim();

        if (start > 0)   excerpt = "…" + excerpt;
        if (start + len < text.Length) excerpt += "…";

        return excerpt;
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    private static readonly HashSet<string> s_stopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "in", "to", "for", "with",
        "on", "at", "by", "from", "is", "it", "this", "that", "be", "are",
        "was", "were", "has", "have", "had", "not", "no", "can", "will",
        "you", "your", "we", "our", "as", "if", "do", "does", "did",
    };

    private static readonly Regex s_tokenizer =
        new(@"[a-z0-9]+", RegexOptions.Compiled);

    internal static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (Match m in s_tokenizer.Matches(text.ToLowerInvariant()))
        {
            var t = m.Value;
            if (t.Length > 1 && !s_stopWords.Contains(t))
                yield return t;
        }
    }
}
