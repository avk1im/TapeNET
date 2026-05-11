using Microsoft.Extensions.Configuration;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Reads TLS configuration for integration tests from <c>remote-test-settings.json</c>
/// (gitignored) and/or <c>TAPE_REMOTE_*</c> environment variables.
/// <para>
/// Extend <c>remote-test-settings.json</c> (create from <c>.example</c>) with:
/// </para>
/// <code>
/// {
///   "RemoteHost":   "192.168.1.50",
///   "RemoteTlsPort": 50552,
///   "TlsCertPath":  "certs/tapesvc.pfx",
///   "TlsCertPassword": "Test$01",
///   "DangerousAcceptAnyServerCertificate": true
/// }
/// </code>
/// <para>
/// Equivalent environment variables (prefix <c>TAPE_REMOTE_</c>):
/// <c>TAPE_REMOTE_TLS_PORT</c>, <c>TAPE_REMOTE_TLS_CERT_PATH</c>,
/// <c>TAPE_REMOTE_TLS_CERT_PASSWORD</c>, <c>TAPE_REMOTE_DANGEROUS_ACCEPT_ANY</c>.
/// </para>
/// </summary>
public sealed class TlsTestSettings
{
    /// <summary>Path to the PFX certificate file, or <see langword="null"/> when not set.</summary>
    public string? CertPath { get; }

    /// <summary>PFX certificate password, or <see langword="null"/> when not set.</summary>
    public string? CertPassword { get; }

    /// <summary>
    /// When <see langword="true"/>, the test gRPC channel disables server certificate
    /// validation (<c>DangerousAcceptAnyServerCertificateValidator</c>).
    /// Intended for self-signed dev certificates only.
    /// </summary>
    public bool DangerousAcceptAny { get; }

    /// <summary>TLS port for the remote service. Defaults to 50552.</summary>
    public int TlsPort { get; }

    /// <summary>
    /// <see langword="true"/> if the minimum TLS configuration is present:
    /// a certificate path and password were supplied.
    /// </summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(CertPath)
                            && !string.IsNullOrWhiteSpace(CertPassword);

    private const int DefaultTlsPort = 50552;
    private const string EnvPrefix   = "TAPE_REMOTE_";
    private const string SettingsFileName = "remote-test-settings.json";

    public TlsTestSettings()
    {
        var configBuilder = new ConfigurationBuilder();

        // Look for settings file next to the test assembly
        string? assemblyDir = Path.GetDirectoryName(typeof(TlsTestSettings).Assembly.Location);
        if (assemblyDir != null)
        {
            string path = Path.Combine(assemblyDir, SettingsFileName);
            if (File.Exists(path))
                configBuilder.AddJsonFile(path, optional: true);
        }

        // Also look in the project/solution directory (for dev convenience)
        string? projectDir = FindProjectDirectory();
        if (projectDir != null)
        {
            string path = Path.Combine(projectDir, SettingsFileName);
            if (File.Exists(path))
                configBuilder.AddJsonFile(path, optional: true);
        }

        configBuilder.AddEnvironmentVariables(EnvPrefix);

        var config = configBuilder.Build();

        CertPath     = config["TlsCertPath"]
                    ?? Environment.GetEnvironmentVariable($"{EnvPrefix}TLS_CERT_PATH");
        CertPassword = config["TlsCertPassword"]
                    ?? Environment.GetEnvironmentVariable($"{EnvPrefix}TLS_CERT_PASSWORD");

        bool dangerousAcceptAny = false;
        string? dangerousStr = config["DangerousAcceptAnyServerCertificate"]
                            ?? Environment.GetEnvironmentVariable($"{EnvPrefix}DANGEROUS_ACCEPT_ANY");
        if (!string.IsNullOrEmpty(dangerousStr))
            bool.TryParse(dangerousStr, out dangerousAcceptAny);
        DangerousAcceptAny = dangerousAcceptAny;

        int tlsPort = DefaultTlsPort;
        string? portStr = config["RemoteTlsPort"]
                       ?? Environment.GetEnvironmentVariable($"{EnvPrefix}TLS_PORT");
        if (!string.IsNullOrEmpty(portStr) && int.TryParse(portStr, out int parsed))
            tlsPort = parsed;
        TlsPort = tlsPort;
    }

    private static string? FindProjectDirectory()
    {
        string? dir = Path.GetDirectoryName(typeof(TlsTestSettings).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "TapeLibNET.Tests.csproj")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
