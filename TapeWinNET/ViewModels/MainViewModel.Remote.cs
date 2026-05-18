using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using TapeLibNET;
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

    /// <summary>Current remote host settings; null when not connected. Used by <see cref="Services.WpfServiceHost"/>.</summary>
    internal RemoteHostSettings? RemoteHostSettings => _remoteHostSettings;

    // ── Remote Commands ───────────────────────────────────────────────────────

    /// <summary>Opens the Connect to Remote Host dialog (or submenu when already connected).</summary>
    public ICommand ConnectToRemoteHostCommand { get; private set; } = null!;

    /// <summary>Disconnects from the currently connected remote host.</summary>
    public ICommand DisconnectRemoteHostCommand { get; private set; } = null!;

    /// <summary>Opens a physical tape drive on the remote host.</summary>
    public ICommand OpenRemoteDriveCommand { get; private set; } = null!;

    /// <summary>Opens the "Open Remote Virtual Drive" dialog (open existing or create new).</summary>
    public ICommand OpenRemoteVirtualDriveCommand { get; private set; } = null!;

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
        ConnectToRemoteHostCommand      = new AsyncRelayCommand(ConnectToRemoteHostAsync,         () => !IsBusy);
        DisconnectRemoteHostCommand     = new AsyncRelayCommand(_ => DisconnectRemoteHostAsync(),  _ => IsRemoteConnected);
        OpenRemoteDriveCommand          = new AsyncRelayCommand(OpenRemoteDriveAsync,             _ => !IsBusy && IsRemoteConnected);
        OpenRemoteVirtualDriveCommand   = new AsyncRelayCommand(OpenRemoteVirtualDriveAsync,      () => !IsBusy && IsRemoteConnected);
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

    private const int RemoteSpecifyDriveNumber = -1;
    private const int RemoteScanningNumber      = -5;   // transient "Scanning drives…" placeholder

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

        RemoteDriveMenuItems.Add(new DriveMenuItem("Drive _0",                       0, OpenRemoteDriveCommand));
        RemoteDriveMenuItems.Add(new DriveMenuItem("Scanning drives…", RemoteScanningNumber, OpenRemoteDriveCommand));
        RemoteDriveMenuItems.Add(new DriveMenuItem("_Specify...",       RemoteSpecifyDriveNumber, OpenRemoteDriveCommand));
        RemoteDriveMenuItems.Add(new Separator());
        RemoteDriveMenuItems.Add(new DriveMenuItem("_Open Remote Virtual Drive...", 0, OpenRemoteVirtualDriveCommand));
        RemoteDriveMenuItems.Add(new Separator());
        RemoteDriveMenuItems.Add(new DriveMenuItem("_Disconnect",                     0, DisconnectRemoteHostCommand));
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

        // Insert discovered drives 1–9 just before "Specify..."
        var driveItems = RemoteDriveMenuItems.OfType<DriveMenuItem>().ToList();
        int specifyIndex = RemoteDriveMenuItems.IndexOf(
            driveItems.FirstOrDefault(i => i.DriveNumber == RemoteSpecifyDriveNumber)!);
        if (specifyIndex < 0)
            specifyIndex = 1; // fallback: insert after Drive 0

        int insertAt = specifyIndex;
        foreach (uint driveNum in probedDrives.Where(n => n >= 1))
        {
            if (driveItems.All(i => i.DriveNumber != (int)driveNum))
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

    // ── Open remote virtual drive (open existing or create new) ─────────────

    private async Task OpenRemoteVirtualDriveAsync()
    {
        if (_remoteHostSettings is null)
            return;

        var settings = _remoteHostSettings;

        // Load available named session volumes for the picker (Open existing mode)
        var availableVolumes = await _tapeService.ListRemoteSessionVolumesAsync().ConfigureAwait(true);

        // Show dialog on the UI thread (AsyncRelayCommand already runs there)
        var viewModel = new OpenRemoteVirtualDriveViewModel(settings);
        foreach (var vol in availableVolumes)
            viewModel.AvailableVolumes.Add(vol);

        var window = new OpenRemoteVirtualDriveWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };

        if (window.ShowDialog() != true || viewModel.Result is null)
            return;

        var request = viewModel.Result;

        if (request.IsCreateNew)
        {
            // Create new remote virtual drive
            if (!await RunBusyAsync($"Creating remote virtual drive on {settings.DisplayLabel}...",
                    () => _tapeService.CreateRemoteVirtualDriveAsync(settings, request.Media, request.Capabilities, request.MediaName)))
            {
                MessageBox.Show(
                    $"Failed to create remote virtual drive on {settings.DisplayLabel}.\n\n{_tapeService.LastError}",
                    "Create Remote Virtual Drive", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateTreeForRemoteDriveOnly(0, settings);
                return;
            }

            if (!await LoadMediaWithUIAsync())
            {
                UpdateTreeForRemoteDriveOnly(0, settings);
                NotifyIoSpeedChanged();
                return;
            }

            // Create the initial TOC on freshly created remote virtual media
            //  (mirrors the local "Create new" path in MainViewModel)
            string tocLabel = string.IsNullOrWhiteSpace(request.MediaName)
                ? "Remote virtual tape"
                : request.MediaName;
            var tocCreated = await RunBusyAsync("Creating initial TOC...",
                () => _tapeService.CreateInitialTOCAsync(tocLabel));
            if (!tocCreated)
                LogWarn("Could not create initial TOC on remote virtual drive");

            if (!await ReadTOCWithUIAsync(offerFileImportOnFailure: false))
            {
                UpdateTreeForRemoteDriveOnly(0, settings);
                NotifyIoSpeedChanged();
                return;
            }
        }
        else
        {
            // Open existing named remote volume
            if (!await RunBusyAsync($"Opening remote virtual volume on {settings.DisplayLabel}...",
                    () => _tapeService.OpenRemoteVirtualFileAsync(settings, request.Media, request.Capabilities, System.IO.FileMode.Open)))
            {
                MessageBox.Show(
                    $"Failed to open remote virtual volume on {settings.DisplayLabel}.\n\n{_tapeService.LastError}",
                    "Open Remote Virtual Drive", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateTreeForRemoteDriveOnly(0, settings);
                return;
            }

            if (!await LoadMediaWithUIAsync() || !await ReadTOCWithUIAsync(offerFileImportOnFailure: false))
            {
                UpdateTreeForRemoteDriveOnly(0, settings);
                NotifyIoSpeedChanged();
                return;
            }
        }

        UpdateTreeFromTOCRemote(0, settings);
        SelectMostRecentSet();
        NotifyIoSpeedChanged();
    }

    // ── Remote Format (re-create) ─────────────────────────────────────────────

    /// <summary>
    /// Handles "Media | Format" when a remote virtual drive is open.
    /// Shows <see cref="OpenRemoteVirtualDriveWindow"/> forced to Create mode so the user
    ///  can reconfigure the drive, then recreates it on the server via
    ///  <see cref="TapeLibNET.Services.TapeServiceBase.CreateRemoteVirtualDriveAsync"/>.
    /// Mirrors the local <c>FormatVirtualDriveAsync</c> flow in <c>MainViewModel.cs</c>.
    /// </summary>
    internal async Task FormatRemoteVirtualDriveAsync(FormatMediaViewModel formatViewModel)
    {
        if (_remoteHostSettings is null)
            return;

        var settings = _remoteHostSettings;

        var viewModel = new OpenRemoteVirtualDriveViewModel(settings)
        {
            MediaName = formatViewModel.MediaName,
            EnableInitiatorPartition = formatViewModel.CreateInitiatorPartition,
        };
        // Format = recreate: force Create mode so the user cannot accidentally switch to Open
        viewModel.ForceCreateMode();

        var window = new OpenRemoteVirtualDriveWindow(viewModel)
        {
            Owner = Application.Current.MainWindow
        };

        if (window.ShowDialog() != true || viewModel.Result is null)
            return;

        var request = viewModel.Result;

        if (!await RunBusyAsync($"Recreating remote virtual drive on {settings.DisplayLabel}...",
                () => _tapeService.CreateRemoteVirtualDriveAsync(settings, request.Media, request.Capabilities,
                    string.IsNullOrWhiteSpace(request.MediaName) ? "Remote virtual tape" : request.MediaName)))
        {
            MessageBox.Show(
                $"Failed to recreate remote virtual drive on {settings.DisplayLabel}.\n\n{_tapeService.LastError}",
                "Format Remote Drive", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateTreeForRemoteDriveOnly(0, settings);
            return;
        }

        if (!await LoadMediaWithUIAsync())
        {
            UpdateTreeForRemoteDriveOnly(0, settings);
            return;
        }

        string tocLabel = string.IsNullOrWhiteSpace(request.MediaName)
            ? "Remote virtual tape"
            : request.MediaName;
        var tocCreated = await RunBusyAsync("Creating initial TOC...",
            () => _tapeService.CreateInitialTOCAsync(tocLabel));
        if (!tocCreated)
            LogWarn("Could not create initial TOC on remote virtual drive");

        if (!await ReadTOCWithUIAsync(offerFileImportOnFailure: false))
        {
            UpdateTreeForRemoteDriveOnly(0, settings);
            return;
        }

        UpdateTreeFromTOCRemote(0, settings);
        SelectMostRecentSet();

        MessageBox.Show("Remote virtual media formatted successfully!", "Format Complete",
            MessageBoxButton.OK, MessageBoxImage.Information);
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
            toc, driveItem, tocFileName, isInMemory: _tapeService.IsInMemoryDrive);
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
    internal async Task DisconnectRemoteHostAsync()
    {
        if (_remoteHostSettings == null)
            return;

        var label = _remoteHostSettings.DisplayLabel;

        // Close the drive first (releases the TapeDrive wrapper / non-owning backend).
        // TapeServiceBase.CloseAsync acquires the operation lock so it is safe to await
        //  from the UI thread via DisconnectRemoteHostCommand.
        await _tapeService.CloseAsync().ConfigureAwait(true); // back to UI thread

        // Send the gRPC Close RPC, tear down the persistent channel, and clear the
        //  server-side session + named-volume catalog.
        await _tapeService.CloseRemoteConnectionAsync().ConfigureAwait(true);

        _remoteHostSettings   = null;
        _remoteServerVersion  = null;
        _remoteServerHostName = null;
        RemoteDriveMenuItems.Clear();

        TreeItems.Clear();
        _tocView = null;
        _currentSetView = null;
        WindowTitle = "TapeWin";
        OnPropertyChanged(nameof(HasMultipleSets));

        OnPropertyChanged(nameof(IsRemoteConnected));
        OnPropertyChanged(nameof(RemoteMenuHeader));
        OnPropertyChanged(nameof(RemoteStatusLabel));
        CommandManager.InvalidateRequerySuggested();

        LogInfo($"Disconnected from remote host {label}");
    }

    /// <summary>Synchronous wrapper used by the relay command (fire-and-forget with logging).</summary>
    internal void DisconnectRemoteHost() => _ = DisconnectRemoteHostAsync();

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
