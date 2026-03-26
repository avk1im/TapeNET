using System.Collections;
using System.ComponentModel;

using TapeLibNET;

namespace TapeWinNET.Utils;

/// <summary>
/// Wraps an immutable <see cref="IReadOnlyList{TapeFileInfo}"/> source and exposes
///  a filtered view, also as <see cref="IReadOnlyList{TapeFileInfo}"/>. Setting
///  <see cref="Filter"/> triggers async recomputation on the thread pool.
///  Implements <see cref="IReadOnlyList{TapeFileInfo}"/> so instances can chain
///  as sources for another <see cref="FilteredFileList"/>.
/// <para>
///  Also manages per-file checked (selected) state via an internal
///  <see cref="HashSet{TapeFileInfo}"/>, enabling centralized statistics
///  for all / filtered / checked / filtered-and-checked counts and sizes.
/// </para>
/// </summary>
/// <remarks>
/// <b>Thread model:</b> <see cref="Filter"/> setter and all checked-state methods
///  must be called from the UI thread. Filter computation runs on the thread pool.
///  All <see cref="PropertyChanged"/> and <see cref="FilterCompleted"/> notifications
///  fire on the UI thread (the <c>async</c>/<c>await</c> continuation resumes on the
///  captured <see cref="SynchronizationContext"/>).
/// <para>
///  <b>Source contract:</b> The source list must be effectively immutable for the
///  lifetime of this instance. If the source content changes (e.g. because the source
///  is a chained <see cref="FilteredFileList"/> whose filter changed), call
///  <see cref="Refilter"/> to refresh.
/// </para>
/// </remarks>
/// <remarks>
/// Creates a new <see cref="FilteredFileList"/> attached to the given source.
/// </remarks>
/// <param name="source">Immutable source list (e.g. <see cref="TapeSetTOC"/>
///  or another <see cref="FilteredFileList"/> for chaining).</param>
public sealed class FilteredFileList(IReadOnlyList<TapeFileInfo> source) : IReadOnlyList<TapeFileInfo>, INotifyPropertyChanged
{
    private readonly IReadOnlyList<TapeFileInfo> _source = source;
    private readonly HashSet<TapeFileInfo> _checkedItems = [];
    private readonly long _sourceTotalSize = ComputeTotalSize(source);

    // Filter state
    private ITapeFileFilter? _filter;
    private List<TapeFileInfo>? _filteredList;    // null = no filter active (all source items visible)
    private HashSet<TapeFileInfo>? _filteredSet;  // null = no filter; for O(1) Contains()
    private CancellationTokenSource? _filterCts;
    private bool _isComputing;

    // Checked statistics — updated incrementally on single-item changes,
    //  recomputed from scratch on bulk changes and filter completions.
    private long _filteredTotalSize;
    private long _checkedTotalSize;
    private int _filteredCheckedCount;
    private long _filteredCheckedTotalSize;

    // Cached PropertyChangedEventArgs to avoid allocations on frequent updates
    private static readonly PropertyChangedEventArgs s_countArgs = new(nameof(Count));
    private static readonly PropertyChangedEventArgs s_isFilteredArgs = new(nameof(IsFiltered));
    private static readonly PropertyChangedEventArgs s_isComputingArgs = new(nameof(IsComputing));
    private static readonly PropertyChangedEventArgs s_filteredTotalSizeArgs = new(nameof(FilteredTotalSize));
    private static readonly PropertyChangedEventArgs s_checkedCountArgs = new(nameof(CheckedCount));
    private static readonly PropertyChangedEventArgs s_checkedTotalSizeArgs = new(nameof(CheckedTotalSize));
    private static readonly PropertyChangedEventArgs s_filteredCheckedCountArgs = new(nameof(FilteredCheckedCount));
    private static readonly PropertyChangedEventArgs s_filteredCheckedTotalSizeArgs = new(nameof(FilteredCheckedTotalSize));
    private static readonly PropertyChangedEventArgs s_areAllFilteredCheckedArgs = new(nameof(AreAllFilteredChecked));


    // ======================================================================
    #region Source properties

    /// <summary>The underlying source list this instance is attached to.</summary>
    public IReadOnlyList<TapeFileInfo> Source => _source;

    /// <summary>Total count of files in the source.</summary>
    public int SourceCount => _source.Count;

    /// <summary>Sum of <see cref="TapeFileDescriptor.Length"/> for all source files.
    ///  Computed once at construction.</summary>
    public long SourceTotalSize => _sourceTotalSize;

    #endregion


    // ======================================================================
    #region Filter

    /// <summary>
    /// Current file filter. Setting a new value:
    /// <list type="bullet">
    ///   <item>Cancels any in-progress computation.</item>
    ///   <item><c>null</c> → immediate synchronous reset to "all files".</item>
    ///   <item>Non-null → starts async computation on the thread pool.</item>
    /// </list>
    /// Fires <see cref="PropertyChanged"/> for <see cref="Count"/>,
    ///  <see cref="IsFiltered"/>, <see cref="IsComputing"/>, and related statistics.
    /// </summary>
    public ITapeFileFilter? Filter
    {
        get => _filter;
        set
        {
            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filter = value;

            if (value is null)
            {
                _filterCts = null;
                ClearFilterResults();
                FilterTask = Task.CompletedTask;
            }
            else
            {
                var cts = new CancellationTokenSource();
                _filterCts = cts;
                FilterTask = ComputeFilteredAsync(value, cts.Token);
            }
        }
    }

    /// <summary>Awaitable task for the current filter computation.
    ///  <see cref="Task.CompletedTask"/> when no filter is active.
    ///  Callers can use <c>await Task.WhenAll(...)</c> across multiple instances
    ///  for parallel filtering.</summary>
    public Task FilterTask { get; private set; } = Task.CompletedTask;

    /// <summary>True while a background filter computation is running.</summary>
    public bool IsComputing
    {
        get => _isComputing;
        private set
        {
            if (_isComputing != value)
            {
                _isComputing = value;
                PropertyChanged?.Invoke(this, s_isComputingArgs);
            }
        }
    }

    /// <summary>True when a filter produced a result AND the filtered count
    ///  differs from the source count.</summary>
    public bool IsFiltered => _filteredList is not null && _filteredList.Count != _source.Count;

    /// <summary>Force re-evaluation with the current filter. Useful after the
    ///  chained source changes or when the filter's behavior has changed.</summary>
    public void Refilter() => Filter = _filter;

    #endregion


    // ======================================================================
    #region IReadOnlyList<TapeFileInfo> — filtered result

    /// <summary>Number of files after filtering
    ///  (equals <see cref="SourceCount"/> when no filter is active).</summary>
    public int Count => _filteredList?.Count ?? _source.Count;

    /// <summary>Access a filtered file by index.</summary>
    public TapeFileInfo this[int index] =>
        (_filteredList ?? (IReadOnlyList<TapeFileInfo>)_source)[index];

    /// <inheritdoc />
    public IEnumerator<TapeFileInfo> GetEnumerator() =>
        (_filteredList ?? (IEnumerable<TapeFileInfo>)_source).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>O(1) check whether a <see cref="TapeFileInfo"/> is in the current
    ///  filtered view. Returns <c>true</c> for any item when no filter is active.</summary>
    public bool Contains(TapeFileInfo item) => _filteredSet?.Contains(item) ?? true;

    /// <summary>Sum of <see cref="TapeFileDescriptor.Length"/> for the filtered files
    ///  (equals <see cref="SourceTotalSize"/> when no filter is active).</summary>
    public long FilteredTotalSize => _filteredList is not null ? _filteredTotalSize : _sourceTotalSize;

    #endregion


    // ======================================================================
    #region Checked state

    /// <summary>Checks whether the given file is marked as checked (selected).</summary>
    public bool IsChecked(TapeFileInfo item) => _checkedItems.Contains(item);

    /// <summary>Returns the set of all currently checked items as a live read-only view.
    ///  Reflects subsequent changes to checked state.</summary>
    public IReadOnlyCollection<TapeFileInfo> CheckedItems => _checkedItems;

    /// <summary>
    /// Sets the checked state for a single file. Updates statistics incrementally
    ///  and fires <see cref="PropertyChanged"/> for all checked-related properties,
    ///  followed by <see cref="CheckedChanged"/> for the individual item.
    /// </summary>
    public void SetChecked(TapeFileInfo item, bool isChecked)
    {
        bool changed = isChecked ? _checkedItems.Add(item) : _checkedItems.Remove(item);
        if (!changed)
            return;

        // Update statistics incrementally
        long size = item.FileDescr.Length;
        bool inFiltered = _filteredSet?.Contains(item) ?? true;

        if (isChecked)
        {
            _checkedTotalSize += size;
            if (inFiltered) { _filteredCheckedCount++; _filteredCheckedTotalSize += size; }
        }
        else
        {
            _checkedTotalSize -= size;
            if (inFiltered) { _filteredCheckedCount--; _filteredCheckedTotalSize -= size; }
        }

        NotifyCheckedStatsChanged();
        CheckedChanged?.Invoke(item);
    }

    /// <summary>
    /// Sets the checked state for multiple files. Updates statistics as a single
    ///  batch — more efficient than calling <see cref="SetChecked(TapeFileInfo, bool)"/>
    ///  in a loop. Does not fire <see cref="CheckedChanged"/> per item.
    /// </summary>
    public void SetChecked(IEnumerable<TapeFileInfo> items, bool isChecked)
    {
        if (isChecked)
        {
            foreach (var item in items)
                _checkedItems.Add(item);
        }
        else
        {
            foreach (var item in items)
                _checkedItems.Remove(item);
        }

        RecomputeCheckedStats();
        NotifyCheckedStatsChanged();
    }

    /// <summary>
    /// Checks or unchecks all files in the current filtered view.
    ///  When no filter is active, affects all source files. Files hidden by
    ///  the filter retain their current checked state.
    ///  Call after awaiting <see cref="FilterTask"/> to ensure the filter
    ///  results are up-to-date.
    /// </summary>
    public void SetFilteredChecked(bool isChecked)
    {
        var items = (IEnumerable<TapeFileInfo>?)_filteredList ?? _source;
        if (isChecked)
        {
            foreach (var item in items)
                _checkedItems.Add(item);
        }
        else
        {
            foreach (var item in items)
                _checkedItems.Remove(item);
        }

        RecomputeCheckedStats();
        NotifyCheckedStatsChanged();
    }

    /// <summary>
    /// Checks or unchecks all files in the source (regardless of filter state).
    /// </summary>
    public void SetAllChecked(bool isChecked)
    {
        if (isChecked)
        {
            foreach (var item in _source)
                _checkedItems.Add(item);
        }
        else
        {
            _checkedItems.Clear();
        }

        RecomputeCheckedStats();
        NotifyCheckedStatsChanged();
    }

    /// <summary>Unchecks all files. Equivalent to <c>SetAllChecked(false)</c>
    ///  but avoids iterating the source.</summary>
    public void ClearChecked()
    {
        if (_checkedItems.Count == 0)
            return;

        _checkedItems.Clear();
        _checkedTotalSize = 0;
        _filteredCheckedCount = 0;
        _filteredCheckedTotalSize = 0;

        NotifyCheckedStatsChanged();
    }

    /// <summary>Total number of checked files (regardless of filter state).</summary>
    public int CheckedCount => _checkedItems.Count;

    /// <summary>Sum of sizes of all checked files (regardless of filter state).</summary>
    public long CheckedTotalSize => _checkedTotalSize;

    /// <summary>Number of files that are both checked AND in the current filtered view.
    ///  Equals <see cref="CheckedCount"/> when no filter is active.</summary>
    public int FilteredCheckedCount => _filteredCheckedCount;

    /// <summary>Sum of sizes of files that are both checked AND in the filtered view.
    ///  Equals <see cref="CheckedTotalSize"/> when no filter is active.</summary>
    public long FilteredCheckedTotalSize => _filteredCheckedTotalSize;

    /// <summary>
    /// Tri-state checked indicator for the current filtered view:
    ///  <c>true</c> if all filtered items are checked, <c>false</c> if none,
    ///  <c>null</c> if some are checked (indeterminate). Returns <c>false</c>
    ///  for empty views.
    /// </summary>
    public bool? AreAllFilteredChecked
    {
        get
        {
            int count = Count;
            if (count == 0)
                return false;
            return _filteredCheckedCount == 0 ? false
                : _filteredCheckedCount == count ? true : null;
        }
    }

    #endregion


    // ======================================================================
    #region Events

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Fired when a filter computation completes or the filter is reset to null.
    ///  Not fired during intermediate computation steps.</summary>
    public event Action? FilterCompleted;

    /// <summary>Fired when an individual file's checked state changes via
    ///  <see cref="SetChecked(TapeFileInfo, bool)"/>. Not fired for bulk operations
    ///  (<see cref="SetChecked(IEnumerable{TapeFileInfo}, bool)"/>,
    ///  <see cref="SetFilteredChecked"/>, <see cref="SetAllChecked"/>,
    ///  <see cref="ClearChecked"/>). The parameter is the affected file.</summary>
    public event Action<TapeFileInfo>? CheckedChanged;

    #endregion


    // ======================================================================
    #region Private implementation

    /// <summary>Sums <see cref="TapeFileDescriptor.Length"/> across a file list.</summary>
    private static long ComputeTotalSize(IReadOnlyList<TapeFileInfo> items)
    {
        long total = 0;
        foreach (var item in items)
            total += item.FileDescr.Length;
        return total;
    }

    /// <summary>
    /// Clears filter results and fires notifications.
    ///  Called synchronously when <see cref="Filter"/> is set to <c>null</c>.
    /// </summary>
    private void ClearFilterResults()
    {
        bool wasFiltered = IsFiltered;
        int oldCount = Count;

        _filteredList = null;
        _filteredSet = null;
        _filteredTotalSize = 0;
        _isComputing = false;

        // With no filter, all checked items are "filtered-checked"
        RecomputeCheckedStats();

        if (oldCount != Count)
            PropertyChanged?.Invoke(this, s_countArgs);
        if (wasFiltered)
            PropertyChanged?.Invoke(this, s_isFilteredArgs);
        PropertyChanged?.Invoke(this, s_isComputingArgs);
        PropertyChanged?.Invoke(this, s_filteredTotalSizeArgs);
        NotifyCheckedStatsChanged();

        FilterCompleted?.Invoke();
    }

    /// <summary>
    /// Async filter computation. Runs matching on the thread pool,
    ///  then applies results on the calling context (UI thread via
    ///  <see cref="SynchronizationContext"/>).
    /// </summary>
    private async Task ComputeFilteredAsync(ITapeFileFilter filter, CancellationToken ct)
    {
        IsComputing = true;

        try
        {
            // Snapshot checked items for thread-safe access during computation
            var checkedSnapshot = new HashSet<TapeFileInfo>(_checkedItems);

            var result = await Task.Run(() =>
            {
                var list = new List<TapeFileInfo>();
                var set = new HashSet<TapeFileInfo>();
                long totalSize = 0;
                int checkedCount = 0;
                long checkedSize = 0;

                foreach (var item in _source)
                {
                    ct.ThrowIfCancellationRequested();

                    if (filter.Matches(item.FileDescr))
                    {
                        list.Add(item);
                        set.Add(item);
                        long size = item.FileDescr.Length;
                        totalSize += size;

                        if (checkedSnapshot.Contains(item))
                        {
                            checkedCount++;
                            checkedSize += size;
                        }
                    }
                }

                return (list, set, totalSize, checkedCount, checkedSize);
            }, ct);

            if (ct.IsCancellationRequested)
                return;

            // Apply results (back on UI thread via SynchronizationContext)
            _filteredList = result.list;
            _filteredSet = result.set;
            _filteredTotalSize = result.totalSize;
            _filteredCheckedCount = result.checkedCount;
            _filteredCheckedTotalSize = result.checkedSize;

            IsComputing = false;

            PropertyChanged?.Invoke(this, s_countArgs);
            PropertyChanged?.Invoke(this, s_isFilteredArgs);
            PropertyChanged?.Invoke(this, s_filteredTotalSizeArgs);
            NotifyCheckedStatsChanged();

            FilterCompleted?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Filter was replaced by a newer one — silently discard
        }
    }

    /// <summary>
    /// Recomputes all checked-related statistics from scratch.
    ///  Called after bulk operations and filter changes.
    /// </summary>
    private void RecomputeCheckedStats()
    {
        _checkedTotalSize = 0;
        _filteredCheckedCount = 0;
        _filteredCheckedTotalSize = 0;

        foreach (var item in _checkedItems)
        {
            long size = item.FileDescr.Length;
            _checkedTotalSize += size;

            // no filter → all checked items count as "filtered-checked"
            if (_filteredSet?.Contains(item) ?? true)
            {
                _filteredCheckedCount++;
                _filteredCheckedTotalSize += size;
            }
        }
    }

    /// <summary>
    /// Fires <see cref="PropertyChanged"/> for all checked-related statistics.
    /// </summary>
    private void NotifyCheckedStatsChanged()
    {
        PropertyChanged?.Invoke(this, s_checkedCountArgs);
        PropertyChanged?.Invoke(this, s_checkedTotalSizeArgs);
        PropertyChanged?.Invoke(this, s_filteredCheckedCountArgs);
        PropertyChanged?.Invoke(this, s_filteredCheckedTotalSizeArgs);
        PropertyChanged?.Invoke(this, s_areAllFilteredCheckedArgs);
    }

    #endregion
}
