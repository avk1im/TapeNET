using System.IO;

using TapeLibNET;
using TapeWinNET.Utils;

namespace TapeWinNET.Models;

using TypeUID = ulong;

/// <summary>
/// Per-source-entry snapshot of the resolved disk files. Mirrors
/// <see cref="BackupSetView"/> for the Restore workflow: wraps the resolved
/// file list in a <see cref="FilteredFileList"/> for filtering and checked state,
/// and builds <see cref="FileListItem"/> binding proxies on demand.
/// <para>
/// Created by <see cref="BackupSourceView.ResolveAsync"/> when the user selects
/// a source in the Folders pane or when auto-scan triggers. Replaced when the
/// user re-scans (e.g. after toggling "Include subdirectories").
/// </para>
/// </summary>
public class BackupSourceSetView
{
    private Dictionary<TapeFileInfo, FileListItem>? _fileListItems;
    private bool _lastShowFullPath;
    private HashSet<TapeFileInfo>? _savedCheckedItems;

    /// <summary>
    /// Creates a new set view from the given resolved disk files.
    /// All files are initially checked (selected for backup).
    /// </summary>
    /// <param name="sourceFiles">Resolved disk files wrapped in
    ///  <see cref="BackupSourceFileInfo"/>.</param>
    public BackupSourceSetView(IReadOnlyList<BackupSourceFileInfo> sourceFiles)
    {
        SourceFiles = sourceFiles;
        FilteredFiles = new FilteredFileList(sourceFiles);

        // Default: all files checked for backup
        FilteredFiles.SetAllChecked(true);
    }

    /// <summary>The resolved source file list (disk files as <see cref="BackupSourceFileInfo"/>).</summary>
    public IReadOnlyList<BackupSourceFileInfo> SourceFiles { get; }

    /// <summary>Filtered view + checked state over <see cref="SourceFiles"/>.</summary>
    public FilteredFileList FilteredFiles { get; }

    /// <summary>
    /// Opaque delegate from <see cref="Controls.FileFilterPane"/> that restores the
    ///  pane's UI state and optionally re-applies the filter. Stored per source so
    ///  the filter definition survives source navigation.
    ///  <c>null</c> when no filter has been defined for this source.
    /// </summary>
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
    /// Migrates checked state from a previous set view (e.g. after re-scan).
    /// Matches files by full path. Only items present in the new
    /// <see cref="SourceFiles"/> are carried over.
    /// </summary>
    public void MigrateCheckedState(BackupSourceSetView previous)
    {
        if (previous.FilteredFiles.CheckedCount == 0)
            return;

        // Build a set of previously checked paths for O(1) lookup
        var checkedPaths = new HashSet<string>(
            previous.FilteredFiles.CheckedItems.Select(
                tfi => tfi.FileDescr.FullName),
            StringComparer.OrdinalIgnoreCase);

        // Check matching files in the new view
        var toCheck = SourceFiles
            .Where(tfi => checkedPaths.Contains(tfi.FileDescr.FullName))
            .ToList();

        if (toCheck.Count > 0)
        {
            // Clear default "all checked" and set only the matching ones
            FilteredFiles.ClearChecked();
            FilteredFiles.SetChecked(toCheck, true);
        }
    }

    // ─────────────────────────────────────────────────
    //  Partial selection save / restore
    //   Enables the source checkbox three-state click cycle:
    //   partial → none → all → partial (restored) → …
    // ─────────────────────────────────────────────────

    /// <summary>Whether a saved partial selection exists that can be restored.</summary>
    public bool HasSavedPartialSelection => _savedCheckedItems is not null;

    /// <summary>
    /// Saves the current per-file checked state if it is partial (some but
    ///  not all checked). Called before <c>SetAllChecked</c> overwrites it.
    /// </summary>
    public void SavePartialSelectionIfNeeded()
    {
        var ff = FilteredFiles;
        if (ff.CheckedCount > 0 && ff.CheckedCount < ff.SourceCount)
            _savedCheckedItems = new HashSet<TapeFileInfo>(ff.CheckedItems);
    }

    /// <summary>
    /// Restores the previously saved partial selection into the
    ///  <see cref="FilteredFileList"/> and clears the saved snapshot.
    /// </summary>
    public void RestorePartialSelection()
    {
        if (_savedCheckedItems is null)
            return;

        FilteredFiles.SetChecked(_savedCheckedItems, true, clearTheRest: true);
        _savedCheckedItems = null;
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
/// Session-level container for all <see cref="BackupSourceSetView"/> instances
/// belonging to a New Backup Set session. Mirrors <see cref="TOCView"/> for the
/// Restore workflow.
/// <para>
/// Manages <see cref="BackupSourceEntry"/> sources, assigns monotonic UIDs to
/// resolved disk files, and provides aggregate statistics for the Preview panel.
/// </para>
/// </summary>
public class BackupSourceView
{
    private readonly Dictionary<BackupSourceEntry, BackupSourceSetView> _setViews = [];
    private TypeUID _nextUID = 1UL; // 0 is reserved / invalid, same as TapeTOC

    // ─────────────────────────────────────────────────
    //  Session-level options
    // ─────────────────────────────────────────────────

    /// <summary>
    /// Whether file resolution should recurse into subdirectories.
    ///  Mirrors the "Include Subfolders" checkbox in NewBackupSetWindow.
    /// </summary>
    public bool IncludeSubdirectories { get; set; } = true;

    // ─────────────────────────────────────────────────
    //  UID generation
    // ─────────────────────────────────────────────────

    /// <summary>
    /// Generates a monotonic UID for a resolved disk file.
    /// Mirrors <c>TapeTOC.GenerateUID()</c>.
    /// </summary>
    internal TypeUID GenerateUID() => _nextUID++;

    // ─────────────────────────────────────────────────
    //  Set view management
    // ─────────────────────────────────────────────────

    /// <summary>Returns the cached set view for the given source, or <c>null</c>.</summary>
    public BackupSourceSetView? this[BackupSourceEntry entry] =>
        _setViews.GetValueOrDefault(entry);

    /// <summary>Whether a resolved set view exists for the given source.</summary>
    public bool HasView(BackupSourceEntry entry) => _setViews.ContainsKey(entry);

    /// <summary>
    /// Replaces (or creates) the set view for a source with newly resolved files.
    /// Migrates checked state from any previous view for the same source.
    /// </summary>
    public void SetView(BackupSourceEntry entry, BackupSourceSetView newView)
    {
        if (_setViews.TryGetValue(entry, out var previous))
            newView.MigrateCheckedState(previous);

        _setViews[entry] = newView;
    }

    /// <summary>Removes the set view for a source (e.g. when the source is removed).</summary>
    public void RemoveView(BackupSourceEntry entry) => _setViews.Remove(entry);

    /// <summary>Clears all set views (e.g. on full reset).</summary>
    public void Clear()
    {
        _setViews.Clear();
        _nextUID = 1UL;
    }

    // ─────────────────────────────────────────────────
    //  File resolution
    // ─────────────────────────────────────────────────

    /// <summary>
    /// Synchronously resolves a single-file source entry. For
    /// <see cref="BackupSourceType.SingleFile"/> sources, no disk enumeration
    /// is needed — we just wrap the <see cref="FileInfo"/> directly.
    /// </summary>
    /// <returns>The new set view, or <c>null</c> if the file doesn't exist.</returns>
    public BackupSourceSetView? ResolveSingleFile(BackupSourceEntry entry)
    {
        var fi = new FileInfo(entry.Pattern);
        if (!fi.Exists)
            return null;

        var tfi = new BackupSourceFileInfo(GenerateUID(), fi);
        var view = new BackupSourceSetView([tfi]);
        SetView(entry, view);
        return view;
    }

    /// <summary>
    /// Resolves disk files for a source entry on a worker thread. Builds
    /// <see cref="BackupSourceFileInfo"/> wrappers and creates a new
    /// <see cref="BackupSourceSetView"/>. The returned view has all files
    /// checked by default; if a previous view existed for this source,
    /// checked state is migrated via <see cref="SetView"/>.
    /// <para>
    /// On cancellation the partial result is discarded — resolution always
    ///  restarts from scratch. The previous view's checked state is preserved
    ///  and migrated automatically on the next successful resolve.
    /// </para>
    /// </summary>
    /// <param name="entry">The source entry to resolve.</param>
    /// <param name="ct">Cancellation token for aborting a long scan
    ///  (e.g. selection change or user stop).</param>
    /// <param name="progress">Optional callback invoked periodically with the
    ///  number of files found so far (for UI feedback).</param>
    /// <returns>The new set view, or <c>null</c> if cancelled.</returns>
    public async Task<BackupSourceSetView?> ResolveAsync(
        BackupSourceEntry entry,
        CancellationToken ct,
        Action<int>? progress = null)
    {
        var files = await Task.Run(() =>
            EnumerateFiles(entry, progress, ct), ct);

        if (ct.IsCancellationRequested)
            return null;

        var view = new BackupSourceSetView(files);
        SetView(entry, view);
        return view;
    }

    /// <summary>
    /// Enumerates disk files for a source entry, wrapping each in
    /// <see cref="BackupSourceFileInfo"/>. Runs on the thread pool.
    ///  Uses <see cref="IncludeSubdirectories"/> for recursion depth.
    /// </summary>
    private List<BackupSourceFileInfo> EnumerateFiles(
        BackupSourceEntry entry,
        Action<int>? progress,
        CancellationToken ct)
    {
        var result = new List<BackupSourceFileInfo>();
        int count = 0;

        void AddFile(FileInfo fi)
        {
            ct.ThrowIfCancellationRequested();
            var tfi = new BackupSourceFileInfo(GenerateUID(), fi);
            result.Add(tfi);

            // Report progress periodically (every 100 files)
            if (progress is not null && ++count % 100 == 0)
                progress(count);
        }

        try
        {
            if (TapeFileBackupAgent.HasWildcards(entry.Pattern))
            {
                // Wildcard pattern: enumerate matching files
                var dirName = Path.GetDirectoryName(entry.Pattern);
                var filePattern = Path.GetFileName(entry.Pattern);

                var dirPath = Directory.Exists(dirName) ? Path.GetFullPath(dirName)
                    : string.IsNullOrEmpty(dirName) ? Directory.GetCurrentDirectory()
                    : null;

                if (!string.IsNullOrEmpty(dirPath))
                {
                    var searchOption = IncludeSubdirectories
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly;

                    foreach (var fi in new DirectoryInfo(dirPath)
                        .EnumerateFiles(filePattern, searchOption))
                    {
                        AddFile(fi);
                    }
                }
            }
            else if (TapeFileBackupAgent.IsDirectory(entry.Pattern))
            {
                // Directory: enumerate all files
                var dirPath = Path.GetFullPath(entry.Pattern);
                if (Directory.Exists(dirPath))
                {
                    var searchOption = IncludeSubdirectories
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly;

                    foreach (var fi in new DirectoryInfo(dirPath)
                        .EnumerateFiles("*", searchOption))
                    {
                        AddFile(fi);
                    }
                }
            }
            else
            {
                // Single file
                var fi = new FileInfo(entry.Pattern);
                if (fi.Exists)
                    AddFile(fi);
            }
        }
        catch (OperationCanceledException)
        {
            // Scan was cancelled — return what we have so far
        }
        catch
        {
            // Ignore enumeration errors (access denied, etc.)
        }

        // Final progress report
        progress?.Invoke(result.Count);

        return result;
    }

    // ─────────────────────────────────────────────────
    //  Aggregate statistics
    // ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the total number of checked files across all resolved sources.
    /// Sources that have not been resolved yet are skipped.
    /// </summary>
    public int GetTotalCheckedCount()
    {
        int total = 0;
        foreach (var view in _setViews.Values)
            total += view.FilteredFiles.CheckedCount;
        return total;
    }

    /// <summary>
    /// Returns the total size of checked files across all resolved sources.
    /// </summary>
    public long GetTotalCheckedSize()
    {
        long total = 0;
        foreach (var view in _setViews.Values)
            total += view.FilteredFiles.CheckedTotalSize;
        return total;
    }

    /// <summary>
    /// Returns the total number of resolved files across all sources.
    /// </summary>
    public int GetTotalFileCount()
    {
        int total = 0;
        foreach (var view in _setViews.Values)
            total += view.FilteredFiles.SourceCount;
        return total;
    }

    /// <summary>
    /// Returns the total size of all resolved files across all sources.
    /// </summary>
    public long GetTotalFileSize()
    {
        long total = 0;
        foreach (var view in _setViews.Values)
            total += view.FilteredFiles.SourceTotalSize;
        return total;
    }

    /// <summary>
    /// Collects all checked files across all resolved sources, preserving
    /// per-source order. Suitable for passing to the backup agent.
    /// </summary>
    /// <param name="sourceEntries">The ordered list of source entries. Only
    ///  entries with resolved views and checked files are included.</param>
    /// <returns>Flat list of checked files for backup.</returns>
    public List<TapeFileInfo> CollectCheckedFiles(
        IEnumerable<BackupSourceEntry> sourceEntries)
    {
        var result = new List<TapeFileInfo>();
        foreach (var entry in sourceEntries)
        {
            if (_setViews.TryGetValue(entry, out var view)
                && view.FilteredFiles.CheckedCount > 0)
            {
                result.AddRange(view.FilteredFiles.CheckedItems);
            }
        }
        return result;
    }

    /// <summary>
    /// Updates a <see cref="BackupSourceListItem"/> with the current checked
    ///  and file-count statistics from its corresponding set view.
    /// </summary>
    public void SyncListItem(BackupSourceListItem listItem)
    {
        if (_setViews.TryGetValue(listItem.Entry, out var view))
        {
            var ff = view.FilteredFiles;
            listItem.IsScanned = true;
            listItem.FileCount = ff.SourceCount;
            listItem.SelectedFileCount = ff.CheckedCount;
            listItem.SelectedSize = ff.CheckedTotalSize;
            listItem.IsCheckedForBackup = ff.CheckedCount == 0 ? false
                : ff.CheckedCount == ff.SourceCount ? true
                : null; // partial

            // Enable three-state clicking when a partial selection exists now
            //  or was saved before the user clicked all/none
            listItem.CanBeThreeState = listItem.HasPartialSelection
                || view.HasSavedPartialSelection;
        }
        else
        {
            listItem.FileCount = 0;
            listItem.SelectedFileCount = 0;
            listItem.SelectedSize = 0;
            listItem.IsCheckedForBackup = true; // default for unresolved
            listItem.CanBeThreeState = false;
        }
    }
}
