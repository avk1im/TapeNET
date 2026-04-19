using System.Net;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TapeLibNET.Remote;
using TapeServiceNET;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// xUnit fixture that hosts a <see cref="TapeDriveGrpcService"/> in-process on localhost
/// with a random available port. Shared across all tests in a collection via
/// <see cref="IAsyncLifetime"/> so the server starts once and stops when all tests finish.
/// <para>
/// The fixture creates a gRPC channel that <see cref="RemoteVirtualTapeFixture"/> uses
/// to construct <see cref="RemoteTapeDriveBackend"/> instances.
/// </para>
/// </summary>
public sealed class LocalHostTapeServiceFixture : IAsyncLifetime, IDisposable, ITapeServiceFixture
{
    private WebApplication? _app;
    private GrpcChannel? _channel;

    /// <summary>The gRPC channel connected to the in-process server.</summary>
    public GrpcChannel Channel => _channel
        ?? throw new InvalidOperationException("Service not started. Await InitializeAsync first.");

    /// <summary>The base address of the in-process server (e.g. http://localhost:12345).</summary>
    public string Address { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // Override any inherited Kestrel endpoint config (e.g. from TapeServiceNET's appsettings.json)
        //  with an empty Endpoints section, then bind programmatically to a random port
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Grpc:Url"] = "http://127.0.0.1:0",
            ["Kestrel:Endpoints:Grpc:Protocols"] = "Http2",
        });

        // Suppress noisy ASP.NET Core logs during tests
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton<TapeDriveSession>();
        builder.Services.AddGrpc();

        _app = builder.Build();
        _app.MapGrpcService<TapeDriveGrpcService>();

        // Start the server in the background
        await _app.StartAsync();

        // Resolve the actual port the server is listening on
        var server = _app.Services.GetRequiredService<IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();
        Address = addressesFeature!.Addresses.First();

        _channel = GrpcChannel.ForAddress(Address, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 16 * 1024 * 1024,
            MaxSendMessageSize = 16 * 1024 * 1024,
        });
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
}

/// <summary>
/// xUnit collection definition that shares a single <see cref="LocalHostTapeServiceFixture"/>
/// across all test classes in the "LocalHostTapeService" collection.
/// </summary>
[CollectionDefinition(Name)]
public class LocalHostTapeServiceCollection : ICollectionFixture<LocalHostTapeServiceFixture>
{
    public const string Name = "LocalHostTapeService";
}
