using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using TapeWinNET.Utils;
using TapeWinNET.ViewModels;

namespace TapeWinNET.Controls;

/// <summary>
/// Reusable filter pane for file lists. Provides DOS-style wildcard filtering
/// (*, ?) with semicolon-separated patterns. Designed to work with large lists
/// by performing filtering asynchronously on a background thread.
/// <para>
/// The pane fully owns its UI state (filter text, etc.). When a filter is applied,
/// it passes the host both the parsed patterns and an opaque restore delegate.
/// The host stores that delegate on the navigation item and invokes it later
/// to restore the pane's state and re-apply the filter.
/// </para>
/// </summary>
public partial class FileFilterPane : UserControl
{
    private bool _isBusy;

    /// <summary>
    /// Async callback invoked when the user applies or removes a filter.
    /// <list type="bullet">
    ///   <item><c>patterns</c> — parsed wildcard patterns, or null to clear the filter.</item>
    ///   <item><c>restoreAction</c> — opaque delegate that restores this pane's UI state
    ///         and re-applies the filter. The host should store it and call it when the
    ///         user navigates back. Null when the filter is being removed.</item>
    /// </list>
    /// </summary>
    public Func<List<string>?, Func<Task>?, Task>? FilterRequested { get; set; }

    public FileFilterPane()
    {
        ApplyFilterCommand = new RelayCommand(async _ => await ApplyFilterAsync(), _ => HasFilterText && !IsBusy);
        RemoveFilterCommand = new RelayCommand(async _ => await RemoveFilterAsync(), _ => IsFilterActive && !IsBusy);

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

    /// <summary>
    /// Whether the control is busy performing a filter operation.
    /// While true, both commands are disabled.
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

    public ICommand ApplyFilterCommand { get; }
    public ICommand RemoveFilterCommand { get; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Resets the pane to its initial state: clears filter text and button states.
    /// Call when the host navigates away from the current content.
    /// </summary>
    public void Reset()
    {
        FilterText = string.Empty;
    }

    #endregion

    #region Private Methods

    private static void OnFilterTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pane = (FileFilterPane)d;
        pane.HasFilterText = !string.IsNullOrWhiteSpace(e.NewValue as string);
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ApplyFilterAsync()
    {
        var text = FilterText?.Trim();
        if (string.IsNullOrEmpty(text) || FilterRequested == null)
            return;

        var patterns = FileFilter.ParsePatterns(text);
        if (patterns.Count == 0)
            return;

        // Capture the filter text so the restore delegate can reconstruct this state
        //  Notice: local function acts as a delegate
        Task restoreAction()
        {
            FilterText = text;
            return ApplyFilterAsync();
        }

        IsBusy = true;
        try
        {
            await FilterRequested(patterns, restoreAction);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveFilterAsync()
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
}
