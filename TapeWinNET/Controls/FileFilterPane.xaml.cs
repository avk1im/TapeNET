using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using FclAiNET;

using FclNET;
using FclNET.Ast;

using Microsoft.Extensions.Logging;

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
/// Reusable filter pane for file lists. Supports two filter input modes:
/// <list type="bullet">
///   <item><b>Pattern mode:</b> DOS-style wildcard filtering (*, ?) with
///         semicolon-separated patterns entered in the text box.</item>
///   <item><b>Advanced mode:</b> Full FCL filter built via
///         <see cref="FclFilterWindow"/>. The text box shows a read-only summary;
///         editing requires reopening the dialog.</item>
/// </list>
/// <para>
/// <b>Host wiring:</b> The pane supports two dispatch modes:
/// <list type="number">
///   <item><b>Direct mode</b> — set <see cref="FilterTarget"/> to a
///         <see cref="FilteredFileList"/>. The pane sets the filter directly
///         and awaits completion, then calls <see cref="FilterStateChanged"/>
///         with the restore delegate.</item>
///   <item><b>Callback mode</b> — set <see cref="FilterRequested"/>. The host
///         receives the evaluator + restore delegate and applies the filter itself.</item>
/// </list>
/// Direct mode takes priority when <see cref="FilterTarget"/> is non-null.
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
    /// Used in <b>callback mode</b> (when <see cref="FilterTarget"/> is null).
    /// </summary>
    public Func<FclEvaluator?, Func<Task>?, Task>? FilterRequested { get; set; }

    /// <summary>
    /// Direct-mode filter target. When set, the pane applies the filter directly
    ///  to this <see cref="FilteredFileList"/> instead of going through
    ///  <see cref="FilterRequested"/>.
    /// </summary>
    public FilteredFileList? FilterTarget { get; set; }

    /// <summary>
    /// Called after a direct-mode filter apply/disable completes. The parameter
    ///  is the restore delegate (<c>null</c> when disabling). The host typically
    ///  rebuilds its display list and stores the delegate on the current
    ///  <see cref="Models.BackupSetView.SavedFilterState"/>.
    /// </summary>
    public Func<Func<Task>?, Task>? FilterStateChanged { get; set; }

    /// <summary>
    /// Optional callback invoked (in addition to the single-set path) when
    ///  <see cref="ApplyToAll"/> is checked. The host applies the evaluator to
    ///  every backup set and updates <see cref="Models.TOCView.PendingGlobalFilter"/>.
    /// <list type="bullet">
    ///   <item><c>evaluator</c> — ready-to-use FCL evaluator, or null to disable for all.</item>
    ///   <item><c>restoreAction</c> — opaque delegate for the current set's pane state.</item>
    /// </list>
    /// </summary>
    public Func<FclEvaluator?, Func<Task>?, Task>? AllSetsFilterRequested { get; set; }

    public FileFilterPane()
    {
        ToggleFilterCommand = new RelayCommand(
            async _ => await ToggleFilterAsync(),
            _ => !IsBusy && (IsFilterActive || CanApplyFilter));
        ApplyFilterCommand = new RelayCommand(
            async _ => await ApplyFilterAsync(),
            _ => !IsBusy && CanApplyFilter);
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

    public static readonly DependencyProperty HasMultipleSetsProperty =
        DependencyProperty.Register(nameof(HasMultipleSets), typeof(bool), typeof(FileFilterPane),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ApplyToAllProperty =
        DependencyProperty.Register(nameof(ApplyToAll), typeof(bool), typeof(FileFilterPane),
            new PropertyMetadata(false));

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
    /// Whether more than one backup set exists in the current TOC.
    /// Controls the visibility of the "all sets" checkbox. Set by the host.
    /// </summary>
    public bool HasMultipleSets
    {
        get => (bool)GetValue(HasMultipleSetsProperty);
        set => SetValue(HasMultipleSetsProperty, value);
    }

    /// <summary>
    /// Whether the filter should be applied to / disabled for all sets.
    /// Bound to the "all sets" checkbox. When true and
    ///  <see cref="AllSetsFilterRequested"/> is wired, the apply/disable
    ///  operations also fan out to every backup set.
    /// </summary>
    public bool ApplyToAll
    {
        get => (bool)GetValue(ApplyToAllProperty);
        set => SetValue(ApplyToAllProperty, value);
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
    /// <summary>
    /// Always (re-)applies the current filter. Used by the text box Enter key
    ///  so that editing patterns and pressing Enter updates the active filter
    ///  instead of toggling it off.
    /// </summary>
    public ICommand ApplyFilterCommand { get; }
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
        ApplyToAll = false;
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
    /// Uses direct mode (<see cref="FilterTarget"/>) when available,
    ///  otherwise falls back to <see cref="FilterRequested"/> callback mode.
    /// </summary>
    private async Task ApplyFilterAsync()
    {
        bool directMode = FilterTarget is not null;
        if (!directMode && FilterRequested is null)
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

            var patterns = FclTapeFileFilter.ParsePatterns(text);
            if (patterns.Count == 0)
                return;

            evaluator = FclPipeline.CreateWildcardEvaluator(patterns);
        }

        var reapplyAction = CaptureRestoreAction(reapply: true);

        IsBusy = true;
        try
        {
            if (directMode)
            {
                // Direct mode — apply filter to FilterTarget, then notify host
                FilterTarget!.Filter = new FclTapeFileFilter(evaluator);
                await FilterTarget.FilterTask;
                if (FilterStateChanged is not null)
                    await FilterStateChanged(reapplyAction);
            }
            else
            {
                // Callback mode — delegate to host
                await FilterRequested!(evaluator, reapplyAction);
            }

            // "Apply to all sets" fan-out — propagate filter to every other set
            if (ApplyToAll && AllSetsFilterRequested is not null)
                await AllSetsFilterRequested(evaluator, reapplyAction);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Disables the active filter without clearing the definition.
    /// The user can re-apply later without rebuilding the filter.
    /// The filter state is still saved so it survives backup-set navigation.
    /// </summary>
    private async Task DisableFilterAsync()
    {
        bool directMode = FilterTarget is not null;
        if (!directMode && FilterRequested is null)
            return;

        // Save the current definition so it can be restored after navigation
        var restoreAction = CaptureRestoreAction(reapply: false);

        IsBusy = true;
        try
        {
            if (directMode)
            {
                FilterTarget!.Filter = null;
                if (FilterStateChanged is not null)
                    await FilterStateChanged(restoreAction);
            }
            else
            {
                await FilterRequested!(null, restoreAction);
            }

            // "Disable for all sets" fan-out
            if (ApplyToAll && AllSetsFilterRequested is not null)
                await AllSetsFilterRequested(null, restoreAction);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Captures the current pane state (filter text, advanced expression, window layout)
    ///  and returns a delegate that restores it. If <paramref name="reapply"/> is true,
    ///  the delegate also re-applies the filter after restoring the UI.
    /// </summary>
    private Func<Task> CaptureRestoreAction(bool reapply)
    {
        var savedExpression = _advancedExpression;
        var savedConditionCount = _advancedConditionCount;
        var savedGroupCount = _advancedGroupCount;
        var savedText = FilterText;
        var savedHasAdvanced = HasAdvancedFilter;
        var savedWindowState = _windowState;

        return () =>
        {
            _advancedExpression = savedExpression;
            _advancedConditionCount = savedConditionCount;
            _advancedGroupCount = savedGroupCount;
            _windowState = savedWindowState;
            HasAdvancedFilter = savedHasAdvanced;
            FilterText = savedText ?? string.Empty;
            UpdateCanApply();
            UpdateAdvancedUI();
            return reapply ? ApplyFilterAsync() : Task.CompletedTask;
        };
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
            },
            CreateFclAiTranslatorAsync);

        // Initialize from current state
        if (_advancedExpression is not null)
            vm.InitFromExpression(_advancedExpression);
        else if (!string.IsNullOrWhiteSpace(FilterText))
            vm.InitFromPatterns(FilterText.Trim());

        // Pass "all sets" context to the dialog
        vm.HasMultipleSets = HasMultipleSets;
        vm.ApplyToAll = ApplyToAll;

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

        // Sync the "apply to all sets" checkbox back from the dialog
        ApplyToAll = vm.ApplyToAll;

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
    /// Lazily builds an <see cref="FclAiTranslator"/> bound to the app-wide
    /// shared AI session — the same provider the Help system uses. Returns
    /// <c>null</c> when no AI provider is configured or the user declines setup.
    /// </summary>
    private static async Task<FclAiTranslator?> CreateFclAiTranslatorAsync(CancellationToken ct)
    {
        // Reuse the process-wide AI session (prompts for provider setup if needed).
        var session = await App.AiSessionHost.EnsureAsync(promptUser: true, ct);
        if (session?.ChatClient is not { } chatClient)
            return null;

        var logger = App.LoggerFactory.CreateLogger<FclAiTranslator>();
        return new FclAiTranslator(chatClient, logger);
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
