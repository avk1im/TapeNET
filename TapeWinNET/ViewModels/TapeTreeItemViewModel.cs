using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TapeLibNET.Headers;
using TapeLibNET.Toc;

namespace TapeWinNET.ViewModels;

public enum TreeItemType
{
    Drive,
    Tape,
    BackupSet,
    /// <summary>A calibration cartridge (no backup TOC) identified from its BOM header.</summary>
    CalibrationCartridge
}

public class TapeTreeItemViewModel : ViewModelBase
{
    // Cache icons to avoid repeated P/Invoke calls
    private static BitmapSource? _driveIcon;
    private static BitmapSource? _tapeIcon;
    // Reuses the regular media icon for now; kept as its own field so a distinct icon can be
    //  composed later without touching the Icon switch below.
    private static BitmapSource? _calibrationIcon;
    private static bool _iconsLoaded;

    private string _displayName = string.Empty;
    private bool _isExpanded;
    private bool _isSelected;
    private TreeItemType _itemType;
    private int _tag;
    private bool _isOnCurrentVolume = true;
    private bool _isTOCImported;
    private bool _isInMemory;
    private bool _isRemote;
    private string? _remoteHost;
    private bool _isEncrypted;
    private bool _isLocked;

    static TapeTreeItemViewModel()
    {
        LoadIcons();
    }

    private static void LoadIcons()
    {
        if (_iconsLoaded)
            return;

        try
        {
            _driveIcon = TapeIcons.GetTapeDriveIcon(large: false);
            _tapeIcon = TapeIcons.GetTapeMediaIcon(large: false);
            _calibrationIcon = TapeIcons.GetTapeMediaIcon(large: false);

            // Freeze the icons for better performance (they won't change)
            _driveIcon?.Freeze();
            _tapeIcon?.Freeze();
            _calibrationIcon?.Freeze();
        }
        catch
        {
            // If icon loading fails, we'll just have no icons
        }

        _iconsLoaded = true;
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string IndexDisplay { get; private set; } = string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public TreeItemType ItemType
    {
        get => _itemType;
        set
        {
            if (SetProperty(ref _itemType, value))
            {
                OnPropertyChanged(nameof(Icon));
            }
        }
    }

    /// <summary>
    /// Gets the icon for this tree item based on its type — and, for a backup set, its encryption state.
    /// </summary>
    public ImageSource? Icon => ItemType switch
    {
        TreeItemType.Drive => _driveIcon,
        TreeItemType.Tape => _tapeIcon,
        TreeItemType.BackupSet => !_isEncrypted ? TapeIcons.BackupSetIconSmall
            : _isLocked ? TapeIcons.LockedBackupSetIconSmall
            : TapeIcons.UnlockedBackupSetIconSmall,
        TreeItemType.CalibrationCartridge => _calibrationIcon,
        _ => null
    };

    /// <summary>A backup set that is encrypted (locked or not).</summary>
    public bool IsEncrypted
    {
        get => _isEncrypted;
        private set
        {
            if (SetProperty(ref _isEncrypted, value))
                OnPropertyChanged(nameof(Icon));
        }
    }

    /// <summary>
    /// A backup set that is encrypted and locked: shown grey italic with a padlock (Design-Encryption §11.1.1) — normal,
    ///  never a warning colour.
    /// </summary>
    public bool IsLocked
    {
        get => _isLocked;
        private set
        {
            if (SetProperty(ref _isLocked, value))
                OnPropertyChanged(nameof(Icon));
        }
    }

    /// <summary>
    /// Whether this backup set resides on the currently loaded volume.
    /// Used to dim icons of sets from previous volumes.
    /// </summary>
    public bool IsOnCurrentVolume
    {
        get => _isOnCurrentVolume;
        set => SetProperty(ref _isOnCurrentVolume, value);
    }

    /// <summary>
    /// When true on a Media item, indicates the TOC was loaded from a file.
    /// Drives warning display: red text, warning prefix in display name.
    /// </summary>
    public bool IsTOCImported
    {
        get => _isTOCImported;
        set => SetProperty(ref _isTOCImported, value);
    }

    /// <summary>
    /// When true on a Media item, indicates the media is backed by memory streams.
    /// Drives info display: blue text, info suffix in display name.
    /// </summary>
    public bool IsInMemory
    {
        get => _isInMemory;
        set => SetProperty(ref _isInMemory, value);
    }

    /// <summary>
    /// When true on a Drive item, indicates the drive is a remote drive.
    /// Drives the green foreground trigger in the tree view.
    /// </summary>
    public bool IsRemote
    {
        get => _isRemote;
        set => SetProperty(ref _isRemote, value);
    }

    /// <summary>
    /// The display label of the remote host (e.g. "192.168.178.22:50551") for
    ///  tooltip display and drive display name prefixing. Null for local drives.
    /// </summary>
    public string? RemoteHost
    {
        get => _remoteHost;
        set => SetProperty(ref _remoteHost, value);
    }

    /// <summary>
    /// Numeric identifier for this item: drive number (Drive), volume number (Media),
    ///  or 1-based set index (BackupSet).
    /// </summary>
    public int Tag
    {
        get => _tag;
        set => SetProperty(ref _tag, value);
    }

    public ObservableCollection<TapeTreeItemViewModel> Children { get; } = [];

    public TapeTreeItemViewModel? Parent { get; set; }

    // Convenience properties for specific item types

    /// <summary>The set a backup-set node shows — to find it again after a rebuild, whatever its index now.</summary>
    public TapeSetTOC? SetTOC { get; private set; }
    
    public int? SetIndex => ItemType == TreeItemType.BackupSet ? Tag : null;
    public int? VolumeNumber => ItemType == TreeItemType.Tape ? Tag : null;

    public static TapeTreeItemViewModel CreateDriveItem(int driveNumber, string deviceName,
        string? remoteHost = null)
    {
        // Prefix display name with remote host label when applicable (§2.6)
        string displayName = remoteHost != null
            ? $"[{remoteHost}] Drive {driveNumber}: {deviceName}"
            : $"Drive {driveNumber}: {deviceName}";

        return new TapeTreeItemViewModel
        {
            DisplayName = displayName,
            IndexDisplay = string.Empty,
            ItemType = TreeItemType.Drive,
            Tag = driveNumber,
            IsExpanded = true,
            IsRemote = remoteHost != null,
            RemoteHost = remoteHost
        };
    }

    public static TapeTreeItemViewModel CreateTapeItem(TapeTOC toc, TapeTreeItemViewModel parent,
        string? tocFileName = null, bool isInMemory = false, bool tocRecovered = false)
    {
        bool fromFile = tocFileName != null;
        string baseName = string.IsNullOrEmpty(toc.Description)
            ? $"Volume #{toc.Volume}"
            : $"Volume #{toc.Volume}: {toc.Description}";

        // Build display name: TOC-from-file warning takes precedence over in-memory info
        string displayName = baseName;
        if (fromFile)
            displayName = $"{baseName}  ⚠ TOC: {tocFileName}";
        else if (tocRecovered)
            displayName = $"{baseName}  ⚠ [recovered TOC]";
        else if (isInMemory)
            displayName = $"{baseName}  ℹ In-memory";

        var item = new TapeTreeItemViewModel
        {
            DisplayName = displayName,
            IndexDisplay = string.Empty,
            ItemType = TreeItemType.Tape,
            Tag = toc.Volume,
            Parent = parent,
            IsExpanded = true,
            IsTOCImported = fromFile || tocRecovered, // a recovered TOC gets the same warning foreground
            IsInMemory = isInMemory
        };

        return item;
    }

    /// <summary>
    /// Creates the tree node for a calibration cartridge (no backup TOC) identified from its BOM header.
    /// </summary>
    public static TapeTreeItemViewModel CreateCalibrationItem(TapeCalibrationHeader header, TapeTreeItemViewModel parent)
    {
        return new TapeTreeItemViewModel
        {
            DisplayName = $"Calibration Cartridge >{header.ProfileKey}<",
            IndexDisplay = string.Empty,
            ItemType = TreeItemType.CalibrationCartridge,
            Tag = 0,
            Parent = parent,
            IsExpanded = false,
        };
    }

    public static TapeTreeItemViewModel CreateBackupSetItem(TapeTOC toc, int setIndex, TapeTreeItemViewModel parent)
    {
        var setTOC = toc[setIndex];
        var altIndex = toc.SetIndexToAlt(setIndex);
        var indexDisplay = $"#{setIndex} | {altIndex}";

        var item = new TapeTreeItemViewModel
        {
            IndexDisplay = indexDisplay,
            ItemType = TreeItemType.BackupSet,
            Tag = setIndex,
            Parent = parent,
            IsOnCurrentVolume = setTOC.Volume == toc.Volume
        };
        item.RefreshFromSet(setTOC);
        return item;
    }

    /// <summary>
    /// Re-reads name and encryption state from <paramref name="setTOC"/> — after an unlock, a lock or a rename.
    ///  A locked set shows <see cref="TapeSetTOC.DisplayName"/>, never its hidden description.
    /// </summary>
    public void RefreshFromSet(TapeSetTOC setTOC)
    {
        SetTOC = setTOC;
        IsEncrypted = setTOC.IsEncrypted;
        IsLocked = setTOC.IsLocked;
        string description = setTOC.IsLocked ? $"\U0001F512 {setTOC.DisplayName}"
            : string.IsNullOrEmpty(setTOC.Description) ? "[Unnamed Set]"
            : setTOC.Description;
        // Display format: "1 | -2" for set 1 of 3
        DisplayName = $"Set {IndexDisplay}: {description}";
    }
}
