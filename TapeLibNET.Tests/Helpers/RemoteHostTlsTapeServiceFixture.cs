using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using TapeLibNET.Remote;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// xUnit fixture that connects to an already-running <c>TapeServiceNET</c> at a
/// configured remote address using <b>TLS / HTTPS</b>. Configuration is read from
/// <c>remote-test-settings.json</c> (gitignored) with environment variable overrides.
/// <para>
/// When no remote host is configured, or the TLS certificate settings are absent, or the
/// HTTPS endpoint is unreachable, <see cref="IsConfigured"/> is set to
/// <see langword="false"/> and tests skip gracefully.
/// </para>
/// <para>Configuration keys (JSON / <c>TAPE_REMOTE_*</c> environment variables):</para>
/// <list type="table">
///   <listheader><term>Key</term><description>Purpose</description></listheader>
///   <item><term>RemoteHost</term><description>IP address or hostname (required)</description></item>
///   <item><term>RemoteTlsPort</term><description>HTTPS gRPC port (default: 50552)</description></item>
///   <item><term>DangerousAcceptAnyServerCertificate</term><description>Skip TLS cert validation for self-signed dev certs (default: false)</description></item>
/// </list>
/// <para>
/// The TLS certificate path and password are NOT needed for the client fixture — they are
/// used server-side only. What matters on the client is <c>DangerousAcceptAnyServerCertificate</c>
/// when the server presents a self-signed cert.
/// </para>
/// </summary>
public sealed class RemoteHostTlsTapeServiceFixture : IAsyncLifetime, IDisposable, ITapeServiceFixture
{
    private const string EnvPrefix = "TAPE_REMOTE_";
    private const string SettingsFileName = "remote-test-settings.json";

    private GrpcChannel? _channel;

    /// <summary>Whether the remote TLS host was configured and reachable. Tests should skip when <c>false</c>.</summary>
    public bool IsConfigured { get; private set; }

    /// <summary>Human-readable skip reason when <see cref="IsConfigured"/> is <c>false</c>.</summary>
    public string SkipReason { get; private set; } = string.Empty;

    /// <summary>The gRPC channel connected to the remote TLS service.</summary>
    public GrpcChannel Channel => _channel
        ?? throw new InvalidOperationException(
            IsConfigured
                ? "TLS service not started. Await InitializeAsync first."
                : $"Remote TLS service unavailable. {SkipReason}");

    /// <summary>The remote HTTPS service address (e.g. <c>https://192.168.1.50:50552</c>).</summary>
    public string Address { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        // Build configuration: JSON file (optional) → environment variables (override)
        var configBuilder = new ConfigurationBuilder();

        string? assemblyDir = Path.GetDirectoryName(typeof(RemoteHostTlsTapeServiceFixture).Assembly.Location);
        if (assemblyDir != null)
        {
            string path = Path.Combine(assemblyDir, SettingsFileName);
            if (File.Exists(path))
                configBuilder.AddJsonFile(path, optional: true);
        }

        string? projectDir = FindProjectDirectory();
        if (projectDir != null)
        {
            string path = Path.Combine(projectDir, SettingsFileName);
            if (File.Exists(path))
                configBuilder.AddJsonFile(path, optional: true);
        }

        configBuilder.AddEnvironmentVariables(EnvPrefix);

        var config = configBuilder.Build();

        // RemoteHost is required — same key as the plain-HTTP fixture
        string? host = config["RemoteHost"]
                    ?? Environment.GetEnvironmentVariable($"{EnvPrefix}HOST");

        if (string.IsNullOrWhiteSpace(host))
        {
            IsConfigured = false;
            SkipReason = $"Remote host not configured. Set '{EnvPrefix}HOST' environment variable "
                       + $"or create '{SettingsFileName}' with a 'RemoteHost' key.";
            return;
        }

        var tls = new TlsTestSettings();  // reads DangerousAcceptAny and TlsPort from the same file

        var settings = new RemoteHostSettings(
            Host: host,
            Port: tls.TlsPort,
            UseTls: true,
            DangerousAcceptAnyServerCertificate: tls.DangerousAcceptAny);

        Address = settings.ChannelAddress;
        IsConfigured = true;

        _channel = GrpcChannel.ForAddress(Address, settings.BuildChannelOptions());

        // Probe the channel so tests can skip cleanly instead of failing with Unavailable exceptions
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _channel.ConnectAsync(cts.Token);
            IsConfigured = true;
        }
        catch (Exception ex)
        {
            IsConfigured = false;
            SkipReason = $"Remote TLS service at {Address} is not reachable: {ex.Message}";
            _channel.Dispose();
            _channel = null;
        }
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

    private static string? FindProjectDirectory()
    {
        string? dir = Path.GetDirectoryName(typeof(RemoteHostTlsTapeServiceFixture).Assembly.Location);
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
/// xUnit collection definition that shares a single <see cref="RemoteHostTlsTapeServiceFixture"/>
/// across all test classes in the "RemoteHostTlsTapeService" collection.
/// </summary>
[CollectionDefinition(RemoteHostTlsTapeServiceCollection.Name)]
public class RemoteHostTlsTapeServiceCollection : ICollectionFixture<RemoteHostTlsTapeServiceFixture>
{
    public const string Name = "RemoteHostTlsTapeService";
}
