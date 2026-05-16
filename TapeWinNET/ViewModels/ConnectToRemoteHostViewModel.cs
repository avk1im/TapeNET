using System.Windows.Input;

using Microsoft.Extensions.Logging.Abstractions;

using TapeLibNET.Remote;
using TapeWinNET.Converters;

namespace TapeWinNET.ViewModels;

/// <summary>
/// View model for the "Connect to Remote Host" dialog.
/// Calls <see cref="RemoteTapeDriveBackend.GetServerInfo"/> off the UI thread on
///  <see cref="ConnectCommand"/>; exposes <see cref="ConnectedSettings"/> on success.
/// </summary>
public class ConnectToRemoteHostViewModel : ViewModelBase
{
    // ── Fields ────────────────────────────────────────────────────────────────

    private string _host = string.Empty;
    private string _port = DefaultPortPlain.ToString();
    private bool _useLocalHost;
    private bool _useTls;
    private bool _isConnecting;
    private string _errorMessage = string.Empty;
    private WarningLevel _warningLevel = WarningLevel.None;

    private const int DefaultPortPlain = 50551;
    private const int DefaultPortTls   = 50552;

    // ── Construction ──────────────────────────────────────────────────────────

    public ConnectToRemoteHostViewModel(
        string? lastHost, int? lastPort, bool lastUseTls, bool lastUseLocalHost)
    {
        _useLocalHost = lastUseLocalHost;
        _useTls = lastUseTls;

        if (lastUseLocalHost)
        {
            _host = "127.0.0.1";
        }
        else if (!string.IsNullOrEmpty(lastHost))
        {
            _host = lastHost;
        }

        if (lastPort.HasValue)
            _port = lastPort.Value.ToString();

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnecting);
        CancelCommand  = new RelayCommand(_ => onCancel?.Invoke());
    }

    // ── Result ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Populated on successful connection; null until then.
    /// The dialog owner reads this to obtain the confirmed settings.
    /// </summary>
    public RemoteHostSettings? ConnectedSettings { get; private set; }

    /// <summary>Server hostname returned by <see cref="RemoteTapeDriveBackend.GetServerInfo"/>.</summary>
    public string? ConnectedServerHostName { get; private set; }

    /// <summary>Server version returned by <see cref="RemoteTapeDriveBackend.GetServerInfo"/>.</summary>
    public string? ConnectedServerVersion { get; private set; }

    // ── Properties ────────────────────────────────────────────────────────────

    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, value))
                ClearError();
        }
    }

    public string Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, value))
                ClearError();
        }
    }

    public bool UseLocalHost
    {
        get => _useLocalHost;
        set
        {
            if (SetProperty(ref _useLocalHost, value))
            {
                // Fill / restore host field based on the checkbox state
                if (value)
                    Host = "127.0.0.1";

                OnPropertyChanged(nameof(IsHostEditable));
                ClearError();
            }
        }
    }

    public bool UseTls
    {
        get => _useTls;
        set
        {
            if (SetProperty(ref _useTls, value))
            {
                // Auto-switch port between the two defaults when the user toggles TLS,
                //  but only if the port still holds the default for the previous state.
                if (value && _port == DefaultPortPlain.ToString())
                    Port = DefaultPortTls.ToString();
                else if (!value && _port == DefaultPortTls.ToString())
                    Port = DefaultPortPlain.ToString();
            }
        }
    }

    /// <summary>True when the Host field should be editable (i.e. UseLocalHost is off).</summary>
    public bool IsHostEditable => !_useLocalHost;

    public bool IsConnecting
    {
        get => _isConnecting;
        private set => SetProperty(ref _isConnecting, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            SetProperty(ref _errorMessage, value);
            OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>Drives the WarningPanelStyle visibility trigger in the dialog XAML.</summary>
    public WarningLevel WarningLevel
    {
        get => _warningLevel;
        private set => SetProperty(ref _warningLevel, value);
    }

    /// <summary>True when an error message is currently displayed.</summary>
    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand ConnectCommand { get; }
    public ICommand CancelCommand  { get; }

    // ── Callback set by the window code-behind ────────────────────────────────

    /// <summary>Invoked when the connection succeeds; the window should close on this.</summary>
    public Action? OnConnectSuccess { get; set; }

    private Action? onCancel;
    /// <summary>Invoked when the user cancels; the window should close on this.</summary>
    public Action? OnCancel
    {
        get => onCancel;
        set => onCancel = value;
    }

    // ── Connect logic ─────────────────────────────────────────────────────────

    private async Task ConnectAsync()
    {
        // Validate fields
        if (string.IsNullOrWhiteSpace(Host))
        {
            ShowError(WarningLevel.Warning, "Please enter a host name or IP address.");
            return;
        }

        if (!int.TryParse(Port, out int port) || port < 1 || port > 65535)
        {
            ShowError(WarningLevel.Warning, "Port must be an integer between 1 and 65535.");
            return;
        }

        ClearError();
        IsConnecting = true;

        try
        {
            var settings = new RemoteHostSettings(Host.Trim(), port, UseTls);

            // Call GetServerInfo off the UI thread to avoid blocking the dispatcher
            var info = await Task.Run(() =>
            {
                using var probe = new RemoteTapeDriveBackend(settings, NullLoggerFactory.Instance);
                return probe.GetServerInfo();
            }).ConfigureAwait(true); // ConfigureAwait(true) so we land back on the UI thread

            if (info is null)
            {
                ShowError(WarningLevel.Error, $"Could not reach the server at {settings.DisplayLabel}. No response.");
                return;
            }

            // Success — store result
            ConnectedSettings        = settings;
            ConnectedServerHostName  = info.HostName;
            ConnectedServerVersion   = info.ServerVersion;

            OnConnectSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            ShowError(WarningLevel.Error, $"Connection failed: {ex.Message}");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private void ShowError(WarningLevel level, string message)
    {
        WarningLevel = level;
        ErrorMessage = message;
    }

    private void ClearError()
    {
        WarningLevel = WarningLevel.None;
        ErrorMessage = string.Empty;
    }
}
