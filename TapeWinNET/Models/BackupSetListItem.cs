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
public class BackupSetListItem : INotifyPropertyChanged
{
    private static BitmapSource? _backupSetIcon;
    private static bool _iconLoaded;

    private readonly TapeSetTOC _setTOC;
    private readonly int _setIndex;
    private readonly int _altIndex;
    private readonly bool _isOnCurrentVolume;

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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileCountFormatted)));
            }
        }
    }

    /// <summary>
    /// Number of checked (selected) files, or <c>null</c> when nothing is checked.
    ///  Set externally for future "restore checked" functionality.
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
            }
        }
    }

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