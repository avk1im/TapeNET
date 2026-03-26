using System.ComponentModel;
using System.Windows.Media.Imaging;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;
using TapeWinNET.Utils;

namespace TapeWinNET.Models;

/// <summary>
/// Represents a backup set item for display in the backup sets ListView.
/// Uses <see cref="FilteredFileList"/> for per-set async filtering and count display.
/// </summary>
public class BackupSetListItem : INotifyPropertyChanged
{
    private static BitmapSource? _backupSetIcon;
    private static bool _iconLoaded;

    private readonly TapeSetTOC _setTOC;
    private readonly int _setIndex;
    private readonly int _altIndex;
    private readonly bool _isOnCurrentVolume;
    private readonly FilteredFileList _filteredFiles;

    static BackupSetListItem()
    {
        LoadIcon();
    }

    public BackupSetListItem(TapeSetTOC setTOC, int setIndex, int altIndex, bool isOnCurrentVolume)
    {
        _setTOC = setTOC;
        _setIndex = setIndex;
        _altIndex = altIndex;
        _isOnCurrentVolume = isOnCurrentVolume;

        _filteredFiles = new FilteredFileList(setTOC);
        _filteredFiles.FilterCompleted += OnFilterCompleted;
    }

    private static void LoadIcon()
    {
        if (_iconLoaded)
            return;

        try
        {
            _backupSetIcon = TapeIcons.GetBackupSetIcon(large: false);
            _backupSetIcon?.Freeze();
        }
        catch
        {
            // If icon loading fails, we'll just have no icon
        }

        _iconLoaded = true;
    }

    /// <summary>
    /// Gets the icon for backup set items.
    /// </summary>
    public static BitmapSource? Icon => _backupSetIcon;

    /// <summary>
    /// Whether this backup set resides on the currently loaded volume.
    /// Used to dim icons of sets from previous volumes.
    /// </summary>
    public bool IsOnCurrentVolume => _isOnCurrentVolume;

    /// <summary>
    /// Standard set index (1-based, from oldest to newest)
    /// </summary>
    public int SetIndex => _setIndex;

    /// <summary>
    /// Alternative index (0 = latest, negative = older)
    /// </summary>
    public int AltIndex => _altIndex;

    /// <summary>
    /// Display format: "1 | -2" for set 1 of 3
    /// </summary>
    public string IndexDisplay => $"{SetIndex} | {AltIndex}";

    public string Description => _setTOC.Description ?? "(unnamed)";

    public int FileCount => _setTOC.Count;

    /// <summary>The internal <see cref="FilteredFileList"/> for this set.</summary>
    public FilteredFileList FilteredFiles => _filteredFiles;

    /// <summary>
    /// Optional file filter. When set, the <see cref="FilteredFiles"/> computes
    ///  the filtered count asynchronously. Setting to null clears the filter.
    /// </summary>
    public ITapeFileFilter? FileFilter
    {
        get => _filteredFiles.Filter;
        set => _filteredFiles.Filter = value;
    }

    /// <summary>
    /// Number of files after filtering, or null if no filter is active (or still computing).
    /// Computed automatically when <see cref="FileFilter"/> is set.
    /// </summary>
    public int? FilteredFileCount => _filteredFiles.IsFiltered ? _filteredFiles.Count : null;

    /// <summary>
    /// Awaitable task for the current filter computation.
    /// Callers can <c>await Task.WhenAll(...)</c> across multiple items for parallel filtering.
    /// </summary>
    public Task FilterTask => _filteredFiles.FilterTask;

    private void OnFilterCompleted()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredFileCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileCountFormatted)));
    }

    /// <summary>
    /// Display format: plain count or "total → filtered" when a filter narrows results.
    /// </summary>
    public string FileCountFormatted => FilteredFileCount is int filtered && filtered != _setTOC.Count
        ? $"{_setTOC.Count:N0} \u2192 {filtered:N0}" /* right arrow */
        : _setTOC.Count.ToString("N0");

    public DateTime CreatedOn => _setTOC.CreationTime;

    public string CreatedOnFormatted => _setTOC.CreationTime.ToString("G");

    public bool IsIncremental => _setTOC.Incremental;

    public string IncrementalDisplay => _setTOC.Incremental ? "Yes" : "No";

    public string BlockSize => Helpers.BytesToString(_setTOC.BlockSize);

    public string HashAlgorithm => _setTOC.HashAlgorithm.ToString();

    public int Volume => _setTOC.Volume;

    public TapeSetTOC SetTOC => _setTOC;

    /// <summary>
    /// Whether this backup set is checked for restore/validate/verify operations.
    /// </summary>
    private bool _isCheckedForRestore;
    public bool IsCheckedForRestore
    {
        get => _isCheckedForRestore;
        set
        {
            if (_isCheckedForRestore != value)
            {
                _isCheckedForRestore = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCheckedForRestore)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}