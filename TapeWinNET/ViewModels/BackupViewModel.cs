using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeWinNET.Converters;
using TapeWinNET.Models;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Request data gathered by the BackupWindow for executing a backup operation.
/// Mirrors <see cref="RestoreRequest"/> for the Restore workflow.
/// </summary>
/// <param name="Description">User description for the new backup set.</param>
/// <param name="CheckedFiles">Flat list of checked files for backup, collected
///  from all sources via <see cref="BackupSourceView.CollectCheckedFiles"/>.</param>
/// <param name="Incremental">Whether this is an incremental backup.</param>
/// <param name="BlockSize">Block size in bytes.</param>
/// <param name="HashAlgorithm">Hash algorithm for verification.</param>
/// <param name="AppendMode">True to append after existing set, false to overwrite all.</param>
/// <param name="AppendAfterSetIndex">Set index to append after (-1 for overwrite).</param>
/// <param name="UseFilemarks">Whether to use filemarks between files.</param>
/// <param name="IncludeSubdirectories">Whether sources were scanned with subdirectory recursion.</param>
public record BackupRequest(
    string Description,
    List<TapeFileInfo> CheckedFiles,
    bool Incremental,
    uint BlockSize,
    TapeHashAlgorithm HashAlgorithm,
    bool AppendMode,
    int AppendAfterSetIndex,
    bool UseFilemarks,
    bool IncludeSubdirectories);

/// <summary>
/// ViewModel for the BackupWindow (Option B: Source-Drill-Down).
/// <para>
/// Upper "Folders" pane shows source entries as <see cref="BackupSourceListItem"/> rows
/// with tri-state checkboxes. Lower "Files" pane shows the resolved files for the
/// currently selected source, with <see cref="Controls.FileFilterPane"/> for filtering.
/// Right panel contains backup options and preview.
/// </para>
/// </summary>
public class BackupViewModel : ViewModelBase
{
    private readonly TapeService _tapeService;
    private readonly Action<BackupRequest> _onStartBackup;
    private readonly Action _onCancel;

    // ─────────────────────────────────────────────────
    //  Source management
    // ─────────────────────────────────────────────────

    private readonly BackupSourceView _sourceView = new();
    private BackupSourceListItem? _selectedSource;
    private CancellationTokenSource? _scanCts;

    // ─────────────────────────────────────────────────
    //  Files pane state
    // ─────────────────────────────────────────────────

    private List<FileListItem>? _currentFileItems;
    private bool _showFullPath;

    // ─────────────────────────────────────────────────
    //  Pattern input
    // ─────────────────────────────────────────────────

    private string _patternInput = string.Empty;
    private string? _prevAutoAdded;

    // ─────────────────────────────────────────────────
    //  Backup options
    // ─────────────────────────────────────────────────

    private string _description = string.Empty;
    private bool _incrementalBackup;
    private bool _useFilemarks;
    private int _selectedBlockSizeIndex = 4;   // Default 16 KB
    private int _selectedHashIndex = 1;        // Default CRC32
    private bool _appendToSet = true;
    private AppendAfterOption? _selectedAppendOption;

    // ─────────────────────────────────────────────────
    //  Scan / busy state
    // ─────────────────────────────────────────────────

    private bool _isScanning;
    private bool _autoScan = true;

    // ─────────────────────────────────────────────────
    //  Preview
    // ─────────────────────────────────────────────────

    private string _previewTotalFiles = "\u2014";
    private string _previewTotalSize = "\u2014";
    private string _previewCheckedFiles = "\u2014";
    private string _previewCheckedSize = "\u2014";

    // ═════════════════════════════════════════════════
    //  Constructor
    // ═════════════════════════════════════════════════

    public BackupViewModel(
        TapeService tapeService,
        Action<BackupRequest> onStartBackup,
        Action onCancel)
    {
        _tapeService = tapeService;
        _onStartBackup = onStartBackup;
        _onCancel = onCancel;

        // Default description
        Description = $"Backup set created {DateTime.Now:g}";

        // Populate append options from current TOC
        PopulateAppendOptions();

        // Commands — source management
        AddFilesCommand = new RelayCommand(AddFiles, () => !IsScanning);
        AddFolderCommand = new RelayCommand(AddFolder, () => !IsScanning);
        AddPatternsCommand = new RelayCommand(AddPatterns,
            () => !IsScanning && !string.IsNullOrWhiteSpace(PatternInput));
        RemoveSelectedCommand = new RelayCommand(
            _ => RemoveSelectedSources(),
            _ => !IsScanning && SourceItems.Any(s => s.IsCheckedForBackup == false));
        ScanAllCommand = new AsyncRelayCommand(ScanAllSourcesAsync,
            () => !IsScanning && SourceItems.Count > 0);

        // Commands — action buttons
        StartBackupCommand = new RelayCommand(ExecuteStart, _ => CanStart);
        CancelCommand = new RelayCommand(_ => _onCancel());
    }

    // ═════════════════════════════════════════════════
    //  Public: data model access
    // ═════════════════════════════════════════════════

    /// <summary>The session-level source view (file resolution + checked state).</summary>
    public BackupSourceView SourceView => _sourceView;

    // ═════════════════════════════════════════════════
    //  Collections
    // ═════════════════════════════════════════════════

    /// <summary>Source entries displayed in the Folders pane.</summary>
    public ObservableCollection<BackupSourceListItem> SourceItems { get; } = [];

    /// <summary>Append-after options for the combo box.</summary>
    public ObservableCollection<AppendAfterOption> AppendOptions { get; } = [];

    // ═════════════════════════════════════════════════
    //  Source selection
    // ═════════════════════════════════════════════════

    /// <summary>
    /// Currently selected source in the Folders pane. Changing triggers file
    /// resolution (if not yet resolved) and updates the Files pane.
    /// </summary>
    public BackupSourceListItem? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
            {
                OnPropertyChanged(nameof(HasSelectedSource));
                OnPropertyChanged(nameof(AreAllFilesChecked));

                // Auto-populate pattern input from selection
                if (value is not null)
                    AutoAddPatternFromSelection(value.Entry);

                // File display is updated from code-behind after resolve
            }
        }
    }

    /// <summary>Whether a source is selected in the Folders pane.</summary>
    public bool HasSelectedSource => _selectedSource is not null;

    // ═════════════════════════════════════════════════
    //  Files pane
    // ═════════════════════════════════════════════════

    /// <summary>
    /// File items for the currently selected source. Bound by the Files pane ListView.
    /// <c>null</c> when no source is selected or not yet resolved.
    /// </summary>
    public List<FileListItem>? CurrentFileItems
    {
        get => _currentFileItems;
        set => SetProperty(ref _currentFileItems, value);
    }

    /// <summary>Whether file names in the Files pane show the full path.</summary>
    public bool ShowFullPath
    {
        get => _showFullPath;
        set
        {
            if (SetProperty(ref _showFullPath, value))
                RefreshFileDisplay();
        }
    }

    // ═════════════════════════════════════════════════
    //  Files header checkbox (tri-state)
    // ═════════════════════════════════════════════════

    /// <summary>
    /// Tri-state header checkbox for the Files pane:
    /// <c>true</c> = all filtered checked, <c>false</c> = none,
    /// <c>null</c> = mixed. Setter toggles between all/none.
    /// </summary>
    public bool? AreAllFilesChecked
    {
        get
        {
            if (_selectedSource is null) return false;
            var setView = _sourceView[_selectedSource.Entry];
            return setView?.FilteredFiles.AreAllFilteredChecked ?? false;
        }
        set
        {
            if (_selectedSource is null) return;
            var setView = _sourceView[_selectedSource.Entry];
            if (setView is null) return;

            // Three-state cycle: false→true→null→false.
            //  true  = user clicked from unchecked       → check all
            //  null  = user clicked from fully checked   → uncheck all
            //  false = user clicked from indeterminate   → check all
            setView.FilteredFiles.SetFilteredChecked(value != null);

            // Notify all visible FileListItem rows
            if (_currentFileItems is not null)
            {
                foreach (var item in _currentFileItems)
                    item.NotifyIsCheckedChanged();
            }

            OnPropertyChanged(nameof(AreAllFilesChecked));

            // Update source-level stats
            _sourceView.SyncListItem(_selectedSource);
            UpdatePreview();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ═════════════════════════════════════════════════
    //  Pattern input
    // ═════════════════════════════════════════════════

    public string PatternInput
    {
        get => _patternInput;
        set
        {
            if (SetProperty(ref _patternInput, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    // ═════════════════════════════════════════════════
    //  Backup options
    // ═════════════════════════════════════════════════

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public bool IncludeSubdirectories
    {
        get => _sourceView.IncludeSubdirectories;
        set
        {
            if (_sourceView.IncludeSubdirectories != value)
            {
                _sourceView.IncludeSubdirectories = value;
                OnPropertyChanged();
                // Re-scan all when toggled (if auto-scan is on)
                if (AutoScan && SourceItems.Count > 0)
                    _ = ScanAllSourcesAsync();
            }
        }
    }

    public bool IncrementalBackup
    {
        get => _incrementalBackup;
        set => SetProperty(ref _incrementalBackup, value);
    }

    public bool UseFilemarks
    {
        get => _useFilemarks;
        set => SetProperty(ref _useFilemarks, value);
    }

    public int SelectedBlockSizeIndex
    {
        get => _selectedBlockSizeIndex;
        set => SetProperty(ref _selectedBlockSizeIndex, value);
    }

    public int SelectedHashIndex
    {
        get => _selectedHashIndex;
        set => SetProperty(ref _selectedHashIndex, value);
    }

    public bool AppendToSet
    {
        get => _appendToSet;
        set
        {
            if (SetProperty(ref _appendToSet, value))
            {
                OnPropertyChanged(nameof(OverwriteMedia));
                OnPropertyChanged(nameof(WarningMessage));
                OnPropertyChanged(nameof(WarningLevel));
            }
        }
    }

    public bool OverwriteMedia
    {
        get => !_appendToSet;
        set => AppendToSet = !value;
    }

    public AppendAfterOption? SelectedAppendOption
    {
        get => _selectedAppendOption;
        set
        {
            if (SetProperty(ref _selectedAppendOption, value))
            {
                OnPropertyChanged(nameof(WarningMessage));
                OnPropertyChanged(nameof(WarningLevel));
            }
        }
    }

    // Block size / hash static options (shared with NewBackupSetViewModel)
    public static string[] BlockSizeOptions { get; } =
        ["1 KB", "2 KB", "4 KB", "8 KB", "16 KB", "32 KB", "64 KB"];
    public static uint[] BlockSizeValues { get; } =
        [1024, 2048, 4096, 8192, 16384, 32768, 65536];

    public static string[] HashOptions { get; } =
        ["None", "CRC32", "CRC64", "XxHash32", "XxHash64", "XxHash3", "XxHash128"];
    public static TapeHashAlgorithm[] HashValues { get; } =
    [
        TapeHashAlgorithm.None,
        TapeHashAlgorithm.Crc32,
        TapeHashAlgorithm.Crc64,
        TapeHashAlgorithm.XxHash32,
        TapeHashAlgorithm.XxHash64,
        TapeHashAlgorithm.XxHash3,
        TapeHashAlgorithm.XxHash128
    ];

    /// <summary>Selected block size in bytes.</summary>
    public uint SelectedBlockSize => BlockSizeValues[SelectedBlockSizeIndex];

    /// <summary>Selected hash algorithm.</summary>
    public TapeHashAlgorithm SelectedHashAlgorithm => HashValues[SelectedHashIndex];

    // ═════════════════════════════════════════════════
    //  Scan state
    // ═════════════════════════════════════════════════

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool AutoScan
    {
        get => _autoScan;
        set => SetProperty(ref _autoScan, value);
    }

    // ═════════════════════════════════════════════════
    //  Warning logic (mirrors NewBackupSetViewModel)
    // ═════════════════════════════════════════════════

    /// <summary>Warning message based on current append/overwrite selection.</summary>
    public string WarningMessage
    {
        get
        {
            string message = string.Empty;
            var toc = _tapeService.TOC;

            if (OverwriteMedia)
                message = "WARNING: This will ERASE ALL DATA on the media!";
            else if (SelectedAppendOption is { IsOverwrite: false })
            {
                if (toc != null && SelectedAppendOption.SetIndex < toc.Count)
                {
                    int setsToRemove = toc.Count - SelectedAppendOption.SetIndex;
                    if (setsToRemove > 0)
                        message = $"{setsToRemove} backup set(s) after this one will be overwritten.";
                }
                else
                    message = "New backup set will be appended after the last set.";
            }
            else if (toc == null || toc.Count == 0)
                message = "New backup set will be the first on the media.";
            else
                message = "New backup set will be appended after the selected set.";

            if (toc?.ContinuedOnNextVolume ?? false)
            {
                if (message != string.Empty)
                    message += "\r\n";
                message += "Note: Writing to this media will invalidate a multi-volume backup";
            }

            return message;
        }
    }

    /// <summary>Warning level for styling.</summary>
    public WarningLevel WarningLevel
    {
        get
        {
            if (OverwriteMedia)
                return WarningLevel.Error;

            var toc = _tapeService.TOC;

            if (SelectedAppendOption is { IsOverwrite: false })
            {
                if (toc != null && SelectedAppendOption.SetIndex < toc.Count)
                    return WarningLevel.Warning;
            }

            if (toc?.ContinuedOnNextVolume ?? false)
                return WarningLevel.Warning;

            return WarningLevel.Info;
        }
    }

    // ═════════════════════════════════════════════════
    //  Preview
    // ═════════════════════════════════════════════════

    /// <summary>Total resolved files across all sources, formatted.</summary>
    public string PreviewTotalFiles
    {
        get => _previewTotalFiles;
        private set => SetProperty(ref _previewTotalFiles, value);
    }

    /// <summary>Total size of all resolved files, formatted.</summary>
    public string PreviewTotalSize
    {
        get => _previewTotalSize;
        private set => SetProperty(ref _previewTotalSize, value);
    }

    /// <summary>Total checked files across all sources, formatted.</summary>
    public string PreviewCheckedFiles
    {
        get => _previewCheckedFiles;
        private set => SetProperty(ref _previewCheckedFiles, value);
    }

    /// <summary>Total size of checked files, formatted.</summary>
    public string PreviewCheckedSize
    {
        get => _previewCheckedSize;
        private set => SetProperty(ref _previewCheckedSize, value);
    }

    /// <summary>Whether the Start Backup button is enabled.</summary>
    public bool CanStart => !IsScanning && _sourceView.GetTotalCheckedCount() > 0;

    // ═════════════════════════════════════════════════
    //  Tri-state "select all" header checkbox
    // ═════════════════════════════════════════════════

    /// <summary>
    /// Tri-state header checkbox: <c>true</c> = all checked, <c>false</c> = none,
    /// <c>null</c> = mixed. Setter toggles between all/none.
    /// </summary>
    public bool? AreAllSourcesChecked
    {
        get
        {
            if (SourceItems.Count == 0) return false;
            bool anyChecked = SourceItems.Any(s => s.IsCheckedForBackup != false);
            if (!anyChecked) return false;
            return SourceItems.All(s => s.IsCheckedForBackup == true) ? true : null;
        }
        set
        {
            // Three-state cycle: false→true, null→check all, true→uncheck all
            bool check = value != null;
            foreach (var item in SourceItems)
            {
                item.IsCheckedForBackup = check;
                // Propagate to underlying FilteredFileList
                var setView = _sourceView[item.Entry];
                setView?.FilteredFiles.SetAllChecked(check);
            }
            NotifySelectionChanged();
        }
    }

    // ═════════════════════════════════════════════════
    //  Commands
    // ═════════════════════════════════════════════════

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand AddPatternsCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand ScanAllCommand { get; }
    public ICommand StartBackupCommand { get; }
    public ICommand CancelCommand { get; }

    // ═════════════════════════════════════════════════
    //  Source management — Add / Remove
    // ═════════════════════════════════════════════════

    private void AddFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Files to Backup",
            Multiselect = true,
            Filter = "All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
                AddSourceIfNew(file);
        }
    }

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Folder to Backup",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var folder in dialog.FolderNames)
                AddSourceIfNew(folder.TrimEnd('\\') + "\\");
        }
    }

    private void AddPatterns()
    {
        if (string.IsNullOrWhiteSpace(PatternInput))
            return;

        var patterns = PatternInput.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern))
                AddSourceIfNew(pattern);
        }

        PatternInput = string.Empty;
    }

    /// <summary>
    /// Adds files and folders from an array of paths (e.g. from drag-and-drop).
    /// Folders get a trailing backslash, duplicates are skipped.
    /// </summary>
    public void AddPaths(string[] paths)
    {
        foreach (var path in paths)
        {
            var normalized = Directory.Exists(path)
                ? path.TrimEnd('\\') + "\\"
                : path;
            AddSourceIfNew(normalized);
        }
    }

    /// <summary>
    /// Adds a source entry if it doesn't already exist (case-insensitive).
    /// Creates a <see cref="BackupSourceListItem"/> display model and optionally
    /// triggers auto-scan.
    /// </summary>
    private void AddSourceIfNew(string pattern)
    {
        if (SourceItems.Any(s => s.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
            return;

        var entry = BackupSourceEntry.Create(pattern);
        var listItem = new BackupSourceListItem(entry);
        SourceItems.Add(listItem);

        OnPropertyChanged(nameof(AreAllSourcesChecked));
        CommandManager.InvalidateRequerySuggested();

        if (AutoScan)
            _ = ScanSourceAsync(listItem);
    }

    /// <summary>
    /// Removes all unchecked sources from the Folders pane and clears their
    /// resolved views from <see cref="BackupSourceView"/>.
    /// </summary>
    private void RemoveSelectedSources()
    {
        var toRemove = SourceItems.Where(s => s.IsCheckedForBackup == false).ToList();
        foreach (var item in toRemove)
        {
            _sourceView.RemoveView(item.Entry);
            SourceItems.Remove(item);
        }

        // If the removed source was selected, clear Files pane
        if (_selectedSource is not null && toRemove.Any(s => s == _selectedSource))
        {
            SelectedSource = null;
            CurrentFileItems = null;
        }

        NotifySelectionChanged();
    }

    // ═════════════════════════════════════════════════
    //  Scanning (file resolution)
    // ═════════════════════════════════════════════════

    /// <summary>
    /// Scans (resolves) a single source. Updates its list item and refreshes
    /// the Files pane if it's the currently selected source.
    /// </summary>
    public async Task ScanSourceAsync(BackupSourceListItem listItem)
    {
        // Cancel any running scan for this source
        CancelScan();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        listItem.IsScanning = true;

        try
        {
            var setView = await _sourceView.ResolveAsync(
                listItem.Entry, cts.Token,
                count => Application.Current.Dispatcher.Invoke(
                    () => listItem.FileCount = count));

            if (setView is null)
                return; // cancelled

            // Sync list item statistics from the new view
            _sourceView.SyncListItem(listItem);

            // If this source is currently selected, update the Files pane
            if (_selectedSource == listItem)
                RefreshFileDisplay();

            UpdatePreview();
        }
        catch (OperationCanceledException)
        {
            // Expected — scan was cancelled
        }
        finally
        {
            listItem.IsScanning = false;
            if (_scanCts == cts)
                _scanCts = null;
        }
    }

    /// <summary>
    /// Re-scans all sources (e.g. after toggling "Include subdirectories").
    /// </summary>
    private async Task ScanAllSourcesAsync()
    {
        CancelScan();
        IsScanning = true;

        try
        {
            foreach (var item in SourceItems.ToList())
            {
                await ScanSourceAsync(item);
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Cancels any in-progress scan.</summary>
    public void CancelScan()
    {
        _scanCts?.Cancel();
        _scanCts = null;
    }

    // ═════════════════════════════════════════════════
    //  Files pane display
    // ═════════════════════════════════════════════════

    /// <summary>
    /// Rebuilds the file items for the currently selected source from its
    /// <see cref="BackupSourceSetView"/>. Called after scan completes, filter
    /// changes, or source selection changes.
    /// </summary>
    public void RefreshFileDisplay()
    {
        if (_selectedSource is null)
        {
            CurrentFileItems = null;
            return;
        }

        var setView = _sourceView[_selectedSource.Entry];
        if (setView is null)
        {
            CurrentFileItems = null;
            return;
        }

        CurrentFileItems = setView.BuildFileItemList(_showFullPath);
    }

    // ═════════════════════════════════════════════════
    //  Item checkbox handling
    // ═════════════════════════════════════════════════

    /// <summary>
    /// Called from code-behind when a source checkbox is toggled in the Folders pane.
    /// Maps the tri-state toggle to check/uncheck all files in the source's
    /// <see cref="FilteredFileList"/>.
    /// </summary>
    public void OnSourceCheckChanged(BackupSourceListItem? changedItem = null)
    {
        if (changedItem is not null)
        {
            var setView = _sourceView[changedItem.Entry];
            if (setView is not null)
            {
                // Map tri-state: true → all, false → none, null → keep partial
                if (changedItem.IsCheckedForBackup == true)
                    setView.FilteredFiles.SetAllChecked(true);
                else if (changedItem.IsCheckedForBackup == false)
                    setView.FilteredFiles.SetAllChecked(false);
                // null (partial) — keep current per-file state
            }

            // Refresh the list item's stats from the updated FilteredFileList
            _sourceView.SyncListItem(changedItem);
        }

        NotifySelectionChanged();
    }

    /// <summary>
    /// Called from code-behind when a file checkbox is toggled in the Files pane.
    /// Updates the parent source's list item statistics.
    /// </summary>
    public void OnFileCheckChanged()
    {
        if (_selectedSource is not null)
        {
            _sourceView.SyncListItem(_selectedSource);
            OnPropertyChanged(nameof(AreAllSourcesChecked));
            OnPropertyChanged(nameof(AreAllFilesChecked));
        }
        UpdatePreview();
        CommandManager.InvalidateRequerySuggested();
    }

    // ═════════════════════════════════════════════════
    //  Notifications / preview
    // ═════════════════════════════════════════════════

    /// <summary>
    /// Centralized notification after any selection state change.
    /// Updates dependent properties, preview, and command states.
    /// </summary>
    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(AreAllSourcesChecked));
        OnPropertyChanged(nameof(CanStart));
        UpdatePreview();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Recomputes the Preview section from aggregate statistics.
    /// </summary>
    private void UpdatePreview()
    {
        int totalFiles = _sourceView.GetTotalFileCount();
        long totalSize = _sourceView.GetTotalFileSize();
        int checkedFiles = _sourceView.GetTotalCheckedCount();
        long checkedSize = _sourceView.GetTotalCheckedSize();

        PreviewTotalFiles = totalFiles > 0 ? totalFiles.ToString("N0") : "\u2014";
        PreviewTotalSize = totalSize > 0 ? Helpers.BytesToStringLong(totalSize) : "\u2014";
        PreviewCheckedFiles = checkedFiles > 0 ? checkedFiles.ToString("N0") : "\u2014";
        PreviewCheckedSize = checkedSize > 0 ? Helpers.BytesToStringLong(checkedSize) : "\u2014";

        OnPropertyChanged(nameof(CanStart));
    }

    // ═════════════════════════════════════════════════
    //  Append options
    // ═════════════════════════════════════════════════

    private void PopulateAppendOptions()
    {
        AppendOptions.Clear();

        var toc = _tapeService.TOC;
        if (toc == null || toc.Count == 0)
            return;

        // Add options from latest to oldest (0, -1, -2, ...)
        for (int setIndex = toc.LastSetOnVolume; setIndex >= toc.FirstSetOnVolume; setIndex--)
        {
            int alt = toc.SetIndexToAlt(setIndex);
            var setTOC = toc[setIndex];
            AppendOptions.Add(new AppendAfterOption(setTOC, setIndex, alt));
        }

        // Select the latest set by default
        if (AppendOptions.Count > 0)
            SelectedAppendOption = AppendOptions[0];
    }

    // ═════════════════════════════════════════════════
    //  Start backup
    // ═════════════════════════════════════════════════

    private void ExecuteStart(object? _)
    {
        var checkedFiles = _sourceView.CollectCheckedFiles(
            SourceItems.Select(s => s.Entry));

        if (checkedFiles.Count == 0)
        {
            MessageBox.Show("No files are checked for backup.",
                "Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var request = new BackupRequest(
            Description: _description,
            CheckedFiles: checkedFiles,
            Incremental: _incrementalBackup,
            BlockSize: SelectedBlockSize,
            HashAlgorithm: SelectedHashAlgorithm,
            AppendMode: _appendToSet,
            AppendAfterSetIndex: _selectedAppendOption?.SetIndex ?? -1,
            UseFilemarks: _useFilemarks,
            IncludeSubdirectories: _sourceView.IncludeSubdirectories);

        _onStartBackup(request);
    }

    // ═════════════════════════════════════════════════
    //  Pattern auto-suggestion
    // ═════════════════════════════════════════════════

    /// <summary>
    /// Auto-populates the PatternInput field based on the selected source entry.
    /// Smart replacement: if the user hasn't modified the previous auto-added
    ///  content, it gets replaced; otherwise, new content is appended.
    /// </summary>
    private void AutoAddPatternFromSelection(BackupSourceEntry entry)
    {
        // Build the pattern to add
        string patternToAdd = entry.Pattern;

        // If it's a folder (ends with \), append *.*
        if (entry.SourceType == BackupSourceType.SingleFolder ||
            patternToAdd.EndsWith('\\') || patternToAdd.EndsWith('/'))
        {
            patternToAdd = patternToAdd.TrimEnd('\\', '/') + "\\*.*";
        }

        // Determine how to add it to PatternInput
        if (string.IsNullOrEmpty(PatternInput))
        {
            PatternInput = patternToAdd;
        }
        else if (_prevAutoAdded != null && PatternInput.EndsWith(_prevAutoAdded))
        {
            // PatternInput ends with previous auto-added content (user didn't modify it)
            // Replace the previous auto-added content with the new pattern
            PatternInput = PatternInput[..^_prevAutoAdded.Length] + patternToAdd;
        }
        else
        {
            // User modified the content or no previous auto-add — append with separator
            PatternInput = PatternInput.TrimEnd().TrimEnd(';') + "; " + patternToAdd;
        }

        // Remember what we auto-added for next time
        _prevAutoAdded = patternToAdd;
    }

    // ═════════════════════════════════════════════════
    //  Cleanup
    // ═════════════════════════════════════════════════

    /// <summary>
    /// Cancels any pending scans. Call when the window is closing.
    /// </summary>
    public void Dispose()
    {
        CancelScan();
    }
}
