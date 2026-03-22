using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using FclNET;
using FclNET.Ast;

using TapeWinNET.Utils;
using TapeWinNET.ViewModels;

namespace TapeWinNET.Controls;

/// <summary>
/// Captures the <see cref="FclFilterWindow"/> layout so it can be restored
/// the next time the dialog opens for the same backup set (session-only,
/// not persisted to disk). Stored alongside the advanced filter expression.
/// </summary>
internal record FclFilterWindowState(
    double Width,
    double Height,
    double Left,
    double Top,
    bool IsProgramPaneOpen,
    double ProgramColumnWidth,
    string? SavedFclText);

/// <summary>
/// Reusable filter pane for file lists. Supports two modes:
/// <list type="bullet">
///   <item><b>Pattern mode:</b> DOS-style wildcard filtering (*, ?) with
///         semicolon-separated patterns entered in the text box.</item>
///   <item><b>Advanced mode:</b> Full FCL filter built via
///         <see cref="FclFilterWindow"/>. The text box shows a read-only summary;
///         editing requires reopening the dialog.</item>
/// </list>
/// <para>
/// The pane fully owns its UI state and the advanced filter expression.
/// When the filter is applied, it builds an <see cref="FclEvaluator"/> and
/// passes it to the host via <see cref="FilterRequested"/>. The host stores
/// an opaque restore delegate on the navigation item for later re-application.
/// </para>
/// </summary>
public partial class FileFilterPane : UserControl
{
    private bool _isBusy;

    // Advanced filter state — the pane owns the expression
    private FclExpression? _advancedExpression;
    private int _advancedConditionCount;
    private int _advancedGroupCount;

    // Per-backup-set window layout (session-only, not persisted to disk)
    private FclFilterWindowState? _windowState;

    /// <summary>
    /// Async callback invoked when the user applies or disables a filter.
    /// <list type="bullet">
    ///   <item><c>evaluator</c> — ready-to-use FCL evaluator, or null to disable.</item>
    ///   <item><c>restoreAction</c> — opaque delegate that restores this pane's state
    ///         and re-applies the filter on navigation. Null when disabling.</item>
    /// </list>
    /// </summary>
    public Func<FclEvaluator?, Func<Task>?, Task>? FilterRequested { get; set; }

    public FileFilterPane()
    {
        ToggleFilterCommand = new RelayCommand(
            async _ => await ToggleFilterAsync(),
            _ => !IsBusy && (IsFilterActive || CanApplyFilter));
        AdvancedFilterCommand = new RelayCommand(
            _ => OpenAdvancedFilter(),
            _ => !IsBusy);

        InitializeComponent();
    }

    #region Dependency Properties

    public static readonly DependencyProperty FilterTextProperty =
        DependencyProperty.Register(nameof(FilterText), typeof(string), typeof(FileFilterPane),
            new PropertyMetadata(string.Empty, OnFilterTextChanged));

    public static readonly DependencyProperty TotalCountProperty =
        DependencyProperty.Register(nameof(TotalCount), typeof(int), typeof(FileFilterPane),
            new PropertyMetadata(0));

    public static readonly DependencyProperty FilteredCountProperty =
        DependencyProperty.Register(nameof(FilteredCount), typeof(int), typeof(FileFilterPane),
            new PropertyMetadata(0));

    public static readonly DependencyProperty IsFilterActiveProperty =
        DependencyProperty.Register(nameof(IsFilterActive), typeof(bool), typeof(FileFilterPane),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasFilterTextProperty =
        DependencyProperty.Register(nameof(HasFilterText), typeof(bool), typeof(FileFilterPane),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasAdvancedFilterProperty =
        DependencyProperty.Register(nameof(HasAdvancedFilter), typeof(bool), typeof(FileFilterPane),
            new PropertyMetadata(false));

    public static readonly DependencyProperty CanApplyFilterProperty =
        DependencyProperty.Register(nameof(CanApplyFilter), typeof(bool), typeof(FileFilterPane),
            new PropertyMetadata(false));

    public static readonly DependencyProperty AdvancedFilterTooltipProperty =
        DependencyProperty.Register(nameof(AdvancedFilterTooltip), typeof(string), typeof(FileFilterPane),
            new PropertyMetadata("Open the advanced filter editor"));

    public string FilterText
    {
        get => (string)GetValue(FilterTextProperty);
        set => SetValue(FilterTextProperty, value);
    }

    public int TotalCount
    {
        get => (int)GetValue(TotalCountProperty);
        set => SetValue(TotalCountProperty, value);
    }

    public int FilteredCount
    {
        get => (int)GetValue(FilteredCountProperty);
        set => SetValue(FilteredCountProperty, value);
    }

    public bool IsFilterActive
    {
        get => (bool)GetValue(IsFilterActiveProperty);
        set => SetValue(IsFilterActiveProperty, value);
    }

    public bool HasFilterText
    {
        get => (bool)GetValue(HasFilterTextProperty);
        private set => SetValue(HasFilterTextProperty, value);
    }

    public bool HasAdvancedFilter
    {
        get => (bool)GetValue(HasAdvancedFilterProperty);
        private set => SetValue(HasAdvancedFilterProperty, value);
    }

    public bool CanApplyFilter
    {
        get => (bool)GetValue(CanApplyFilterProperty);
        private set => SetValue(CanApplyFilterProperty, value);
    }

    public string AdvancedFilterTooltip
    {
        get => (string)GetValue(AdvancedFilterTooltipProperty);
        private set => SetValue(AdvancedFilterTooltipProperty, value);
    }

    /// <summary>
    /// Whether the control is busy performing a filter operation.
    /// While true, all commands are disabled.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    #endregion

    #region Commands

    public ICommand ToggleFilterCommand { get; }
    public ICommand AdvancedFilterCommand { get; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Resets the pane to its initial state: clears filter text, advanced
    /// filter state, and button states. Call when the host navigates away.
    /// </summary>
    public void Reset()
    {
        FilterText = string.Empty;
        ClearAdvancedFilter();
    }

    #endregion

    #region Private Methods — Filter Toggle

    private static void OnFilterTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pane = (FileFilterPane)d;
        pane.HasFilterText = !string.IsNullOrWhiteSpace(e.NewValue as string);
        pane.UpdateCanApply();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateCanApply()
    {
        CanApplyFilter = HasAdvancedFilter || HasFilterText;
    }

    /// <summary>
    /// Toggles the filter: if active → disable; if inactive → apply.
    /// </summary>
    private async Task ToggleFilterAsync()
    {
        if (IsFilterActive)
            await DisableFilterAsync();
        else
            await ApplyFilterAsync();
    }

    /// <summary>
    /// Applies the current filter (pattern or advanced) to the file list.
    /// </summary>
    private async Task ApplyFilterAsync()
    {
        if (FilterRequested == null)
            return;

        FclEvaluator? evaluator;

        if (HasAdvancedFilter && _advancedExpression is not null)
        {
            // Advanced mode — build evaluator from the stored expression
            evaluator = new FclEvaluator(_advancedExpression);
        }
        else
        {
            // Pattern mode — build evaluator from the text box
            var text = FilterText?.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            var patterns = FileFilter.ParsePatterns(text);
            if (patterns.Count == 0)
                return;

            evaluator = FclPipeline.CreateWildcardEvaluator(patterns);
        }

        // Capture state for the restore delegate
        var savedExpression = _advancedExpression;
        var savedConditionCount = _advancedConditionCount;
        var savedGroupCount = _advancedGroupCount;
        var savedText = FilterText;
        var savedHasAdvanced = HasAdvancedFilter;
        var savedWindowState = _windowState;

        Task reapplyAction()
        {
            // Restore the pane's UI state
            _advancedExpression = savedExpression;
            _advancedConditionCount = savedConditionCount;
            _advancedGroupCount = savedGroupCount;
            _windowState = savedWindowState;
            HasAdvancedFilter = savedHasAdvanced;
            FilterText = savedText ?? string.Empty;
            UpdateCanApply();
            UpdateAdvancedUI();
            return ApplyFilterAsync();
        }

        IsBusy = true;
        try
        {
            await FilterRequested(evaluator, reapplyAction);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Disables the active filter without clearing the definition.
    /// The user can re-apply later without rebuilding the filter.
    /// </summary>
    private async Task DisableFilterAsync()
    {
        if (FilterRequested == null)
            return;

        IsBusy = true;
        try
        {
            await FilterRequested(null, null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    #endregion

    #region Private Methods — Advanced Filter

    /// <summary>
    /// Opens the <see cref="FclFilterWindow"/> modal dialog.
    /// On Apply, stores the expression and applies the filter.
    /// On Clear, removes the advanced filter definition.
    /// </summary>
    private void OpenAdvancedFilter()
    {
        FclExpression? dialogResult = null;
        bool wasCleared = false;

        var vm = new FclFilterWindowVM(
            expression =>
            {
                // Apply callback — expression is null when "Clear" is clicked
                dialogResult = expression;
                wasCleared = expression is null;
                Window.GetWindow(this)?.OwnedWindows
                    .OfType<FclFilterWindow>().FirstOrDefault()?.Close();
            },
            () =>
            {
                // Cancel callback
                Window.GetWindow(this)?.OwnedWindows
                    .OfType<FclFilterWindow>().FirstOrDefault()?.Close();
            });

        // Initialize from current state
        if (_advancedExpression is not null)
            vm.InitFromExpression(_advancedExpression);
        else if (!string.IsNullOrWhiteSpace(FilterText))
            vm.InitFromPatterns(FilterText.Trim());

        // Restore saved user-edited FCL text (preserves comments, formatting)
        if (_windowState is { SavedFclText: { } savedText })
            vm.RestoreSavedFclText(savedText);

        var dialog = new FclFilterWindow(vm)
        {
            Owner = Window.GetWindow(this)
        };

        // Restore saved window layout for this backup set (if any)
        if (_windowState is { } ws)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = ws.Left;
            dialog.Top = ws.Top;
            dialog.Width = ws.Width;
            dialog.Height = ws.Height;
            if (ws.IsProgramPaneOpen)
                dialog.RestoreProgramPaneOpen(ws.ProgramColumnWidth);
            if (!string.IsNullOrWhiteSpace(ws.SavedFclText))
                dialog.ProgramTextBox.Text = ws.SavedFclText;
        }

        dialog.ShowDialog();

        // Capture the window layout so it persists across re-opens for this set
        _windowState = new FclFilterWindowState(
            dialog.Width, dialog.Height,
            dialog.Left, dialog.Top,
            vm.IsProgramPaneOpen,
            dialog.ProgramColumnWidth,
            vm.AppliedFclText);

        // Process result
        if (wasCleared)
        {
            // User explicitly cleared the filter in the dialog
            ClearAdvancedFilter();
            FilterText = string.Empty;
            if (IsFilterActive)
                _ = DisableFilterAsync();
        }
        else if (dialogResult is not null)
        {
            // User applied a new advanced filter
            SetAdvancedFilter(dialogResult);
            _ = ApplyFilterAsync();
        }
        // else: cancelled — no changes
    }

    /// <summary>
    /// Stores the advanced filter expression and updates the UI.
    /// </summary>
    private void SetAdvancedFilter(FclExpression expression)
    {
        _advancedExpression = expression;
        CountConditions(expression, out _advancedConditionCount, out _advancedGroupCount);
        HasAdvancedFilter = true;
        UpdateCanApply();
        UpdateAdvancedUI();
    }

    /// <summary>
    /// Clears the advanced filter state, returning to pattern mode.
    /// </summary>
    private void ClearAdvancedFilter()
    {
        _advancedExpression = null;
        _advancedConditionCount = 0;
        _advancedGroupCount = 0;
        _windowState = null;
        HasAdvancedFilter = false;
        UpdateCanApply();
        AdvancedFilterTooltip = "Open the advanced filter editor";
    }

    /// <summary>
    /// Updates the textbox summary and tooltip to reflect the advanced filter.
    /// </summary>
    private void UpdateAdvancedUI()
    {
        if (!HasAdvancedFilter)
            return;

        // Summary in the textbox
        var condWord = _advancedConditionCount == 1 ? "condition" : "conditions";
        var groupWord = _advancedGroupCount == 1 ? "group" : "groups";
        FilterText = $"[{_advancedConditionCount} {condWord} in {_advancedGroupCount} {groupWord}]";

        // Tooltip on the Advanced button
        AdvancedFilterTooltip = $"Advanced filter: {_advancedConditionCount} {condWord} in {_advancedGroupCount} {groupWord}";
    }

    /// <summary>
    /// Counts conditions and groups in an FCL expression (DNF-aware).
    /// </summary>
    private static void CountConditions(FclExpression expression, out int conditions, out int groups)
    {
        var dnfGroups = FclPipeline.ExtractDnfGroups(expression);
        if (dnfGroups is not null)
        {
            groups = dnfGroups.Count;
            conditions = 0;
            foreach (var g in dnfGroups)
                conditions += g.Count;
        }
        else
        {
            // Non-DNF expression — count all FclCondition nodes
            groups = 1;
            conditions = CountConditionNodes(expression);
        }
    }

    /// <summary>
    /// Recursively counts <see cref="FclCondition"/> nodes in an expression tree.
    /// </summary>
    private static int CountConditionNodes(FclExpression expr) => expr switch
    {
        FclCondition => 1,
        FclNotExpression not => CountConditionNodes(not.Operand),
        FclGroupExpression grp => CountConditionNodes(grp.Inner),
        FclChainExpression chain => chain.Operands.Sum(CountConditionNodes),
        _ => 0
    };

    #endregion
}
