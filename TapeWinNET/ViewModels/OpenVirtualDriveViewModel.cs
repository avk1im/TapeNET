using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;

using TapeLibNET.Virtual;
using TapeLibNET.Services;
using TapeWinNET.Converters;
using TapeWinNET.Models;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Represents a block size option for the ComboBox.
/// </summary>
public record BlockSizeOption(uint Bytes, string Display)
{
    public override string ToString() => Display;

    public static BlockSizeOption[] All { get; } =
    [
        new(2, "2 B"), // indeed supported by Sony AIT! FIXME: Try to use it, see if we can handle it!
        new(256, "256 B"),
        new(512, "512 B"),
        new(1024, "1 KB"),
        new(2 * 1024, "2 KB"),
        new(4 * 1024, "4 KB"),
        new(8 * 1024, "8 KB"),
        new(16 * 1024, "16 KB"),
        new(32 * 1024, "32 KB"),
        new(64 * 1024, "64 KB"), // the max supported by known USB-attached drives
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
public record PresetOption(
    string Name, 
    VirtualTapeDriveCapabilities Capabilities,
    long ContentCapacity,
    long InitiatorPartitionCapacity = 0)
{
    public override string ToString() => Name;

    public static PresetOption[] All { get; } =
    [
        new("Basic", VirtualTapeDriveCapabilities.Basic, 100 * 1024 * 1024),
        new("With Setmarks", VirtualTapeDriveCapabilities.WithSetmarks, 500 * 1024 * 1024),
        new("With Seq. Filemarks", VirtualTapeDriveCapabilities.WithSeqFilemarks, 500 * 1024 * 1024),
        new("With Partitions", VirtualTapeDriveCapabilities.WithPartitions, 1024L * 1024 * 1024, 16 * 1024 * 1024),
        new("Full Featured", VirtualTapeDriveCapabilities.FullFeatured, 1024L * 1024 * 1024, 16 * 1024 * 1024),
    ];
}

/// <summary>
/// Represents a simulated IO speed option for the virtual tape drive.
/// BytesPerSecond of 0 means unlimited (no throttling).
/// Each tier carries three rates plus mechanical overhead:
///   - BytesPerSecond: streaming read/write speed
///   - LocateBytesPerSecond: blind seek speed (rewind, seek to block/EOD) — tape at full mechanical speed
///   - SearchBytesPerSecond: mark scanning speed (space filemarks/setmarks) — tape reading at high speed
///   - SeekOverheadMs: fixed per-operation overhead (acceleration + deceleration + settle/servo lock)
/// </summary>
public record IoSpeedOption(long BytesPerSecond, long LocateBytesPerSecond, long SearchBytesPerSecond, int SeekOverheadMs, string Display)
{
    public override string ToString() => Display;

    private const long MB = 1024 * 1024;
    private const long GB = 1024 * 1024 * 1024;
    private const int FuFa1 = 2; // fudge factor to provide more realistic speeds for testing
    private const int FuFa2 = 3; // fudge factor to provide more realistic speeds for testing

    public static IoSpeedOption[] All { get; } =
    [
        new(  6 * MB / FuFa1,    1500 * MB / FuFa2,   36 * MB / FuFa2,  400 * FuFa2, "6 MB/s (AIT-1, DAT-160)"),
        new( 12 * MB / FuFa1,       3 * GB / FuFa2,   72 * MB / FuFa2,  350 * FuFa2, "12 MB/s (AIT-3Ex, DLT-V4)"),
        new( 24 * MB / FuFa1,       5 * GB / FuFa2,  144 * MB / FuFa2,  300 * FuFa2, "24 MB/s (AIT-4/5)"),
        new( 60 * MB / FuFa1,       8 * GB / FuFa2,  300 * MB / FuFa2,  250 * FuFa2, "60 MB/s (LTO-3/4)"),
        new(160 * MB / FuFa1,      18 * GB / FuFa2,  640 * MB / FuFa2,  200 * FuFa2, "160 MB/s (LTO-5/6)"),
        new(400 * MB / FuFa1,      80 * GB / FuFa2, 1200 * MB / FuFa2,  150 * FuFa2, "400 MB/s (LTO-8/9)"),
        new(  0,            0,        0,           0, "Unlimited"),
    ];

    /// <summary>Returns the Unlimited option (default).</summary>
    public static IoSpeedOption Unlimited => All[^1];

    public static IoSpeedOption FromBytesPerSecond(long bytesPerSecond) =>
        All.FirstOrDefault(o => o.BytesPerSecond == bytesPerSecond) ?? Unlimited;
}

/// <summary>
/// Status of the virtual media probe operation.
/// </summary>
public enum VirtualDriveProbeStatus
{
    None,
    Probing,
    ExistingFound,
    NewMedia,
    Error
}

/// <summary>
/// Request data for opening/creating a virtual drive.
/// </summary>
public record VirtualDriveOpenRequest(
    VirtualTapeDriveCapabilities Capabilities,
    VirtualMediaDescriptor Media,
    bool IsCreateNew,
    string? MediaName,
    IoSpeedOption? IoSpeed = null
);

/// <summary>
/// ViewModel for the Open Virtual Drive dialog.
/// Supports both opening existing virtual media and creating new virtual media.
/// </summary>
public class OpenVirtualDriveViewModel : ViewModelBase
{
    #region *** Static Path Helpers ***

    /// <summary>
    /// Builds the metadata file path for the content partition.
    /// Example: "mymedia.vt" → "mymedia.vt.vrt"
    /// </summary>
    public static string BuildContentMetadataFilePath(string contentFilePath)
        => contentFilePath + VirtualTapeDriveBackend.MetadataExtension;

    /// <summary>
    /// Checks the specified content file path and removes the initiator suffix if present, returning the modified path.
    /// Example: "mymedia_init.vt" → "mymedia.vt"
    /// </summary>
    public static string CheckContentFilePath(string contentFilePath)
    {
        // Remove initiator suffix if present
        var nameWithoutExt = Path.GetFileNameWithoutExtension(contentFilePath);
        if (nameWithoutExt.EndsWith(VirtualTapeDriveBackend.InitiatorSuffix))
        {
            var baseName = nameWithoutExt[..^VirtualTapeDriveBackend.InitiatorSuffix.Length];
            var dir = Path.GetDirectoryName(contentFilePath) ?? string.Empty;
            var ext = Path.GetExtension(contentFilePath);
            return Path.Combine(dir, $"{baseName}{ext}");
        }
        return contentFilePath;
    }

    /// <summary>
    /// Builds the initiator partition file path from content file path.
    /// Example: "mymedia.vt" → "mymedia_init.vt"
    /// </summary>
    public static string BuildInitiatorFilePath(string contentFilePath)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(contentFilePath);
        // Check if the file name already ends with the initiator suffix to avoid double-appending it
        if (nameWithoutExt.EndsWith(VirtualTapeDriveBackend.InitiatorSuffix))
            return contentFilePath; // Already has the suffix, return as is
        var dir = Path.GetDirectoryName(contentFilePath) ?? string.Empty;
        var ext = Path.GetExtension(contentFilePath);
        return Path.Combine(dir, $"{nameWithoutExt}{VirtualTapeDriveBackend.InitiatorSuffix}{ext}");
    }

    /// <summary>
    /// Builds the metadata file path for the initiator partition.
    /// Example: "mymedia.vt" → "mymedia_init.vt.vrt"
    /// </summary>
    public static string BuildInitiatorMetadataFilePath(string contentFilePath)
        => BuildInitiatorFilePath(contentFilePath) + VirtualTapeDriveBackend.MetadataExtension;

    /// <summary>
    /// Checks if initiator partition files exist for the given content file path.
    /// </summary>
    public static bool InitiatorFilesExist(string contentFilePath)
    {
        if (string.IsNullOrWhiteSpace(contentFilePath))
            return false;

        var initiatorPath = BuildInitiatorFilePath(contentFilePath);
        var initiatorMetadataPath = BuildInitiatorMetadataFilePath(contentFilePath);

        // Both the data file and metadata file must exist
        return File.Exists(initiatorPath) && File.Exists(initiatorMetadataPath);
    }

    /// <summary>
    /// Decorates a file path with a volume suffix.
    /// Strips any existing volume suffix before applying the new one.
    /// Example: "myvtape.vt" + volume 2 → "myvtape_vol02.vt"
    /// Example: "myvtape_vol01.vt" + volume 2 → "myvtape_vol02.vt"
    /// </summary>
    public static string BuildVolumeFilePath(string path, int volume)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var ext = Path.GetExtension(path);
        var name = Path.GetFileNameWithoutExtension(path);

        // Strip existing volume suffix (e.g. "_vol01", "_vol12")
        var volSuffix = VirtualTapeDriveBackend.VolumeSuffix;
        int volIdx = name.LastIndexOf(volSuffix, StringComparison.OrdinalIgnoreCase);
        if (volIdx >= 0)
        {
            // Verify that everything after the suffix is digits
            var afterSuffix = name[(volIdx + volSuffix.Length)..];
            if (afterSuffix.Length > 0 && afterSuffix.All(char.IsDigit))
                name = name[..volIdx];
        }

        return Path.Combine(dir, $"{name}{volSuffix}{volume:D2}{ext}");
    }

    #endregion

    private readonly Action<VirtualDriveOpenRequest> _onOpen;
    private readonly Action _onCancel;
    private readonly bool _newMediaOnly;
    private readonly bool _existingMediaOnly;

    // Mode selection
    private bool _isOpenExistingMode = true;
    private bool _isInMemory;

    // File paths
    private string _contentFilePath = string.Empty;
    private bool _enableInitiatorPartition;

    // Block sizes
    private BlockSizeOption _minBlockSize = BlockSizeOption.FromBytes(512);
    private BlockSizeOption _defaultBlockSize = BlockSizeOption.FromBytes(16 * 1024);
    private BlockSizeOption _maxBlockSize = BlockSizeOption.FromBytes(64 * 1024);

    // Capacity
    private string _contentCapacityValue = "500";
    private CapacityUnit _contentCapacityUnit = CapacityUnit.All[2]; // MB
    private string _initiatorCapacityValue = "16";
    private CapacityUnit _initiatorCapacityUnit = CapacityUnit.All[2]; // MB

    // Features
    private bool _supportsSetmarks = true;
    private bool _supportsSeqFilemarks;

    // IO Speed
    private IoSpeedOption _selectedIoSpeed = IoSpeedOption.Unlimited;
    private readonly bool _isIoSpeedLocked;

    // Preset
    private PresetOption _selectedPreset = PresetOption.All[1]; // WithSetmarks

    // Media name (for new media)
    private string _mediaName = $"Virtual media created {DateTime.Now:yyyy-MM-dd HH:mm}";

    // Probe state
    private VirtualDriveProbeStatus _probeStatus = VirtualDriveProbeStatus.None;
    private VirtualDriveProbeResult? _lastProbeResult;
    private CancellationTokenSource? _probeCts;
    private Task? _probeTask;

    public OpenVirtualDriveViewModel(
        Action<VirtualDriveOpenRequest> onOpen,
        Action onCancel,
        VirtualMediaDescriptor? prePopulate = null,
        bool? preferCreateNew = null,
        FileMode mediaMode = FileMode.OpenOrCreate,
        VirtualTapeDriveCapabilities? currentCapabilities = null,
        IoSpeedOption? currentIoSpeed = null)
    {
        _onOpen = onOpen;
        _onCancel = onCancel;
        _newMediaOnly = mediaMode == FileMode.Create;
        _existingMediaOnly = mediaMode == FileMode.Open;

        // In-memory is available in the generic open dialog, or when continuing
        //  from a previous in-memory volume (newMediaOnly mode with InMemory prePopulate)
        _isInMemoryAvailable = !_existingMediaOnly
            && (!_newMediaOnly || (prePopulate?.InMemory ?? false));

        // In newMediaOnly / existingMediaOnly mode, force the mode and pre-populate from current drive
        if (_newMediaOnly || _existingMediaOnly)
        {
            _isOpenExistingMode = _existingMediaOnly;

            if (currentCapabilities.HasValue)
            {
                var caps = currentCapabilities.Value;
                _minBlockSize = BlockSizeOption.FromBytes(caps.MinBlockSize);
                _defaultBlockSize = BlockSizeOption.FromBytes(caps.DefaultBlockSize);
                _maxBlockSize = BlockSizeOption.FromBytes(caps.MaxBlockSize);
                _supportsSetmarks = caps.SupportsSetmarks;
                _supportsSeqFilemarks = caps.SupportsSeqFilemarks;
                _enableInitiatorPartition = caps.SupportsInitiatorPartition;
            }

            // Lock IO speed to the drive's current setting
            if (currentIoSpeed != null)
            {
                _selectedIoSpeed = currentIoSpeed;
                _isIoSpeedLocked = true;
            }
        }

        // Pre-populate from previous media descriptor if provided
        if (prePopulate != null)
        {
            // If previous media was in-memory, pre-select in-memory mode
            if (prePopulate.InMemory)
                _isInMemory = true;

            // Set file path and trigger probe only for file-backed media
            if (!prePopulate.InMemory && !string.IsNullOrWhiteSpace(prePopulate.ContentPath))
            {
                _contentFilePath = CheckContentFilePath(prePopulate.ContentPath);
                // Trigger probe after setting path
                _ = TriggerProbeAsync();
            }

            if (prePopulate.ContentCapacity > 0)
                SetCapacityFromBytes(prePopulate.ContentCapacity, v => _contentCapacityValue = v, u => _contentCapacityUnit = u);

            if (prePopulate.InitiatorPath != null)
                _enableInitiatorPartition = true;

            if (prePopulate.InitiatorPartitionCapacity > 0)
                SetCapacityFromBytes(prePopulate.InitiatorPartitionCapacity, v => _initiatorCapacityValue = v, u => _initiatorCapacityUnit = u);
        }

        // Pre-select mode if specified (ignored in newMediaOnly / existingMediaOnly modes)
        if (!_newMediaOnly && !_existingMediaOnly && preferCreateNew.HasValue)
        {
            _isOpenExistingMode = !preferCreateNew.Value;
        }

        BrowseContentFileCommand = new RelayCommand(BrowseContentFile);
        ApplyPresetCommand = new RelayCommand(ApplyPreset);
        OpenCommand = new RelayCommand(ExecuteOpen, _ => CanExecute);
        CancelCommand = new RelayCommand(_ => _onCancel());
    }

    #region Mode Selection

    /// <summary>Dialog title — changes based on media mode.</summary>
    public string DialogTitle => _newMediaOnly ? "Create New Media Volume" 
        : _existingMediaOnly ? "Load Another Media Volume" : "Open Virtual Drive";

    /// <summary>Whether the "In-memory" checkbox is available (only in generic open dialog).</summary>
    public bool IsInMemoryAvailable => _isInMemoryAvailable;
    private readonly bool _isInMemoryAvailable;

    /// <summary>Whether in-memory mode is selected (no file backing).</summary>
    public bool IsInMemory
    {
        get => _isInMemory;
        set
        {
            if (SetProperty(ref _isInMemory, value))
            {
                if (value)
                {
                    // Force "Create new" mode and cancel any running probe
                    IsCreateNewMode = true;
                    CancelProbe();
                    ProbeStatus = VirtualDriveProbeStatus.None;
                }
                OnPropertyChanged(nameof(IsOpenExistingEnabled));
                OnPropertyChanged(nameof(IsFilePathsEnabled));
                OnPropertyChanged(nameof(CanExecute));
                OnPropertyChanged(nameof(WarningLevel));
                OnPropertyChanged(nameof(WarningMessage));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Whether the mode radio buttons are enabled (disabled in both restricted modes and in-memory mode).</summary>
    public bool IsOpenExistingEnabled => !_newMediaOnly && !_existingMediaOnly && !_isInMemory;

    /// <summary>Whether the File Paths group is enabled (disabled in in-memory mode).</summary>
    public bool IsFilePathsEnabled => !_isInMemory;

    /// <summary>Whether the block size controls are enabled (disabled in restricted modes and in Open existing mode — block sizes are drive-level).</summary>
    public bool IsBlockSizesEnabled => !_newMediaOnly && !_existingMediaOnly && IsCreateNewMode;

    /// <summary>Whether the preset controls are enabled (disabled in restricted modes and in Open existing mode).</summary>
    public bool IsPresetsEnabled => !_newMediaOnly && !_existingMediaOnly && IsCreateNewMode;

    /// <summary>Whether the features controls are enabled (disabled in restricted modes and in Open existing mode).</summary>
    public bool IsFeaturesEnabled => !_newMediaOnly && !_existingMediaOnly && IsCreateNewMode;

    public bool IsOpenExistingMode
    {
        get => _isOpenExistingMode;
        set
        {
            if (SetProperty(ref _isOpenExistingMode, value))
            {
                OnPropertyChanged(nameof(IsCreateNewMode));
                OnPropertyChanged(nameof(IsPresetsEnabled));
                OnPropertyChanged(nameof(IsFeaturesEnabled));
                OnPropertyChanged(nameof(IsBlockSizesEnabled));
                OnPropertyChanged(nameof(ActionButtonText));
                OnPropertyChanged(nameof(WarningLevel));
                OnPropertyChanged(nameof(WarningMessage));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsCreateNewMode
    {
        get => !_isOpenExistingMode;
        set => IsOpenExistingMode = !value;
    }

    #endregion

    #region File Paths

    public string ContentFilePath
    {
        get => _contentFilePath;
        set
        {
            if (SetProperty(ref _contentFilePath, CheckContentFilePath(value)))
            {
                OnPropertyChanged(nameof(CanExecute));
                OnPropertyChanged(nameof(WarningLevel));
                OnPropertyChanged(nameof(WarningMessage));
                OnPropertyChanged(nameof(InitiatorFilePathDisplay));
                // Trigger probe when path changes (with debouncing via Delay in XAML binding)
                _ = TriggerProbeAsync();
            }
        }
    }

    /// <summary>
    /// Gets the computed initiator file path for display purposes.
    /// Read-only - derived from content file path using naming convention.
    /// </summary>
    public string InitiatorFilePathDisplay =>
        string.IsNullOrWhiteSpace(ContentFilePath)
            ? "(auto-generated from content path)"
            : BuildInitiatorFilePath(ContentFilePath);

    public bool EnableInitiatorPartition
    {
        get => _enableInitiatorPartition;
        set
        {
            if (SetProperty(ref _enableInitiatorPartition, value))
            {
                OnPropertyChanged(nameof(IsInitiatorCapacityEnabled));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsInitiatorCapacityEnabled => EnableInitiatorPartition && IsCreateNewMode;

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
                OnPropertyChanged(nameof(CanExecute));
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

    #endregion

    #region IO Speed

    /// <summary>Available IO speed simulation options.</summary>
    public IoSpeedOption[] IoSpeedOptions { get; } = IoSpeedOption.All;

    /// <summary>
    /// Selected IO speed for the new drive. In newMediaOnly mode this is
    /// locked to the drive's current value and the combo is disabled.
    /// </summary>
    public IoSpeedOption SelectedIoSpeed
    {
        get => _selectedIoSpeed;
        set => SetProperty(ref _selectedIoSpeed, value);
    }

    /// <summary>Whether the IO speed combo is enabled (disabled in newMediaOnly mode).</summary>
    public bool IsIoSpeedEnabled => !_isIoSpeedLocked;

    #endregion

    #region Presets

    public ObservableCollection<PresetOption> Presets { get; } = new(PresetOption.All);

    public PresetOption SelectedPreset
    {
        get => _selectedPreset;
        set => SetProperty(ref _selectedPreset, value);
    }

    #endregion

    #region Media Name

    public string MediaName
    {
        get => _mediaName;
        set => SetProperty(ref _mediaName, value);
    }

    #endregion

    #region Probe Status

    public VirtualDriveProbeStatus ProbeStatus
    {
        get => _probeStatus;
        private set
        {
            if (SetProperty(ref _probeStatus, value))
            {
                OnPropertyChanged(nameof(ProbeStatusText));
                OnPropertyChanged(nameof(ProbeStatusIcon));
                OnPropertyChanged(nameof(IsProbing));
                OnPropertyChanged(nameof(CanExecute));
            }
        }
    }

    public bool IsProbing => ProbeStatus == VirtualDriveProbeStatus.Probing;

    public string ProbeStatusIcon => ProbeStatus switch
    {
        VirtualDriveProbeStatus.ExistingFound => "✓",
        VirtualDriveProbeStatus.NewMedia => "○",
        VirtualDriveProbeStatus.Probing => "⟳",
        VirtualDriveProbeStatus.Error => "✗",
        _ => ""
    };

    public string ProbeStatusText => ProbeStatus switch
    {
        VirtualDriveProbeStatus.ExistingFound =>
            $"Existing media found: {_lastProbeResult?.MediaName ?? "unnamed"} ({_lastProbeResult?.BackupSetCount ?? 0} sets)" +
            (EnableInitiatorPartition ? " [with initiator partition]" : ""),
        VirtualDriveProbeStatus.NewMedia => "New media location",
        VirtualDriveProbeStatus.Probing => "Checking...",
        VirtualDriveProbeStatus.Error => _lastProbeResult?.ErrorMessage ?? "Error",
        _ => ""
    };

    #endregion

    #region Commands

    public ICommand BrowseContentFileCommand { get; }
    public ICommand ApplyPresetCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand CancelCommand { get; }

    #endregion

    #region UI Properties

    public string ActionButtonText => IsOpenExistingMode ? "Open" : "Create";

    public WarningLevel WarningLevel =>
        _isInMemory
            ? WarningLevel.Info
            : IsCreateNewMode &&
              !string.IsNullOrWhiteSpace(ContentFilePath) &&
              (File.Exists(ContentFilePath) || File.Exists(BuildContentMetadataFilePath(ContentFilePath)))
                ? WarningLevel.Warning : WarningLevel.None;

    public string WarningMessage => _isInMemory
        ? "The content of in-memory virtual media cannot be saved."
        : WarningLevel != WarningLevel.None
            ? "Existing files will be overwritten."
            : string.Empty;

    public bool CanExecute =>
        !IsProbing &&
        (_isInMemory
            ? IsContentCapacityValid && (!EnableInitiatorPartition || IsInitiatorCapacityValid)
            : !string.IsNullOrWhiteSpace(ContentFilePath) &&
              (IsOpenExistingMode || (IsContentCapacityValid && (!EnableInitiatorPartition || IsInitiatorCapacityValid))));

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

    private void ApplyPreset(object? _)
    {
        var caps = SelectedPreset.Capabilities;

        MinBlockSize = BlockSizeOption.FromBytes(caps.MinBlockSize);
        DefaultBlockSize = BlockSizeOption.FromBytes(caps.DefaultBlockSize);
        MaxBlockSize = BlockSizeOption.FromBytes(caps.MaxBlockSize);

        SupportsSetmarks = caps.SupportsSetmarks;
        SupportsSeqFilemarks = caps.SupportsSeqFilemarks;
        EnableInitiatorPartition = caps.SupportsInitiatorPartition;

        // Convert capacity to appropriate unit
        SetCapacityFromBytes(SelectedPreset.ContentCapacity, v => ContentCapacityValue = v, u => ContentCapacityUnit = u);
        SetCapacityFromBytes(SelectedPreset.InitiatorPartitionCapacity, v => InitiatorCapacityValue = v, u => InitiatorCapacityUnit = u);
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
            SupportsCompression = false,
        };

        // Use computed initiator path from naming convention
        //  For in-memory: use a placeholder path to signal initiator partition presence
        string? initiatorPath = EnableInitiatorPartition
            ? (_isInMemory ? "(in-memory)" : BuildInitiatorFilePath(ContentFilePath))
            : null;

        var request = new VirtualDriveOpenRequest(
            Capabilities: capabilities,
            Media: new VirtualMediaDescriptor(
                _isInMemory ? "(in-memory)" : ContentFilePath,
                ContentCapacityBytes,
                initiatorPath,
                EnableInitiatorPartition ? InitiatorCapacityBytes : 0,
                InMemory: _isInMemory),
            IsCreateNew: IsCreateNewMode,
            MediaName: IsCreateNewMode ? MediaName : null,
            IoSpeed: _isIoSpeedLocked ? null : SelectedIoSpeed);

        _onOpen(request);
    }

    #endregion

    #region Probe Logic

    /// <summary>
    /// Cancels any running probe operation and cleans up resources.
    /// Called when the dialog is closing.
    /// </summary>
    public void CancelProbe()
    {
        if (_probeCts != null)
        {
            _probeCts.Cancel();
            // Don't wait for the task - just trigger cancellation and let it clean up
            // The task will dispose its own resources when it completes
            _probeCts.Dispose();
            _probeCts = null;
        }
        _probeTask = null;
        ProbeStatus = VirtualDriveProbeStatus.None;
        _lastProbeResult = null;
    }

    /// <summary>
    /// Triggers an async probe of the virtual media file.
    /// Cancels any existing probe first.
    /// Auto-detects initiator partition files using the naming convention.
    /// </summary>
    private async Task TriggerProbeAsync()
    {
        // Cancel any existing probe
        if (_probeCts != null)
        {
            _probeCts.Cancel();
            try
            {
                // Wait for the previous probe to complete (it should abort quickly)
                if (_probeTask != null)
                    await _probeTask;
            }
            catch
            {
                // Ignore cancellation exceptions
            }
            _probeCts.Dispose();
            _probeCts = null;
        }

        // Don't probe if path is empty
        if (string.IsNullOrWhiteSpace(ContentFilePath))
        {
            ProbeStatus = VirtualDriveProbeStatus.None;
            _lastProbeResult = null;
            return;
        }

        // Check if content metadata file exists (quick check before full probe)
        var contentMetadataPath = BuildContentMetadataFilePath(ContentFilePath);
        if (!File.Exists(contentMetadataPath))
        {
            ProbeStatus = VirtualDriveProbeStatus.NewMedia;
            _lastProbeResult = null;

            // Auto-select "Create new" mode if no existing media
            if (!_newMediaOnly && !_existingMediaOnly && IsOpenExistingMode)
                IsCreateNewMode = true;

            // Check if initiator files would exist (for UI hint)
            EnableInitiatorPartition = false;
            return;
        }

        // Auto-detect initiator partition files using naming convention
        bool hasInitiatorFiles = InitiatorFilesExist(ContentFilePath);

        // Auto-check the initiator partition checkbox if files exist
        EnableInitiatorPartition = hasInitiatorFiles;

        // Start new probe
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;

        ProbeStatus = VirtualDriveProbeStatus.Probing;

        _probeTask = Task.Run(async () =>
        {
            try
            {
                // Determine initiator path based on auto-detection
                string? initiatorPath = hasInitiatorFiles
                    ? BuildInitiatorFilePath(ContentFilePath)
                    : null;

                // Use TapeService to probe the virtual drive
                var result = await TapeService.ProbeVirtualDriveAsync(
                    ContentFilePath,
                    initiatorPath,
                    token);

                if (token.IsCancellationRequested)
                    return;

                // Update UI on dispatcher thread
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    _lastProbeResult = result;

                    if (result.Success)
                    {
                        ProbeStatus = VirtualDriveProbeStatus.ExistingFound;
                        // Auto-select "Open existing" mode if existing media found
                        if (!_newMediaOnly && !_existingMediaOnly)
                            IsOpenExistingMode = true;

                        // Pre-populate all fields from probe result
                        if (result.Media != null)
                        {
                            SetCapacityFromBytes(result.Media.ContentCapacity, v => ContentCapacityValue = v, u => ContentCapacityUnit = u);
                            EnableInitiatorPartition = result.Media.InitiatorPath != null;
                            if (result.Media.InitiatorPartitionCapacity > 0)
                                SetCapacityFromBytes(result.Media.InitiatorPartitionCapacity, v => InitiatorCapacityValue = v, u => InitiatorCapacityUnit = u);
                        }

                        if (!string.IsNullOrEmpty(result.MediaName))
                            MediaName = result.MediaName;

                        if (result.DetectedCapabilities.HasValue)
                        {
                            var caps = result.DetectedCapabilities.Value;
                            MinBlockSize = BlockSizeOption.FromBytes(caps.MinBlockSize);
                            DefaultBlockSize = BlockSizeOption.FromBytes(caps.DefaultBlockSize);
                            MaxBlockSize = BlockSizeOption.FromBytes(caps.MaxBlockSize);
                            SupportsSetmarks = caps.SupportsSetmarks;
                            SupportsSeqFilemarks = caps.SupportsSeqFilemarks;
                        }
                    }
                    else
                    {
                        ProbeStatus = VirtualDriveProbeStatus.NewMedia;
                        // Auto-select "Create new" mode if no valid media
                        if (!_newMediaOnly && !_existingMediaOnly && IsOpenExistingMode)
                            IsCreateNewMode = true;
                    }

                    OnPropertyChanged(nameof(ProbeStatusText));
                    CommandManager.InvalidateRequerySuggested();
                });
            }
            catch (OperationCanceledException)
            {
                // Probe was cancelled - do nothing
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        _lastProbeResult = new VirtualDriveProbeResult(false, null, null, null, null, ex.Message);
                        ProbeStatus = VirtualDriveProbeStatus.Error;
                    });
                }
            }
        }, token);

        await _probeTask;
    }

    #endregion
}