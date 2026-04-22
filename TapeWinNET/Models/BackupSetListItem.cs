using System.ComponentModel;
using System.Windows.Media.Imaging;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;

namespace TapeWinNET.Models;

/// <summary>
/// Represents a backup set item for display in the backup sets ListView.
///  Filter and checked counts are externally managed and pushed in via
///  <see cref="FilteredFileCount"/> and <see cref="CheckedFileCount"/>.
/// </summary>
public class BackupSetListItem(TapeSetTOC setTOC, int setIndex, int altIndex, bool isOnCurrentVolume)
    : INotifyPropertyChanged
{
    private static BitmapSource? _backupSetIcon;
    private static bool _iconLoaded;

    private readonly TapeSetTOC _setTOC = setTOC;
    private readonly int _setIndex = setIndex;
    private readonly int _altIndex = altIndex;
    private readonly bool _isOnCurrentVolume = isOnCurrentVolume;

    static BackupSetListItem()
    {
        LoadIcon();
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
    public string IndexDisplay => $"#{SetIndex} | {AltIndex}";

    public string Description => _setTOC.Description ?? "(unnamed)";

    public int FileCount => _setTOC.Count;

    /// <summary>
    /// Number of files after filtering, or <c>null</c> when no filter narrows results.
    ///  Set externally (e.g. by <c>RestoreViewModel</c>) after async filter computation.
    /// </summary>
    private int? _filteredFileCount;
    public int? FilteredFileCount
    {
        get => _filteredFileCount;
        set
        {
            if (_filteredFileCount != value)
            {
                _filteredFileCount = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredFileCount)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredFileCountFormatted)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileCountFormatted)));
            }
        }
    }

    /// <summary>
    /// Display format for the "Filtered" column: filtered count or "--" when no filter is active.
    /// </summary>
    public string FilteredFileCountFormatted => _filteredFileCount is int count
        ? count.ToString("N0") : "\u2014";

    /// <summary>
    /// Number of checked (selected) files, or <c>null</c> when nothing is checked.
    ///  Set externally (e.g. by <c>MainViewModel.ToggleBackupSetChecked</c>)
    ///  after checked-state changes.
    /// </summary>
    private int? _checkedFileCount;
    public int? CheckedFileCount
    {
        get => _checkedFileCount;
        set
        {
            if (_checkedFileCount != value)
            {
                _checkedFileCount = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckedFileCount)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedFileCountFormatted)));
            }
        }
    }

    /// <summary>
    /// Display format for the "Selected" column: checked count or "--" when nothing is checked.
    /// </summary>
    public string SelectedFileCountFormatted => _checkedFileCount is int count and > 0
        ? count.ToString("N0") : "\u2014";

    /// <summary>
    /// Display format: plain count or "total → filtered" when a filter narrows results.
    /// </summary>
    public string FileCountFormatted => _filteredFileCount is int filtered && filtered != _setTOC.Count
        ? $"{_setTOC.Count:N0} \u2192 {filtered:N0}" /* right arrow */
        : _setTOC.Count.ToString("N0");

    public DateTime CreatedOn => _setTOC.CreationTime;

    public string CreatedOnFormatted => _setTOC.CreationTime.ToString("G");

    public bool IsIncremental => _setTOC.Incremental;

    public string IncrementalDisplay => _setTOC.Incremental ? "Yes" : "No";

    public string BlockSize => Helpers.BytesToString(_setTOC.BlockSize);

    public string HashAlgorithm => _setTOC.HashAlgorithm.ToString();

    public int Volume => _setTOC.Volume;

    public bool ContinuedFromPrevVolume => _setTOC.ContinuedFromPrevVolume;

    /// <summary>
    /// Total logical file size for this backup set (sum of all file lengths).
    /// </summary>
    public long TotalSize => _setTOC.Sum(tfi => tfi.FileDescr.Length);

    /// <summary>
    /// Total logical file size for this backup set, formatted.
    /// </summary>
    public string TotalSizeFormatted => Helpers.BytesToString(TotalSize);

    public TapeSetTOC SetTOC => _setTOC;

    /// <summary>
    /// Whether this backup set is checked for restore/validate/verify operations.
    /// Tri-state: <c>true</c> = all files checked, <c>false</c> = unchecked,
    ///  <c>null</c> = partially checked (some files selected). Sets that have never
    ///  been navigated to can only be <c>false</c> or <c>true</c>; the indeterminate
    ///  state arises only when a <see cref="BackupSetView"/> with partial file
    ///  selection exists.
    /// </summary>
    private bool? _isCheckedForRestore = false;
    public bool? IsCheckedForRestore
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

    /// <summary>
    /// Whether this item has a partial file selection (some but not all files checked).
    ///  Derived from <see cref="CheckedFileCount"/> vs <see cref="FileCount"/>.
    ///  Used in RestoreWindow to enable 3-state checkbox cycling so the user can
    ///  toggle between all files, no files, and the original partial selection.
    /// </summary>
    public bool HasPartialSelection => _checkedFileCount is > 0 and var c && c < FileCount;

    public event PropertyChangedEventHandler? PropertyChanged;
}