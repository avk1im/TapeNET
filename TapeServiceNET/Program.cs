using TapeServiceNET;

var builder = WebApplication.CreateBuilder(args);

// Enable running as a Windows Service (no-op when launched as console).
// Install with: sc.exe create TapeService binPath="<path>\tapesvc.exe"
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "TapeNET Tape Service";
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
