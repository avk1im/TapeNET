using HelpNET.Assistants;
using HelpNET.Content;
using HelpNET.Indexing;

namespace HelpNET.Session;

/// <summary>
/// Concrete implementation of <see cref="IHelpSession"/>.
/// Owns navigation history, conversation state, and an <see cref="IHelpAssistant"/>.
/// </summary>
public sealed class HelpSession : IHelpSession
{
    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly HelpContentStore  _store;
    private readonly IHelpIndex        _index;
    private readonly HelpSessionOptions _options;

    // Mutable assistant; replaced when IAiSession.ProviderChanged fires.
    private IHelpAssistant _assistant;

    // Navigation stacks — back/forward histories.
    private readonly LinkedList<HelpTopic> _backStack    = new();
    private readonly LinkedList<HelpTopic> _forwardStack = new();

    // Conversation turns.
    private readonly List<ConversationTurn> _conversation = [];

    // ── Constructor ───────────────────────────────────────────────────────────

    internal HelpSession(
        HelpContentStore   store,
        IHelpIndex         index,
        IHelpAssistant     assistant,
        HelpSessionOptions options)
    {
        _store     = store;
        _index     = index;
        _assistant = assistant;
        _options   = options;
    }

    // ── IHelpSession — State ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public HelpTopic? CurrentTopic { get; private set; }

    /// <inheritdoc/>
    public IReadOnlyList<HelpTopic> BackHistory
        => _backStack.ToArray();   // copy; most-recent is the head

    /// <inheritdoc/>
    public IReadOnlyList<HelpTopic> ForwardHistory
        => _forwardStack.ToArray();

    /// <inheritdoc/>
    public IReadOnlyList<ConversationTurn> Conversation
        => _conversation.AsReadOnly();

    /// <inheritdoc/>
    public HelpAssistantMode AssistantMode => _assistant.Mode;

    // ── IHelpSession — Navigation ─────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<HelpTopic> NavigateAsync(HelpNavigationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var topic = _store.GetById(request.TopicId)
            ?? throw new KeyNotFoundException(
                $"Help topic '{request.TopicId}' not found in the content store.");

        NavigateTo(topic);
        return Task.FromResult(topic);
    }

    /// <inheritdoc/>
    public Task<HelpTopic?> BackAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_backStack.Count == 0)
            return Task.FromResult<HelpTopic?>(null);

        var previous = _backStack.First!.Value;
        _backStack.RemoveFirst();

        // Push current to forward stack before going back.
        if (CurrentTopic is not null)
            PushForward(CurrentTopic);

        SetCurrent(previous);
        return Task.FromResult<HelpTopic?>(previous);
    }

    /// <inheritdoc/>
    public Task<HelpTopic?> ForwardAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_forwardStack.Count == 0)
            return Task.FromResult<HelpTopic?>(null);

        var next = _forwardStack.First!.Value;
        _forwardStack.RemoveFirst();

        // Push current to back stack before going forward.
        if (CurrentTopic is not null)
            PushBack(CurrentTopic);

        SetCurrent(next);
        return Task.FromResult<HelpTopic?>(next);
    }

    /// <inheritdoc/>
    public async Task<HelpTopic> HomeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var home = _store.GetById(_options.HomeTopicId);
        if (home is null)
        {
            // Fall back to the first topic in the store.
            home = _store.All.Count > 0
                ? _store.All[0]
                : throw new InvalidOperationException(
                    "No help topics are loaded. Cannot navigate home.");
        }

        NavigateTo(home);
        return home;
    }

    // ── IHelpSession — Search / Ask ───────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<HelpSearchHit>> SearchAsync(
        string            query,
        int               topK,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_index.Search(query, topK));
    }

    /// <inheritdoc/>
    public async Task<HelpAssistantResponse> AskAsync(string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var request = new HelpAssistantRequest(
            Query:          query,
            CurrentHost:    null,     // not tracked at session level; VM supplies this
            CurrentTopicId: CurrentTopic?.Id,
            History:        _conversation.AsReadOnly());

        var response = await _assistant.AskAsync(request, ct).ConfigureAwait(false);

        // Record the turn.
        var turn = new ConversationTurn(query, response.AnswerMarkdown, DateTime.UtcNow);
        _conversation.Add(turn);

        // Enforce maximum.
        while (_conversation.Count > _options.MaxConversationTurns)
            _conversation.RemoveAt(0);

        AnswerReceived?.Invoke(this, response);
        return response;
    }

    // ── IHelpSession — Content helpers ────────────────────────────────────────

    /// <inheritdoc/>
    public IReadOnlyList<WalkthroughScript> GetWalkthroughsForHost(string hostName)
        => _store.GetByHost(hostName)
                 .Where(t => t.Kind == HelpTopicKind.Walkthrough && t.Walkthrough is not null)
                 .Select(t => t.Walkthrough!)
                 .ToList()
                 .AsReadOnly();

    /// <inheritdoc/>
    public HelpTopic? GetTopicForControl(string hostName, string topicId)
    {
        var topic = _store.GetById(topicId);
        if (topic is null) return null;
        if (topic.Host is null) return null;
        return topic.Host.Equals(hostName, StringComparison.OrdinalIgnoreCase) ? topic : null;
    }

    /// <inheritdoc/>
    public void ClearConversation()
        => _conversation.Clear();

    // ── Events ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public event EventHandler? CurrentTopicChanged;

    /// <inheritdoc/>
    public event EventHandler<HelpAssistantResponse>? AnswerReceived;

    /// <inheritdoc/>
    public event EventHandler? AssistantModeChanged;

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the assistant (called by <see cref="HelpSessionFactory"/> when
    /// the AI provider changes) and raises <see cref="AssistantModeChanged"/>.
    /// </summary>
    internal void ReplaceAssistant(IHelpAssistant assistant)
    {
        _assistant = assistant;
        AssistantModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NavigateTo(HelpTopic topic)
    {
        // Push current onto back stack and clear the forward stack (new branch).
        if (CurrentTopic is not null)
        {
            PushBack(CurrentTopic);
            _forwardStack.Clear();
        }

        SetCurrent(topic);
    }

    private void SetCurrent(HelpTopic topic)
    {
        CurrentTopic = topic;
        CurrentTopicChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PushBack(HelpTopic topic)
    {
        _backStack.AddFirst(topic);
        while (_backStack.Count > _options.MaxHistoryDepth)
            _backStack.RemoveLast();
    }

    private void PushForward(HelpTopic topic)
    {
        _forwardStack.AddFirst(topic);
        while (_forwardStack.Count > _options.MaxHistoryDepth)
            _forwardStack.RemoveLast();
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // Nothing to dispose in the Lexical session.
        // Phase 4 will add async disposal of embedding resources.
        return ValueTask.CompletedTask;
    }
}
