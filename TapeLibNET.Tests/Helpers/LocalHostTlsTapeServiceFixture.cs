using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;
using TapeServiceNET;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// xUnit fixture that hosts a <see cref="TapeDriveGrpcService"/> in-process on localhost
/// over <b>TLS / HTTPS</b>, on a random available port. Shared across all tests in the
/// <see cref="LocalHostTlsTapeServiceCollection"/> collection.
/// <para>
/// TLS certificate path and password are read via <see cref="TlsTestSettings"/> from
/// <c>remote-test-settings.json</c> or <c>TAPE_REMOTE_TLS_CERT_*</c> environment
/// variables. When the cert is not configured or the HTTPS connection cannot be
/// established, <see cref="IsConfigured"/> is set to <see langword="false"/> and every
/// test in the collection skips gracefully.
/// </para>
/// </summary>
public sealed class LocalHostTlsTapeServiceFixture : IAsyncLifetime, IDisposable, ITapeServiceFixture
{
    private WebApplication? _app;
    private GrpcChannel? _channel;

    /// <summary>Whether TLS was configured and the server started. Tests should skip when <c>false</c>.</summary>
    public bool IsConfigured { get; private set; }

    /// <summary>Human-readable skip reason when <see cref="IsConfigured"/> is <c>false</c>.</summary>
    public string SkipReason { get; private set; } = string.Empty;

    /// <summary>The gRPC channel connected to the in-process TLS server.</summary>
    public GrpcChannel Channel => _channel
        ?? throw new InvalidOperationException(
            IsConfigured
                ? "TLS service not started. Await InitializeAsync first."
                : $"TLS service unavailable. {SkipReason}");

    /// <summary>The base HTTPS address of the in-process server (e.g. <c>https://localhost:12345</c>).</summary>
    public string Address { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var tls = new TlsTestSettings();

        if (!tls.IsAvailable)
        {
            IsConfigured = false;
            SkipReason = "TLS certificate not configured. Add 'TlsCertPath' and 'TlsCertPassword' "
                       + "to remote-test-settings.json or set TAPE_REMOTE_TLS_CERT_PATH / "
                       + "TAPE_REMOTE_TLS_CERT_PASSWORD environment variables.";
            return;
        }

        // Resolve the certificate path relative to the project directory if not absolute
        string certPath = ResolveCertPath(tls.CertPath!);
        if (!File.Exists(certPath))
        {
            IsConfigured = false;
            SkipReason = $"TLS certificate file not found: {certPath}";
            return;
        }

        // Load the certificate ourselves so we can pass the X509Certificate2 object directly
        // to UseHttps. Passing a file path goes through Kestrel's ConfigurationLoader, which
        // can be hijacked by any appsettings cert path that leaks through. Using the object
        // overload bypasses that code path entirely.
        X509Certificate2 cert;
        try
        {
            cert = new X509Certificate2(certPath, tls.CertPassword);
        }
        catch (Exception ex)
        {
            IsConfigured = false;
            SkipReason = $"Failed to load TLS certificate from '{certPath}': {ex.Message}";
            return;
        }

        var builder = WebApplication.CreateBuilder();

        // Clear all configuration sources so no appsettings cert paths bleed into Kestrel.
        builder.Configuration.Sources.Clear();

        // Bind to a random HTTPS port on 127.0.0.1.
        // ListenLocalhost(0, ...) does not support ephemeral ports — use Listen(IPAddress.Loopback, 0).
        // Pass the pre-loaded certificate object to UseHttps to bypass Kestrel's config-based loader.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, 0, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
                listenOptions.UseHttps(cert);
            });
        });

        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<TapeDriveSessionRegistry>();
        builder.Services.AddGrpc();

        _app = builder.Build();
        _app.MapGrpcService<TapeDriveGrpcService>();

        try
        {
            await _app.StartAsync();
        }
        catch (Exception ex)
        {
            IsConfigured = false;
            SkipReason = $"TLS server failed to start: {ex.Message}";
            _app = null;
            return;
        }

        // Resolve the actual port the server chose
        var server = _app.Services.GetRequiredService<IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();
        Address = addressesFeature!.Addresses.First();

        // Build an HTTPS channel. GrpcChannel requires SocketsHttpHandler (not HttpClientHandler)
        //  to support connectivity state tracking used by ConnectAsync.
        //  Configure TLS options on the SocketsHttpHandler's SslOptions when bypassing cert validation.
        var socketsHandler = new System.Net.Http.SocketsHttpHandler();
        if (tls.DangerousAcceptAny)
        {
            socketsHandler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                // Bypass all certificate validation for self-signed dev certificates.
                // This is intentionally verbose and is only reached when DangerousAcceptAny is true.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        }

        _channel = GrpcChannel.ForAddress(Address, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 16 * 1024 * 1024,
            MaxSendMessageSize    = 16 * 1024 * 1024,
            HttpHandler           = socketsHandler,
        });

        // The server was just started in-process — it is reachable by definition.
        IsConfigured = true;
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();

        if (_app != null)
            await _app.StopAsync();

        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }

    /// <summary>
    /// Resolves a certificate path: returns as-is if absolute, otherwise looks relative
    /// to the project directory and then next to the test assembly.
    /// </summary>
    private static string ResolveCertPath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        // Try relative to the solution / project directory first
        string? dir = Path.GetDirectoryName(typeof(LocalHostTlsTapeServiceFixture).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "TapeLibNET.Tests.csproj")))
                return Path.GetFullPath(Path.Combine(dir, path));
            dir = Path.GetDirectoryName(dir);
        }

        // Fall back: relative to solution root
        string? solutionDir = Path.GetDirectoryName(typeof(LocalHostTlsTapeServiceFixture).Assembly.Location);
        return Path.GetFullPath(Path.Combine(solutionDir ?? ".", path));
    }
}

/// <summary>
/// xUnit collection definition that shares a single <see cref="LocalHostTlsTapeServiceFixture"/>
/// across all test classes in the "LocalHostTlsTapeService" collection.
/// </summary>
[CollectionDefinition(LocalHostTlsTapeServiceCollection.Name)]
public class LocalHostTlsTapeServiceCollection : ICollectionFixture<LocalHostTlsTapeServiceFixture>
{
    public const string Name = "LocalHostTlsTapeService";
}
