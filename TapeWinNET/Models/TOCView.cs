using System.Diagnostics;

using FclNET;
using TapeLibNET;
using TapeWinNET.Utils;

namespace TapeWinNET.Models;

/// <summary>
/// Per-backup-set snapshot view of the file content. Encapsulates the resolved
/// source file list (flat or incremental), the <see cref="FilteredFileList"/> for
/// filtering and checked state, and the <see cref="FileListItem"/> binding proxies.
/// <para>
/// Created lazily by <see cref="TOCView.GetOrCreate"/> on first navigation to a
/// set, then cached for reuse across tree navigation. Invalidated when
/// <see cref="IsIncrementalView"/> no longer matches the current
/// <c>ShowIncrementalSets</c> setting.
/// </para>
/// </summary>
public class BackupSetView(int setIndex, bool isIncrementalView,
    IReadOnlyList<TapeFileInfo> sourceFiles, TapeSetTOC setTOC)
{
    private Dictionary<TapeFileInfo, FileListItem>? _fileListItems;
    private bool _lastShowFullPath;

    /// <summary>1-based set index within the TOC.</summary>
    public int SetIndex { get; } = setIndex;

    /// <summary>Whether this view was built with <c>ShowIncrementalSets</c> enabled
    ///  for an incremental set (affects source file resolution).</summary>
    public bool IsIncrementalView { get; } = isIncrementalView;

    /// <summary>The resolved source file list — flat (<see cref="TapeSetTOC"/>)
    ///  or merged incremental chain.</summary>
    public IReadOnlyList<TapeFileInfo> SourceFiles { get; } = sourceFiles;

    /// <summary>The <see cref="TapeSetTOC"/> this view was built from.
    ///  Used to detect staleness after TOC modification (e.g. after a backup).
    ///  For flat views this equals <see cref="SourceFiles"/>; for incremental
    ///  views the source files are synthesized but this still tracks the
    ///  original set reference.</summary>
    public TapeSetTOC SetTOC { get; } = setTOC;

    /// <summary>Filtered view + checked state over <see cref="SourceFiles"/>.</summary>
    public FilteredFileList FilteredFiles { get; } = new FilteredFileList(sourceFiles);

    /// <summary>Opaque delegate from <c>FileFilterPane</c> that restores the pane's
    ///  UI state and optionally re-applies the filter. Stored per set so the filter
    ///  definition survives tree navigation, even when the filter is disabled.
    ///  <c>null</c> when no filter has been defined.</summary>
    public Func<Task>? SavedFilterState { get; set; }

    /// <summary>
    /// Builds the display list from the current filtered view, creating or
    /// refreshing <see cref="FileListItem"/> proxies as needed.
    /// </summary>
    /// <param name="showFullPath">Whether file names show the full path.</param>
    /// <returns>A new list of <see cref="FileListItem"/> for binding.</returns>
    public List<FileListItem> BuildFileItemList(bool showFullPath)
    {
        EnsureFileListItems(showFullPath);

        var list = new List<FileListItem>(FilteredFiles.Count);
        foreach (var fi in FilteredFiles)
        {
            if (_fileListItems!.TryGetValue(fi, out var item))
                list.Add(item);
        }
        return list;
    }

    /// <summary>
    /// Migrates checked state from a previous view (e.g. after
    /// <c>ShowIncrementalSets</c> toggle). Only items present in the
    /// new <see cref="SourceFiles"/> are carried over.
    /// </summary>
    public void MigrateCheckedState(FilteredFileList previous)
    {
        if (previous.CheckedCount > 0)
            FilteredFiles.SetChecked(previous.CheckedItems, true);
    }

    /// <summary>
    /// Creates or recreates the <see cref="FileListItem"/> dictionary when
    /// <paramref name="showFullPath"/> differs from the last build.
    /// </summary>
    private void EnsureFileListItems(bool showFullPath)
    {
        if (_fileListItems is not null && _lastShowFullPath == showFullPath)
            return;

        _lastShowFullPath = showFullPath;
        _fileListItems = new Dictionary<TapeFileInfo, FileListItem>(SourceFiles.Count);
        foreach (var fi in SourceFiles)
            _fileListItems[fi] = new FileListItem(FilteredFiles, fi, showFullPath);
    }
}


/// <summary>
/// Session-level container for all <see cref="BackupSetView"/> instances belonging
/// to a single <see cref="TapeTOC"/>. Views are created lazily on first access and
/// cached in an array indexed by the 1-based set index.
/// <para>
/// Replaces the pattern of storing <c>FilteredFileList</c> and filter state on
/// individual <c>TapeTreeItemViewModel</c> nodes, decoupling the data model from
/// the tree UI structure.
/// </para>
/// </summary>
/// <summary>
/// Captures the filter to apply to all backup sets at once.
/// <see cref="Evaluator"/> is null when disabling a global filter.
/// <see cref="RestoreAction"/> is the pane-state restore delegate for the current set.
/// </summary>
public record PendingGlobalFilterRecord(FclEvaluator? Evaluator, Func<Task>? RestoreAction);

/// <remarks>Creates a new <see cref="TOCView"/> for the given TOC.</remarks>
public class TOCView(TapeTOC toc)
{
    private readonly TapeTOC _toc = toc;
    private BackupSetView?[] _setViews = new BackupSetView?[toc.Count + 1]; // 1-based: [0] unused

    /// <summary>The underlying TOC this view represents.</summary>
    public TapeTOC TOC => _toc;

    /// <summary>Number of backup sets (= <see cref="TapeTOC.Count"/>).</summary>
    public int Count => _toc.Count;

    /// <summary>
    /// Pending global filter to apply to newly created <see cref="BackupSetView"/>
    ///  instances. Set when the user applies a filter "to all sets"; consumed (applied)
    ///  inside <see cref="GetOrCreate"/> for each newly created view.
    ///  Cleared by <see cref="ClearPendingGlobalFilter"/> when the global filter is
    ///  explicitly disabled or superseded.
    /// </summary>
    public PendingGlobalFilterRecord? PendingGlobalFilter { get; private set; }

    /// <summary>Clears the pending global filter record.</summary>
    public void ClearPendingGlobalFilter() => PendingGlobalFilter = null;

    /// <summary>
    /// Sets the pending global filter to be applied to all future (lazily created) views.
    /// </summary>
    public void SetPendingGlobalFilter(FclEvaluator? evaluator, Func<Task>? restoreAction)
        => PendingGlobalFilter = new PendingGlobalFilterRecord(evaluator, restoreAction);

    /// <summary>
    /// Enumerates every <see cref="BackupSetView"/> that has already been created
    ///  (i.e. the user has visited or <see cref="BuildBackupSetItemList"/> has
    ///  materialised them). Used to apply global filter changes eagerly.
    /// </summary>
    public IEnumerable<BackupSetView> ExistingViews
    {
        get
        {
            for (int i = 1; i < _setViews.Length; i++)
            {
                if (_setViews[i] is { } view)
                    yield return view;
            }
        }
    }

    /// <summary>Returns the cached view for the given set, or <c>null</c> if not yet created.</summary>
    public BackupSetView? this[int setIndex] =>
        setIndex >= 1 && setIndex <= _toc.Count ? _setViews[setIndex] : null;

    /// <summary>
    /// Returns an existing <see cref="BackupSetView"/> if it is still valid for the
    /// current <paramref name="showIncrementalSets"/> setting, or creates a new one.
    /// When a stale view is replaced, checked state is migrated.
    /// </summary>
    public BackupSetView GetOrCreate(int setIndex, bool showIncrementalSets)
    {
        Debug.Assert(setIndex >= 1 && setIndex <= _toc.Count,
            $"setIndex {setIndex} out of range [1..{_toc.Count}]");

        var setTOC = _toc[setIndex];
        bool needsIncremental = showIncrementalSets && setTOC.Incremental;
        var existing = _setViews[setIndex];

        // Reuse if the incremental-view flag still matches AND the underlying TapeSetTOC
        //  is still the same object (guards against stale views after TOC modification)
        if (existing is not null && existing.IsIncrementalView == needsIncremental
            && ReferenceEquals(existing.SetTOC, setTOC))
            return existing;

        // Resolve source files
        IReadOnlyList<TapeFileInfo> sourceFiles;
        if (needsIncremental)
        {
            _toc.CurrentSetIndex = setIndex;
            var filesBySets = _toc.SelectFiles(incremental: true, filter: null);
            sourceFiles = _toc.SelectedFilesToList(filesBySets);
        }
        else
        {
            // notice for non-incremental we intentionally do not want multi-volume resolution,
            //  therefore we don't call _toc.SelectFiles(incremental: false, filter: null)
            sourceFiles = setTOC;
        }

        var view = new BackupSetView(setIndex, needsIncremental, sourceFiles, setTOC);

        // Migrate checked state from the previous (stale) view
        if (existing is not null)
            view.MigrateCheckedState(existing.FilteredFiles);

        // Apply the pending global filter (if any) to this freshly created view.
        //  This covers sets never visited before the user pressed "Apply to all".
        if (PendingGlobalFilter is { } pgf)
        {
            view.FilteredFiles.Filter = pgf.Evaluator is not null
                ? new FclTapeFileFilter(pgf.Evaluator)
                : null;
            // Store a restore delegate so the set behaves like a directly filtered one
            view.SavedFilterState = pgf.RestoreAction;
        }

        _setViews[setIndex] = view;
        return view;
    }

    /// <summary>
    /// Refreshes the view array after the TOC has been modified (e.g. after a backup).
    /// Invalidates stale <see cref="BackupSetView"/> instances whose underlying
    ///  <see cref="TapeSetTOC"/> has been replaced, so that subsequent
    ///  <see cref="GetOrCreate"/> calls will rebuild them with the new source files.
    /// Unchanged views are preserved to carry over checked state.
    /// We don't checjk for incremental status, since GetOrCreate() handles the check.
    /// </summary>
    /// <param name="showIncrementalSets">Current incremental display setting.</param>
    /// <param name="refreshSets">Force re-creation of all existing views (e.g. when
    ///  incremental display setting changed).</param>
    public void Refresh(bool refreshSets = false)
    {
        // TOC size might've changed -> resize _setViews if needed
        if (_setViews.Length != _toc.Count + 1)
        {
            Array.Resize(ref _setViews, _toc.Count + 1);
        }

        // Invalidate stale views: a view is stale when its SourceFiles no longer
        //  references the current TapeSetTOC (backup may have replaced or emptied sets).
        //  For incremental views the chain may have changed, so always invalidate those.
        for (int i = 1; i <= _toc.Count; i++)
        {
            if (_setViews[i] is not { } view)
                continue;

            // A view is stale when its underlying TapeSetTOC was replaced
            //  (e.g. backup overwrote the set). Incremental-mode mismatches are
            //  handled by GetOrCreate with proper checked-state migration.
            bool stale = !ReferenceEquals(view.SetTOC, _toc[i]);

            if (refreshSets || stale)
            {
                // Clear the cached view; GetOrCreate will rebuild it on next access.
                //  Don't migrate checked state here — the old files may no longer exist
                //  in the new set, so carrying over checks would produce phantom counts.
                _setViews[i] = null;
            }
        }
    }

    /// <summary>
    /// Collects all checked files across every populated <see cref="BackupSetView"/>.
    /// Views that have never been accessed are skipped.
    /// </summary>
    public IReadOnlyList<TapeFileInfo> GetAllCheckedFiles()
    {
        var result = new List<TapeFileInfo>();
        for (int i = 1; i <= _toc.Count; i++)
        {
            if (_setViews[i] is { } view && view.FilteredFiles.CheckedCount > 0)
                result.AddRange(view.FilteredFiles.CheckedItems);
        }
        return result;
    }

    /// <summary>
    /// Returns the total number of checked files across all populated set views,
    ///  without materializing the file lists.
    /// </summary>
    public int GetTotalCheckedCount()
    {
        int total = 0;
        for (int i = 1; i <= _toc.Count; i++)
        {
            if (_setViews[i] is { } view)
                total += view.FilteredFiles.CheckedCount;
        }
        return total;
    }

    /// <summary>
    /// Builds a dictionary of checked files per backup set, suitable for
    ///  <see cref="TapeTOC.SelectFilesFromSets"/>.
    /// <para>
    /// A <c>null</c> value means "all files in set" (every file is checked).
    /// A non-null list contains only the checked files from that set.
    /// Sets with no checked files (or never accessed) are absent from the dictionary.
    /// </para>
    /// </summary>
    public Dictionary<int, IReadOnlyList<TapeFileInfo>?> GetCheckedFilesBySet()
    {
        var result = new Dictionary<int, IReadOnlyList<TapeFileInfo>?>();
        // remember to parse sets in reverse from newest to oldest
        for (int i = _toc.Count; i > 0; i--)
        {
            if (_setViews[i] is not { } view)
                continue; // skip sets that have never been accessed

            var ffiles = view.FilteredFiles;
            if (ffiles.CheckedCount == 0)
                continue;

            result[i] = (ffiles.CheckedCount == ffiles.SourceCount)
                ? null // null means all files in set — no need to materialize the list
                : [.. ffiles.CheckedItems];
        }
        return result;
    }

    /// <summary>
    /// Applies a checked/unchecked state to files identified by the per-set dictionary.
    /// A <c>null</c> value means all files in that set; a non-null list targets only
    /// those specific files. Uses the batch <see cref="FilteredFileList.SetChecked"/>
    /// overload for efficiency. Skips sets whose views have not been created yet.
    /// </summary>
    public void SetCheckedFilesBySet(Dictionary<int, IReadOnlyList<TapeFileInfo>?> checkedFilesBySet, bool isChecked)
    {
        // Iterate over populated set views and look up in the dictionary —
        //  array-indexed access is O(1) and avoids redundant dictionary enumeration
        //  when only a subset of views exist.
        for (int i = 1; i < _setViews.Length; i++)
        {
            if (_setViews[i] is not { } view)
                continue;

            if (!checkedFilesBySet.TryGetValue(i, out var chfiles))
                continue;

            var ffiles = view.FilteredFiles;
            if (chfiles == null)
            {
                // null means all files in set
                ffiles.SetAllChecked(isChecked);
            }
            else
            {
                ffiles.SetChecked(chfiles, isChecked);
            }
        }
    }

    /* // implemented by below overload with checkedOnly flag
    /// <summary>
    /// Builds the backup set display list with checked-state sync from existing
    ///  <see cref="BackupSetView"/> instances. Sets are ordered newest-first
    ///  (alternative index 0 down to <see cref="TapeTOC.MinSetIndex"/>).
    /// <para>
    /// Mirrors <see cref="BackupSetView.BuildFileItemList"/> at the TOC level.
    /// </para>
    /// </summary>
    public List<BackupSetListItem> BuildBackupSetItemList()
    {
        var list = new List<BackupSetListItem>(_toc.Count);

        for (int alt = 0; alt >= _toc.MinSetIndex; alt--)
        {
            int setIndex = _toc.SetIndexToAlt(alt);
            var setTOC = _toc[setIndex];
            var item = new BackupSetListItem(setTOC, setIndex, alt, setTOC.Volume == _toc.Volume);

            // Sync checked and filtered state from existing BackupSetView (user may have
            //  checked/filtered files before navigating back to the media view)
            if (_setViews[setIndex] is { } existingView)
            {
                var ff = existingView.FilteredFiles;
                int checkedCount = ff.CheckedCount;
                int sourceCount = ff.SourceCount;
                item.CheckedFileCount = checkedCount > 0 ? checkedCount : null;
                item.FilteredFileCount = ff.IsFiltered ? ff.Count : null;
                item.IsCheckedForRestore = checkedCount == 0 ? false
                    : checkedCount == sourceCount ? true
                    : null; // partial
            }

            list.Add(item);
        }

        return list;
    }
    */

    /// <summary>
    /// Builds the backup set display list with checked-state sync from existing
    ///  <see cref="BackupSetView"/> instances. Sets are ordered newest-first
    ///  (alternative index 0 down to <see cref="TapeTOC.MinSetIndex"/>).
    /// <para>
    /// Mirrors <see cref="BackupSetView.BuildFileItemList"/> at the TOC level.
    /// </para>
    /// </summary>
    /// <param name="checkedOnly">Whether to include only sets with at least one checked file.</param>
    /// <param name="showIncrementalSets">Current incremental display setting. Required when
    ///  <see cref="PendingGlobalFilter"/> is set, so that unvisited sets can be materialised
    ///  with the correct file resolution before their filter counts are read.</param>
    public List<BackupSetListItem> BuildBackupSetItemList(bool checkedOnly = false, bool showIncrementalSets = false)
    {
        var list = new List<BackupSetListItem>(_toc.Count);

        for (int alt = 0; alt >= _toc.MinSetIndex; alt--)
        {
            int setIndex = _toc.SetIndexToAlt(alt);
            var setTOC = _toc[setIndex];

            // When a global filter is pending, force-create any unvisited set view now
            //  so the filter is applied and counts are correct for the media snapshot.
            if (PendingGlobalFilter is not null && _setViews[setIndex] is null)
                GetOrCreate(setIndex, showIncrementalSets);

            // Sync checked and filtered state from existing BackupSetView (user may have
            //  checked/filtered files before navigating back to the media view)
            if (_setViews[setIndex] is { } view)
            {
                var ffiles = view.FilteredFiles;
                int checkedCount = ffiles.CheckedCount;
                if (checkedOnly && checkedCount == 0)
                    continue; // skip sets with no checked files when building a checked-only list

                var item = new BackupSetListItem(setTOC, setIndex, alt, setTOC.Volume == _toc.Volume);
                int sourceCount = ffiles.SourceCount;
                item.CheckedFileCount = checkedCount > 0 ? checkedCount : null;
                item.FilteredFileCount = ffiles.IsFiltered ? ffiles.Count : null;
                item.IsCheckedForRestore = checkedCount == 0 ? false
                    : checkedCount == sourceCount ? true
                    : null; // partial
                list.Add(item);
            }
            else if (!checkedOnly)
            {
                // Create a default item without checked nor filtered files 
                var item = new BackupSetListItem(setTOC, setIndex, alt, setTOC.Volume == _toc.Volume);
                list.Add(item);
            }
        }

        return list;
    }

    /// <summary>
    /// Maps from backup set indexes to the lists of checked files for each set.
    /// <para>
    /// A <c>null</c> value means "all files in set" (every file is checked).
    /// A non-null list contains only the checked files from that set.
    /// Sets with no checked files (or never accessed) are absent from the dictionary.
    /// </para>
    /// </summary>
    /// <remarks>The backup set items must've been generated for this TOCView</remarks>
    /// <param name="backupSetItems">A list of backup set items to process</param>
    /// <returns>A dictionary mapping each backup set index to a list of checked files in that set</returns>
    public Dictionary<int, IReadOnlyList<TapeFileInfo>?> FromCheckedBackupSetsToFilesBySet(
        IReadOnlyList<BackupSetListItem> backupSetItems)
    {
        var result = new Dictionary<int, IReadOnlyList<TapeFileInfo>?>();

        foreach (var item in backupSetItems)
        {
            if (item.CheckedFileCount == 0)
                continue; // skip sets with no checked files

            if (item.IsCheckedForRestore == true)
            {
                // All files in set are checked -> can skip materializing the list
                result[item.SetIndex] = null; // null means all files in set
                continue;
            }

            int setIndex = item.SetIndex;
            if (_setViews[setIndex] is not { } view)
                continue; // skip sets that have never been accessed
            
            var ffiles = view.FilteredFiles;
            result[setIndex] = (ffiles.CheckedCount == ffiles.SourceCount)
                ? null // null means all files in set — no need to materialize the list
                : [.. ffiles.CheckedItems];
        }
        return result;
    }
}
