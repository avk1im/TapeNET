using System.Runtime.CompilerServices;
using System.Text;

using HelpNET.Assistants;
using HelpNET.Content;
using HelpNET.Indexing;
using HelpNET.Retrieval;
using HelpNET.Session;

using Microsoft.Extensions.AI;

using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="RagHelpAssistant"/> — the Retrieval-Augmented Generation
/// assistant that combines hybrid/lexical retrieval with LLM answer synthesis.
/// </summary>
/// <remarks>
/// A <see cref="RecordingChatClient"/> fake stands in for the LLM: it captures the
/// prompt messages it receives (so we can assert the retrieved excerpts were packed
/// in) and streams back a canned answer (so we can assert citation parsing). No real
/// model is required, so these tests run anywhere.
/// </remarks>
public class RagHelpAssistantTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static async Task<(RagHelpAssistant Assistant, HelpContentStore Store, RecordingChatClient Chat)>
        BuildAsync(string cannedAnswer)
    {
        var store = await TestContentFixture.LoadStoreAsync();
        var bm25  = BM25HelpIndex.Build(store.All);
        var chat  = new RecordingChatClient(cannedAnswer);
        // LexicalRetriever is internal; RagHelpAssistant accepts any IHelpRetriever,
        //  so we use a small test retriever over the same BM25 index.
        var retriever = new TestLexicalRetriever(bm25, store);
        var asst  = new RagHelpAssistant(retriever, chat, store, topK: 5);
        return (asst, store, chat);
    }

    private static HelpAssistantRequest Req(string query, string? currentTopicId = null)
        => new(query, null, currentTopicId, []);

    // ── Mode ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mode_IsRag()
    {
        var (asst, _, _) = await BuildAsync("ok");
        Assert.Equal(HelpAssistantMode.Rag, asst.Mode);
    }

    // ── Prompt assembly ───────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_PromptIncludesRetrievedExcerpts()
    {
        var (asst, _, chat) = await BuildAsync("Sure — click **Start backup**. [quickstart.backup]");
        await asst.AskAsync(Req("how do I create a backup"), CancellationToken.None);

        // The user message must contain the retrieved topic-id headers so the LLM
        //  can ground its answer and cite them.
        var prompt = chat.LastUserMessageText;
        Assert.Contains("[quickstart.backup]", prompt);
        Assert.Contains("Help excerpts", prompt);
        Assert.Contains("how do I create a backup", prompt);
    }

    [Fact]
    public async Task AskAsync_PromptIncludesSystemPrompt()
    {
        var (asst, _, chat) = await BuildAsync("ok [quickstart.backup]");
        await asst.AskAsync(Req("backup"), CancellationToken.None);

        Assert.Contains(chat.LastMessages, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task AskAsync_PriorHistory_IsIncludedInPrompt()
    {
        var store = await TestContentFixture.LoadStoreAsync();
        var bm25  = BM25HelpIndex.Build(store.All);
        var chat  = new RecordingChatClient("ok [quickstart.backup]");
        var asst  = new RagHelpAssistant(new TestLexicalRetriever(bm25, store), chat, store, topK: 5);

        var history = new[]
        {
            new ConversationTurn("earlier question", "earlier answer", DateTime.UtcNow),
        };
        var request = new HelpAssistantRequest("backup", null, null, history);

        await asst.AskAsync(request, CancellationToken.None);

        Assert.Contains(chat.LastMessages,
            m => m.Role == ChatRole.User && m.Text.Contains("earlier question"));
        Assert.Contains(chat.LastMessages,
            m => m.Role == ChatRole.Assistant && m.Text.Contains("earlier answer"));
    }

    // ── Citation parsing ──────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_ParsesCitationsFromAnswer()
    {
        var (asst, _, _) = await BuildAsync(
            "Insert a tape, then click **Start backup**. [quickstart.backup]");
        var response = await asst.AskAsync(Req("first backup"), CancellationToken.None);

        Assert.Contains(response.Citations, c => c.TopicId == "quickstart.backup");
        Assert.Equal(HelpAssistantMode.Rag, response.Mode);
    }

    [Fact]
    public async Task AskAsync_IgnoresHallucinatedCitations()
    {
        // The model cites a topic id that was NOT in the retrieved excerpts.
        var (asst, _, _) = await BuildAsync(
            "Here is some made-up guidance. [does.not.exist]");
        var response = await asst.AskAsync(Req("backup"), CancellationToken.None);

        Assert.DoesNotContain(response.Citations, c => c.TopicId == "does.not.exist");
    }

    [Fact]
    public async Task AskAsync_CitedTopicsResolveInStore()
    {
        var (asst, store, _) = await BuildAsync(
            "Restore copies files from tape. [quickstart.restore]");
        var response = await asst.AskAsync(Req("restore files"), CancellationToken.None);

        foreach (var citation in response.Citations)
            Assert.NotNull(store.GetById(citation.TopicId));
    }

    // ── No-match handling ─────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_NoRetrievalAndNoContext_ReturnsNoMatchWithoutCallingLlm()
    {
        var (asst, _, chat) = await BuildAsync("should never be returned");
        var response = await asst.AskAsync(
            Req("xyzzy plugh wumpus grznblx"), CancellationToken.None);

        Assert.Empty(response.Citations);
        Assert.Equal(0f, response.Confidence);
        Assert.False(chat.WasCalled, "LLM must not be called when retrieval is empty.");
    }

    // ── Current-topic context bias ────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_CurrentTopic_InjectsFullTopicBodyIntoPrompt()
    {
        // Query terms that do NOT lexically match the restore dialog, but the user
        //  is *viewing* that dialog's topic — its full body must reach the LLM.
        var (asst, _, chat) = await BuildAsync("Pick a destination folder. [dialog.restore]");
        await asst.AskAsync(
            Req("how do I pick where things go", currentTopicId: "dialog.restore"),
            CancellationToken.None);

        var prompt = chat.LastUserMessageText;
        Assert.Contains("[dialog.restore]", prompt);
        // Full body content (not just a clipped snippet) should be present.
        Assert.Contains("Destination folder", prompt);
    }

    [Fact]
    public async Task AskAsync_CurrentTopic_AnswersEvenWhenRetrievalWeak()
    {
        // Retrieval alone would fall below the confidence threshold for this query,
        //  but the current-topic context keeps the assistant answering.
        var (asst, _, chat) = await BuildAsync(
            "Tick the backup sets you want. [dialog.restore]");
        var response = await asst.AskAsync(
            Req("zzqq nonsense token", currentTopicId: "dialog.restore"),
            CancellationToken.None);

        Assert.True(chat.WasCalled, "LLM should be called when current-topic context exists.");
        Assert.True(response.Confidence > 0f);
    }

    [Fact]
    public async Task AskAsync_CurrentTopic_NotDuplicatedInExcerpts()
    {
        var (asst, _, chat) = await BuildAsync("ok [quickstart.backup]");
        await asst.AskAsync(
            Req("how do I create a backup", currentTopicId: "quickstart.backup"),
            CancellationToken.None);

        // The same topic header must appear exactly once in the excerpt block even
        //  though it is both the current topic and a lexical hit.
        var prompt = chat.LastUserMessageText;
        int occurrences = CountOccurrences(prompt, "### [quickstart.backup]");
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task AskAsync_UnknownCurrentTopic_IsIgnoredGracefully()
    {
        var (asst, _, chat) = await BuildAsync("ok [quickstart.backup]");
        var response = await asst.AskAsync(
            Req("how do I create a backup", currentTopicId: "no.such.topic"),
            CancellationToken.None);

        // Falls back to plain retrieval; no crash, answer still produced.
        Assert.True(chat.WasCalled);
        Assert.Equal(HelpAssistantMode.Rag, response.Mode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    // ── Test retriever (mirrors the internal LexicalRetriever) ─────────────────

    private sealed class TestLexicalRetriever(BM25HelpIndex bm25, HelpContentStore store)
        : IHelpRetriever
    {
        public Task<IReadOnlyList<HelpExcerpt>> RetrieveAsync(
            string query, int topK, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var hits = bm25.Search(query, topK);
            var results = hits
                .Select(h => new HelpExcerpt(h.Topic, h.Topic.Title, h.Excerpt, h.Score))
                .ToList();
            _ = store; // store kept for parity with the production retriever
            return Task.FromResult<IReadOnlyList<HelpExcerpt>>(results);
        }
    }

    // ── Recording fake chat client ─────────────────────────────────────────────

    /// <summary>
    /// An <see cref="IChatClient"/> that records the messages it receives and streams
    /// back a fixed answer one chunk at a time.
    /// </summary>
    private sealed class RecordingChatClient(string cannedAnswer) : IChatClient
    {
        public bool WasCalled { get; private set; }
        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        /// <summary>Concatenated text of the last user message sent to the model.</summary>
        public string LastUserMessageText =>
            LastMessages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

        public ChatClientMetadata Metadata => new("recording", null, null);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions?             options = null,
            CancellationToken        ct      = default)
        {
            Capture(messages);
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, cannedAnswer)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions?             options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Capture(messages);

            // Stream the canned answer in small chunks to exercise the assembler.
            foreach (var chunk in Chunk(cannedAnswer, 8))
            {
                ct.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private void Capture(IEnumerable<ChatMessage> messages)
        {
            WasCalled    = true;
            LastMessages = messages.ToList();
        }

        private static IEnumerable<string> Chunk(string s, int size)
        {
            for (int i = 0; i < s.Length; i += size)
                yield return s.Substring(i, Math.Min(size, s.Length - i));
        }
    }
}
