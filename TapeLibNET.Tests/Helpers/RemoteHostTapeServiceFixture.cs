using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using TapeLibNET.Remote;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// xUnit fixture that connects to an already-running <c>TapeServiceNET</c> at a
/// configured remote address. Configuration is read from
/// <c>remote-test-settings.json</c> (gitignored) with environment variable overrides.
/// <para>
/// When no host is configured, the fixture sets <see cref="IsConfigured"/> to
/// <c>false</c> so tests can skip gracefully.
/// </para>
/// <para>Configuration keys (JSON / environment variables):</para>
/// <list type="table">
///   <listheader><term>Key</term><description>Purpose</description></listheader>
///   <item><term>RemoteHost</term><description>IP address or hostname (required)</description></item>
///   <item><term>RemotePort</term><description>plain-HTTP gRPC port (default: 50551)</description></item>
///   <item><term>UseTls</term><description><c>true</c> for HTTPS, <c>false</c> for HTTP (default: false)</description></item>
///   <item><term>DangerousAcceptAnyServerCertificate</term><description>Skip TLS cert validation for self-signed dev certs (default: false)</description></item>
/// </list>
/// </summary>
public sealed class RemoteHostTapeServiceFixture : IAsyncLifetime, IDisposable, ITapeServiceFixture
{
    #region *** Configuration Constants ***

    /// <summary>Environment variable prefix for overrides (e.g. <c>TAPE_REMOTE_HOST</c>).</summary>
    private const string EnvPrefix = "TAPE_REMOTE_";

    /// <summary>Name of the optional JSON settings file (expected next to the test assembly).</summary>
    private const string SettingsFileName = "remote-test-settings.json";

    private const int DefaultPort = 50551;

    #endregion

    private GrpcChannel? _channel;

    /// <summary>Whether a remote host was configured. Tests should skip when <c>false</c>.</summary>
    public bool IsConfigured { get; private set; }

    /// <summary>The gRPC channel connected to the remote service.</summary>
    public GrpcChannel Channel => _channel
        ?? throw new InvalidOperationException(
            IsConfigured
                ? "Service not started. Await InitializeAsync first."
                : "Remote host not configured. Tests should skip.");

    /// <summary>The remote service address (e.g. <c>http://192.168.1.50:50551</c>).</summary>
    public string Address { get; private set; } = string.Empty;

    /// <summary>Human-readable reason when <see cref="IsConfigured"/> is <c>false</c>.</summary>
    public string SkipReason { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        // Build configuration: JSON file (optional) → environment variables (override)
        var configBuilder = new ConfigurationBuilder();

        // Look for settings file next to the test assembly
        string? assemblyDir = Path.GetDirectoryName(typeof(RemoteHostTapeServiceFixture).Assembly.Location);
        if (assemblyDir != null)
        {
            string settingsPath = Path.Combine(assemblyDir, SettingsFileName);
            if (File.Exists(settingsPath))
                configBuilder.AddJsonFile(settingsPath, optional: true);
        }

        // Also look in the project/solution directory (for dev convenience)
        string? projectDir = FindProjectDirectory();
        if (projectDir != null)
        {
            string settingsPath = Path.Combine(projectDir, SettingsFileName);
            if (File.Exists(settingsPath))
                configBuilder.AddJsonFile(settingsPath, optional: true);
        }

        // Environment variables override JSON (prefix: TAPE_REMOTE_)
        configBuilder.AddEnvironmentVariables(EnvPrefix);

        var config = configBuilder.Build();

        // Read settings — env vars use the key name without the prefix
        //  (e.g. TAPE_REMOTE_HOST → "HOST", but IConfiguration normalizes to "RemoteHost")
        string? host = config["RemoteHost"]
            ?? Environment.GetEnvironmentVariable($"{EnvPrefix}HOST");

        if (string.IsNullOrWhiteSpace(host))
        {
            IsConfigured = false;
            SkipReason = $"Remote host not configured. Set '{EnvPrefix}HOST' environment variable " +
                $"or create '{SettingsFileName}' with a 'RemoteHost' key.";
            return;
        }

        int port = DefaultPort;
        string? portStr = config["RemotePort"]
            ?? Environment.GetEnvironmentVariable($"{EnvPrefix}PORT");
        if (!string.IsNullOrEmpty(portStr) && int.TryParse(portStr, out int parsedPort))
            port = parsedPort;

        bool useTls = false;
        string? tlsStr = config["UseTls"]
            ?? Environment.GetEnvironmentVariable($"{EnvPrefix}TLS");
        if (!string.IsNullOrEmpty(tlsStr) && bool.TryParse(tlsStr, out bool parsedTls))
            useTls = parsedTls;

        // Read DangerousAcceptAny from TlsTestSettings (same JSON file / env vars)
        bool dangerousAcceptAny = false;
        if (useTls)
            dangerousAcceptAny = new TlsTestSettings().DangerousAcceptAny;

        var settings = new RemoteHostSettings(
            Host: host,
            Port: port,
            UseTls: useTls,
            DangerousAcceptAnyServerCertificate: dangerousAcceptAny);

        Address = settings.ChannelAddress;
        IsConfigured = true;

        _channel = GrpcChannel.ForAddress(Address, settings.BuildChannelOptions());

        // Probe the channel — gRPC channels are lazy and won't fail until the first RPC.
        // A 3-second ConnectAsync tells us right now whether the service is actually up,
        // so tests can skip cleanly instead of failing with Unavailable exceptions.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _channel.ConnectAsync(cts.Token);
            IsConfigured = true;
        }
        catch (Exception ex)
        {
            IsConfigured = false;
            SkipReason = $"Remote service at {Address} is not reachable: {ex.Message}";
            _channel.Dispose();
            _channel = null;
        }

        return;
    }

    public Task DisposeAsync()
    {
        _channel?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }

    /// <summary>
    /// Walks up from the assembly location to find the test project directory
    /// (contains <c>TapeLibNET.Tests.csproj</c>).
    /// </summary>
    private static string? FindProjectDirectory()
    {
        string? dir = Path.GetDirectoryName(typeof(RemoteHostTapeServiceFixture).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "TapeLibNET.Tests.csproj")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}

/// <summary>
/// xUnit collection definition that shares a single <see cref="RemoteHostTapeServiceFixture"/>
/// across all test classes in the "RemoteHostTapeService" collection.
/// </summary>
[CollectionDefinition(RemoteHostTapeServiceCollection.Name)]
public class RemoteHostTapeServiceCollection : ICollectionFixture<RemoteHostTapeServiceFixture>
{
    public const string Name = "RemoteHostTapeService";
}
