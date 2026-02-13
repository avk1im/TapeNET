using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using TapeLibNET.Virtual;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Represents a block size option for the ComboBox.
/// </summary>
public record BlockSizeOption(uint Bytes, string Display)
{
    public override string ToString() => Display;
    
    public static BlockSizeOption[] All { get; } =
    [
        new(2, "2 B"),
        new(64, "64 B"),
        new(256, "256 B"),
        new(512, "512 B"),
        new(1024, "1 KB"),
        new(2 * 1024, "2 KB"),
        new(4 * 1024, "4 KB"),
        new(8 * 1024, "8 KB"),
        new(16 * 1024, "16 KB"),
        new(32 * 1024, "32 KB"),
        new(64 * 1024, "64 KB"),
        new(128 * 1024, "128 KB"),
        new(256 * 1024, "256 KB"),
    ];
    
    public static BlockSizeOption FromBytes(uint bytes) => 
        All.FirstOrDefault(b => b.Bytes == bytes) ?? All[0];
}

/// <summary>
/// Represents a capacity unit for the ComboBox.
/// </summary>
public record CapacityUnit(string Name, long Multiplier)
{
    public override string ToString() => Name;
    
    public static CapacityUnit[] All { get; } =
    [
        new("B", 1),
        new("KB", 1024),
        new("MB", 1024 * 1024),
        new("GB", 1024L * 1024 * 1024),
    ];
}

/// <summary>
/// Represents a preset configuration.
/// </summary>
public record PresetOption(string Name, VirtualTapeDriveCapabilities Capabilities)
{
    public override string ToString() => Name;
    
    public static PresetOption[] All { get; } =
    [
        new("Basic", VirtualTapeDriveCapabilities.Basic),
        new("With Setmarks", VirtualTapeDriveCapabilities.WithSetmarks),
        new("With Seq. Filemarks", VirtualTapeDriveCapabilities.WithSeqFilemarks),
        new("With Partitions", VirtualTapeDriveCapabilities.WithPartitions),
        new("Full Featured", VirtualTapeDriveCapabilities.FullFeatured),
    ];
}

public class OpenVirtualDriveViewModel : ViewModelBase
{
    private string _contentFilePath = string.Empty;
    private string _initiatorFilePath = string.Empty;
    private bool _enableInitiatorPartition;
    
    private BlockSizeOption _minBlockSize = BlockSizeOption.FromBytes(512);
    private BlockSizeOption _defaultBlockSize = BlockSizeOption.FromBytes(16 * 1024);
    private BlockSizeOption _maxBlockSize = BlockSizeOption.FromBytes(64 * 1024);
    
    private string _contentCapacityValue = "500";
    private CapacityUnit _contentCapacityUnit = CapacityUnit.All[2]; // MB
    private string _initiatorCapacityValue = "24";
    private CapacityUnit _initiatorCapacityUnit = CapacityUnit.All[2]; // MB
    
    private bool _supportsSetmarks = true;
    private bool _supportsSeqFilemarks;
    private bool _supportsCompression;
    
    private PresetOption _selectedPreset = PresetOption.All[1]; // WithSetmarks

    private readonly Action<VirtualTapeDriveCapabilities, string, string?> _onOpen;
    private readonly Action _onCancel;

    public OpenVirtualDriveViewModel(
        Action<VirtualTapeDriveCapabilities, string, string?> onOpen,
        Action onCancel)
    {
        _onOpen = onOpen;
        _onCancel = onCancel;

        BrowseContentFileCommand = new RelayCommand(BrowseContentFile);
        BrowseInitiatorFileCommand = new RelayCommand(BrowseInitiatorFile, _ => EnableInitiatorPartition);
        ApplyPresetCommand = new RelayCommand(ApplyPreset);
        OpenCommand = new RelayCommand(ExecuteOpen, _ => CanOpen);
        CancelCommand = new RelayCommand(_ => _onCancel());
    }

    #region File Paths

    public string ContentFilePath
    {
        get => _contentFilePath;
        set
        {
            if (SetProperty(ref _contentFilePath, value))
                OnPropertyChanged(nameof(CanOpen));
        }
    }

    public string InitiatorFilePath
    {
        get => _initiatorFilePath;
        set => SetProperty(ref _initiatorFilePath, value);
    }

    public bool EnableInitiatorPartition
    {
        get => _enableInitiatorPartition;
        set
        {
            if (SetProperty(ref _enableInitiatorPartition, value))
            {
                OnPropertyChanged(nameof(IsInitiatorFileEnabled));
                OnPropertyChanged(nameof(IsInitiatorCapacityEnabled));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsInitiatorFileEnabled => EnableInitiatorPartition;
    public bool IsInitiatorCapacityEnabled => EnableInitiatorPartition;

    #endregion

    #region Block Sizes

    public ObservableCollection<BlockSizeOption> BlockSizeOptions { get; } = new(BlockSizeOption.All);

    public BlockSizeOption MinBlockSize
    {
        get => _minBlockSize;
        set
        {
            if (SetProperty(ref _minBlockSize, value))
            {
                // Auto-adjust: ensure min <= default <= max
                if (DefaultBlockSize.Bytes < value.Bytes)
                    DefaultBlockSize = value;
                if (MaxBlockSize.Bytes < value.Bytes)
                    MaxBlockSize = value;
            }
        }
    }

    public BlockSizeOption DefaultBlockSize
    {
        get => _defaultBlockSize;
        set
        {
            if (SetProperty(ref _defaultBlockSize, value))
            {
                // Auto-adjust: ensure min <= default <= max
                if (MinBlockSize.Bytes > value.Bytes)
                    MinBlockSize = value;
                if (MaxBlockSize.Bytes < value.Bytes)
                    MaxBlockSize = value;
            }
        }
    }

    public BlockSizeOption MaxBlockSize
    {
        get => _maxBlockSize;
        set
        {
            if (SetProperty(ref _maxBlockSize, value))
            {
                // Auto-adjust: ensure min <= default <= max
                if (DefaultBlockSize.Bytes > value.Bytes)
                    DefaultBlockSize = value;
                if (MinBlockSize.Bytes > value.Bytes)
                    MinBlockSize = value;
            }
        }
    }

    #endregion

    #region Capacity

    public ObservableCollection<CapacityUnit> CapacityUnits { get; } = new(CapacityUnit.All);

    public string ContentCapacityValue
    {
        get => _contentCapacityValue;
        set
        {
            if (SetProperty(ref _contentCapacityValue, value))
            {
                OnPropertyChanged(nameof(ContentCapacityBytes));
                OnPropertyChanged(nameof(ContentCapacityBytesDisplay));
                OnPropertyChanged(nameof(IsContentCapacityValid));
                OnPropertyChanged(nameof(CanOpen));
            }
        }
    }

    public CapacityUnit ContentCapacityUnit
    {
        get => _contentCapacityUnit;
        set
        {
            if (SetProperty(ref _contentCapacityUnit, value))
            {
                OnPropertyChanged(nameof(ContentCapacityBytes));
                OnPropertyChanged(nameof(ContentCapacityBytesDisplay));
            }
        }
    }

    public long ContentCapacityBytes => 
        long.TryParse(ContentCapacityValue, out var val) ? val * ContentCapacityUnit.Multiplier : 0;
    
    public string ContentCapacityBytesDisplay => 
        IsContentCapacityValid ? $"= {ContentCapacityBytes:N0} bytes" : "Invalid";

    public bool IsContentCapacityValid => 
        long.TryParse(ContentCapacityValue, out var val) && val > 0;

    public string InitiatorCapacityValue
    {
        get => _initiatorCapacityValue;
        set
        {
            if (SetProperty(ref _initiatorCapacityValue, value))
            {
                OnPropertyChanged(nameof(InitiatorCapacityBytes));
                OnPropertyChanged(nameof(InitiatorCapacityBytesDisplay));
                OnPropertyChanged(nameof(IsInitiatorCapacityValid));
            }
        }
    }

    public CapacityUnit InitiatorCapacityUnit
    {
        get => _initiatorCapacityUnit;
        set
        {
            if (SetProperty(ref _initiatorCapacityUnit, value))
            {
                OnPropertyChanged(nameof(InitiatorCapacityBytes));
                OnPropertyChanged(nameof(InitiatorCapacityBytesDisplay));
            }
        }
    }

    public long InitiatorCapacityBytes => 
        long.TryParse(InitiatorCapacityValue, out var val) ? val * InitiatorCapacityUnit.Multiplier : 0;

    public string InitiatorCapacityBytesDisplay => 
        IsInitiatorCapacityValid ? $"= {InitiatorCapacityBytes:N0} bytes" : "Invalid";

    public bool IsInitiatorCapacityValid => 
        long.TryParse(InitiatorCapacityValue, out var val) && val > 0;

    #endregion

    #region Features

    public bool SupportsSetmarks
    {
        get => _supportsSetmarks;
        set => SetProperty(ref _supportsSetmarks, value);
    }

    public bool SupportsSeqFilemarks
    {
        get => _supportsSeqFilemarks;
        set => SetProperty(ref _supportsSeqFilemarks, value);
    }

    public bool SupportsCompression
    {
        get => _supportsCompression;
        set => SetProperty(ref _supportsCompression, value);
    }

    #endregion

    #region Presets

    public ObservableCollection<PresetOption> Presets { get; } = new(PresetOption.All);

    public PresetOption SelectedPreset
    {
        get => _selectedPreset;
        set => SetProperty(ref _selectedPreset, value);
    }

    #endregion

    #region Commands

    public ICommand BrowseContentFileCommand { get; }
    public ICommand BrowseInitiatorFileCommand { get; }
    public ICommand ApplyPresetCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand CancelCommand { get; }

    #endregion

    #region Validation

    public bool CanOpen =>
        !string.IsNullOrWhiteSpace(ContentFilePath) &&
        IsContentCapacityValid &&
        (!EnableInitiatorPartition || IsInitiatorCapacityValid);

    #endregion

    #region Command Handlers

    private void BrowseContentFile(object? _)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Content Partition File",
            Filter = "Virtual Tape Files (*.vt)|*.vt|All Files (*.*)|*.*",
            CheckFileExists = false
        };

        if (dialog.ShowDialog() == true)
            ContentFilePath = dialog.FileName;
    }

    private void BrowseInitiatorFile(object? _)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Initiator Partition File",
            Filter = "Virtual Tape Files (*.vt)|*.vt|All Files (*.*)|*.*",
            CheckFileExists = false
        };

        if (dialog.ShowDialog() == true)
            InitiatorFilePath = dialog.FileName;
    }

    private void ApplyPreset(object? _)
    {
        var caps = SelectedPreset.Capabilities;

        MinBlockSize = BlockSizeOption.FromBytes(caps.MinBlockSize);
        DefaultBlockSize = BlockSizeOption.FromBytes(caps.DefaultBlockSize);
        MaxBlockSize = BlockSizeOption.FromBytes(caps.MaxBlockSize);

        SupportsSetmarks = caps.SupportsSetmarks;
        SupportsSeqFilemarks = caps.SupportsSeqFilemarks;
        SupportsCompression = caps.SupportsCompression;
        EnableInitiatorPartition = caps.SupportsInitiatorPartition;

        // Convert capacity to appropriate unit
        SetCapacityFromBytes(caps.Capacity, v => ContentCapacityValue = v, u => ContentCapacityUnit = u);
        SetCapacityFromBytes(caps.InitiatorPartitionCapacity, v => InitiatorCapacityValue = v, u => InitiatorCapacityUnit = u);
    }

    private static void SetCapacityFromBytes(long bytes, Action<string> setValue, Action<CapacityUnit> setUnit)
    {
        if (bytes >= 1024L * 1024 * 1024 && bytes % (1024L * 1024 * 1024) == 0)
        {
            setValue((bytes / (1024L * 1024 * 1024)).ToString());
            setUnit(CapacityUnit.All[3]); // GB
        }
        else if (bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0)
        {
            setValue((bytes / (1024 * 1024)).ToString());
            setUnit(CapacityUnit.All[2]); // MB
        }
        else if (bytes >= 1024 && bytes % 1024 == 0)
        {
            setValue((bytes / 1024).ToString());
            setUnit(CapacityUnit.All[1]); // KB
        }
        else
        {
            setValue(bytes.ToString());
            setUnit(CapacityUnit.All[0]); // B
        }
    }

    private void ExecuteOpen(object? _)
    {
        var capabilities = new VirtualTapeDriveCapabilities
        {
            MinBlockSize = MinBlockSize.Bytes,
            DefaultBlockSize = DefaultBlockSize.Bytes,
            MaxBlockSize = MaxBlockSize.Bytes,
            SupportsSetmarks = SupportsSetmarks,
            SupportsSeqFilemarks = SupportsSeqFilemarks,
            SupportsInitiatorPartition = EnableInitiatorPartition,
            SupportsCompression = SupportsCompression,
            Capacity = ContentCapacityBytes,
            InitiatorPartitionCapacity = EnableInitiatorPartition ? InitiatorCapacityBytes : 0
        };

        string? initiatorPath = EnableInitiatorPartition && !string.IsNullOrWhiteSpace(InitiatorFilePath)
            ? InitiatorFilePath
            : null;

        _onOpen(capabilities, ContentFilePath, initiatorPath);
    }

    #endregion
}