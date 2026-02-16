using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;

using TapeLibNET.Virtual;
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
    /// Builds the initiator partition file path from content file path.
    /// Example: "mymedia.vt" → "mymedia_init.vt"
    /// </summary>
    public static string BuildInitiatorFilePath(string contentFilePath)
    {
        var dir = Path.GetDirectoryName(contentFilePath) ?? string.Empty;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(contentFilePath);
        var ext = Path.GetExtension(contentFilePath);
        return Path.Combine(dir, $"{nameWithoutExt}_init{ext}");
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

    #endregion

    private readonly Action<VirtualTapeDriveCapabilities, string, string?> _onOpen;
    private readonly Action _onCancel;

    // Mode selection
    private bool _isOpenExistingMode = true;

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
    private string _initiatorCapacityValue = "24";
    private CapacityUnit _initiatorCapacityUnit = CapacityUnit.All[2]; // MB

    // Features
    private bool _supportsSetmarks = true;
    private bool _supportsSeqFilemarks;
    private bool _supportsCompression;

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
        Action<VirtualTapeDriveCapabilities, string, string?> onOpen,
        Action onCancel)
    {
        _onOpen = onOpen;
        _onCancel = onCancel;

        BrowseContentFileCommand = new RelayCommand(BrowseContentFile);
        ApplyPresetCommand = new RelayCommand(ApplyPreset);
        OpenCommand = new RelayCommand(ExecuteOpen, _ => CanExecute);
        CancelCommand = new RelayCommand(_ => _onCancel());
    }

    #region Mode Selection

    public bool IsOpenExistingMode
    {
        get => _isOpenExistingMode;
        set
        {
            if (SetProperty(ref _isOpenExistingMode, value))
            {
                OnPropertyChanged(nameof(IsCreateNewMode));
                OnPropertyChanged(nameof(ActionButtonText));
                OnPropertyChanged(nameof(ShowOverwriteWarning));
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
            if (SetProperty(ref _contentFilePath, value))
            {
                OnPropertyChanged(nameof(CanExecute));
                OnPropertyChanged(nameof(ShowOverwriteWarning));
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

    public bool ShowOverwriteWarning =>
        IsCreateNewMode &&
        !string.IsNullOrWhiteSpace(ContentFilePath) &&
        (File.Exists(ContentFilePath) || File.Exists(BuildContentMetadataFilePath(ContentFilePath)));

    public bool CanExecute =>
        !string.IsNullOrWhiteSpace(ContentFilePath) &&
        !IsProbing &&
        (IsOpenExistingMode || (IsContentCapacityValid && (!EnableInitiatorPartition || IsInitiatorCapacityValid)));

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

        // Use computed initiator path from naming convention
        string? initiatorPath = EnableInitiatorPartition
            ? BuildInitiatorFilePath(ContentFilePath)
            : null;

        _onOpen(capabilities, ContentFilePath, initiatorPath);
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
            if (IsOpenExistingMode)
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
                        IsOpenExistingMode = true;

                        // Update initiator partition checkbox based on probe result
                        if (result.DetectedCapabilities?.SupportsInitiatorPartition == true)
                            EnableInitiatorPartition = true;
                    }
                    else
                    {
                        ProbeStatus = VirtualDriveProbeStatus.NewMedia;
                        // Auto-select "Create new" mode if no valid media
                        if (IsOpenExistingMode)
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