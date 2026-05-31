using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeLibNET.Services;
using TapeWinNET.Converters;
using TapeWinNET.Models;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Request data gathered by the BackupWindow for executing a backup operation.
/// Mirrors <see cref="RestoreFormData"/> for the Restore workflow.
/// </summary>
/// <param name="Description">User description for the new backup set.</param>
/// <param name="CheckedFiles">Flat list of checked files for backup, collected
///  from all sources via <see cref="BackupSourceView.CollectCheckedFiles"/>.</param>
/// <param name="Incremental">Whether this is an incremental backup.</param>
/// <param name="BlockSize">Block size in bytes.</param>
/// <param name="HashAlgorithm">Hash algorithm for verification.</param>
/// <param name="AppendMode">True to append after existing set, false to overwrite all.</param>
/// <param name="AppendAfterSetIndex">Set index to append after (-1 for overwrite).</param>
/// <param name="IncludeSubdirectories">Whether sources were scanned with subdirectory recursion.</param>
/// <param name="MediaName">New media description when writing to media with no existing TOC;
///  <see langword="null"/> when the media already has a TOC.</param>
public record BackupFormData(
    string Description,
    List<TapeFileInfo> CheckedFiles,
    bool Incremental,
    uint BlockSize,
    TapeHashAlgorithm HashAlgorithm,
    bool AppendMode,
    int AppendAfterSetIndex,
    bool IncludeSubdirectories,
    bool SkipAllErrors,
    bool NoMultivolume,
    string? MediaName = null,
    TapeCompression Compression = TapeCompression.None,
    int CompressionLevel = ZstdLevel.Default);

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
    private readonly Action<BackupFormData> _onStartBackup;
    private readonly Action _onCancel;

    // ─────────────────────────────────────────────────
    //  Source management
    // ─────────────────────────────────────────────────

    private readonly BackupSourceView _sourceView = new();
    private BackupSourceListItem? _selectedSource;
    private readonly Dictionary<BackupSourceEntry, CancellationTokenSource> _scanCtsMap = [];

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
    private string _mediaName = string.Empty;
    private bool _incrementalBackup;
    private bool _skipAllErrors;
    private bool _noMultivolume;
    private int _selectedBlockSizeIndex;
    private int _selectedHashIndex = 1;        // Default CRC32
    private int _selectedCompressionIndex;     // Default None; overridden in constructor
    private int _compressionLevel = ZstdLevel.Default;
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
        Action<BackupFormData> onStartBackup,
        Action onCancel)
    {
        _tapeService = tapeService;
        _onStartBackup = onStartBackup;
        _onCancel = onCancel;

        // Default description
        Description = $"Backup set created {DateTime.Now:g}";

        // Pre-populate the media name for the overwrite case. When there is no
        //  TOC the only valid mode is overwrite, so also force that selection.
        _mediaName = TapeServiceBase.DefaultNewMediaName;
        if (tapeService.TOC is null)
            _appendToSet = false;

        // Build block size options dynamically from drive capabilities
        (BlockSizeValues, BlockSizeOptions, _selectedBlockSizeIndex) =
            BuildBlockSizeOptions();

        // Populate append options from current TOC
        PopulateAppendOptions();

        // Construct the media usage bar presenter and do an initial build so the
        //  bar shows the existing-sets layout even before any source is added.
        UsageBar = new BackupMediaUsageBarPresenter(_tapeService, this);
        UsageBar.Rebuild();
        UsageBar.UpdateHighlight(_selectedAppendOption?.SetIndex);

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

        // Default to Hardware compression when the drive supports it
        if (tapeService.SupportsCompression)
            _selectedCompressionIndex = Array.IndexOf(CompressionModeValues, TapeCompression.Hardware);

        // Commands — compression level presets
        SetCompressionFastCommand     = new RelayCommand(_ => CompressionLevel = ZstdLevel.Fast);
        SetCompressionBalancedCommand = new RelayCommand(_ => CompressionLevel = ZstdLevel.Balanced);
        SetCompressionHighCommand     = new RelayCommand(_ => CompressionLevel = ZstdLevel.High);
    }

    // ═════════════════════════════════════════════════
    //  Public: data model access
    // ═════════════════════════════════════════════════

    /// <summary>The session-level source view (file resolution + checked state).</summary>
    public BackupSourceView SourceView => _sourceView;

    /// <summary>Total checked size (bytes) across all sources — raw value used by the usage bar.</summary>
    public long CheckedFileSizeBytes => _sourceView.GetTotalCheckedSize();

    /// <summary>Total checked file count across all sources — raw value used by the usage bar.</summary>
    public int CheckedFileCount => _sourceView.GetTotalCheckedCount();

    /// <summary>
    /// Owns the segment list, capacity, and click command bound to the
    ///  <c>MediaUsageBarControl</c> in BackupWindow. Adds a pending
    ///  new-set segment that reflects the user's current selection.
    /// </summary>
    public BackupMediaUsageBarPresenter UsageBar { get; }

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
        set
        {
            if (SetProperty(ref _description, value))
                UsageBar?.Rebuild();
        }
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
        set
        {
            if (SetProperty(ref _incrementalBackup, value))
                UsageBar?.Rebuild();
        }
    }

    /// <summary>
    /// When checked, all file errors during backup are silently skipped without
    /// showing an error dialog (non-assisted / unattended backup mode).
    /// </summary>
    public bool SkipAllErrors
    {
        get => _skipAllErrors;
        set => SetProperty(ref _skipAllErrors, value);
    }

    /// <summary>
    /// When checked, multi-volume continuation is disabled: if the media becomes
    /// full the backup ends with the files written so far.
    /// </summary>
    public bool NoMultivolume
    {
        get => _noMultivolume;
        set => SetProperty(ref _noMultivolume, value);
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
                UsageBar?.Rebuild();
                UsageBar?.UpdateHighlight(
                    _appendToSet ? _selectedAppendOption?.SetIndex : null);
            }
        }
    }

    public bool OverwriteMedia
    {
        get => !_appendToSet;
        set => AppendToSet = !value;
    }

    /// <summary>
    /// True when the loaded media has no TOC (new or foreign media).
    /// In this state the only valid backup mode is overwrite, and the user is
    ///  shown a <see cref="MediaName"/> field instead of the warning panel.
    /// </summary>
    public bool IsNoToc => _tapeService.TOC is null;

    /// <summary>
    /// Description to write to the new media when <see cref="IsNoToc"/> is true.
    /// Pre-populated from <see cref="TapeServiceBase.DefaultNewMediaName"/>.
    /// </summary>
    public string MediaName
    {
        get => _mediaName;
        set => SetProperty(ref _mediaName, value);
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
                UsageBar?.Rebuild();
                UsageBar?.UpdateHighlight(
                    OverwriteMedia ? null : value?.SetIndex);
            }
        }
    }

    // Block size / hash / compression options (block sizes generated dynamically from drive capabilities)
    public string[] BlockSizeOptions { get; }
    public uint[] BlockSizeValues { get; }

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

    /// <summary>True when the connected drive supports hardware compression.</summary>
    public bool SupportsHardwareCompression => _tapeService.SupportsCompression;

    /// <summary>
    /// Compression mode display names. Includes "Hardware" only when the drive supports it.
    /// </summary>
    public string[] CompressionModeOptions => _tapeService.SupportsCompression
        ? ["None", "Hardware", "Software"]
        : ["None", "Software"];

    /// <summary>Corresponding <see cref="TapeCompression"/> values for each option.</summary>
    public TapeCompression[] CompressionModeValues => _tapeService.SupportsCompression
        ? [TapeCompression.None, TapeCompression.Hardware, TapeCompression.Software]
        : [TapeCompression.None, TapeCompression.Software];

    /// <summary>Selected block size in bytes.</summary>
    public uint SelectedBlockSize => BlockSizeValues[SelectedBlockSizeIndex];

    /// <summary>Selected hash algorithm.</summary>
    public TapeHashAlgorithm SelectedHashAlgorithm => HashValues[SelectedHashIndex];

    /// <summary>Index into <see cref="CompressionModeOptions"/> / <see cref="CompressionModeValues"/>.</summary>
    public int SelectedCompressionIndex
    {
        get => _selectedCompressionIndex;
        set
        {
            if (SetProperty(ref _selectedCompressionIndex, value))
            {
                OnPropertyChanged(nameof(SelectedCompression));
                OnPropertyChanged(nameof(IsSoftwareCompression));
            }
        }
    }

    /// <summary>Selected compression mode.</summary>
    public TapeCompression SelectedCompression => CompressionModeValues[_selectedCompressionIndex];

    /// <summary>
    /// ZSTD compression level (1–19). Only meaningful when
    ///  <see cref="SelectedCompression"/> is <see cref="TapeCompression.Software"/>.
    /// </summary>
    public int CompressionLevel
    {
        get => _compressionLevel;
        set => SetProperty(ref _compressionLevel, ZstdLevel.Clamp(value));
    }

    /// <summary>
    /// True when Software compression is selected; drives the visibility of the
    ///  level slider and preset buttons in BackupWindow.
    /// </summary>
    public bool IsSoftwareCompression => SelectedCompression == TapeCompression.Software;

    // ── Compression preset commands ───────────────────────────────────────────
    public ICommand SetCompressionFastCommand     { get; }
    public ICommand SetCompressionBalancedCommand { get; }
    public ICommand SetCompressionHighCommand     { get; }

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
                message = _tapeService.TOC is null
                    ? "WARNING: Entire media will be overwritten."
                    : "WARNING: This will ERASE ALL DATA on the media!";
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
                message += "Note: Writing to this media may invalidate a multi-volume backup";
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

        // Single files resolve synchronously (no disk enumeration needed)
        if (entry.SourceType == BackupSourceType.SingleFile)
        {
            _sourceView.ResolveSingleFile(entry);
            _sourceView.SyncListItem(listItem);
            UpdatePreview();
        }
        else if (AutoScan)
        {
            _ = ScanSourceAsync(listItem);
        }
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
        // Cancel any previous scan for this specific source
        if (_scanCtsMap.Remove(listItem.Entry, out var oldCts))
            oldCts.Cancel();

        var cts = new CancellationTokenSource();
        _scanCtsMap[listItem.Entry] = cts;

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
            _scanCtsMap.Remove(listItem.Entry, out _);
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

    /// <summary>Cancels all in-progress scans.</summary>
    public void CancelScan()
    {
        foreach (var cts in _scanCtsMap.Values)
            cts.Cancel();
        _scanCtsMap.Clear();
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
    /// <see cref="FilteredFileList"/>. Saves partial selection before overwriting
    ///  with all/none, and restores it when the user clicks to indeterminate.
    /// </summary>
    public void OnSourceCheckChanged(BackupSourceListItem? changedItem = null)
    {
        if (changedItem is not null)
        {
            var setView = _sourceView[changedItem.Entry];
            if (setView is not null)
            {
                // Map tri-state click cycle (false → true → null → false):
                //  true  = check all   — save partial first so it can be restored
                //  false = uncheck all — save partial first so it can be restored
                //  null  = restore the previously saved partial selection
                if (changedItem.IsCheckedForBackup == true)
                {
                    setView.SavePartialSelectionIfNeeded();
                    setView.FilteredFiles.SetAllChecked(true);
                }
                else if (changedItem.IsCheckedForBackup == false)
                {
                    setView.SavePartialSelectionIfNeeded();
                    setView.FilteredFiles.SetAllChecked(false);
                }
                else
                {
                    // null (indeterminate) — restore saved partial selection
                    setView.RestorePartialSelection();
                }
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

        // Refresh the usage bar's pending segment to match the new totals.
        UsageBar?.Rebuild();
    }

    // ═════════════════════════════════════════════════
    //  Block size options (dynamic from drive capabilities)
    // ═════════════════════════════════════════════════

    /// <summary>
    /// Generates block size options as powers of two from the drive's minimum
    /// to maximum block size. Falls back to a sensible default range if the
    /// drive reports zero. Returns the values, display strings, and the index
    /// matching <see cref="TapeService.DefaultBlockSize"/>.
    /// </summary>
    private (uint[] values, string[] labels, int defaultIndex)
        BuildBlockSizeOptions()
    {
        // Sensible fallbacks when drive parameters are unavailable
        uint min = _tapeService.MinimumBlockSize;
        uint max = _tapeService.MaximumBlockSize;
        uint def = _tapeService.DefaultBlockSize;

        if (min == 0) min = 1024;
        if (max == 0) max = 65536;
        if (def == 0) def = 16384;

        // Clamp minimum to at least 1 KB (smaller sizes are impractical for backup)
        if (min < 1024) min = 1024;

        // Enumerate powers of two from min to max
        var values = new List<uint>();
        for (uint size = min; size <= max; size *= 2)
        {
            values.Add(size);

            if (size > uint.MaxValue / 2)
                break; // overflow guard
        }

        // Ensure at least the default is present
        if (values.Count == 0)
            values.Add(def);

        var arr = values.ToArray();
        var labels = arr
            .Select(v => Helpers.BytesToString(v, precision: 0))
            .ToArray();

        // Find default index, fall back to first entry
        int defaultIndex = Array.IndexOf(arr, def);
        if (defaultIndex < 0) defaultIndex = 0;

        return (arr, labels, defaultIndex);
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

        var request = new BackupFormData(
            Description: _description,
            CheckedFiles: checkedFiles,
            Incremental: _incrementalBackup,
            BlockSize: SelectedBlockSize,
            HashAlgorithm: SelectedHashAlgorithm,
            AppendMode: _appendToSet,
            AppendAfterSetIndex: _selectedAppendOption?.SetIndex ?? -1,
            IncludeSubdirectories: _sourceView.IncludeSubdirectories,
            SkipAllErrors: _skipAllErrors,
            NoMultivolume: _noMultivolume,
            MediaName: OverwriteMedia ? _mediaName : null,
            Compression: SelectedCompression,
            CompressionLevel: _compressionLevel);

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
