using System.Collections.ObjectModel;
using System.Windows.Input;

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

    private string _fclText = string.Empty;
    private bool _isProgramPaneOpen;
    private bool _isFclTextModified;
    private FclDiagnostic? _selectedDiagnostic;
    private bool _isDnfCompatible = true;
    private string? _nonDnfMessage;

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
        ApplyVisualToTextCommand = new RelayCommand(_ => SyncVisualToText());
        ApplyTextToVisualCommand = new RelayCommand(
            _ => SyncTextToVisual(),
            _ => IsProgramPaneOpen);
        ToggleProgramPaneCommand = new RelayCommand(_ => ToggleProgramPane());
        ApplyFilterCommand = new RelayCommand(_ => ExecuteApply(), _ => CanApply);
        ClearFilterCommand = new RelayCommand(_ => _onApply(null));
        CancelCommand = new RelayCommand(_ => _onCancel());
        GenerateWithAiCommand = new RelayCommand(_ => { }, _ => false); // placeholder
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

    /// <summary>Whether the user has modified the FCL text since the last sync.</summary>
    public bool IsFclTextModified
    {
        get => _isFclTextModified;
        private set => SetProperty(ref _isFclTextModified, value);
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

        var expression = result.Expression!;

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
        }
        else
        {
            // Use the visual editor
            result = BuildExpressionFromGroups();
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
        Groups.Add(group);
        UpdateGroupStates();
        return group;
    }

    private void OnGroupRemoveRequested(FclConditionGroupVM group)
    {
        group.RemoveRequested -= OnGroupRemoveRequested;
        group.GroupEmptied -= OnGroupRemoveRequested;
        group.Clear();
        Groups.Remove(group);

        // Ensure at least one group remains
        if (Groups.Count == 0)
            AddGroup();
        else
            UpdateGroupStates();
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
}
