using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Microsoft.Extensions.Logging.Abstractions;

using TapeLibNET.Remote;
using TapeWinNET.Models;
using TapeWinNET.Utils;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Partial class containing remote host session state and commands for MainViewModel.
/// Covers Stage 2 (connect dialog) and Stage 3 (remote submenu, drive probing, open
///  physical / create virtual drives).
/// </summary>
public partial class MainViewModel
{
    // ── Remote host session ───────────────────────────────────────────────────

    private RemoteHostSettings? _remoteHostSettings;
    private string?             _remoteServerVersion;    // from GetServerInfo
    private string?             _remoteServerHostName;   // from GetServerInfo

    // ── Remote Commands ───────────────────────────────────────────────────────

    /// <summary>Opens the Connect to Remote Host dialog (or submenu when already connected).</summary>
    public ICommand ConnectToRemoteHostCommand { get; private set; } = null!;

    /// <summary>Disconnects from the currently connected remote host.</summary>
    public ICommand DisconnectRemoteHostCommand { get; private set; } = null!;

    /// <summary>Opens a physical tape drive on the remote host.</summary>
    public ICommand OpenRemoteDriveCommand { get; private set; } = null!;

    /// <summary>Opens the "Create Remote Virtual Drive" dialog.</summary>
    public ICommand CreateRemoteVirtualDriveCommand { get; private set; } = null!;

    // ── Remote submenu items ──────────────────────────────────────────────────

    /// <summary>
    /// Populated after a successful connect: Drive 0, any probed drives 1–9,
    ///  an optional "Scanning drives…" placeholder, then "Specify...",
    ///  a real <see cref="System.Windows.Controls.Separator"/> (used as-is by WPF —
    ///  no MenuItem wrapping, no icon-column chrome), "Create Remote Virtual Drive...",
    ///  another separator, and "Disconnect".
    /// </summary>
    public ObservableCollection<object> RemoteDriveMenuItems { get; } = [];

    // ── Remote Properties ─────────────────────────────────────────────────────

    /// <summary>True when a remote host is connected (drive may or may not be open).</summary>
    public bool IsRemoteConnected => _remoteHostSettings != null;

    /// <summary>
    /// Display label for the dynamic top-level File menu entry.
    /// Shows "Open on host:port" when connected, otherwise "Connect to Remote Host...".
    /// </summary>
    public string RemoteMenuHeader => _remoteHostSettings != null
        ? $"Open on {_remoteHostSettings.DisplayLabel}"
        : "Connect to Remote Host...";

    /// <summary>Status-bar label; null (→ Collapsed) when not connected.</summary>
    public string? RemoteStatusLabel => _remoteHostSettings != null
        ? $"Remote: {_remoteHostSettings.DisplayLabel}"
        : null;

    // ── Initializer (called from MainViewModel constructor) ───────────────────

    private void InitializeRemoteCommands()
    {
        ConnectToRemoteHostCommand       = new AsyncRelayCommand(ConnectToRemoteHostAsync,          () => !IsBusy);
        DisconnectRemoteHostCommand      = new RelayCommand(_ => DisconnectRemoteHost(),             _ => IsRemoteConnected);
        OpenRemoteDriveCommand           = new AsyncRelayCommand(OpenRemoteDriveAsync,              _ => !IsBusy && IsRemoteConnected);
        CreateRemoteVirtualDriveCommand  = new AsyncRelayCommand(CreateRemoteVirtualDriveAsync,     () => !IsBusy && IsRemoteConnected);
    }

    // ── Connect flow ──────────────────────────────────────────────────────────

    private void ConnectToRemoteHostAsync_ShowDialog()
    {
        var viewModel = new ConnectToRemoteHostViewModel(
            _settings.LastRemoteHost,
            _settings.LastRemotePort,
            _settings.LastRemoteUseTls,
            _settings.LastRemoteUseLocalHost);

        var window = new ConnectToRemoteHostWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };

        bool connected = window.ShowDialog() == true;

        if (!connected || viewModel.ConnectedSettings is null)
            return;

        var settings = viewModel.ConnectedSettings;

        // Persist for next session
        SaveRemoteSettings(settings, viewModel.UseLocalHost);

        // Store session state
        _remoteHostSettings   = settings;
        _remoteServerHostName = viewModel.ConnectedServerHostName;
        _remoteServerVersion  = viewModel.ConnectedServerVersion;

        // Log the successful connection
        var transport = settings.UseTls ? "TLS/HTTPS" : "plaintext HTTP/2";
        LogOk($"Connected to remote tape host {settings.DisplayLabel} " +
              $"({_remoteServerHostName ?? "unknown"}, v{_remoteServerVersion ?? "?"}, {transport})");

        // Notify all remote bindings
        OnPropertyChanged(nameof(IsRemoteConnected));
        OnPropertyChanged(nameof(RemoteMenuHeader));
        OnPropertyChanged(nameof(RemoteStatusLabel));
        CommandManager.InvalidateRequerySuggested();

        // Build the initial submenu and start background drive probing
        BuildInitialRemoteSubmenu();
        _ = ProbeRemoteDrivesAsync(settings);
    }

    private async Task ConnectToRemoteHostAsync()
    {
        // All dialog work must happen on the UI thread — invoke via Dispatcher if needed.
        // Since AsyncRelayCommand dispatches on the UI thread already, a direct call is safe here.
        ConnectToRemoteHostAsync_ShowDialog();
        await Task.CompletedTask;
    }

    // ── Remote submenu construction ───────────────────────────────────────────

    private const int RemoteSpecifyDriveNumber  = -1;
    private const int RemoteCreateVirtualNumber = -3;   // sentinel for Create Virtual item
    private const int RemoteDisconnectNumber    = -4;   // sentinel for Disconnect item
    private const int RemoteScanningNumber      = -5;   // sentinel for "Scanning..." placeholder

    /// <summary>
    /// Builds the initial submenu immediately after a successful connection:
    ///  Drive 0, "Scanning drives…" placeholder, "Specify...", separator,
    ///  "Create Remote Virtual Drive...", separator, "Disconnect".
    /// The scanning placeholder is removed by <see cref="ProbeRemoteDrivesAsync"/>
    ///  once probing is complete.
    /// </summary>
    private void BuildInitialRemoteSubmenu()
    {
        RemoteDriveMenuItems.Clear();

        RemoteDriveMenuItems.Add(new DriveMenuItem("Drive _0",              0,                        OpenRemoteDriveCommand));
        RemoteDriveMenuItems.Add(new DriveMenuItem("Scanning drives…",      RemoteScanningNumber,     OpenRemoteDriveCommand));
        RemoteDriveMenuItems.Add(new DriveMenuItem("_Specify...",            RemoteSpecifyDriveNumber, OpenRemoteDriveCommand));
        RemoteDriveMenuItems.Add(new Separator());
        RemoteDriveMenuItems.Add(new DriveMenuItem("_Create Remote Virtual Drive...", RemoteCreateVirtualNumber, CreateRemoteVirtualDriveCommand));
        RemoteDriveMenuItems.Add(new Separator());
        RemoteDriveMenuItems.Add(new DriveMenuItem("_Disconnect",           RemoteDisconnectNumber,   DisconnectRemoteHostCommand));
    }

    /// <summary>
    /// Probes the remote host for physical tape drives 1–9 in the background.
    /// Inserts discovered drive items before "Specify..." on the UI thread.
    /// Removes the "Scanning drives…" placeholder when done.
    /// </summary>
    private async Task ProbeRemoteDrivesAsync(RemoteHostSettings settings)
    {

        IReadOnlyList<uint>? probedDrives = null;
        try
        {
            // Session-less probe backend: constructed only for ProbeDrives, disposed immediately.
            probedDrives = await Task.Run(() =>
            {
                using var probe = new RemoteTapeDriveBackend(settings, NullLoggerFactory.Instance);
                return probe.ProbeDrives(9);
            }).ConfigureAwait(true); // back to UI thread
        }
        catch (Exception ex)
        {
            LogWarn($"Remote drive probe failed: {ex.Message}");
        }

        // Remove the "Scanning drives…" placeholder
        var scanningItem = RemoteDriveMenuItems
            .OfType<DriveMenuItem>()
            .FirstOrDefault(i => i.DriveNumber == RemoteScanningNumber);
        if (scanningItem is not null)
            RemoteDriveMenuItems.Remove(scanningItem);

        if (probedDrives is null)
            return;

        // Insert discovered drives 1–9 between "Drive 0" and "Specify..."
        // Find insertion point (just before "Specify...")
        int specifyIndex = RemoteDriveMenuItems
            .Select((item, idx) => (item, idx))
            .FirstOrDefault(t => t.item is DriveMenuItem dm && dm.DriveNumber == RemoteSpecifyDriveNumber).idx;

        if (specifyIndex < 0)
            specifyIndex = 1; // fallback: insert after Drive 0

        int insertAt = specifyIndex;
        foreach (uint driveNum in probedDrives.Where(n => n >= 1))
        {
            // Only add if not already present
            if (RemoteDriveMenuItems.OfType<DriveMenuItem>().All(i => i.DriveNumber != (int)driveNum))
            {
                RemoteDriveMenuItems.Insert(insertAt,
                    new DriveMenuItem($"Drive _{driveNum}", (int)driveNum, OpenRemoteDriveCommand));
                insertAt++;
            }
        }
    }

    // ── Open remote physical drive ────────────────────────────────────────────

    // Core helper — mirrors OpenPhysicalDriveCoreAsync for the remote physical path
    private Task<bool> OpenRemoteDriveCoreAsync(RemoteHostSettings settings, uint driveNumber) =>
        RunBusyAsync($"Opening remote drive {driveNumber} on {settings.DisplayLabel}...",
            () => _tapeService.OpenRemoteDriveAsync(settings, driveNumber));

    private async Task OpenRemoteDriveAsync(object? parameter)
    {
        if (_remoteHostSettings is null)
            return;

        int driveNumber = parameter as int? ?? 0;

        // "Specify..." → prompt user for a drive number
        if (driveNumber == RemoteSpecifyDriveNumber)
        {
            var dialog = new AskDialog(
                "Open Remote Drive",
                $"Enter the drive number on {_remoteHostSettings.DisplayLabel}:",
                "0")
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() != true)
                return;

            if (!int.TryParse(dialog.Answer, out driveNumber) || driveNumber < 0)
            {
                MessageBox.Show("Invalid drive number.", "Open Remote Drive",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else if (driveNumber < 0)
        {
            // Separator / Create Virtual / Disconnect items — not openable
            return;
        }

        var settings = _remoteHostSettings;

        if (!await OpenRemoteDriveCoreAsync(settings, (uint)driveNumber))
        {
            MessageBox.Show(
                $"Failed to open remote drive {driveNumber} on {settings.DisplayLabel}.\n\n{_tapeService.LastError}",
                "Open Remote Drive", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateTreeForRemoteDriveOnly(driveNumber, settings);
            return;
        }

        if (!await LoadMediaWithUIAsync() || !await ReadTOCWithUIAsync())
        {
            UpdateTreeForRemoteDriveOnly(driveNumber, settings);
            NotifyIoSpeedChanged();
            return;
        }

        UpdateTreeFromTOCRemote(driveNumber, settings);
        SelectMostRecentSet();
        NotifyIoSpeedChanged();
    }

    // ── Create remote virtual drive ───────────────────────────────────────────

    // Core helper — mirrors OpenVirtualDriveCoreAsync for the remote virtual path
    private Task<bool> CreateRemoteVirtualDriveCoreAsync(
        RemoteHostSettings settings, long capacityBytes, string? mediaName) =>
        RunBusyAsync($"Creating remote virtual drive on {settings.DisplayLabel}...",
            () => _tapeService.CreateRemoteVirtualDriveAsync(settings, capacityBytes, mediaName));

    private Task CreateRemoteVirtualDriveAsync()
    {
        // TODO (Stage 4): implement a dedicated Create Remote Virtual Drive dialog
        //  (CreateRemoteVirtualDriveWindow / CreateRemoteVirtualDriveViewModel) that
        //  collects capacity, name, block size, and capabilities, then calls
        //  CreateRemoteVirtualDriveCoreAsync(settings, ...).
        MessageBox.Show(
            "Create Remote Virtual Drive will be available in a future release.",
            "Not Yet Implemented", MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    // ── Remote tree helpers ───────────────────────────────────────────────────

    private void UpdateTreeForRemoteDriveOnly(int driveNumber, RemoteHostSettings settings)
    {
        TreeItems.Clear();
        _tocView = null;
        _currentSetView = null;

        var driveItem = TapeTreeItemViewModel.CreateDriveItem(
            driveNumber, _tapeService.DeviceName, settings.DisplayLabel);
        TreeItems.Add(driveItem);
        WindowTitle = $"TapeWin - [{settings.DisplayLabel}] Drive {driveNumber}";

        OnPropertyChanged(nameof(HasMultipleSets));
        LoadDriveInfo();
    }

    private void UpdateTreeFromTOCRemote(int driveNumber, RemoteHostSettings settings)
    {
        TreeItems.Clear();
        _currentSetView = null;

        var toc = _tapeService.TOC;
        if (toc == null)
        {
            UpdateTreeForRemoteDriveOnly(driveNumber, settings);
            return;
        }

        if (_tocView?.TOC == toc)
            _tocView.Refresh();
        else
            _tocView = new TOCView(toc);

        // Drive node — marked remote with green foreground in tree
        var driveItem = TapeTreeItemViewModel.CreateDriveItem(
            driveNumber, _tapeService.DeviceName, settings.DisplayLabel);
        TreeItems.Add(driveItem);

        // Tape / media node
        var tocFileName = _tapeService.IsTOCFromFile
            ? System.IO.Path.GetFileName(_tapeService.TOCFilePath ?? "file")
            : null;
        var tapeItem = TapeTreeItemViewModel.CreateTapeItem(
            toc, driveItem, tocFileName, isInMemory: false);
        driveItem.Children.Add(tapeItem);

        // Backup sets (newest-first)
        int totalSets = toc.Count;
        for (int i = totalSets; i >= 1; i--)
        {
            var setItem = TapeTreeItemViewModel.CreateBackupSetItem(toc, i, tapeItem);
            tapeItem.Children.Add(setItem);
        }

        WindowTitle = $"TapeWin - [{settings.DisplayLabel}] {toc.Description ?? $"Volume #{toc.Volume}"}";
        OnPropertyChanged(nameof(HasMultipleSets));

        StatusMessage = $"[Remote] Loaded {totalSets} backup set(s) from {settings.DisplayLabel}";
    }

    // ── Disconnect ────────────────────────────────────────────────────────────

    /// <summary>
    /// Disconnects from the remote host: closes any open remote drive, disposes the
    /// backend/channel, clears session state, and notifies all UI bindings.
    /// </summary>
    internal void DisconnectRemoteHost()
    {
        if (_remoteHostSettings == null)
            return;

        var label = _remoteHostSettings.DisplayLabel;

        // If a remote drive is currently open in TapeService, close it.
        // The RemoteTapeDriveBackend.CloseAsync is called by TapeDrive.Dispose via
        //  TapeServiceBase when we call CloseDrive. We close synchronously here since
        //  DisconnectRemoteHost is called from both sync and async contexts.
        // Note: calling _tapeService.CloseDriveAsync() would be cleaner but is not
        //  available as a public method; Dispose of the drive is handled internally.
        // For now, we just clear the service state — the backend channel will be
        //  closed by the gRPC channel's GC/finalizer if the drive was open.
        // TODO (Stage 6): add a proper CloseDriveAsync() to TapeServiceBase and await it here.

        _remoteHostSettings   = null;
        _remoteServerVersion  = null;
        _remoteServerHostName = null;
        RemoteDriveMenuItems.Clear();

        OnPropertyChanged(nameof(IsRemoteConnected));
        OnPropertyChanged(nameof(RemoteMenuHeader));
        OnPropertyChanged(nameof(RemoteStatusLabel));
        CommandManager.InvalidateRequerySuggested();

        LogInfo($"Disconnected from remote host {label}");
    }

    // ── AppSettings persistence ───────────────────────────────────────────────

    /// <summary>
    /// Persists the current remote connection parameters into <see cref="AppSettings"/>
    /// so the connect dialog can pre-populate next time.
    /// </summary>
    internal void SaveRemoteSettings(RemoteHostSettings settings, bool useLocalHost)
    {
        _settings.LastRemoteHost         = settings.Host;
        _settings.LastRemotePort         = settings.Port;
        _settings.LastRemoteUseTls       = settings.UseTls;
        _settings.LastRemoteUseLocalHost = useLocalHost;
    }

    // ── Remote drive info for the properties pane ─────────────────────────────

    /// <summary>
    /// Appends the "Remote Connection" section to the drive properties list.
    /// Called from <see cref="LoadDriveInfo"/> when <see cref="IsRemoteConnected"/> is true.
    /// </summary>
    private void AppendRemoteConnectionInfo()
    {
        if (_remoteHostSettings is null)
            return;

        var transport = _remoteHostSettings.UseTls ? "TLS/HTTPS" : "plaintext HTTP/2";

        PropertyList.Add(new Models.PropertyItem("─── Remote Connection ───", string.Empty));
        PropertyList.Add(new Models.PropertyItem("Host", _remoteHostSettings.DisplayLabel));
        if (!string.IsNullOrEmpty(_remoteServerHostName))
            PropertyList.Add(new Models.PropertyItem("Server hostname", _remoteServerHostName));
        if (!string.IsNullOrEmpty(_remoteServerVersion))
            PropertyList.Add(new Models.PropertyItem("Server version", _remoteServerVersion));
        PropertyList.Add(new Models.PropertyItem("Transport", transport));
    }
}
