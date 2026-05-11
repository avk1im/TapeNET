using System.Net.Http;
using Grpc.Net.Client;

namespace TapeLibNET.Remote;

/// <summary>
/// Connection parameters for a remote tape service.
/// Passed to <see cref="RemoteTapeDriveBackend"/> and <see cref="TapeDrive.CreateRemote"/>
/// to describe how to reach the server and which transport security to apply.
/// </summary>
/// <param name="Host">Hostname or IP address of the tape service.</param>
/// <param name="Port">gRPC port. Default: 50551 (plain) — use 50552 for TLS.</param>
/// <param name="UseTls">
///  When <see langword="true"/>, the gRPC channel uses <c>https://</c>.
///  Requires the server to expose a TLS endpoint (see <c>GrpcTls</c> in
///  <c>TapeServiceNET/appsettings.json</c>).
/// </param>
/// <param name="DangerousAcceptAnyServerCertificate">
///  When <see langword="true"/>, TLS certificate validation is suppressed entirely.
///  <b>For development / self-signed certificates only — never use in production.</b>
///  Ignored when <paramref name="UseTls"/> is <see langword="false"/>.
/// </param>
public record RemoteHostSettings(
    string Host,
    int    Port                              = 50551,
    bool   UseTls                            = false,
    bool   DangerousAcceptAnyServerCertificate = false)
{
    /// <summary>
    /// Display label shown in menus and the status bar, e.g. <c>192.168.178.22:50551</c>.
    /// </summary>
    public string DisplayLabel => $"{Host}:{Port}";

    /// <summary>
    /// The gRPC channel address URI derived from <see cref="UseTls"/>,
    /// e.g. <c>http://tape-server:50551</c> or <c>https://tape-server:50552</c>.
    /// </summary>
    public string ChannelAddress => $"{(UseTls ? "https" : "http")}://{Host}:{Port}";

    /// <summary>
    /// Builds a <see cref="GrpcChannelOptions"/> instance appropriate for these settings.
    /// When <see cref="DangerousAcceptAnyServerCertificate"/> is set, an
    /// <see cref="HttpClientHandler"/> that bypasses certificate validation is injected.
    /// </summary>
    public GrpcChannelOptions BuildChannelOptions()
    {
        // Tape I/O can transfer large blocks — allow up to 16 MB messages (+ 8 KB slack).
        const int maxMessageSize = 16 * 1024 * 1024 + 8 * 1024;

        var options = new GrpcChannelOptions
        {
            MaxReceiveMessageSize = maxMessageSize,
            MaxSendMessageSize    = maxMessageSize,
        };

        if (UseTls && DangerousAcceptAnyServerCertificate)
        {
            // Bypass certificate validation for self-signed / dev certs.
            // This path must never be reached in production builds — the flag name
            // is intentionally verbose to discourage casual use.
            // GrpcChannel requires SocketsHttpHandler (not HttpClientHandler) for connectivity
            //  state tracking; cert bypass is applied via SslOptions instead.
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            };
            options.HttpHandler = handler;
        }

        return options;
    }
}
