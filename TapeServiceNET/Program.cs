using TapeServiceNET;

var builder = WebApplication.CreateBuilder(args);

// Enable running as a Windows Service (no-op when launched as console).
// Install with: sc.exe create TapeService binPath="<path>\tapesvc.exe"
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "TapeNET Tape Service";
});

// Singleton registry that owns all active TapeDriveBackend instances, keyed by session ID.
// Disposed automatically on host shutdown, closing any open drives.
builder.Services.AddSingleton<TapeDriveSessionRegistry>();

builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<TapeDriveGrpcService>();

app.Run();
