using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;

using Windows.Win32.System.SystemServices;

using TapeLibNET;
using TapeWinNET.Utils;


namespace TapeWinNET.Models;

/// <summary>
/// Thin WPF binding proxy for a single file row. When an <see cref="Owner"/> is
///  provided, <see cref="IsCheckedForRestore"/> delegates to
///  <see cref="FilteredFileList.IsChecked"/>/<see cref="FilteredFileList.SetChecked(TapeFileInfo, bool)"/>,
///  keeping checked state centralized. Without an owner (e.g. in RestoreViewModel
///  file mode), it falls back to a local field.
/// </summary>
public class FileListItem(FilteredFileList? owner, TapeFileInfo fileInfo, bool showFullPath) : INotifyPropertyChanged
{
    private static BitmapSource? _fileIcon;
    private static bool _iconLoaded;

    private static readonly PropertyChangedEventArgs s_isCheckedArgs = new(nameof(IsCheckedForRestore));

    private readonly FilteredFileList? _owner = owner;
    private readonly TapeFileInfo _fileInfo = fileInfo;
    private readonly bool _showFullPath = showFullPath;

    // Standalone fallback when no owner is set
    private bool _isCheckedForRestore;

    static FileListItem()
    {
        LoadIcon();
    }

    private static void LoadIcon()
    {
        if (_iconLoaded)
            return;

        try
        {
            _fileIcon = TapeIcons.GetTapeFileIcon(large: false);
            _fileIcon?.Freeze();
        }
        catch
        {
            // If icon loading fails, we'll just have no icon
        }

        _iconLoaded = true;
    }

    /// <summary>
    /// Gets the icon for file items.
    /// </summary>
    public static BitmapSource? Icon => _fileIcon;

    public string FileName => _showFullPath 
        ? _fileInfo.FileDescr.FullName 
        : Path.GetFileName(_fileInfo.FileDescr.FullName);

    public string FullPath => _fileInfo.FileDescr.FullName;

    public long Size => _fileInfo.FileDescr.Length;

    public string SizeFormatted => Helpers.BytesToString(_fileInfo.FileDescr.Length);

    public DateTime LastModified => _fileInfo.FileDescr.LastWriteTime;

    public long Block => _fileInfo.Block;

    public TapeFileInfo FileInfo => _fileInfo;

    /// <summary>
    /// Whether this file is checked for restore. When an <see cref="Owner"/> is set,
    ///  delegates to the centralized <see cref="FilteredFileList"/> checked state.
    /// </summary>
    public bool IsCheckedForRestore
    {
        get => _owner?.IsChecked(_fileInfo) ?? _isCheckedForRestore;
        set
        {
            if (_owner is not null)
            {
                _owner.SetChecked(_fileInfo, value);
                // Notify the binding so the CheckBox visual updates immediately
                //  (e.g. when the row is clicked outside the checkbox cell).
                NotifyIsCheckedChanged();
            }
            else if (_isCheckedForRestore != value)
            {
                _isCheckedForRestore = value;
                PropertyChanged?.Invoke(this, s_isCheckedArgs);
            }
        }
    }

    /// <summary>The owning <see cref="FilteredFileList"/>, if any.</summary>
    public FilteredFileList? Owner => _owner;

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for <see cref="IsCheckedForRestore"/>.
    /// Called externally by <see cref="FilteredFileList.CheckedChanged"/> or
    ///  bulk operations that need to refresh the binding.
    /// </summary>
    public void NotifyIsCheckedChanged()
    {
        PropertyChanged?.Invoke(this, s_isCheckedArgs);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}