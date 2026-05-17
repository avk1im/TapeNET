using System.IO;

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// An <see cref="ITapeServiceHost"/> for remote multi-volume service-layer tests.
/// <para>
/// Mirrors <see cref="MultiVolumeTapeServiceHost"/> but performs volume swaps via
///  <see cref="TapeServiceBase.InsertRemoteVirtualMediaAsync"/> (the <c>InsertMedia</c>
///  gRPC RPC) instead of the local <c>InsertVirtualMedia</c> call, so the session
///  remains alive across tape changes.
/// </para>
/// </summary>
/// <remarks>
/// <b>Volume order:</b> pass volumes in backup order (index 0 = first/vol-1, …).
///  During restore, <c>volumeNeeded</c> (1-based) selects the correct entry.
/// <para>
/// <b>Drive service interaction:</b> the service calls <c>_drive.UnloadMedia()</c>
///  itself before asking the host, so the swap callbacks only need to issue
///  <see cref="TapeServiceBase.InsertRemoteVirtualMediaAsync"/>.
/// </para>
/// </remarks>
public sealed class RemoteMultiVolumeServiceHost(
    IReadOnlyList<TempVirtualMedia> volumes) : TestTapeServiceHost
{
    // ── Service reference ─────────────────────────────────────────────────────

    /// <summary>
    /// The <see cref="TapeServiceBase"/> this host drives. Must be set immediately
    ///  after construction (before any backup/restore operations).
    /// </summary>
    public TapeServiceBase Service { get; set; } = null!;

    // ── Volume tracking ───────────────────────────────────────────────────────

    /// <summary>
    /// Total number of volumes inserted (across backup and restore phases).
    /// </summary>
    public int VolumesInserted { get; private set; }

    // ── Volume-swap callbacks ─────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Always agrees to continue — the actual media swap happens in
    ///  <see cref="OnInsertNewMediaConfirm"/>.
    /// </remarks>
    public override bool OnVolumeFullConfirm(int currentVolume, int nextVolume,
        int filesProcessed, int totalFiles, long bytesBackedup)
        => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Creates a fresh tape for the next backup volume (0-based index = nextVolume − 1).
    /// Returns <see langword="false"/> if the requested volume index is out of range.
    /// </remarks>
    public override bool OnInsertNewMediaConfirm(int nextVolume)
    {
        int idx = nextVolume - 1;
        if (idx < 0 || idx >= volumes.Count)
            return false;

        var media = volumes[idx];
        var caps  = media.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;
        bool ok = Service.InsertRemoteVirtualMedia(
            media.ContentPath,
            media.ContentCapacity,
            media.InitiatorPath,
            media.InitiatorCapacity,
            caps,
            mediaMode: FileMode.Create);

        if (ok) VolumesInserted++;
        return ok;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Always agrees to continue — the actual media swap happens in
    ///  <see cref="OnInsertMediaConfirm"/>.
    /// </remarks>
    public override bool OnVolumeContinueConfirm(int volumeNeeded, RestoreMode mode)
        => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Re-inserts a previously written volume (<paramref name="volumeNeeded"/> is
    ///  1-based) from the pre-provided <see cref="volumes"/> list.
    /// Returns <see langword="false"/> if the requested volume index is out of range.
    /// </remarks>
    public override bool OnInsertMediaConfirm(int volumeNeeded, RestoreMode mode)
    {
        int idx = volumeNeeded - 1;
        if (idx < 0 || idx >= volumes.Count)
            return false;

        var media = volumes[idx];
        var caps  = media.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;
        bool ok = Service.InsertRemoteVirtualMedia(
            media.ContentPath,
            media.ContentCapacity,
            media.InitiatorPath,
            media.InitiatorCapacity,
            caps,
            mediaMode: FileMode.Open);

        if (ok) VolumesInserted++;
        return ok;
    }
}
