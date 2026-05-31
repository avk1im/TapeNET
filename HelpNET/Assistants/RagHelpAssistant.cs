using System.Text;
using System.Text.RegularExpressions;
using HelpNET.Assistants.SystemPrompts;
using HelpNET.Content;
using HelpNET.Retrieval;
using Microsoft.Extensions.AI;

namespace HelpNET.Assistants;

/// <summary>
/// RAG (Retrieval-Augmented Generation) assistant that combines hybrid retrieval
/// with LLM-based answer synthesis.
/// </summary>
/// <remarks>
/// Retrieval is performed by an <see cref="IHelpRetriever"/> (typically a
/// <see cref="HybridRetriever"/>).  The top excerpts are packed into a prompt
/// and sent to the <see cref="IChatClient"/>; citations are parsed from the
/// model's response by scanning for <c>[topic-id]</c> tags.
/// </remarks>
public sealed class RagHelpAssistant : IHelpAssistant
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int   DefaultTopK            = 5;
    private const float LowConfidenceThreshold = 0.05f;

    // Matches [topic.id] or [topic-id] style citation tags in LLM output.
    private static readonly Regex s_citationTag =
        new(@"\[(?<id>[a-z0-9][\w.\-]*)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly IHelpRetriever   _retriever;
    private readonly IChatClient      _chatClient;
    private readonly HelpContentStore _store;
    private readonly int              _topK;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <param name="retriever">Hybrid or semantic retriever that fetches excerpts.</param>
    /// <param name="chatClient">LLM client used for answer synthesis.</param>
    /// <param name="store">Content store for citation and suggestion resolution.</param>
    /// <param name="topK">Maximum number of excerpts to feed to the LLM.</param>
    public RagHelpAssistant(
        IHelpRetriever   retriever,
        IChatClient      chatClient,
        HelpContentStore store,
        int              topK = DefaultTopK)
    {
        _retriever  = retriever;
        _chatClient = chatClient;
        _store      = store;
        _topK       = topK;
    }

    /// <inheritdoc/>
    public HelpAssistantMode Mode => HelpAssistantMode.Rag;

    // ── IHelpAssistant ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<HelpAssistantResponse> AskAsync(
        HelpAssistantRequest request,
        CancellationToken    ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Retrieve excerpts.
        var excerpts = await _retriever.RetrieveAsync(request.Query, _topK, ct)
                                       .ConfigureAwait(false);

        float retrievalConfidence = excerpts.Count > 0 ? excerpts[0].Score : 0f;

        // 1a. Context bias — when the caller is viewing a specific topic (e.g. a
        //     dialog asking about its own controls), ensure that topic's *full*
        //     body is available to the LLM as a top-priority excerpt. Snippet-only
        //     retrieval often omits the exact section the user is asking about, so
        //     the question the user is most likely to ask cannot otherwise be
        //     answered from the snippets alone.
        excerpts = PrependCurrentTopic(request.CurrentTopicId, excerpts);

        // Only bail out when retrieval found nothing *and* there is no current-topic
        //  context to fall back on.
        if (excerpts.Count == 0 ||
            (retrievalConfidence < LowConfidenceThreshold && request.CurrentTopicId is null))
            return NoMatch(request.Query);

        // 2. Build the chat messages.
        var messages = BuildMessages(request, excerpts);

        // 3. Call the LLM — using streaming so that cancellation (abort) takes effect
        //    immediately rather than waiting for the full response to be buffered.
        var sb = new StringBuilder();
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, null, ct)
                                                .ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(update.Text);
        }

        string answerMarkdown = sb.ToString();

        // 4. Parse [topic-id] citation tags from the LLM response.
        var citations  = ParseCitations(answerMarkdown, excerpts);
        var cited      = new HashSet<string>(citations.Select(c => c.TopicId));
        var suggested  = BuildSuggested(cited);

        // When we injected the current topic as authoritative context, treat the
        //  answer as fully confident even if plain retrieval scored low.
        float confidence = request.CurrentTopicId is not null
            ? 1f
            : Math.Min(retrievalConfidence, 1f);

        return new HelpAssistantResponse(
            answerMarkdown,
            citations,
            suggested,
            [],
            confidence,
            HelpAssistantMode.Rag);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HelpAssistantResponse NoMatch(string query) =>
        new($"No relevant help topics found for **{query}**. " +
             "Try rephrasing your question or browse the topic list.",
            [],
            [],
            [],
            0f,
            HelpAssistantMode.Rag);

    /// <summary>
    /// Ensures the topic the user is currently viewing (<paramref name="currentTopicId"/>)
    /// is present at the front of the excerpt list with its <b>full</b> body, not just a
    /// retrieval snippet. This biases the LLM toward the active context — essential for
    /// dialogs that ask about their own controls, where the relevant section may not
    /// surface (or may be truncated) in plain query-based retrieval.
    /// </summary>
    private IReadOnlyList<HelpExcerpt> PrependCurrentTopic(
        string?                    currentTopicId,
        IReadOnlyList<HelpExcerpt> excerpts)
    {
        if (string.IsNullOrWhiteSpace(currentTopicId))
            return excerpts;

        var topic = _store.GetById(currentTopicId);
        if (topic is null || !topic.IncludeInAiCorpus)
            return excerpts;

        // Use the full plain text so the entire topic is available to the model.
        var contextExcerpt = new HelpExcerpt(topic, topic.Title, topic.PlainText, 1f);

        // Drop any snippet-only excerpt for the same topic to avoid duplication.
        var ordered = new List<HelpExcerpt>(excerpts.Count + 1) { contextExcerpt };
        ordered.AddRange(excerpts.Where(e =>
            !e.Topic.Id.Equals(topic.Id, StringComparison.OrdinalIgnoreCase)));

        return ordered;
    }

    /// <summary>
    /// Builds the <see cref="ChatMessage"/> list for the LLM call.
    /// Includes the system prompt, any prior conversation turns, and a user
    /// message containing the numbered excerpts and the current question.
    /// </summary>
    private static List<ChatMessage> BuildMessages(
        HelpAssistantRequest        request,
        IReadOnlyList<HelpExcerpt>  excerpts)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, HelpRagSystemPrompt.Build())
        };

        // Prior conversation turns (multi-turn context for the LLM).
        foreach (var turn in request.History)
        {
            messages.Add(new ChatMessage(ChatRole.User,      turn.Query));
            messages.Add(new ChatMessage(ChatRole.Assistant, turn.AnswerMarkdown));
        }

        // User message: numbered excerpts + the current question.
        var userMsg = new StringBuilder();
        userMsg.AppendLine("## Help excerpts");
        userMsg.AppendLine();

        for (int i = 0; i < excerpts.Count; i++)
        {
            var e = excerpts[i];
            userMsg.AppendLine($"### [{e.Topic.Id}] {e.Heading}");
            userMsg.AppendLine(e.Snippet);
            userMsg.AppendLine();
        }

        userMsg.AppendLine("---");
        userMsg.AppendLine($"**Question:** {request.Query}");

        messages.Add(new ChatMessage(ChatRole.User, userMsg.ToString()));

        return messages;
    }

    /// <summary>
    /// Scans the LLM's Markdown output for <c>[topic-id]</c> tags and resolves
    /// them into <see cref="HelpCitation"/> records.  Only ids that appear in the
    /// provided excerpts are accepted (prevents hallucinated citations).
    /// </summary>
    private List<HelpCitation> ParseCitations(
        string                      answerMarkdown,
        IReadOnlyList<HelpExcerpt>  excerpts)
    {
        var validIds = new Dictionary<string, HelpExcerpt>(
            excerpts.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in excerpts)
            validIds.TryAdd(e.Topic.Id, e);

        var cited   = new LinkedList<HelpCitation>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in s_citationTag.Matches(answerMarkdown))
        {
            string id = m.Groups["id"].Value;
            if (!seen.Add(id)) continue;
            if (!validIds.TryGetValue(id, out var excerpt)) continue;

            cited.AddLast(new HelpCitation(
                excerpt.Topic.Id,
                excerpt.Topic.Title,
                excerpt.Snippet));
        }

        return [.. cited];
    }

    /// <summary>Builds the suggested-topics list from related topics of cited items.</summary>
    private List<HelpTopicRef> BuildSuggested(HashSet<string> citedIds)
    {
        var seen      = new HashSet<string>(citedIds, StringComparer.OrdinalIgnoreCase);
        var suggested = new List<HelpTopicRef>();

        foreach (var id in citedIds)
        {
            var topic = _store.GetById(id);
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
