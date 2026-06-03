using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

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

    /// <summary>Exposes the renderer so the host control can delegate hyperlink clicks.</summary>
    internal MarkdownRenderer Renderer => _renderer;

    private FlowDocument? _currentDocument;
    private string?       _currentTopicTitle;
    private string        _searchText   = string.Empty;
    private string        _pendingQuery = string.Empty;
    private bool          _isBusy;
    private bool          _isPaneOpen;
    private bool          _isAsking;
    private double        _chatPaneHeight = 200.0;
    private string        _thinkingAnimationText = string.Empty;
    private HelpAssistantMode _assistantMode;

    // Pulsing-star animation shown below the chat input while the AI is thinking
    private readonly DispatcherTimer    _thinkTimer;
    private int                         _thinkFrame;
    private CancellationTokenSource?    _askCts;
    private static readonly string[] ThinkFrames = ["★", "✦", "✧", "✶"];
    // Stop icon (solid square) shown on the Ask button while thinking
    private const string StopLabel  = "\u25A0";
    private const string AskLabel   = "Ask";

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

        // Pulsing-star timer — ticks only while IsAsking; updates the animation strip below the input
        _thinkTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _thinkTimer.Tick += (_, _) =>
        {
            _thinkFrame = (_thinkFrame + 1) % ThinkFrames.Length;
            ThinkingAnimationText = ThinkFrames[_thinkFrame];
        };

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
        AskCommand     = new AsyncRelayCommand(async _ => await ExecuteAsk(), _ => (!string.IsNullOrWhiteSpace(_pendingQuery) && !_isBusy) || _isAsking);
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

    /// <summary>
    /// Raised when a session operation fails unexpectedly.
    /// The <see cref="string"/> argument is a human-readable error description.
    /// The host (MainWindow) subscribes to forward the message to the log pane.
    /// </summary>
    public event EventHandler<string>? SessionError;

    /// <summary>
    /// Raised for user-noticeable but non-error conditions (e.g. thinking aborted by user).
    /// The host (MainWindow) subscribes to forward the message to the log pane as a warning.
    /// </summary>
    public event EventHandler<string>? SessionWarning;

    /// <summary>
    /// Raised with transient informational messages (e.g. "AI is preparing an answer…").
    /// The host (MainWindow) subscribes to forward the message to the log pane as a sub entry.
    /// </summary>
    public event EventHandler<string>? SessionInfo;

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

    /// <summary>
    /// Height (pixels) of the chat sub-pane.
    /// Set from <c>AppSettings.HelpPaneChatHeight</c> on pane open; updated live by the
    ///  <c>ChatSplitter_DragCompleted</c> handler in <see cref="Controls.HelpPane"/>; persisted
    ///  back to <c>AppSettings</c> when the pane closes or the window is saved.
    /// </summary>
    public double ChatPaneHeight
    {
        get => _chatPaneHeight;
        set => SetProperty(ref _chatPaneHeight, Math.Max(80.0, value));
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

    /// <summary>
    /// <c>true</c> while the AI assistant is preparing an answer.
    /// Drives the thinking animation strip and the Ask/Abort button state.
    /// </summary>
    public bool IsAsking
    {
        get => _isAsking;
        private set
        {
            if (SetProperty(ref _isAsking, value))
            {
                // Refresh the Ask button content and tooltip when thinking state changes
                OnPropertyChanged(nameof(AskButtonContent));
                OnPropertyChanged(nameof(AskButtonTooltip));
                // Re-evaluate canExecute so the button becomes enabled as an abort control
                //  even when the query text box is empty.
                AsyncRelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Content of the Ask button: <c>"Ask"</c> normally, stop icon (■) while thinking.
    /// </summary>
    public string AskButtonContent => _isAsking ? StopLabel : AskLabel;

    /// <summary>Tooltip for the Ask button, context-sensitive to thinking state.</summary>
    public string AskButtonTooltip => _isAsking ? "Abort thinking" : "Ask";

    /// <summary>
    /// Pulsing multi-star text displayed below the chat input while the AI is thinking.
    /// </summary>
    public string ThinkingAnimationText
    {
        get => _thinkingAnimationText;
        private set => SetProperty(ref _thinkingAnimationText, value);
    }

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
        await TrySessionAsync(
            () => _session.BackAsync(CancellationToken.None),
            "Help: navigation back failed.");
    }

    private async Task ExecuteForward()
    {
        await TrySessionAsync(
            () => _session.ForwardAsync(CancellationToken.None),
            "Help: navigation forward failed.");
    }

    private async Task ExecuteHome(CancellationToken ct = default)
    {
        await TrySessionAsync(
            () => _session.HomeAsync(ct),
            "Help: could not navigate to home topic. Check that help content is embedded correctly.");
    }

    private async Task ExecuteNavigate(string? topicId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topicId)) return;
        await TrySessionAsync(
            () => _session.NavigateAsync(new HelpNavigationRequest(topicId), ct),
            $"Help: could not navigate to topic '{topicId}'.");
    }

    private async Task ExecuteAsk()
    {
        // If already thinking, the button acts as abort
        if (_isAsking)
        {
            _askCts?.Cancel();
            return;
        }

        var query = _pendingQuery.Trim();
        if (string.IsNullOrEmpty(query)) return;

        // Append the user bubble immediately
        ConversationItems.Add(new ConversationItem
        {
            Role      = ConversationItemRole.User,
            PlainText = query,
        });
        PendingQuery = string.Empty;

        // Signal that the AI is thinking — starts the button animation
        IsAsking = true;
        _thinkFrame = 0;
        ThinkingAnimationText = ThinkFrames[0];
        _thinkTimer.Start();
        _askCts = new CancellationTokenSource();

        // Notify the log pane (sub-level) which provider is preparing the answer
        var providerLabel = _session.AssistantMode != HelpAssistantMode.Lexical
            ? (App.AiSessionHost.CurrentConfig is { } cfg
                ? cfg.DisplayLabel
                : "AI assistant")
            : null;
        if (providerLabel is not null)
            SessionInfo?.Invoke(this, $"{providerLabel} is preparing an answer…");

        bool aborted = false;
        try
        {
            await TrySessionAsync(
                () => _session.AskAsync(query, _askCts.Token),
                "Help: the AI assistant did not respond. Check your AI provider settings.");
        }
        finally
        {
            // Check cancellation before disposing the source
            aborted = _askCts?.IsCancellationRequested ?? false;
            _thinkTimer.Stop();
            _askCts?.Dispose();
            _askCts = null;
            IsAsking = false;
            ThinkingAnimationText = string.Empty;
            if (aborted)
                SessionWarning?.Invoke(this, "Answering aborted by the user.");
            else
                SessionInfo?.Invoke(this, "Answering completed");
        }
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
        _thinkTimer.Stop();
        _session.CurrentTopicChanged  -= OnCurrentTopicChanged;
        _session.AnswerReceived       -= OnAnswerReceived;
        _session.AssistantModeChanged -= OnAssistantModeChanged;
        await _session.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a session operation, setting <see cref="IsBusy"/> around it and catching
    /// any exception. On failure, raises <see cref="SessionError"/> with a human-readable
    /// message and renders a brief error notice into <see cref="CurrentDocument"/> so the
    /// pane doesn't go blank.
    /// </summary>
    private async Task TrySessionAsync(Func<Task> operation, string friendlyMessage)
    {
        IsBusy = true;
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is intentional — no error to surface.
        }
        catch (Exception ex)
        {
            var detail = $"{friendlyMessage} ({ex.GetType().Name}: {ex.Message})";
            SessionError?.Invoke(this, detail);
            Dispatch(() => CurrentDocument = RenderErrorDocument(friendlyMessage));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Builds a minimal <see cref="FlowDocument"/> error notice for the content subpane.
    /// </summary>
    private static FlowDocument RenderErrorDocument(string message)
    {
        var doc = new FlowDocument();
        var para = new Paragraph(new Run($"⚠ {message}"))
        {
            Foreground = System.Windows.Media.Brushes.OrangeRed,
            FontStyle  = System.Windows.FontStyles.Italic,
            Margin     = new Thickness(8),
        };
        doc.Blocks.Add(para);
        return doc;
    }

    private static void Dispatch(Action action)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            action();
        else
            Application.Current?.Dispatcher.Invoke(action);
    }
}
