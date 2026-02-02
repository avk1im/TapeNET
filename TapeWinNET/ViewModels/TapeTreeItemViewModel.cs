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

    public static TapeTreeItemViewModel CreateTapeItem(TapeTOC toc, TapeTreeItemViewModel parent)
    {
        var item = new TapeTreeItemViewModel
        {
            DisplayName = string.IsNullOrEmpty(toc.Description) 
                ? $"Volume #{toc.Volume}" 
                : $"Volume #{toc.Volume}: {toc.Description}",
            ItemType = TreeItemType.Tape,
            Tag = toc.Volume,
            Parent = parent,
            IsExpanded = true
        };
        return item;
    }

    public static TapeTreeItemViewModel CreateBackupSetItem(TapeSetTOC setTOC, int setIndex, int totalSets, TapeTreeItemViewModel parent)
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
            Parent = parent
        };
        return item;
    }
}