using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

using TapeLibNET;

namespace TapeWinNET.ViewModels;

public enum TreeItemType
{
    Drive,
    Tape,
    BackupSet
}

public class TapeTreeItemViewModel : ViewModelBase
{
    // Cache icons to avoid repeated P/Invoke calls
    private static BitmapSource? _driveIcon;
    private static BitmapSource? _tapeIcon;
    private static BitmapSource? _backupSetIcon;
    private static bool _iconsLoaded;

    private string _displayName = string.Empty;
    private bool _isExpanded;
    private bool _isSelected;
    private TreeItemType _itemType;
    private object? _tag;
    private bool _isOnCurrentVolume = true;
    private bool _isTOCFromFile;
    private bool _isInMemory;

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
            _backupSetIcon = TapeIcons.GetBackupSetIcon(large: false);

            // Freeze the icons for better performance (they won't change)
            _driveIcon?.Freeze();
            _tapeIcon?.Freeze();
            _backupSetIcon?.Freeze();
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
    /// Gets the icon for this tree item based on its type.
    /// </summary>
    public BitmapSource? Icon => ItemType switch
    {
        TreeItemType.Drive => _driveIcon,
        TreeItemType.Tape => _tapeIcon,
        TreeItemType.BackupSet => _backupSetIcon,
        _ => null
    };

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
    /// When true on a Tape item, indicates the TOC was loaded from a file.
    /// Drives warning display: red text, warning prefix in display name.
    /// </summary>
    public bool IsTOCFromFile
    {
        get => _isTOCFromFile;
        set => SetProperty(ref _isTOCFromFile, value);
    }

    /// <summary>
    /// When true on a Tape item, indicates the media is backed by memory streams.
    /// Drives info display: blue text, info suffix in display name.
    /// </summary>
    public bool IsInMemory
    {
        get => _isInMemory;
        set => SetProperty(ref _isInMemory, value);
    }

    /// <summary>
    /// Additional data associated with this item (e.g., set index for BackupSet items)
    /// </summary>
    public object? Tag
    {
        get => _tag;
        set => SetProperty(ref _tag, value);
    }

    public ObservableCollection<TapeTreeItemViewModel> Children { get; } = [];

    public TapeTreeItemViewModel? Parent { get; set; }

    // Convenience properties for specific item types
    public int? SetIndex => ItemType == TreeItemType.BackupSet ? Tag as int? : null;
    public int? VolumeNumber => ItemType == TreeItemType.Tape ? Tag as int? : null;

    public static TapeTreeItemViewModel CreateDriveItem(int driveNumber, string deviceName)
    {
        return new TapeTreeItemViewModel
        {
            DisplayName = $"Drive {driveNumber}: {deviceName}",
            ItemType = TreeItemType.Drive,
            Tag = driveNumber,
            IsExpanded = true
        };
    }

    public static TapeTreeItemViewModel CreateTapeItem(TapeTOC toc, TapeTreeItemViewModel parent,
        string? tocFileName = null, bool isInMemory = false)
    {
        bool fromFile = tocFileName != null;
        string baseName = string.IsNullOrEmpty(toc.Description)
            ? $"Volume #{toc.Volume}"
            : $"Volume #{toc.Volume}: {toc.Description}";

        // Build display name: TOC-from-file warning takes precedence over in-memory info
        string displayName = baseName;
        if (fromFile)
            displayName = $"{baseName}  ⚠ TOC: {tocFileName}";
        else if (isInMemory)
            displayName = $"{baseName}  ℹ In-memory";

        var item = new TapeTreeItemViewModel
        {
            DisplayName = displayName,
            ItemType = TreeItemType.Tape,
            Tag = toc.Volume,
            Parent = parent,
            IsExpanded = true,
            IsTOCFromFile = fromFile,
            IsInMemory = isInMemory
        };
        return item;
    }

    public static TapeTreeItemViewModel CreateBackupSetItem(TapeSetTOC setTOC, int setIndex, int totalSets, TapeTreeItemViewModel parent, bool isOnCurrentVolume)
    {
        var altIndex = -(totalSets - setIndex);
        var description = string.IsNullOrEmpty(setTOC.Description) 
            ? "Unnamed Set" 
            : setTOC.Description;
        
        var item = new TapeTreeItemViewModel
        {
            DisplayName = $"Set #{setIndex} | {altIndex}: {description}",
            ItemType = TreeItemType.BackupSet,
            Tag = setIndex,
            Parent = parent,
            IsOnCurrentVolume = isOnCurrentVolume
        };
        return item;
    }
}