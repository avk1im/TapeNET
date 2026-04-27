using System.IO;

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Virtual;

using TapeLibNET.Tests.Helpers; // TestTapeServiceHost

namespace TapeConNET.Tests.Helpers;

/// <summary>
/// An <see cref="ITapeServiceHost"/> for multi-volume service-layer tests.
/// <para>
/// Extends <see cref="TestTapeServiceHost"/> (inheriting its full recording and
///  assertion surface — <see cref="TestTapeServiceHost.Reports"/>,
///  <see cref="TestTapeServiceHost.StateChanges"/>,
///  <see cref="TestTapeServiceHost.HasErrors"/>, etc.) and overrides the four
///  volume-swap callbacks to perform automatic file-backed volume swapping using
///  pre-created <see cref="TempVirtualMedia"/> instances:
/// <list type="bullet">
///  <item><see cref="OnVolumeFullConfirm"/> / <see cref="OnInsertNewMediaConfirm"/>
///   — used during backup; inserts the next blank volume.</item>
///  <item><see cref="OnVolumeContinueConfirm"/> / <see cref="OnInsertMediaConfirm"/>
///   — used during restore; re-inserts a previously written volume.</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// <b>Volume order:</b> pass volumes in backup order (index 0 = first/vol-1,
///  index 1 = second/vol-2, …). During restore, <c>volumeNeeded</c> (1-based)
///  selects the correct entry from the list.
/// <para>
/// <b>Drive service interaction:</b> the service calls <c>_drive.UnloadMedia()</c>
///  itself before asking the host, so the swap callbacks only need to call
///  <see cref="TapeServiceBase.InsertVirtualMedia"/> with the next descriptor.
/// </para>
/// </remarks>
public sealed class MultiVolumeTapeServiceHost(
    IReadOnlyList<TempVirtualMedia> volumes) : TestTapeServiceHost
{
    // ── Service reference (set after construction to break the circular dependency) ──

    /// <summary>
    /// The <see cref="TapeServiceBase"/> this host drives. Must be set immediately
    ///  after construction (before any backup/restore operations).
    /// </summary>
    public TapeServiceBase Service { get; set; } = null!;

    // ── Volume tracking ───────────────────────────────────────────────────────

    /// <summary>
    /// Total number of volumes inserted (across both backup and restore phases).
    /// Incremented by each successful <see cref="OnInsertNewMediaConfirm"/> or
    ///  <see cref="OnInsertMediaConfirm"/> call.
    /// </summary>
    public int VolumesInserted { get; private set; }

    // ── Volume-swap callbacks ─────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Always agrees to continue — the actual media swap happens in
    ///  <see cref="OnInsertNewMediaConfirm"/> after the service ejects the current volume.
    /// </remarks>
    public override bool OnVolumeFullConfirm(int currentVolume, int nextVolume,
        int filesProcessed, int totalFiles, long bytesBackedup)
        => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Inserts the next blank volume (0-based index = <paramref name="nextVolume"/> − 1)
    ///  from the pre-provided <see cref="volumes"/> list.
    /// Returns <see langword="false"/> if the requested volume index is out of range.
    /// </remarks>
    public override bool OnInsertNewMediaConfirm(int nextVolume)
    {
        int idx = nextVolume - 1;
        if (idx < 0 || idx >= volumes.Count)
            return false;

        bool ok = Service.InsertVirtualMedia(MakeDescriptor(volumes[idx]), FileMode.Create);
        if (ok) VolumesInserted++;
        return ok;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Always agrees to continue — the actual media swap happens in
    ///  <see cref="OnInsertMediaConfirm"/> after the service ejects the current volume.
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

        bool ok = Service.InsertVirtualMedia(MakeDescriptor(volumes[idx]), FileMode.Open);
        if (ok) VolumesInserted++;
        return ok;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="VirtualMediaDescriptor"/> from a <see cref="TempVirtualMedia"/>
    ///  using the media's own recorded capacity values.
    /// </summary>
    private static VirtualMediaDescriptor MakeDescriptor(TempVirtualMedia media)
        => new(media.ContentPath,
               media.ContentCapacity,
               media.InitiatorPath,
               media.HasInitiator ? media.InitiatorCapacity : 0);
}
