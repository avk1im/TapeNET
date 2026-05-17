using System.Windows.Input;

using TapeLibNET.Remote;
using TapeLibNET.Services;
using TapeLibNET.Virtual;
using TapeWinNET.Models;

namespace TapeWinNET.ViewModels;

/// <summary>
/// View model for the "Create Temporary Remote Virtual Drive" dialog.
/// Extends <see cref="VirtualDriveConfigViewModelBase"/> for the full virtual-media
///  configuration surface (preset, block sizes, capacity, initiator partition, features,
///  media description) and adds the remote-specific in-memory / named toggle.
/// The result is a <see cref="VirtualDriveOpenRequest"/> whose
///  <see cref="VirtualDriveOpenRequest.Media"/> carries a <see cref="VirtualMediaDescriptor"/>
///  that the service layer stores as <c>_vmdLast</c>.
/// </summary>
public class CreateRemoteVirtualDriveViewModel : VirtualDriveConfigViewModelBase
{
    // ── Fields ────────────────────────────────────────────────────────────────

    private bool   _isNamed       = false;  // false → in-memory, true → named (server temp file)
    private string _contentFilePath = string.Empty;

    // ── Construction ──────────────────────────────────────────────────────────

    public CreateRemoteVirtualDriveViewModel(RemoteHostSettings settings)
    {
        HostLabel = settings.DisplayLabel;

        CreateCommand = new RelayCommand(_ => ExecuteCreate(), _ => CanExecute);
        CancelCommand = new RelayCommand(_ => OnCancel?.Invoke());

        // Apply the default preset to initialise all fields
        ApplyPreset();
    }

    // ── Callbacks (wired by code-behind) ──────────────────────────────────────

    /// <summary>Invoked when the user confirms the dialog successfully.</summary>
    public Action? OnCreateSuccess { get; set; }

    /// <summary>Invoked when the user cancels the dialog.</summary>
    public Action? OnCancel { get; set; }

    // ── Result (read by MainViewModel after dialog closes) ────────────────────

    /// <summary>
    /// Populated after a successful Create confirmation.
    /// Carries <see cref="VirtualMediaDescriptor"/> so the service layer can store it as
    ///  <c>_vmdLast</c> and report <c>IsInMemoryDrive</c> correctly.
    /// </summary>
    public VirtualDriveOpenRequest? Result { get; private set; }

    // ── Properties ────────────────────────────────────────────────────────────

    /// <summary>Read-only host label shown at the top of the dialog.</summary>
    public string HostLabel { get; }

    /// <summary>True when a named (server temp-file-backed) drive is requested.</summary>
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

    /// <summary>True when an anonymous in-memory drive is requested.</summary>
    public bool IsInMemory
    {
        get => !_isNamed;
        set => IsNamed = !value;
    }

    /// <summary>Whether the Name text field is enabled.</summary>
    public bool IsNameFieldEnabled => _isNamed;

    /// <summary>Whether the content file-path text field is enabled (same as <see cref="IsNameFieldEnabled"/>).</summary>
    public bool IsContentFilePathEnabled => _isNamed;

    /// <summary>
    /// Server-side backing filename for the content partition (used when <see cref="IsNamed"/> is true).
    /// Corresponds to <c>ContentFilePath</c> in <see cref="OpenVirtualDriveViewModel"/>.
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

    // ── Validation

    /// <inheritdoc/>
    public override bool CanExecute =>
        IsContentCapacityValid &&
        (!EnableInitiatorPartition || IsInitiatorCapacityValid) &&
        (!_isNamed || !string.IsNullOrWhiteSpace(ContentFilePath));

    // ── Warning panel (mirrors OpenVirtualDriveViewModel) ─────────────────────

    /// <summary>Warn when in-memory is selected (data is not persisted).</summary>
    public WarningLevel WarningLevel => IsInMemory ? WarningLevel.Info : WarningLevel.None;

    /// <summary>Warning message displayed below the configuration groups.</summary>
    public string WarningMessage => IsInMemory
        ? "The content of in-memory virtual media cannot be saved."
        : string.Empty;

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand CreateCommand { get; }
    public ICommand CancelCommand { get; }

    // ── Internal ─────────────────────────────────────────────────────────────

    private void ExecuteCreate()
    {
        if (!CanExecute)
            return;

        // Build the initiator-partition placeholder path; derive from content path for named drives.
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
            MediaName: MediaName.Trim());  // MediaName = description, used as TOC label

        OnCreateSuccess?.Invoke();
    }
}
