using System.Diagnostics;

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
    IReadOnlyList<TapeFileInfo> sourceFiles)
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
    public List<FileListItem> BuildDisplayList(bool showFullPath)
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
public class TOCView
{
    private readonly TapeTOC _toc;
    private readonly BackupSetView?[] _setViews; // 1-based: [0] unused

    /// <summary>Creates a new <see cref="TOCView"/> for the given TOC.</summary>
    public TOCView(TapeTOC toc)
    {
        _toc = toc;
        _setViews = new BackupSetView?[toc.Count + 1];
    }

    /// <summary>The underlying TOC this view represents.</summary>
    public TapeTOC TOC => _toc;

    /// <summary>Number of backup sets (= <see cref="TapeTOC.Count"/>).</summary>
    public int Count => _toc.Count;

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

        // Reuse if the incremental-view flag still matches
        if (existing is not null && existing.IsIncrementalView == needsIncremental)
            return existing;

        // Resolve source files
        IReadOnlyList<TapeFileInfo> sourceFiles;
        if (needsIncremental)
        {
            _toc.CurrentSetIndex = setIndex;
            var allFiles = new List<TapeFileInfo>();
            var filesBySets = _toc.SelectFiles(incremental: true, filter: null);
            foreach (var setFiles in filesBySets)
            {
                if (setFiles != null)
                    allFiles.AddRange(setFiles);
                // null entries = "all files from that set" — replicate existing
                //  MainViewModel.LoadBackupSetInfo behavior for now
            }
            sourceFiles = allFiles;
        }
        else
        {
            sourceFiles = setTOC;
        }

        var view = new BackupSetView(setIndex, needsIncremental, sourceFiles);

        // Migrate checked state from the previous (stale) view
        if (existing is not null)
            view.MigrateCheckedState(existing.FilteredFiles);

        _setViews[setIndex] = view;
        return view;
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
}
