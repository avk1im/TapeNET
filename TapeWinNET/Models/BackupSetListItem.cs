using System.Windows.Media.Imaging;
using TapeLibNET;
using Windows.Win32.System.SystemServices;

namespace TapeWinNET.Models;

/// <summary>
/// Represents a backup set item for display in the backup sets ListView.
/// </summary>
public class BackupSetListItem(TapeSetTOC setTOC, int setIndex, int altIndex)
{
    private static BitmapSource? _backupSetIcon;
    private static bool _iconLoaded;

    private readonly TapeSetTOC _setTOC = setTOC;
    private readonly int _setIndex = setIndex;
    private readonly int _altIndex = altIndex;

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
    public BitmapSource? Icon => _backupSetIcon;

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

    public string FileCountFormatted => _setTOC.Count.ToString("N0");

    public DateTime CreatedOn => _setTOC.CreationTime;

    public string CreatedOnFormatted => _setTOC.CreationTime.ToString("G");

    public bool IsIncremental => _setTOC.Incremental;

    public string IncrementalDisplay => _setTOC.Incremental ? "Yes" : "No";

    public string BlockSize => Helpers.BytesToString(_setTOC.BlockSize);

    public string HashAlgorithm => _setTOC.HashAlgorithm.ToString();

    public int Volume => _setTOC.Volume;

    public TapeSetTOC SetTOC => _setTOC;
}