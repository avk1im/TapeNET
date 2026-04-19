using TapeServiceNET;

var builder = WebApplication.CreateBuilder(args);

// Enable running as a Windows Service (no-op when launched as console).
// Install with: sc.exe create TapeService binPath="<path>\tapesvc.exe"
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "TapeNET Tape Service";
});

// Singleton session that owns the active TapeDriveBackend across gRPC requests.
// Disposed automatically on host shutdown, closing any open drive.
builder.Services.AddSingleton<TapeDriveSession>();

builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<TapeDriveGrpcService>();

app.Run();
