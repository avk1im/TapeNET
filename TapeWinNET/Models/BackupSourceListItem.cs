using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Windows.Win32.System.SystemServices; // for Helpers

using TapeLibNET;

namespace TapeWinNET.Models;

/// <summary>
/// Display model for a single source entry in the New Backup Set "Folders" pane.
/// Mirrors <see cref="BackupSetListItem"/> for the Restore workflow:
/// <list type="bullet">
///   <item>Tri-state <see cref="IsCheckedForBackup"/>: all / partial / none.</item>
///   <item>File count, selected count, and selected size columns.</item>
///   <item>Icons from <see cref="BackupSourceIcons"/>.</item>
/// </list>
/// Counts and checked state are pushed externally by the owning view model
///  after scans or file-level check changes complete.
/// </summary>
public class BackupSourceListItem(BackupSourceEntry entry) : INotifyPropertyChanged
{
    private readonly BackupSourceEntry _entry = entry;

    // Scan state
    private int _fileCount;          // 0 = not scanned or empty
    private bool _isScanning;

    // Checked / selected state — pushed by the view model
    private int _selectedFileCount;
    private long _selectedSize;
    private bool? _isCheckedForBackup = true; // default: all files checked

    /// <summary>The underlying source entry (path/pattern + type).</summary>
    public BackupSourceEntry Entry => _entry;

    /// <summary>Display path/pattern.</summary>
    public string Pattern => _entry.Pattern;

    /// <summary>Source type (file, folder, pattern).</summary>
    public BackupSourceType SourceType => _entry.SourceType;

    /// <summary>Icon based on source type.</summary>
    public BitmapSource? Icon => BackupSourceIcons.GetIcon(_entry.SourceType);

    // ─────────────────────────────────────────────────
    //  Scan state
    // ─────────────────────────────────────────────────

    /// <summary>Total number of resolved files for this source.</summary>
    public int FileCount
    {
        get => _fileCount;
        set => SetProperty(ref _fileCount, value,
            [nameof(FileCountFormatted)]);
    }

    /// <summary>Whether this source is currently being scanned.</summary>
    public bool IsScanning
    {
        get => _isScanning;
        set => SetProperty(ref _isScanning, value,
            [nameof(FileCountFormatted)]);
    }

    /// <summary>
    /// Display format for file count: "1,234" or "Scanning…" while active.
    /// </summary>
    public string FileCountFormatted => _isScanning
        ? "Scanning\u2026"
        : _fileCount > 0 ? _fileCount.ToString("N0") : "\u2014";

    // ─────────────────────────────────────────────────
    //  Selected state (mirrors BackupSetListItem)
    // ─────────────────────────────────────────────────

    /// <summary>Number of files checked (selected) for backup.</summary>
    public int SelectedFileCount
    {
        get => _selectedFileCount;
        set => SetProperty(ref _selectedFileCount, value,
            [nameof(SelectedFileCountFormatted)]);
    }

    /// <summary>Total size of selected files.</summary>
    public long SelectedSize
    {
        get => _selectedSize;
        set => SetProperty(ref _selectedSize, value,
            [nameof(SelectedSizeFormatted)]);
    }

    /// <summary>
    /// Display format for the "Selected" column: count or "—" when nothing selected.
    /// </summary>
    public string SelectedFileCountFormatted => _selectedFileCount > 0
        ? _selectedFileCount.ToString("N0") : "\u2014";

    /// <summary>
    /// Display format for the "Size" column: human-readable size of selected files.
    /// </summary>
    public string SelectedSizeFormatted => _selectedSize > 0
        ? Helpers.BytesToString(_selectedSize) : "\u2014";

    // ─────────────────────────────────────────────────
    //  Tri-state checkbox (mirrors BackupSetListItem)
    // ─────────────────────────────────────────────────

    /// <summary>
    /// Tri-state check for this source:
    /// <c>true</c> = all files checked, <c>false</c> = none,
    /// <c>null</c> = partial (some files selected via the Files pane).
    /// </summary>
    public bool? IsCheckedForBackup
    {
        get => _isCheckedForBackup;
        set
        {
            if (_isCheckedForBackup != value)
            {
                _isCheckedForBackup = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Whether this item has a partial file selection (some but not all).
    /// Enables 3-state checkbox cycling in the UI.
    /// </summary>
    public bool HasPartialSelection =>
        _selectedFileCount > 0 && _selectedFileCount < _fileCount;

    // ─────────────────────────────────────────────────
    //  INotifyPropertyChanged
    // ─────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value,
        string[]? alsoNotify = null,
        [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);

        if (alsoNotify is not null)
        {
            foreach (var extra in alsoNotify)
                OnPropertyChanged(extra);
        }

        return true;
    }
}
