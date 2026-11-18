using Microsoft.Extensions.Hosting.WindowsServices;
using TapeServiceNET;

// As a Windows Service, the process starts in %windir%\system32. Pin the content root to the
//  executable's folder so appsettings*.json (Kestrel endpoints, session settings) load from the
//  install folder. As a console app, keep the default (current directory) for 'dotnet run'.
var webOptions = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default,
};
var builder = WebApplication.CreateBuilder(webOptions);

// Enable running as a Windows Service (no-op when launched as console).
// The installer registers it as "TapeService"; manual install:
//   sc.exe create TapeService binPath= "\"<path>\tapesvc.exe\"" start= auto
builder.Host.UseWindowsService(serviceOptions =>
{
    serviceOptions.ServiceName = "TapeNET Media Service";
});

// Session lifecycle settings (IdleTimeout, ReaperInterval) — override in appsettings.json.
builder.Services.Configure<TapeSessionSettings>(
    builder.Configuration.GetSection(TapeSessionSettings.Section));

// Singleton registry that owns all active TapeDriveBackend instances, keyed by session ID.
// Disposed automatically on host shutdown, closing any open drives.
builder.Services.AddSingleton<TapeDriveSessionRegistry>();

// Background service that periodically reaps idle sessions.
builder.Services.AddHostedService<TapeSessionReaperService>();

builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<TapeDriveGrpcService>();

app.Run();
