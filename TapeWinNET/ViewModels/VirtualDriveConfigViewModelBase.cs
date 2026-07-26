using System.Collections.ObjectModel;
using System.Windows.Input;

using TapeLibNET;
using TapeLibNET.Virtual;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Abstract base that encapsulates the shared "Create new virtual media" configuration surface:
/// preset, block sizes, capacity (content + optional initiator partition), setmark features,
/// and media description.
/// <para>
/// Used by both <see cref="OpenVirtualDriveViewModel"/> (local) and
///  <see cref="OpenRemoteVirtualDriveViewModel"/> (remote).
/// </para>
/// </summary>
public abstract class VirtualDriveConfigViewModelBase : ViewModelBase
{
    // ── Backing fields ────────────────────────────────────────────────────────

    protected BlockSizeOption _minBlockSize    = BlockSizeOption.FromBytes(512);
    protected BlockSizeOption _defaultBlockSize = BlockSizeOption.FromBytes(16 * 1024);
    protected BlockSizeOption _maxBlockSize    = BlockSizeOption.FromBytes(64 * 1024);

    protected string       _contentCapacityValue  = "500";
    protected CapacityUnit _contentCapacityUnit   = CapacityUnit.All[2]; // MB
    protected string       _initiatorCapacityValue = "16";
    protected CapacityUnit _initiatorCapacityUnit  = CapacityUnit.All[2]; // MB

    protected bool _enableInitiatorPartition;
    protected bool _supportsSetmarks    = true;
    protected bool _supportsSeqFilemarks;

    protected PresetOption _selectedPreset = PresetOption.All[1]; // WithSetmarks

    protected string _mediaName = $"Virtual media created {DateTime.Now:yyyy-MM-dd HH:mm}";

    // End-of-media emulation (early warning zone + capacity overreport)
    protected EwProfileOption _selectedEwProfile = EwProfileOption.None;
    protected string       _ewZoneValue     = "4";
    protected CapacityUnit _ewZoneUnit      = CapacityUnit.Percent;
    protected string       _overreportValue = "4";
    protected CapacityUnit _overreportUnit  = CapacityUnit.Percent;

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Applies the selected preset to all configuration fields.</summary>
    public ICommand ApplyPresetCommand { get; }

    protected VirtualDriveConfigViewModelBase()
    {
        ApplyPresetCommand = new RelayCommand(_ => ApplyPreset());
    }

    // ── Presets ───────────────────────────────────────────────────────────────

    public ObservableCollection<PresetOption> Presets { get; } = new(PresetOption.All);

    public PresetOption SelectedPreset
    {
        get => _selectedPreset;
        set => SetProperty(ref _selectedPreset, value);
    }

    // ── Block Sizes ───────────────────────────────────────────────────────────

    public ObservableCollection<BlockSizeOption> BlockSizeOptions { get; } = new(BlockSizeOption.All);

    public BlockSizeOption MinBlockSize
    {
        get => _minBlockSize;
        set
        {
            if (SetProperty(ref _minBlockSize, value))
            {
                // Auto-adjust: ensure min <= default <= max
                if (DefaultBlockSize.Bytes < value.Bytes) DefaultBlockSize = value;
                if (MaxBlockSize.Bytes < value.Bytes)     MaxBlockSize     = value;
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
                if (MinBlockSize.Bytes > value.Bytes) MinBlockSize = value;
                if (MaxBlockSize.Bytes < value.Bytes) MaxBlockSize = value;
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
                if (DefaultBlockSize.Bytes > value.Bytes) DefaultBlockSize = value;
                if (MinBlockSize.Bytes > value.Bytes)     MinBlockSize     = value;
            }
        }
    }

    // ── Capacity ──────────────────────────────────────────────────────────────

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
                OnEwBaseCapacityChanged();
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
                OnEwBaseCapacityChanged();
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
                OnPropertyChanged(nameof(CanExecute));
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

    // End-of-Media Behavior (early warning + capacity overreport)

    /// <summary>
    /// Selectable EW/EOM emulation profiles. Always contains <c>[Custom]</c> and <c>[LTO-4]</c>; any stored
    /// calibration profiles are appended by the owning view-model.
    /// </summary>
    public ObservableCollection<EwProfileOption> EwProfiles { get; } =
        new([EwProfileOption.None, EwProfileOption.Custom, EwProfileOption.Lto4]);

    public EwProfileOption SelectedEwProfile
    {
        get => _selectedEwProfile;
        set
        {
            if (SetProperty(ref _selectedEwProfile, value))
            {
                OnPropertyChanged(nameof(IsEwCustom));
                OnPropertyChanged(nameof(EwZoneBytesDisplay));
                OnPropertyChanged(nameof(OverreportBytesDisplay));
            }
        }
    }

    /// <summary>
    /// Whether the EW-zone / overreport value inputs are editable. Only the <c>[Custom]</c> profile exposes
    /// its parameters; every other profile is opaque, so the inputs are disabled (and blanked in the UI).
    /// </summary>
    public bool IsEwCustom => _selectedEwProfile.IsCustom;

    /// <summary>Units offered for the EW-zone and overreport inputs: % of capacity, MB, or GB.</summary>
    public ObservableCollection<CapacityUnit> EwCapacityUnits { get; } =
        new(CapacityUnit.AllWithPercent);

    public string EwZoneValue
    {
        get => _ewZoneValue;
        set
        {
            if (SetProperty(ref _ewZoneValue, value))
                OnPropertyChanged(nameof(EwZoneBytesDisplay));
        }
    }

    public CapacityUnit EwZoneUnit
    {
        get => _ewZoneUnit;
        set
        {
            if (SetProperty(ref _ewZoneUnit, value))
                OnPropertyChanged(nameof(EwZoneBytesDisplay));
        }
    }

    public string OverreportValue
    {
        get => _overreportValue;
        set
        {
            if (SetProperty(ref _overreportValue, value))
                OnPropertyChanged(nameof(OverreportBytesDisplay));
        }
    }

    public CapacityUnit OverreportUnit
    {
        get => _overreportUnit;
        set
        {
            if (SetProperty(ref _overreportUnit, value))
                OnPropertyChanged(nameof(OverreportBytesDisplay));
        }
    }

    /// <summary>EW-zone size resolved to bytes against the current content capacity.</summary>
    public long EwZoneBytes => _ewZoneUnit.ToBytes(_ewZoneValue, ContentCapacityBytes);

    /// <summary>Capacity-overreport (floor) size resolved to bytes against the current content capacity.</summary>
    public long OverreportBytes => _overreportUnit.ToBytes(_overreportValue, ContentCapacityBytes);

    public string EwZoneBytesDisplay =>
        IsEwCustom ? $"= {EwZoneBytes:N0} bytes" : string.Empty;

    public string OverreportBytesDisplay =>
        IsEwCustom ? $"= {OverreportBytes:N0} bytes" : string.Empty;

    /// <summary>
    /// Builds the <see cref="VirtualTapeEwProfile"/> for the current content capacity and EW settings, or
    /// <see langword="null"/> when no meaningful emulation is configured. Transient — never persisted, like
    /// the IO-rate emulation.
    /// </summary>
    public VirtualTapeEwProfile? BuildEwProfile() =>
        _selectedEwProfile.BuildProfile(ContentCapacityBytes, EwZoneBytes, OverreportBytes);

    /// <summary>
    /// Appends stored calibration profiles to <see cref="EwProfiles"/>. Non-throwing: a store failure simply
    /// leaves only the built-in options. Call once, after construction, when calibrations are available.
    /// </summary>
    protected void AddCalibrationProfiles(IEnumerable<ITapeCalibration>? calibrations)
    {
        if (calibrations is null)
            return;

        foreach (var cal in calibrations)
            EwProfiles.Add(new EwProfileOption($"[{cal.ProfileKey}]", EnableEw: true, cal));
    }

    /// <summary>
    /// Re-evaluates the EW byte displays when the base content capacity changes. Percentage-based EW inputs
    /// resolve against <see cref="ContentCapacityBytes"/>, so their byte equivalents shift with capacity.
    /// </summary>
    private void OnEwBaseCapacityChanged()
    {
        OnPropertyChanged(nameof(EwZoneBytesDisplay));
        OnPropertyChanged(nameof(OverreportBytesDisplay));
    }

    // ── Initiator Partition ───────────────────────────────────────────────────

    public bool EnableInitiatorPartition
    {
        get => _enableInitiatorPartition;
        set
        {
            if (SetProperty(ref _enableInitiatorPartition, value))
            {
                OnPropertyChanged(nameof(IsInitiatorCapacityEnabled));
                OnPropertyChanged(nameof(CanExecute));
            }
        }
    }

    /// <summary>
    /// Whether the initiator capacity fields are editable.
    /// Base implementation: enabled when <see cref="EnableInitiatorPartition"/> is true.
    /// <see cref="OpenVirtualDriveViewModel"/> overrides this to add <c>&amp;&amp; IsCreateNewMode</c>.
    /// </summary>
    public virtual bool IsInitiatorCapacityEnabled => _enableInitiatorPartition;

    // ── Features ──────────────────────────────────────────────────────────────

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

    // ── Media Description ─────────────────────────────────────────────────────

    public string MediaName
    {
        get => _mediaName;
        set => SetProperty(ref _mediaName, value);
    }

    // ── CanExecute (abstract, derived classes add their own guards) ───────────

    /// <summary>True when all required fields are valid and the action button should be enabled.</summary>
    public abstract bool CanExecute { get; }

    // ── Preset application ────────────────────────────────────────────────────

    /// <summary>
    /// Applies the currently selected preset to block sizes, capacity, and feature checkboxes.
    /// </summary>
    public void ApplyPreset()
    {
        var caps = SelectedPreset.Capabilities;

        MinBlockSize     = BlockSizeOption.FromBytes(caps.MinBlockSize);
        DefaultBlockSize = BlockSizeOption.FromBytes(caps.DefaultBlockSize);
        MaxBlockSize     = BlockSizeOption.FromBytes(caps.MaxBlockSize);

        SupportsSetmarks      = caps.SupportsSetmarks;
        SupportsSeqFilemarks  = caps.SupportsSeqFilemarks;
        EnableInitiatorPartition = caps.SupportsInitiatorPartition;

        SetCapacityFromBytes(SelectedPreset.ContentCapacity,
            v => ContentCapacityValue = v, u => ContentCapacityUnit = u);
        // Only update initiator capacity when the preset defines one; otherwise keep the existing
        //  default so the field doesn't reset to "0 B" for non-partition presets.
        if (SelectedPreset.InitiatorPartitionCapacity > 0)
            SetCapacityFromBytes(SelectedPreset.InitiatorPartitionCapacity,
                v => InitiatorCapacityValue = v, u => InitiatorCapacityUnit = u);
    }

    // ── Capacity helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Converts a byte count to the most appropriate capacity value + unit pair.
    /// </summary>
    public static void SetCapacityFromBytes(long bytes, Action<string> setValue, Action<CapacityUnit> setUnit)
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

    /// <summary>
    /// Builds the <see cref="VirtualTapeDriveCapabilities"/> from the current UI state.
    /// </summary>
    protected VirtualTapeDriveCapabilities BuildCapabilities() => new()
    {
        MinBlockSize               = _minBlockSize.Bytes,
        DefaultBlockSize           = _defaultBlockSize.Bytes,
        MaxBlockSize               = _maxBlockSize.Bytes,
        SupportsSetmarks           = _supportsSetmarks,
        SupportsSeqFilemarks       = _supportsSeqFilemarks,
        SupportsInitiatorPartition = _enableInitiatorPartition,
        SupportsCompression        = false,
    };
}
