using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;

using HelpNET.Assistants;
using HelpNET.Content;
using HelpNET.Session;

using TapeWinNET.Help;

namespace TapeWinNET.ViewModels;

// ── Display model for one chat message ───────────────────────────────────────

/// <summary>Whether a chat item is a user query or an assistant answer.</summary>
public enum ConversationItemRole { User, Assistant }

/// <summary>
/// Represents one row in the Chat subpane conversation list.
/// </summary>
public sealed class ConversationItem
{
    /// <summary>Role of the speaker.</summary>
    public ConversationItemRole Role { get; init; }

    /// <summary>
    /// Rendered <see cref="FlowDocument"/> for assistant answers,
    /// or <c>null</c> for user queries (rendered as plain text).
    /// </summary>
    public FlowDocument? Document { get; init; }

    /// <summary>Plain text for user query bubbles.</summary>
    public string? PlainText { get; init; }

    /// <summary>Suggested related-topic chips (assistant answers only).</summary>
    public IReadOnlyList<HelpTopicRef> SuggestedTopics { get; init; } = [];

    /// <summary>Suggested action buttons (assistant answers only).</summary>
    public IReadOnlyList<HelpActionRef> SuggestedActions { get; init; } = [];
}

// ── ViewModel ────────────────────────────────────────────────────────────────

/// <summary>
/// ViewModel for the <see cref="Controls.HelpPane"/> UserControl.
/// </summary>
public sealed class HelpPaneViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IHelpSession   _session;
    private readonly IHelpPaneHost  _host;
    private readonly HelpActionRouter _actions;
    private readonly MarkdownRenderer _renderer;

    private FlowDocument? _currentDocument;
    private string?       _currentTopicTitle;
    private string        _searchText   = string.Empty;
    private string        _pendingQuery = string.Empty;
    private bool          _isBusy;
    private bool          _isPaneOpen;
    private HelpAssistantMode _assistantMode;

    // ── Construction ─────────────────────────────────────────────────────────

    public HelpPaneViewModel(
        IHelpSession    session,
        IHelpPaneHost   host,
        HelpActionRouter actions)
    {
        _session  = session;
        _host     = host;
        _actions  = actions;
        _renderer = new MarkdownRenderer(session, actions);

        ConversationItems   = [];
        SearchSuggestions   = [];

        // Initialise assistant mode badge
        _assistantMode = session.AssistantMode;

        // Wire session events (always marshalled to the UI thread)
        _session.CurrentTopicChanged += OnCurrentTopicChanged;
        _session.AnswerReceived      += OnAnswerReceived;
        _session.AssistantModeChanged += OnAssistantModeChanged;

        // Commands
        BackCommand    = new RelayCommand(async () => await ExecuteBack(),    () => _session.BackHistory.Count > 0);
        ForwardCommand = new RelayCommand(async () => await ExecuteForward(), () => _session.ForwardHistory.Count > 0);
        HomeCommand    = new AsyncRelayCommand(async _ => await ExecuteHome());
        AskCommand     = new AsyncRelayCommand(async _ => await ExecuteAsk(), _ => !string.IsNullOrWhiteSpace(_pendingQuery) && !_isBusy);
        ClearChatCommand   = new RelayCommand(ExecuteClearChat);
        CloseCommand       = new RelayCommand(ExecuteClose);
        NavigateCommand    = new AsyncRelayCommand(async p => await ExecuteNavigate(p as string));
        NavigateTopicRefCommand = new AsyncRelayCommand(async p => await ExecuteNavigate((p as HelpTopicRef)?.Id));
        InvokeActionCommand     = new RelayCommand(p => { if (p is HelpActionRef a) _actions.Invoke(a.ActionId); });
        OpenAiSetupCommand      = new AsyncRelayCommand(async _ => await Services.AppAiSessionHost.ReconfigureAndNotifyAsync());
    }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised by <see cref="CloseCommand"/> to let the host window (or
    /// <see cref="Controls.HelpPaneWindow"/>) close itself.
    /// </summary>
    public event EventHandler? PaneCloseRequested;

    // ── Bindable properties ───────────────────────────────────────────────────

    /// <summary>Rendered FlowDocument for the content subpane.</summary>
    public FlowDocument? CurrentDocument
    {
        get => _currentDocument;
        private set => SetProperty(ref _currentDocument, value);
    }

    /// <summary>Title of the currently displayed topic.</summary>
    public string? CurrentTopicTitle
    {
        get => _currentTopicTitle;
        private set => SetProperty(ref _currentTopicTitle, value);
    }

    /// <summary>Id of the currently displayed topic; <c>null</c> when no topic is loaded.</summary>
    public string? CurrentTopicId => _session.CurrentTopic?.Id;

    /// <summary>Text typed in the search box (triggers live suggestions).</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                _ = UpdateSearchSuggestionsAsync(value);
        }
    }

    /// <summary>Text typed in the chat input box.</summary>
    public string PendingQuery
    {
        get => _pendingQuery;
        set => SetProperty(ref _pendingQuery, value);
    }

    /// <summary><c>true</c> while an async operation is in flight.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Whether the HelpPane is currently open/visible.</summary>
    public bool IsPaneOpen
    {
        get => _isPaneOpen;
        set => SetProperty(ref _isPaneOpen, value);
    }

    /// <summary>Current retrieval/synthesis mode — shown in the header badge.</summary>
    public HelpAssistantMode AssistantMode
    {
        get => _assistantMode;
        private set => SetProperty(ref _assistantMode, value);
    }

    /// <summary>Human-readable mode badge text.</summary>
    public string AssistantModeBadge => _assistantMode switch
    {
        HelpAssistantMode.Rag      => "AI: RAG",
        HelpAssistantMode.Semantic => "AI: Semantic",
        _                          => "Local Search",
    };

    /// <summary>Navigation history availability — drives Back/Forward button state.</summary>
    public bool CanGoBack    => _session.BackHistory.Count > 0;
    public bool CanGoForward => _session.ForwardHistory.Count > 0;

    // ── Collections ───────────────────────────────────────────────────────────

    /// <summary>Chat subpane messages (user + assistant turns).</summary>
    public ObservableCollection<ConversationItem> ConversationItems { get; }

    /// <summary>Live search suggestions from the header search box.</summary>
    public ObservableCollection<HelpSearchHit> SearchSuggestions { get; }

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand BackCommand             { get; }
    public ICommand ForwardCommand          { get; }
    public ICommand HomeCommand             { get; }
    public ICommand AskCommand              { get; }
    public ICommand ClearChatCommand        { get; }
    public ICommand CloseCommand            { get; }
    public ICommand NavigateCommand         { get; }
    /// <summary>Navigate to a topic from a <see cref="HelpTopicRef"/> chip.</summary>
    public ICommand NavigateTopicRefCommand { get; }
    /// <summary>Invoke a host action from a <see cref="HelpActionRef"/> button.</summary>
    public ICommand InvokeActionCommand     { get; }
    public ICommand OpenAiSetupCommand      { get; }

    // ── Public helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Navigates to the given topic id.  Called during pane open to restore the
    /// last-viewed topic or to respond to an F1 press.
    /// </summary>
    public async Task NavigateToAsync(string topicId, CancellationToken ct = default)
    {
        await ExecuteNavigate(topicId, ct);
    }

    /// <summary>Navigates to the home topic.</summary>
    public async Task GoHomeAsync(CancellationToken ct = default)
    {
        await ExecuteHome(ct);
    }

    // ── Session event handlers ────────────────────────────────────────────────

    private void OnCurrentTopicChanged(object? sender, EventArgs e)
    {
        Dispatch(() =>
        {
            var topic = _session.CurrentTopic;
            CurrentTopicTitle = topic?.Title;
            CurrentDocument   = topic is not null
                ? _renderer.Render(topic.MarkdownBody)
                : null;

            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        });
    }

    private void OnAnswerReceived(object? sender, HelpAssistantResponse response)
    {
        Dispatch(() =>
        {
            // The user query was already appended in ExecuteAsk; add the answer now.
            var answerDoc = _renderer.Render(response.AnswerMarkdown);
            ConversationItems.Add(new ConversationItem
            {
                Role            = ConversationItemRole.Assistant,
                Document        = answerDoc,
                SuggestedTopics = response.SuggestedTopics,
                SuggestedActions = response.SuggestedActions,
            });
        });
    }

    private void OnAssistantModeChanged(object? sender, EventArgs e)
    {
        Dispatch(() =>
        {
            AssistantMode = _session.AssistantMode;
            OnPropertyChanged(nameof(AssistantModeBadge));
        });
    }

    // ── Command implementations ───────────────────────────────────────────────

    private async Task ExecuteBack()
    {
        IsBusy = true;
        try   { await _session.BackAsync(CancellationToken.None); }
        finally { IsBusy = false; }
    }

    private async Task ExecuteForward()
    {
        IsBusy = true;
        try   { await _session.ForwardAsync(CancellationToken.None); }
        finally { IsBusy = false; }
    }

    private async Task ExecuteHome(CancellationToken ct = default)
    {
        IsBusy = true;
        try   { await _session.HomeAsync(ct); }
        finally { IsBusy = false; }
    }

    private async Task ExecuteNavigate(string? topicId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topicId)) return;
        IsBusy = true;
        try   { await _session.NavigateAsync(new HelpNavigationRequest(topicId), ct); }
        finally { IsBusy = false; }
    }

    private async Task ExecuteAsk()
    {
        var query = _pendingQuery.Trim();
        if (string.IsNullOrEmpty(query)) return;

        // Append the user bubble immediately
        ConversationItems.Add(new ConversationItem
        {
            Role      = ConversationItemRole.User,
            PlainText = query,
        });
        PendingQuery = string.Empty;

        IsBusy = true;
        try   { await _session.AskAsync(query, CancellationToken.None); }
        finally { IsBusy = false; }
    }

    private void ExecuteClearChat()
    {
        _session.ClearConversation();
        ConversationItems.Clear();
    }

    private void ExecuteClose()
    {
        IsPaneOpen = false;
        _host.OnPaneClosed();
        PaneCloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task UpdateSearchSuggestionsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchSuggestions.Clear();
            return;
        }

        var hits = await _session.SearchAsync(query, topK: 5, CancellationToken.None);
        Dispatch(() =>
        {
            SearchSuggestions.Clear();
            foreach (var hit in hits)
                SearchSuggestions.Add(hit);
        });
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _session.CurrentTopicChanged  -= OnCurrentTopicChanged;
        _session.AnswerReceived       -= OnAnswerReceived;
        _session.AssistantModeChanged -= OnAssistantModeChanged;
        await _session.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Dispatch(Action action)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            action();
        else
            Application.Current?.Dispatcher.Invoke(action);
    }
}
