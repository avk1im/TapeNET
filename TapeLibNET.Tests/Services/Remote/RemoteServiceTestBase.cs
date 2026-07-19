using System.IO;

using Microsoft.Extensions.Logging.Abstractions;

using TapeLibNET;
using TapeLibNET.Remote;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers; // TempVirtualMedia, TestTapeServiceHost, RemoteMultiVolumeServiceHost
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Services.Remote;

/// <summary>
/// Shared infrastructure for remote service-layer round-trip tests.
/// <para>
/// Mirrors <see cref="ServiceTestBase"/> but drives <see cref="TapeServiceBase"/> via
///  a remote gRPC backend (the in-process server started by
///  <see cref="LocalHostTapeServiceFixture"/>).  The fixture address is parsed into a
///  <see cref="RemoteHostSettings"/> instance; all helper methods
///  (<see cref="OpenAndFormatRemoteAsync"/>, <see cref="ReopenRemoteAsync"/>, etc.)
///  route drive creation and reopening through
///  <see cref="TapeServiceBase.CreateRemoteVirtualDriveAsync"/> and
///  <see cref="TapeServiceBase.OpenRemoteVirtualFileAsync"/> instead of the local
///  <see cref="TapeServiceBase.OpenVirtualDriveAsync"/>.
/// </para>
/// <para>
/// <b>File access:</b> <see cref="TempVirtualMedia"/> files are created on the local
///  file system; because the gRPC server runs in-process the same paths are accessible
///  from both the client and server sides.
/// </para>
/// </summary>
/// <remarks>
/// Initializes the base class and derives <see cref="RemoteSettings"/> from the
///  fixture's address (e.g. <c>http://127.0.0.1:12345</c>).
/// </remarks>
public abstract class RemoteServiceTestBase(LocalHostTapeServiceFixture fixture) : ServiceTestBase
{
    // ── Fixture reference ─────────────────────────────────────────────────────

    /// <summary>The in-process fixture that owns the gRPC channel and address.</summary>
    protected readonly LocalHostTapeServiceFixture Fixture = fixture;

    /// <summary>
    /// <see cref="RemoteHostSettings"/> derived from <see cref="Fixture"/>'s address.
    /// </summary>
    protected RemoteHostSettings RemoteSettings { get; } = ParseAddress(fixture.Address);

    // ── Address parsing ───────────────────────────────────────────────────────

    /// <summary>
    /// Converts a URI string (e.g. <c>http://127.0.0.1:12345</c>) into a plain-HTTP
    ///  <see cref="RemoteHostSettings"/> record.
    /// </summary>
    private static RemoteHostSettings ParseAddress(string address)
    {
        var uri = new Uri(address);
        return new RemoteHostSettings(Host: uri.Host, Port: uri.Port, UseTls: false);
    }

    // ── Single-volume remote factory helpers ──────────────────────────────────

    /// <summary>
    /// Creates a <see cref="TapeServiceBase"/> wired to a new <see cref="TestTapeServiceHost"/>.
    /// </summary>
    protected static (TapeServiceBase service, TestTapeServiceHost host) CreateRemoteService()
    {
        var host    = new TestTapeServiceHost();
        var service = new TapeServiceBase(TestLoggerFactory.Default, host);
        return (service, host);
    }

    /// <summary>
    /// Creates a remote virtual drive for <paramref name="media"/> (equivalent to
    ///  <see cref="ServiceTestBase.OpenAndFormatAsync"/> but routed over gRPC).
    /// Formats the tape and leaves the service ready for backup.
    /// </summary>
    protected async Task<(TapeServiceBase service, TestTapeServiceHost host)> OpenAndFormatRemoteAsync(
        TempVirtualMedia media,
        CancellationToken _ = default)
    {
        var (service, host) = CreateRemoteService();

        var caps = media.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        // OpenRemoteVirtualFileAsync with FileMode.Create opens a new named file-backed drive
        //  on the server side without the temp-path prefix that CreateRemoteVirtualDriveAsync adds.
        Assert.True(
            await service.OpenRemoteVirtualFileAsync(
                RemoteSettings, media.ToVmd(), caps,
                mediaMode: System.IO.FileMode.Create),
            $"OpenRemoteVirtualFileAsync (create) failed: {service.LastError}");
        Assert.True(
            await service.LoadMediaAsync(),
            $"LoadMediaAsync (remote create) failed: {service.LastError}");

        long initSize = media.HasInitiator ? service.DefaultTOCCapacity : -1L;
        Assert.True(
            await service.FormatMediaAsync(initSize, MediaName),
            $"FormatMediaAsync (remote) failed: {service.LastError}");

        return (service, host);
    }

    /// <summary>
    /// Opens an existing remote virtual media file for reading (equivalent to
    ///  <see cref="ServiceTestBase.ReopenAsync"/> but over gRPC).
    /// Loads media and restores the TOC.
    /// </summary>
    protected async Task<(TapeServiceBase service, TestTapeServiceHost host)> ReopenRemoteAsync(
        TempVirtualMedia media,
        CancellationToken _ = default)
    {
        var (service, host) = CreateRemoteService();

        var caps = media.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        Assert.True(
            await service.OpenRemoteVirtualFileAsync(
                RemoteSettings, media.ToVmd(), caps),
            $"OpenRemoteVirtualFileAsync (reopen) failed: {service.LastError}");
        Assert.True(
            await service.LoadMediaAsync(),
            $"LoadMediaAsync (remote reopen) failed: {service.LastError}");
        Assert.True(
            await service.RestoreTOCAsync(),
            $"RestoreTOCAsync (remote reopen) failed: {service.LastError}");

        return (service, host);
    }

    // ── Multi-volume remote factory helpers ───────────────────────────────────

    /// <summary>
    /// Creates a <see cref="TapeServiceBase"/> wired to a <see cref="RemoteMultiVolumeServiceHost"/>
    ///  that services volume-swap callbacks via <c>InsertMedia</c> gRPC RPCs.
    /// </summary>
    protected static (TapeServiceBase service, RemoteMultiVolumeServiceHost host)
        CreateRemoteMultiVolumeService(IReadOnlyList<TempVirtualMedia> volumes)
    {
        var host    = new RemoteMultiVolumeServiceHost(volumes);
        var service = new TapeServiceBase(TestLoggerFactory.Default, host);
        host.Service = service;
        return (service, host);
    }

    /// <summary>
    /// Creates a remote virtual drive for the first volume of a multi-volume sequence
    ///  and formats it. Mirrors <see cref="ServiceTestBase.OpenAndFormatMultiVolumeAsync"/>.
    /// </summary>
    protected async Task<(TapeServiceBase service, RemoteMultiVolumeServiceHost host)>
        OpenAndFormatRemoteMultiVolumeAsync(
            IReadOnlyList<TempVirtualMedia> volumes,
            CancellationToken _ = default)
    {
        var (service, host) = CreateRemoteMultiVolumeService(volumes);

        var first = volumes[0];
        var caps = first.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        // Use OpenRemoteVirtualFileAsync(FileMode.Create) so the server opens the named file
        //  directly without the temp-path prefix that CreateRemoteVirtualDriveAsync adds.
        Assert.True(
            await service.OpenRemoteVirtualFileAsync(
                RemoteSettings, first.ToVmd(), caps,
                mediaMode: System.IO.FileMode.Create),
            $"OpenRemoteVirtualFileAsync (create, multi-vol, vol-1) failed: {service.LastError}");
        Assert.True(
            await service.LoadMediaAsync(),
            $"LoadMediaAsync (remote multi-vol, vol-1) failed: {service.LastError}");

        long initSize = first.HasInitiator ? service.DefaultTOCCapacity : -1L;
        Assert.True(
            await service.FormatMediaAsync(initSize, MediaName),
            $"FormatMediaAsync (remote multi-vol, vol-1) failed: {service.LastError}");

        return (service, host);
    }

    /// <summary>
    /// Opens the last volume of a multi-volume sequence for reading (remote counterpart of
    ///  <see cref="ServiceTestBase.ReopenMultiVolumeAsync"/>).
    /// </summary>
    protected async Task<(TapeServiceBase service, RemoteMultiVolumeServiceHost host)>
        ReopenRemoteMultiVolumeAsync(
            IReadOnlyList<TempVirtualMedia> volumes,
            CancellationToken _ = default)
    {
        var (service, host) = CreateRemoteMultiVolumeService(volumes);

        // The complete TOC is on the last volume; open it first.
        var last = volumes[^1];
        var caps = last.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        Assert.True(
            await service.OpenRemoteVirtualFileAsync(
                RemoteSettings, last.ToVmd(), caps),
            $"OpenRemoteVirtualFileAsync (remote multi-vol reopen) failed: {service.LastError}");
        Assert.True(
            await service.LoadMediaAsync(),
            $"LoadMediaAsync (remote multi-vol reopen) failed: {service.LastError}");
        Assert.True(
            await service.RestoreTOCAsync(),
            $"RestoreTOCAsync (remote multi-vol reopen) failed: {service.LastError}");

        return (service, host);
    }
}
