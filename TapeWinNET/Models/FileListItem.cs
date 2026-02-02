using System.IO;
using System.Windows.Media.Imaging;

using Windows.Win32.System.SystemServices;

using TapeLibNET;


namespace TapeWinNET.Models;

public class FileListItem(TapeFileInfo fileInfo, bool showFullPath)
{
    private static BitmapSource? _fileIcon;
    private static bool _iconLoaded;

    private readonly TapeFileInfo _fileInfo = fileInfo;
    private readonly bool _showFullPath = showFullPath;

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
    public BitmapSource? Icon => _fileIcon;

    public string FileName => _showFullPath 
        ? _fileInfo.FileDescr.FullName 
        : Path.GetFileName(_fileInfo.FileDescr.FullName);

    public string FullPath => _fileInfo.FileDescr.FullName;

    public long Size => _fileInfo.FileDescr.Length;

    public string SizeFormatted => Helpers.BytesToString(_fileInfo.FileDescr.Length);

    public DateTime LastModified => _fileInfo.FileDescr.LastWriteTime;

    public long Block => _fileInfo.Block;

    public TapeFileInfo FileInfo => _fileInfo;
}