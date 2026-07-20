using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using TapeLibNET;
using Windows.Win32.System.SystemServices;

namespace TapeWinNET.Models;

/// <summary>
/// Represents an entry in the "Files to Backup" list.
/// Can be a single file, a single folder, or a wildcard pattern.
/// </summary>
public class BackupSourceEntry : INotifyPropertyChanged
{
    private int _fileCount = -1;  // -1 = not scanned yet
    private long _totalSize = -1; // -1 = not scanned yet
    private bool _isSelected;

    /// <summary>
    /// Original pattern/path entered by the user.
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// Type of this entry (determined at creation time).
    /// </summary>
    public BackupSourceType SourceType { get; }

    /// <summary>
    /// Icon for display based on source type.
    /// </summary>
    public BitmapSource? Icon => BackupSourceIcons.GetIcon(SourceType);

    /// <summary>
    /// Number of files that match this entry. -1 if not yet scanned.
    /// </summary>
    public int FileCount
    {
        get => _fileCount;
        set
        {
            if (_fileCount != value)
            {
                _fileCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FileCountDisplay));
            }
        }
    }

    /// <summary>
    /// Total size of files that match this entry. -1 if not yet scanned.
    /// </summary>
    public long TotalSize
    {
        get => _totalSize;
        set
        {
            if (_totalSize != value)
            {
                _totalSize = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalSizeDisplay));
            }
        }
    }

    /// <summary>
    /// 1-based set index of the previous backup set this entry was created from.
    /// -1 for entries that are not <see cref="BackupSourceType.FilesFromBackupSet"/>.
    /// </summary>
    public int SourceSetIndex { get; } = -1;

    /// <summary>
    /// Whether this entry is selected in the list.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Display string for file count.
    /// </summary>
    public string FileCountDisplay => _fileCount switch
    {
        -1 => "?",
        1 => "1 file",
        _ => $"{_fileCount:N0} files"
    };

    /// <summary>
    /// Display string for total size.
    /// </summary>
    public string TotalSizeDisplay => _totalSize < 0
        ? "?"
        : Helpers.BytesToString(_totalSize);

    /// <summary>
    /// Creates a new BackupSourceEntry with automatic type detection.
    /// Uses the same logic as TapeFileBackupAgent.BuildFileNameList().
    /// </summary>
    public static BackupSourceEntry Create(string pattern)
    {
        var type = DetermineSourceType(pattern);
        return new BackupSourceEntry(pattern, type);
    }

    /// <summary>
    /// Creates a source entry representing all current-disk files belonging to a
    /// previous backup set. Only the set index is captured; files are resolved
    /// directly from the TOC on demand (see <see cref="BackupSourceView.EnumerateFiles"/>).
    /// </summary>
    /// <param name="setIndex">1-based set index within the current TOC.</param>
    /// <param name="displayText">Set description -- the display pattern.</param>
    public static BackupSourceEntry CreateFromBackupSet(int setIndex, string displayText)
    {
        return new BackupSourceEntry("From set " + displayText, BackupSourceType.FilesFromBackupSet, setIndex);
    }

    /// <summary>
    /// Determines the source type using the same logic as TapeFileBackupAgent.
    /// </summary>
    private static BackupSourceType DetermineSourceType(string pattern)
    {
        // Check for wildcards first (same as TapeFileBackupAgent.HasWildcards)
        if (TapeFileBackupAgent.HasWildcards(pattern))
            return BackupSourceType.FilePattern;

        // Check if it's a directory (same as TapeFileBackupAgent.IsDirectory)
        if (TapeFileBackupAgent.IsDirectory(pattern))
            return BackupSourceType.SingleFolder;

        // Otherwise it's a single file
        return BackupSourceType.SingleFile;
    }

    private BackupSourceEntry(string pattern, BackupSourceType type, int sourceSetIndex = -1)
    {
        Pattern = pattern;
        SourceType = type;
        SourceSetIndex = sourceSetIndex;
    }

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion
}