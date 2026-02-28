using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TapeLibNET;
using TapeWinNET.Converters;
using TapeWinNET.Models;
using TapeWinNET.Services;
using Windows.Win32.System.SystemServices;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for the New Backup Set window.
/// </summary>
public class NewBackupSetViewModel : ViewModelBase
{
    private readonly TapeService _tapeService;
    private readonly Action<NewBackupSetViewModel> _onStartBackup;
    private readonly Action _onCancel;

    private string _description = string.Empty;
    private bool _includeSubdirectories = true;
    private bool _incrementalBackup;
    private bool _useFilemarks = false; // Default to false
    private bool _previewFilesBeforeBackup;
    private int _selectedBlockSizeIndex = 4; // Default 16KB
    private int _selectedHashIndex = 1; // Default CRC32
    private bool _appendToSet = true;
    private AppendAfterOption? _selectedAppendOption;
    private string _patternInput = string.Empty;
    private bool _isScanning;
    private int _totalFileCount;
    private long _totalSize;
    private DateTime? _lastScanTime;
    private bool _autoScan = true;
    // for auto-adding to pattern input:
    private BackupSourceEntry? _selectedSourceEntry;
    private string? _prevAutoAdded;

    public NewBackupSetViewModel(TapeService tapeService, Action<NewBackupSetViewModel> onStartBackup, Action onCancel)
    {
        _tapeService = tapeService;
        _onStartBackup = onStartBackup;
        _onCancel = onCancel;

        // Initialize commands
        AddFilesCommand = new RelayCommand(AddFiles, () => !IsScanning);
        AddFolderCommand = new RelayCommand(AddFolder, () => !IsScanning);
        AddPatternsCommand = new RelayCommand(AddPatterns, () => !IsScanning && !string.IsNullOrWhiteSpace(PatternInput));
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => !IsScanning && HasSelectedEntries);
        SelectAllCommand = new RelayCommand(SelectAll);
        ScanNowCommand = new AsyncRelayCommand(ScanNowAsync, () => !IsScanning && SourceEntries.Count > 0);
        StartBackupCommand = new RelayCommand(StartBackup, param => CanStartBackup());
        CancelCommand = new RelayCommand(_ => _onCancel());

        // Initialize collections
        SourceEntries.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(CanRemoveSelected));
            CommandManager.InvalidateRequerySuggested();
            if (AutoScan && e.NewItems != null)
            {
                // Auto-scan new entries
                _ = ScanEntriesAsync(e.NewItems.Cast<BackupSourceEntry>().ToList());
            }
        };

        // Populate append options from TOC
        PopulateAppendOptions();

        // Set default description
        Description = $"Backup set created {DateTime.Now:g}";
    }

    #region Properties

    public ObservableCollection<BackupSourceEntry> SourceEntries { get; } = [];
    public ObservableCollection<AppendAfterOption> AppendOptions { get; } = [];

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public bool IncludeSubdirectories
    {
        get => _includeSubdirectories;
        set
        {
            if (SetProperty(ref _includeSubdirectories, value) && AutoScan)
            {
                _ = ScanAllEntriesAsync();
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

    public bool PreviewFilesBeforeBackup
    {
        get => _previewFilesBeforeBackup;
        set
        {
            if (SetProperty(ref _previewFilesBeforeBackup, value))
            {
                OnPropertyChanged(nameof(StartBackupButtonText));
            }
        }
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

    public string PatternInput
    {
        get => _patternInput;
        set
        {
            if (SetProperty(ref _patternInput, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// Currently selected source entry in the ListView.
    /// When set, auto-populates the PatternInput field.
    /// </summary>
    public BackupSourceEntry? SelectedSourceEntry
    {
        get => _selectedSourceEntry;
        set
        {
            if (SetProperty(ref _selectedSourceEntry, value) && value != null)
            {
                AutoAddPatternFromSelection(value);
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (SetProperty(ref _isScanning, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int TotalFileCount
    {
        get => _totalFileCount;
        set
        {
            if (SetProperty(ref _totalFileCount, value))
            {
                OnPropertyChanged(nameof(TotalFileCountDisplay));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public long TotalSize
    {
        get => _totalSize;
        set
        {
            if (SetProperty(ref _totalSize, value))
            {
                OnPropertyChanged(nameof(TotalSizeDisplay));
            }
        }
    }

    public DateTime? LastScanTime
    {
        get => _lastScanTime;
        set
        {
            if (SetProperty(ref _lastScanTime, value))
            {
                OnPropertyChanged(nameof(LastScanTimeDisplay));
            }
        }
    }

    public bool AutoScan
    {
        get => _autoScan;
        set => SetProperty(ref _autoScan, value);
    }

    public string TotalFileCountDisplay => TotalFileCount == 0 ? "No files" : $"{TotalFileCount:N0} files";
    public string TotalSizeDisplay => TotalSize < 0 ? "?" : Helpers.BytesToString(TotalSize);
    public string LastScanTimeDisplay => LastScanTime?.ToString("T") ?? "Not scanned";

    public bool HasEntries => SourceEntries.Count > 0;
    public bool HasSelectedEntries => SourceEntries.Any(e => e.IsSelected);
    public bool CanRemoveSelected => HasSelectedEntries && !IsScanning;

    public string StartBackupButtonText => PreviewFilesBeforeBackup ? "Start Backup..." : "Start Backup";

    /// <summary>
    /// Warning message based on current append/overwrite selection.
    /// </summary>
    public string WarningMessage
    {
        get
        {
            if (OverwriteMedia)
                return "WARNING: This will ERASE ALL DATA on the media!";

            if (SelectedAppendOption != null && !SelectedAppendOption.IsOverwrite)
            {
                var toc = _tapeService.TOC;
                if (toc != null && SelectedAppendOption.SetIndex < toc.Count)
                {
                    int setsToRemove = toc.Count - SelectedAppendOption.SetIndex;
                    if (setsToRemove > 0)
                        return $"{setsToRemove} backup set(s) after this one will be overwritten.";
                }
            }

            return "New backup set will be appended after the selected set.";
        }
    }

    /// <summary>
    /// Warning level for styling.
    /// </summary>
    public WarningLevel WarningLevel
    {
        get
        {
            if (OverwriteMedia)
                return WarningLevel.Error;

            if (SelectedAppendOption != null && !SelectedAppendOption.IsOverwrite)
            {
                var toc = _tapeService.TOC;
                if (toc != null && SelectedAppendOption.SetIndex < toc.Count)
                    return WarningLevel.Warning;
            }

            return WarningLevel.Info;
        }
    }

    // Block size options
    public static string[] BlockSizeOptions { get; } = ["1 KB", "2 KB", "4 KB", "8 KB", "16 KB", "32 KB", "64 KB"];
    public static uint[] BlockSizeValues { get; } = [1024, 2048, 4096, 8192, 16384, 32768, 65536];

    // Hash algorithm options
    public static string[] HashOptions { get; } = ["None", "CRC32", "CRC64", "XxHash32", "XxHash64", "XxHash3", "XxHash128"];
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

    /// <summary>
    /// Gets the selected block size in bytes.
    /// </summary>
    public uint SelectedBlockSize => BlockSizeValues[SelectedBlockSizeIndex];

    /// <summary>
    /// Gets the selected hash algorithm.
    /// </summary>
    public TapeHashAlgorithm SelectedHashAlgorithm => HashValues[SelectedHashIndex];

    #endregion

    #region Commands

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand AddPatternsCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ScanNowCommand { get; }
    public ICommand StartBackupCommand { get; }
    public ICommand CancelCommand { get; }

    #endregion

    #region Command Implementations

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
            {
                if (!SourceEntries.Any(e => e.Pattern.Equals(file, StringComparison.OrdinalIgnoreCase)))
                {
                    SourceEntries.Add(BackupSourceEntry.Create(file));
                }
            }
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
            {
                var folderPath = folder.TrimEnd('\\') + "\\";
                if (!SourceEntries.Any(e => e.Pattern.Equals(folderPath, StringComparison.OrdinalIgnoreCase)))
                {
                    SourceEntries.Add(BackupSourceEntry.Create(folderPath));
                }
            }
        }
    }

    private void AddPatterns()
    {
        if (string.IsNullOrWhiteSpace(PatternInput))
            return;

        // Split by semicolon and add each pattern
        var patterns = PatternInput.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern) &&
                !SourceEntries.Any(e => e.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                SourceEntries.Add(BackupSourceEntry.Create(pattern));
            }
        }

        PatternInput = string.Empty;
    }

    private void RemoveSelected()
    {
        var toRemove = SourceEntries.Where(e => e.IsSelected).ToList();
        foreach (var entry in toRemove)
        {
            SourceEntries.Remove(entry);
        }
        UpdateTotals();
        OnPropertyChanged(nameof(HasSelectedEntries));
        OnPropertyChanged(nameof(CanRemoveSelected));
    }

    private void SelectAll()
    {
        bool anyUnselected = SourceEntries.Any(e => !e.IsSelected);
        foreach (var entry in SourceEntries)
        {
            entry.IsSelected = anyUnselected;
        }
        OnPropertyChanged(nameof(HasSelectedEntries));
        OnPropertyChanged(nameof(CanRemoveSelected));
    }

    private async Task ScanNowAsync()
    {
        await ScanAllEntriesAsync();
    }

    private async Task ScanAllEntriesAsync()
    {
        await ScanEntriesAsync(SourceEntries.ToList());
    }

    private async Task ScanEntriesAsync(List<BackupSourceEntry> entries)
    {
        if (entries.Count == 0)
            return;

        IsScanning = true;

        try
        {
            await Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    try
                    {
                        var files = BuildFileListForEntry(entry);
                        entry.FileCount = files.Count;
                        entry.TotalSize = files.Sum(f =>
                        {
                            try { return new FileInfo(f).Length; }
                            catch { return 0L; }
                        });
                    }
                    catch
                    {
                        entry.FileCount = 0;
                        entry.TotalSize = 0;
                    }
                }
            });

            UpdateTotals();
            LastScanTime = DateTime.Now;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private List<string> BuildFileListForEntry(BackupSourceEntry entry)
    {
        List<string> files = [];

        try
        {
            if (TapeFileBackupAgent.HasWildcards(entry.Pattern))
            {
                var directoryName = Path.GetDirectoryName(entry.Pattern);
                var fileNameWithWildcards = Path.GetFileName(entry.Pattern);

                var directoryPath = Directory.Exists(directoryName) ? Path.GetFullPath(directoryName) :
                    string.IsNullOrEmpty(directoryName) ? Directory.GetCurrentDirectory() : null;

                if (!string.IsNullOrEmpty(directoryPath))
                {
                    var searchOption = IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    files.AddRange(Directory.EnumerateFiles(directoryPath, fileNameWithWildcards, searchOption));
                }
            }
            else if (TapeFileBackupAgent.IsDirectory(entry.Pattern))
            {
                var directoryPath = Path.GetFullPath(entry.Pattern);
                if (Directory.Exists(directoryPath))
                {
                    var searchOption = IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    files.AddRange(Directory.EnumerateFiles(directoryPath, "*", searchOption));
                }
            }
            else
            {
                if (File.Exists(entry.Pattern))
                {
                    files.Add(Path.GetFullPath(entry.Pattern));
                }
            }
        }
        catch
        {
            // Ignore errors during scanning
        }

        return files;
    }

    private void UpdateTotals()
    {
        TotalFileCount = SourceEntries.Sum(e => e.FileCount >= 0 ? e.FileCount : 0);
        TotalSize = SourceEntries.Sum(e => e.TotalSize >= 0 ? e.TotalSize : 0);
    }

    private bool CanStartBackup()
    {
        return !IsScanning && TotalFileCount > 0;
    }

    private void StartBackup(object? parameter)
    {
        _onStartBackup(this);
    }

    #endregion

    #region Private Methods

    private void PopulateAppendOptions()
    {
        AppendOptions.Clear();

        var toc = _tapeService.TOC;
        if (toc == null || toc.Count == 0)
        {
            // No existing sets - only overwrite option makes sense but we'll create new set anyway
            return;
        }

        // Add options from latest to oldest (0, -1, -2, ...)
        for (int alt = 0; alt >= toc.MinSetIndex; alt--)
        {
            int setIndex = toc.SetIndexToAlt(alt);
            var setTOC = toc[setIndex];
            AppendOptions.Add(new AppendAfterOption(setTOC, setIndex, alt));
        }

        // Select the latest set by default
        if (AppendOptions.Count > 0)
        {
            SelectedAppendOption = AppendOptions[0];
        }
    }

    /// <summary>
    /// Gets the list of patterns for backup.
    /// </summary>
    public List<string> GetPatterns()
    {
        return [.. SourceEntries.Select(e => e.Pattern)];
    }

    /// <summary>
    /// Auto-populates the PatternInput field based on the selected source entry.
    /// Implements smart replacement: if the user hasn't modified the previous auto-added content,
    /// it gets replaced; otherwise, new content is appended.
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
            // Empty input - just set the pattern
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
            // User modified the content or no previous auto-add - append with separator
            PatternInput = PatternInput.TrimEnd().TrimEnd(';') + "; " + patternToAdd;
        }

        // Remember what we auto-added for next time
        _prevAutoAdded = patternToAdd;
    }

    #endregion
}