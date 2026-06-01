using System.Collections.ObjectModel;
using System.Windows.Input;

using FclAiNET;

using FclNET;
using FclNET.Ast;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for the <see cref="TapeWinNET.FclFilterWindow"/> modal dialog.
/// Manages the visual DNF condition editor (left pane) and the FCL program
/// text editor (right pane), including bidirectional sync between them.
/// </summary>
public class FclFilterWindowVM : ViewModelBase
{
    private readonly Action<FclExpression?> _onApply;
    private readonly Action _onCancel;

    /// <summary>
    /// Factory that lazily provides an <see cref="FclAiTranslator"/> bound to the
    /// app-wide shared AI session — the same provider the Help system uses.
    /// Returns <c>null</c> if no AI provider is configured / the user declined setup.
    /// Injected by the host (<see cref="Controls.FileFilterPane"/>) so the VM stays
    /// decoupled from <c>App</c>.
    /// </summary>
    private readonly Func<CancellationToken, Task<FclAiTranslator?>>? _translatorFactory;

    private string _fclText = string.Empty;
    private string? _appliedFclText;
    private bool _isProgramPaneOpen;
    private bool _isFclTextModified;
    private bool _isVisualModified;
    private FclDiagnostic? _selectedDiagnostic;
    private bool _isDnfCompatible = true;
    private string? _nonDnfMessage;
    private bool _applyToAll;
    private bool _hasMultipleSets;

    // ── AI-assisted generation state ──
    private bool _isAiPanelOpen;
    private bool _isAiBusy;
    private string _aiPromptText = string.Empty;
    private string? _aiStatusMessage;
    private WarningLevel _aiStatusLevel = WarningLevel.None;

    // ─────────────────────────────────────────────────────
    //  Construction
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates the filter window VM.
    /// </summary>
    /// <param name="onApply">
    /// Callback invoked when the user clicks "Apply Filter". Receives the
    /// resulting <see cref="FclExpression"/>, or <c>null</c> to clear.
    /// </param>
    /// <param name="onCancel">Callback invoked on Cancel / Escape.</param>
    public FclFilterWindowVM(Action<FclExpression?> onApply, Action onCancel)
    {
        _onApply = onApply;
        _onCancel = onCancel;

        // Ensure there's always at least one group with one empty row
        AddGroup();

        // Commands
        AddGroupCommand = new RelayCommand(_ => AddGroup());
        ApplyVisualToTextCommand = new RelayCommand(
            _ => SyncVisualToText(),
            _ => IsVisualModified || IsFclTextModified);
        ApplyTextToVisualCommand = new RelayCommand(
            _ => SyncTextToVisual(),
            _ => IsFclTextModified && IsProgramPaneOpen);
        ToggleProgramPaneCommand = new RelayCommand(_ => ToggleProgramPane());
        ApplyFilterCommand = new RelayCommand(_ => ExecuteApply(), _ => CanApply);
        ClearFilterCommand = new RelayCommand(_ => _onApply(null));
        CancelCommand = new RelayCommand(_ => _onCancel());
        GenerateWithAiCommand = new RelayCommand(_ => ToggleAiPanel());
        SubmitAiPromptCommand = new AsyncRelayCommand(
            _ => GenerateFromPromptAsync(),
            _ => !IsAiBusy && !string.IsNullOrWhiteSpace(AiPromptText));
    }

    /// <summary>
    /// Creates the filter window VM with AI-assisted FCL generation enabled.
    /// </summary>
    /// <param name="onApply">See <see cref="FclFilterWindowVM(Action{FclExpression?}, Action)"/>.</param>
    /// <param name="onCancel">See <see cref="FclFilterWindowVM(Action{FclExpression?}, Action)"/>.</param>
    /// <param name="translatorFactory">
    /// Lazily provides an <see cref="FclAiTranslator"/> bound to the shared AI
    /// session (the same provider the Help system uses). Returns <c>null</c>
    /// when no AI provider is available.
    /// </param>
    public FclFilterWindowVM(
        Action<FclExpression?> onApply,
        Action onCancel,
        Func<CancellationToken, Task<FclAiTranslator?>> translatorFactory)
        : this(onApply, onCancel)
    {
        _translatorFactory = translatorFactory;
    }

    // ─────────────────────────────────────────────────────
    //  Visual editor — groups
    // ─────────────────────────────────────────────────────

    /// <summary>The OR-connected condition groups (DNF clauses).</summary>
    public ObservableCollection<FclConditionGroupVM> Groups { get; } = [];

    // ─────────────────────────────────────────────────────
    //  Program pane — FCL text
    // ─────────────────────────────────────────────────────

    /// <summary>The FCL program text in the right pane editor.</summary>
    public string FclText
    {
        get => _fclText;
        set
        {
            if (!SetProperty(ref _fclText, value))
                return;
            IsFclTextModified = true;
        }
    }

    /// <summary>Whether the program pane is expanded.</summary>
    public bool IsProgramPaneOpen
    {
        get => _isProgramPaneOpen;
        set
        {
            if (!SetProperty(ref _isProgramPaneOpen, value))
                return;
            OnPropertyChanged(nameof(ToggleProgramPaneText));
        }
    }

    /// <summary>Button text for the toggle: "Show Program ▶" / "◀ Hide Program".</summary>
    public string ToggleProgramPaneText =>
        IsProgramPaneOpen ? "◀ Hide Program" : "Show Program ▶";

    /// <summary>
    /// Whether more than one backup set exists. Controls visibility of the
    ///  "all sets" checkbox in the dialog.
    /// </summary>
    public bool HasMultipleSets
    {
        get => _hasMultipleSets;
        set => SetProperty(ref _hasMultipleSets, value);
    }

    /// <summary>
    /// Whether the filter should be applied to / disabled for all backup sets.
    /// Mirrors <see cref="Controls.FileFilterPane.ApplyToAll"/>; initialised from
    ///  the pane before the dialog opens and read back when it closes.
    /// </summary>
    public bool ApplyToAll
    {
        get => _applyToAll;
        set => SetProperty(ref _applyToAll, value);
    }

    /// <summary>
    /// Whether the user has modified the FCL text since the last sync.
    /// When true, the program text is out of sync with the visual editor
    /// and the "last-modified wins" rule applies on Apply.
    /// </summary>
    public bool IsFclTextModified
    {
        get => _isFclTextModified;
        private set
        {
            if (SetProperty(ref _isFclTextModified, value))
            {
                OnPropertyChanged(nameof(ProgramPaneHeader));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// Header for the FCL Program GroupBox. Shows " ●" suffix when the
    /// program text has been manually modified and is not yet synced back
    /// to the visual editor.
    /// </summary>
    public string ProgramPaneHeader =>
        IsFclTextModified ? "Program ●" : "Program";

    /// <summary>
    /// Whether the visual condition editor has been modified since the last sync.
    /// Used to enable the "Update →" button independently of text modifications.
    /// </summary>
    public bool IsVisualModified
    {
        get => _isVisualModified;
        private set
        {
            if (SetProperty(ref _isVisualModified, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// The FCL text that was used when the filter was applied via the
    /// text-path ("last-modified wins"). <c>null</c> when the visual
    /// editor was the winning source. Used by <see cref="Controls.FileFilterPane"/>
    /// to preserve user-edited text (including comments) across dialog re-opens.
    /// </summary>
    public string? AppliedFclText => _appliedFclText;

    /// <summary>
    /// Restores previously saved FCL text without setting
    /// <see cref="IsFclTextModified"/>. Called during dialog initialization
    /// when re-opening a dialog whose last apply used user-edited text.
    /// </summary>
    public void RestoreSavedFclText(string text)
    {
        _fclText = text;
        OnPropertyChanged(nameof(FclText));
        // Text matches what was applied, so it's not "modified"
        IsFclTextModified = false;
    }

    // ─────────────────────────────────────────────────────
    //  Diagnostics
    // ─────────────────────────────────────────────────────

    /// <summary>Parse/validation diagnostics from the FCL text editor.</summary>
    public ObservableCollection<FclDiagnostic> Diagnostics { get; } = [];

    /// <summary>
    /// The currently selected diagnostic — selecting it should highlight
    /// the corresponding span in the text editor.
    /// </summary>
    public FclDiagnostic? SelectedDiagnostic
    {
        get => _selectedDiagnostic;
        set
        {
            if (SetProperty(ref _selectedDiagnostic, value))
                OnPropertyChanged(nameof(SelectedDiagnosticSpan));
        }
    }

    /// <summary>The source span of the selected diagnostic, for text highlighting.</summary>
    public SourceSpan? SelectedDiagnosticSpan =>
        SelectedDiagnostic?.Span is { Length: > 0 } span ? span : null;

    /// <summary>Whether there are parse/validation errors.</summary>
    public bool HasErrors => Diagnostics.Count > 0;

    /// <summary>Summary string: "✓ Valid" or "⚠ N error(s)".</summary>
    public string ValidationSummary =>
        Diagnostics.Count == 0
            ? "✓ Valid"
            : $"⚠ {Diagnostics.Count} error{(Diagnostics.Count != 1 ? "s" : "")}";

    /// <summary>Whether the parsed FCL text fits the visual DNF editor.</summary>
    public bool IsDnfCompatible
    {
        get => _isDnfCompatible;
        private set => SetProperty(ref _isDnfCompatible, value);
    }

    /// <summary>Explanation when the FCL text is not DNF-compatible.</summary>
    public string? NonDnfMessage
    {
        get => _nonDnfMessage;
        private set => SetProperty(ref _nonDnfMessage, value);
    }

    // ─────────────────────────────────────────────────────
    //  Commands
    // ─────────────────────────────────────────────────────

    /// <summary>Adds a new OR group.</summary>
    public ICommand AddGroupCommand { get; }

    /// <summary>"Update →": syncs the visual editor to the FCL text pane.</summary>
    public ICommand ApplyVisualToTextCommand { get; }

    /// <summary>"← Apply": parses FCL text and attempts to populate the visual editor.</summary>
    public ICommand ApplyTextToVisualCommand { get; }

    /// <summary>Toggles the program pane visibility.</summary>
    public ICommand ToggleProgramPaneCommand { get; }

    /// <summary>Applies the filter and closes the dialog.</summary>
    public ICommand ApplyFilterCommand { get; }

    /// <summary>Clears the filter definition and closes the dialog.</summary>
    public ICommand ClearFilterCommand { get; }

    /// <summary>Cancels and closes the dialog.</summary>
    public ICommand CancelCommand { get; }

    /// <summary>Placeholder for AI-assisted FCL generation.</summary>
    public ICommand GenerateWithAiCommand { get; }

    /// <summary>Submits the natural-language prompt for AI translation.</summary>
    public ICommand SubmitAiPromptCommand { get; }

    // ─────────────────────────────────────────────────────
    //  AI-assisted generation
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Whether the natural-language input panel is shown. Toggled by the
    /// "AI Generate…" button. Hidden when no AI provider is available.
    /// </summary>
    public bool IsAiPanelOpen
    {
        get => _isAiPanelOpen;
        set => SetProperty(ref _isAiPanelOpen, value);
    }

    /// <summary>Whether an AI translation request is currently in flight.</summary>
    public bool IsAiBusy
    {
        get => _isAiBusy;
        private set
        {
            if (SetProperty(ref _isAiBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The natural-language filter description entered by the user.</summary>
    public string AiPromptText
    {
        get => _aiPromptText;
        set
        {
            if (SetProperty(ref _aiPromptText, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Status / result message shown beneath the AI input
    /// (e.g. "Generating…", an error explanation, or a success note).
    /// </summary>
    public string? AiStatusMessage
    {
        get => _aiStatusMessage;
        private set
        {
            if (SetProperty(ref _aiStatusMessage, value))
                OnPropertyChanged(nameof(HasAiStatus));
        }
    }

    /// <summary>Severity of <see cref="AiStatusMessage"/>, for colouring.</summary>
    public WarningLevel AiStatusLevel
    {
        get => _aiStatusLevel;
        private set => SetProperty(ref _aiStatusLevel, value);
    }

    /// <summary>Whether a status message is present.</summary>
    public bool HasAiStatus => !string.IsNullOrEmpty(AiStatusMessage);

    /// <summary>Toggles the AI input panel; auto-opens the program pane to show the result.</summary>
    private void ToggleAiPanel()
    {
        IsAiPanelOpen = !IsAiPanelOpen;
        if (!IsAiPanelOpen)
        {
            AiStatusMessage = null;
            AiStatusLevel = WarningLevel.None;
        }
    }

    /// <summary>
    /// Translates <see cref="AiPromptText"/> into FCL via the shared AI provider
    /// and loads the result into the program pane (and visual editor when DNF-compatible).
    /// </summary>
    private async Task GenerateFromPromptAsync()
    {
        var prompt = AiPromptText?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        if (_translatorFactory is null)
        {
            AiStatusLevel = WarningLevel.Error;
            AiStatusMessage = "AI generation is not available.";
            return;
        }

        IsAiBusy = true;
        AiStatusLevel = WarningLevel.Info;
        AiStatusMessage = "Generating FCL…";

        try
        {
            var translator = await _translatorFactory(CancellationToken.None);
            if (translator is null)
            {
                AiStatusLevel = WarningLevel.Warning;
                AiStatusMessage = "No AI provider is configured. "
                    + "Set one up via Help → AI Provider settings.";
                return;
            }

            var result = await translator.TranslateAsync(prompt);

            if (!result.Success || string.IsNullOrWhiteSpace(result.Fcl))
            {
                AiStatusLevel = WarningLevel.Failed;
                AiStatusMessage = result.Explanation
                    ?? "Could not generate FCL from that description. Try rephrasing.";
                return;
            }

            // Load the generated FCL into the program pane.
            IsProgramPaneOpen = true;
            FclText = result.Fcl;          // setter marks IsFclTextModified = true
            ClearDiagnostics();

            // Best-effort: also populate the visual editor when DNF-compatible.
            SyncTextToVisual();

            AiStatusLevel = WarningLevel.Completed;
            AiStatusMessage = $"Generated in {result.Attempts} attempt"
                + (result.Attempts != 1 ? "s" : "") + ".";
        }
        catch (OperationCanceledException)
        {
            AiStatusLevel = WarningLevel.Warning;
            AiStatusMessage = "Generation cancelled.";
        }
        catch (Exception ex)
        {
            AiStatusLevel = WarningLevel.Error;
            AiStatusMessage = $"AI request failed: {ex.Message}";
        }
        finally
        {
            IsAiBusy = false;
        }
    }

    // ─────────────────────────────────────────────────────
    //  Initialization entry points
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Initializes the dialog from an existing <see cref="FclExpression"/>
    /// (e.g. a previously applied advanced filter).
    /// </summary>
    public void InitFromExpression(FclExpression? expression)
    {
        if (expression is null)
            return;

        // Attempt to extract DNF groups
        var groups = FclPipeline.ExtractDnfGroups(expression);
        if (groups is not null)
        {
            PopulateGroupsFromDnf(groups);
            SyncVisualToText();
        }
        else
        {
            // Expression is not DNF-convertible — show in text pane only
            ClearGroups();
            AddGroup(); // keep one empty group
            FclText = FclFormatter.Format(expression, FclFormatOptions.MultiLine);
            IsFclTextModified = false;
            IsProgramPaneOpen = true;
            IsDnfCompatible = false;
            NonDnfMessage = "This expression cannot be displayed visually. "
                + "Use the Program pane to edit, or click ← Apply to attempt conversion.";
        }
    }

    /// <summary>
    /// Initializes the dialog from a wildcard pattern string
    /// (from <see cref="Controls.FileFilterPane"/>).
    /// Creates a single condition: <c>FullName matches "&lt;patterns&gt;"</c>.
    /// </summary>
    public void InitFromPatterns(string patterns)
    {
        ClearGroups();
        var group = AddGroup();
        var row = group.Conditions[0]; // AddGroup creates one empty row
        row.SelectedField = FclField.FullName;
        row.SelectedOperator = FclOperator.Matches;
        row.ValueText = patterns;
    }

    // ─────────────────────────────────────────────────────
    //  Sync: Visual → Text
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the AST from the visual groups, formats it to FCL text,
    /// and updates <see cref="FclText"/>.
    /// </summary>
    public void SyncVisualToText()
    {
        var expr = BuildExpressionFromGroups();
        if (expr is not null)
        {
            // Temporarily bypass the IsFclTextModified setter
            _fclText = FclFormatter.Format(expr, FclFormatOptions.MultiLine);
            OnPropertyChanged(nameof(FclText));
        }
        else
        {
            _fclText = string.Empty;
            OnPropertyChanged(nameof(FclText));
        }

        IsFclTextModified = false;
        IsVisualModified = false;
        ClearDiagnostics();
        IsDnfCompatible = true;
        NonDnfMessage = null;
    }

    // ─────────────────────────────────────────────────────
    //  Sync: Text → Visual
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses the FCL text, validates it, and attempts to populate the
    /// visual editor groups. Shows diagnostics on failure.
    /// </summary>
    public void SyncTextToVisual()
    {
        ClearDiagnostics();

        var text = FclText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            ClearGroups();
            AddGroup();
            IsFclTextModified = false;
            IsDnfCompatible = true;
            NonDnfMessage = null;
            return;
        }

        // Parse and validate
        var result = FclPipeline.TryParse(text);
        if (!result.IsValid)
        {
            foreach (var d in result.Diagnostics)
                Diagnostics.Add(d);
            UpdateDiagnosticsProperties();
            return;
        }

        var expression = result.Expression!; // since result.IsValid

        // Check if DNF-shaped or convertible
        if (FclPipeline.IsDnf(expression))
        {
            var groups = FclPipeline.ExtractDnfGroups(expression);
            if (groups is not null)
            {
                PopulateGroupsFromDnf(groups);
                IsFclTextModified = false;
                IsDnfCompatible = true;
                NonDnfMessage = null;
                return;
            }
        }

        // Not DNF — attempt conversion
        var convertedGroups = FclPipeline.ExtractDnfGroups(expression);
        if (convertedGroups is not null)
        {
            PopulateGroupsFromDnf(convertedGroups);
            IsFclTextModified = false;
            IsDnfCompatible = true;
            NonDnfMessage = null;

            // Preserve the user-edited text (may contain comments, formatting)
            _appliedFclText = FclText;
        }
        else
        {
            // Cannot convert to DNF
            IsDnfCompatible = false;
            NonDnfMessage = "This expression cannot be represented in the visual editor "
                + "(DNF conversion would produce too many clauses). "
                + "You can still apply it directly as FCL text.";
        }
    }

    // ─────────────────────────────────────────────────────
    //  Apply filter
    // ─────────────────────────────────────────────────────

    private bool CanApply => HasAnyCompleteCondition || !string.IsNullOrWhiteSpace(FclText);

    private void ExecuteApply()
    {
        FclExpression? result;

        if (IsFclTextModified && IsProgramPaneOpen && !string.IsNullOrWhiteSpace(FclText))
        {
            // Text was modified — try to use the text-derived AST
            var parseResult = FclPipeline.TryParse(FclText.Trim());
            if (!parseResult.IsValid)
            {
                // Show errors, don't close
                ClearDiagnostics();
                foreach (var d in parseResult.Diagnostics)
                    Diagnostics.Add(d);
                UpdateDiagnosticsProperties();
                return;
            }
            result = parseResult.Expression;

            // Preserve the user-edited text (may contain comments, formatting)
            _appliedFclText = FclText;
        }
        else
        {
            // Use the visual editor — no custom text to preserve
            result = BuildExpressionFromGroups();
            _appliedFclText = null;
                // FIXME: Consider the case that user closed the program pane
                //  but hasn't modified the visual program - then we should keep the text!
        }

        _onApply(result);
    }

    // ─────────────────────────────────────────────────────
    //  Group management
    // ─────────────────────────────────────────────────────

    /// <summary>Adds a new OR group with one empty condition row.</summary>
    private FclConditionGroupVM AddGroup()
    {
        var group = new FclConditionGroupVM { IsFirst = Groups.Count == 0 };
        group.AddCondition();
        group.RemoveRequested += OnGroupRemoveRequested;
        group.GroupEmptied += OnGroupRemoveRequested;
        group.Modified += OnVisualModified;
        Groups.Add(group);
        UpdateGroupStates();
        IsVisualModified = true;
        return group;
    }

    private void OnGroupRemoveRequested(FclConditionGroupVM group)
    {
        group.RemoveRequested -= OnGroupRemoveRequested;
        group.GroupEmptied -= OnGroupRemoveRequested;
        group.Modified -= OnVisualModified;
        group.Clear();
        Groups.Remove(group);

        // Ensure at least one group remains
        if (Groups.Count == 0)
            AddGroup();
        else
            UpdateGroupStates();

        IsVisualModified = true;
    }

    /// <summary>
    /// Updates <see cref="FclConditionGroupVM.IsFirst"/> and
    /// <see cref="FclConditionGroupVM.CanRemoveGroup"/> on all groups.
    /// </summary>
    private void UpdateGroupStates()
    {
        for (int i = 0; i < Groups.Count; i++)
        {
            Groups[i].IsFirst = i == 0;
            // The sole remaining group cannot be removed
            Groups[i].CanRemoveGroup = Groups.Count > 1;
        }
    }

    private void ClearGroups()
    {
        foreach (var group in Groups)
        {
            group.RemoveRequested -= OnGroupRemoveRequested;
            group.GroupEmptied -= OnGroupRemoveRequested;
            group.Modified -= OnVisualModified;
            group.Clear();
        }
        Groups.Clear();
    }

    /// <summary>
    /// Populates the visual editor from extracted DNF groups
    /// (list of lists of literal expressions).
    /// </summary>
    private void PopulateGroupsFromDnf(List<List<FclExpression>> dnfGroups)
    {
        ClearGroups();

        foreach (var literals in dnfGroups)
        {
            var groupVm = new FclConditionGroupVM { IsFirst = Groups.Count == 0 };
            groupVm.RemoveRequested += OnGroupRemoveRequested;
            groupVm.GroupEmptied += OnGroupRemoveRequested;
            groupVm.Modified += OnVisualModified;

            foreach (var literal in literals)
            {
                var row = groupVm.AddCondition();
                row.FromExpression(literal);
            }

            Groups.Add(groupVm);
        }

        if (Groups.Count == 0)
            AddGroup();
        else
            UpdateGroupStates();
    }

    // ─────────────────────────────────────────────────────
    //  AST building
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="FclExpression"/> from the visual groups.
    /// Returns <c>null</c> if no complete conditions exist.
    /// </summary>
    private FclExpression? BuildExpressionFromGroups()
    {
        var groupExpressions = new List<FclExpression>();
        foreach (var group in Groups)
        {
            var expr = group.ToExpression();
            if (expr is not null)
                groupExpressions.Add(expr);
        }

        return groupExpressions.Count switch
        {
            0 => null,
            1 => groupExpressions[0],
            _ => new FclOrExpression([.. groupExpressions], SourceSpan.None)
        };
    }

    /// <summary>Whether any condition row across all groups is complete.</summary>
    private bool HasAnyCompleteCondition
    {
        get
        {
            foreach (var group in Groups)
                foreach (var row in group.Conditions)
                    if (row.IsComplete)
                        return true;
            return false;
        }
    }

    // ─────────────────────────────────────────────────────
    //  Diagnostics helpers
    // ─────────────────────────────────────────────────────

    private void ClearDiagnostics()
    {
        Diagnostics.Clear();
        SelectedDiagnostic = null;
        UpdateDiagnosticsProperties();
    }

    private void UpdateDiagnosticsProperties()
    {
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ValidationSummary));
    }

    private void ToggleProgramPane()
    {
        IsProgramPaneOpen = !IsProgramPaneOpen;

        // Auto-sync visual → text when opening the pane
        if (IsProgramPaneOpen && !IsFclTextModified)
            SyncVisualToText();
    }

    private void OnVisualModified() => IsVisualModified = true;
}
