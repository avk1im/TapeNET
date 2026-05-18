using System.Collections.ObjectModel;
using System.Windows.Input;

using TapeLibNET.Remote;
using TapeLibNET.Services;
using TapeLibNET.Virtual;
using TapeWinNET.Models;

namespace TapeWinNET.ViewModels;

/// <summary>
/// View model for the "Open Remote Virtual Drive" dialog.
/// Supports two top-level modes:
/// <list type="bullet">
///  <item><b>Create new</b> — same as the former CreateRemoteVirtualDriveViewModel; the user
///   chooses preset / capacity / block size and the server creates a new temp file.</item>
///  <item><b>Open existing</b> — shows a volume picker populated from
///   <see cref="AvailableVolumes"/>; configuration fields become read-only and reflect
///   the selected volume's stored metadata.</item>
/// </list>
/// The result is a <see cref="VirtualDriveOpenRequest"/> consumed by
/// <see cref="MainViewModel"/> after the dialog closes.
/// </summary>
public class OpenRemoteVirtualDriveViewModel : VirtualDriveConfigViewModelBase
{
    // ── Fields ────────────────────────────────────────────────────────────────

    private bool   _isCreateNewMode   = true;
    private bool   _isNamed           = false;   // Create mode: false → in-memory
    private string _contentFilePath   = string.Empty;

    private RemoteVirtualVolumeInfo? _selectedVolume;

    // ── Construction ──────────────────────────────────────────────────────────

    public OpenRemoteVirtualDriveViewModel(RemoteHostSettings settings)
    {
        HostLabel = settings.DisplayLabel;

        ConfirmCommand = new RelayCommand(_ => ExecuteConfirm(), _ => CanExecute);
        CancelCommand  = new RelayCommand(_ => OnCancel?.Invoke());

        // Apply the default preset to initialise all fields
        ApplyPreset();
    }

    // ── Callbacks (wired by code-behind) ──────────────────────────────────────

    /// <summary>Invoked when the user confirms the dialog successfully.</summary>
    public Action? OnConfirmSuccess { get; set; }

    /// <summary>Invoked when the user cancels the dialog.</summary>
    public Action? OnCancel { get; set; }

    // ── Result (read by MainViewModel after dialog closes) ────────────────────

    /// <summary>
    /// Populated after a successful confirmation.
    /// Carries <see cref="VirtualMediaDescriptor"/> so the service layer stores it as
    ///  <c>_vmdLast</c> and reports <c>IsInMemoryDrive</c> correctly.
    /// </summary>
    public VirtualDriveOpenRequest? Result { get; private set; }

    // ── Properties: shared ────────────────────────────────────────────────────

    /// <summary>Read-only host label shown at the top of the dialog.</summary>
    public string HostLabel { get; }

    // ── Properties: mode toggle ───────────────────────────────────────────────

    /// <summary>True when the Create new mode is active.</summary>
    public bool IsCreateNewMode
    {
        get => _isCreateNewMode;
        set
        {
            if (SetProperty(ref _isCreateNewMode, value))
            {
                OnPropertyChanged(nameof(IsOpenExistingMode));
                OnPropertyChanged(nameof(AreCreateFieldsEnabled));
                OnPropertyChanged(nameof(IsVolumePickerVisible));
                OnPropertyChanged(nameof(IsNoVolumesMessageVisible));
                OnPropertyChanged(nameof(CanExecute));
                OnPropertyChanged(nameof(ConfirmButtonLabel));
            }
        }
    }

    /// <summary>True when the Open existing mode is active.</summary>
    public bool IsOpenExistingMode
    {
        get => !_isCreateNewMode;
        set => IsCreateNewMode = !value;
    }

    /// <summary>Whether the create-mode configuration fields (preset, capacity, etc.) are enabled.</summary>
    public bool AreCreateFieldsEnabled => _isCreateNewMode;

    /// <summary>Whether the volume picker ComboBox is visible (Open existing mode only).</summary>
    public bool IsVolumePickerVisible => !_isCreateNewMode;

    /// <summary>Label for the confirm button ("Create" or "Open").</summary>
    public string ConfirmButtonLabel => _isCreateNewMode ? "Create" : "Open";

    // ── Properties: Create mode ───────────────────────────────────────────────

    /// <summary>True when a named (server temp-file-backed) drive is requested (Create mode).</summary>
    public bool IsNamed
    {
        get => _isNamed;
        set
        {
            if (SetProperty(ref _isNamed, value))
            {
                OnPropertyChanged(nameof(IsInMemory));
                OnPropertyChanged(nameof(IsNameFieldEnabled));
                OnPropertyChanged(nameof(IsContentFilePathEnabled));
                OnPropertyChanged(nameof(CanExecute));
                OnPropertyChanged(nameof(WarningLevel));
                OnPropertyChanged(nameof(WarningMessage));
            }
        }
    }

    /// <summary>True when an anonymous in-memory drive is requested (Create mode).</summary>
    public bool IsInMemory
    {
        get => !_isNamed;
        set => IsNamed = !value;
    }

    /// <summary>Whether the Name text field is enabled (Create + Named mode).</summary>
    public bool IsNameFieldEnabled => _isCreateNewMode && _isNamed;

    /// <summary>Whether the content file-path text field is enabled (same as <see cref="IsNameFieldEnabled"/>).</summary>
    public bool IsContentFilePathEnabled => _isCreateNewMode && _isNamed;

    /// <summary>
    /// Server-side backing filename for the content partition (Create + Named mode).
    /// </summary>
    public string ContentFilePath
    {
        get => _contentFilePath;
        set
        {
            if (SetProperty(ref _contentFilePath, value))
                OnPropertyChanged(nameof(CanExecute));
        }
    }

    // ── Properties: Open existing mode ───────────────────────────────────────

    /// <summary>Available named volumes in the current session (populated asynchronously).</summary>
    public ObservableCollection<RemoteVirtualVolumeInfo> AvailableVolumes { get; } = [];

    /// <summary>True when the volume list is empty, to show a hint message in the picker.</summary>
    public bool IsNoVolumesMessageVisible => !_isCreateNewMode && AvailableVolumes.Count == 0;

    /// <summary>Currently selected volume in the picker.</summary>
    public RemoteVirtualVolumeInfo? SelectedVolume
    {
        get => _selectedVolume;
        set
        {
            if (SetProperty(ref _selectedVolume, value))
            {
                OnPropertyChanged(nameof(CanExecute));
                ApplySelectedVolume();
            }
        }
    }

    // ── Properties: warning panel ─────────────────────────────────────────────

    /// <summary>Warn when in-memory is selected (Create mode only).</summary>
    public WarningLevel WarningLevel => (_isCreateNewMode && IsInMemory) ? WarningLevel.Info : WarningLevel.None;

    /// <summary>Warning message displayed below the configuration groups.</summary>
    public string WarningMessage => (_isCreateNewMode && IsInMemory)
        ? "The content of in-memory virtual media cannot be saved."
        : string.Empty;

    // ── Validation ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override bool CanExecute =>
        _isCreateNewMode
            ? IsContentCapacityValid
              && (!EnableInitiatorPartition || IsInitiatorCapacityValid)
              && (!_isNamed || !string.IsNullOrWhiteSpace(ContentFilePath))
            : _selectedVolume != null;

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand  { get; }

    // ── Public methods ────────────────────────────────────────────────────────

    /// <summary>
    /// Force the dialog into Create-new mode with the radio button disabled, so the user
    /// cannot switch to Open existing during a backup (volume-swap scenario).
    /// </summary>
    public void ForceCreateMode()
    {
        IsCreateNewMode = true;
        IsCreateModeForced = true;
        OnPropertyChanged(nameof(IsModeSwitchEnabled));
    }

    /// <summary>
    /// Force the dialog into Open-existing mode with the radio button disabled, so the user
    /// cannot switch to Create during a restore (volume-swap scenario).
    /// </summary>
    public void ForceOpenMode()
    {
        IsCreateNewMode = false;
        IsOpenModeForced = true;
        OnPropertyChanged(nameof(IsModeSwitchEnabled));
    }

    /// <summary>Whether the Open / Create radio buttons are enabled (false when forced by a swap prompt).</summary>
    public bool IsModeSwitchEnabled => !IsCreateModeForced && !IsOpenModeForced;

    public bool IsCreateModeForced { get; private set; }
    public bool IsOpenModeForced   { get; private set; }

    /// <summary>
    /// Pre-selects the volume whose name best matches <paramref name="volumeName"/>.
    /// Falls back to no selection if not found.
    /// </summary>
    public void TryPreSelectVolume(string volumeName)
    {
        var match = AvailableVolumes.FirstOrDefault(v =>
            string.Equals(v.Name, volumeName, StringComparison.OrdinalIgnoreCase));
        SelectedVolume = match;
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When the user selects an existing volume, populate the configuration fields from its
    /// stored metadata so the user can see the volume's parameters at a glance.
    /// Fields are shown read-only in XAML when <see cref="AreCreateFieldsEnabled"/> is false,
    ///  but we still populate them so the user can inspect the volume's configuration.
    /// </summary>
    private void ApplySelectedVolume()
    {
        if (_selectedVolume == null) return;

        // Block size — find closest option or synthesise one
        DefaultBlockSize = BlockSizeOption.FromBytes(_selectedVolume.BlockSize);
        MinBlockSize     = BlockSizeOption.FromBytes(_selectedVolume.Capabilities.MinBlockSize);
        MaxBlockSize     = BlockSizeOption.FromBytes(_selectedVolume.Capabilities.MaxBlockSize);

        // Capacity
        SetCapacityFromBytes(_selectedVolume.Media.ContentCapacity,
            v => ContentCapacityValue = v, u => ContentCapacityUnit = u);
    }

    private void ExecuteConfirm()
    {
        if (!CanExecute) return;

        if (_isCreateNewMode)
            ExecuteCreate();
        else
            ExecuteOpenExisting();
    }

    private void ExecuteCreate()
    {
        string? initiatorPath = EnableInitiatorPartition
            ? (_isNamed ? $"{ContentFilePath.Trim()}_init" : "(in-memory)")
            : null;

        Result = new VirtualDriveOpenRequest(
            Capabilities: BuildCapabilities(),
            Media: new VirtualMediaDescriptor(
                ContentPath:                  _isNamed ? ContentFilePath.Trim() : "(in-memory)",
                ContentCapacity:              ContentCapacityBytes,
                InitiatorPath:                initiatorPath,
                InitiatorPartitionCapacity:   EnableInitiatorPartition ? InitiatorCapacityBytes : 0,
                InMemory:                     !_isNamed),
            IsCreateNew: true,
            MediaName: MediaName.Trim());

        OnConfirmSuccess?.Invoke();
    }

    private void ExecuteOpenExisting()
    {
        if (_selectedVolume == null) return;

        Result = new VirtualDriveOpenRequest(
            Capabilities: _selectedVolume.Capabilities,
            Media: _selectedVolume.Media,
            IsCreateNew: false,
            MediaName: string.Empty);  // media name is already on the tape; not changed on re-open

        OnConfirmSuccess?.Invoke();
    }
}
