using Microsoft.Extensions.Logging;

using TapeLibNET.Virtual;
using TapeLibNET.Services;

namespace TapeLibNET.Tests.Helpers;


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
///  <item><see cref="OnVolumeContinueSelect"/> / <see cref="OnInsertMediaConfirm"/>
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
/// <para>
/// <b>Per-volume faults</b> (<see cref="VolumeFaults"/>): the deterministic way to make ONE cartridge misbehave at
///  service level — armed on every insertion of that volume, cleared on every other.
/// </para>
/// </remarks>
public sealed class MultiVolumeTapeServiceHost(
    IReadOnlyList<TempVirtualMedia> volumes,
    ILogger? logger = null) : TestTapeServiceHost(logger)
{
    // ── Service reference (set after construction to break the circular dependency) ──

    /// <summary>
    /// The <see cref="TapeServiceBase"/> this host drives. Must be set immediately
    ///  after construction (before any backup/restore operations).
    /// </summary>
    public TestTapeService Service { get; set; } = null!;

    // ── Volume tracking ───────────────────────────────────────────────────────

    /// <summary>
    /// Total number of volumes inserted (across both backup and restore phases).
    ///  Incremented by each successful <see cref="OnInsertNewMediaConfirm"/> or
    ///  <see cref="OnInsertMediaConfirm"/> call.
    /// </summary>
    public int VolumesInserted { get; private set; }

    /// <summary>
    /// Called after a volume was inserted successfully, with its 1-based number — for both backup
    ///  (<see cref="OnInsertNewMediaConfirm"/>) and restore (<see cref="OnInsertMediaConfirm"/>). Lets a test arm a fault
    ///  on exactly that cartridge. Runs AFTER <see cref="VolumeFaults"/> was applied.
    /// </summary>
    public Action<int>? AfterMediaInserted { get; set; }

    // ── Per-volume faults ─────────────────────────────────────────────────────

    /// <summary>
    /// Faults to arm on the drive's backend whenever the volume (1-based key) is inserted — after the insertion, before the
    ///  service loads and reads the cartridge. While any plan is registered, EVERY insertion first clears the read
    ///  injectors of both partitions, so a fault never leaks onto another cartridge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Remove a volume's entry to model "the cartridge was cleaned": its next insertion is clean.
    /// </para>
    /// <para>
    /// The injectors are owned by the backend and survive the load; tape SURGERY
    ///  (<c>VirtualTapeMedia.TruncateAt</c> etc.) does not belong here — the medium is not loaded yet at insertion. Cut a
    ///  volume between operations instead.
    /// </para>
    /// </remarks>
    public Dictionary<int, Action<VirtualTapeDriveBackend>> VolumeFaults { get; } = [];

    /// <summary>
    /// Plans <paramref name="volume"/> as UNIDENTIFIABLE: every block read from it — media header, TOC copies, the set's
    ///  first block — comes back with its magic corrupted, in both partitions. The agent can identify the cartridge by
    ///  nothing (Design-TocLocation §7) and must reject it.
    /// </summary>
    public void MakeVolumeUnreadable(int volume) => VolumeFaults[volume] = CorruptEveryRead;

    /// <summary>Corrupts the first bytes of every read, both partitions: nothing identifies, nothing parses.</summary>
    public static void CorruptEveryRead(VirtualTapeDriveBackend backend)
    {
        backend.ContentReadFaults.CorruptAlways(bits: 2, offset: 0);
        backend.InitiatorReadFaults.CorruptAlways(bits: 2, offset: 0);
    }

    // Whether a plan armed the injectors for the cartridge now in the drive — the next insertion disarms them even when
    //  that plan has since been removed ("the cartridge was cleaned").
    private bool m_faultsArmed;

    /// <summary>
    /// Disarms what a plan armed for the previous cartridge, applies this volume's plan (if any), then the test's own hook.
    ///  Untouched when no plan was ever involved, so tests arming faults by hand keep full control.
    /// </summary>
    private void OnVolumeInserted(int volume)
    {
        VolumesInserted++;
        bool planned = VolumeFaults.TryGetValue(volume, out var arm);
        if (planned || m_faultsArmed)
        {
            VirtualTapeDriveBackend backend = Service.VirtualBackend
                ?? throw new InvalidOperationException("Volume faults need a virtual drive");
            backend.ContentReadFaults.Reset();
            backend.InitiatorReadFaults.Reset();
            m_faultsArmed = false;
            if (planned)
            {
                arm!(backend);
                m_faultsArmed = true;
            }
        }
        AfterMediaInserted?.Invoke(volume);
    }

    // ── Volume-swap callbacks ─────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Always agrees to continue — the actual media swap happens in
    ///  <see cref="OnInsertNewMediaConfirm"/> after the service ejects the current volume.
    /// </remarks>
    public override bool OnVolumeFullConfirm(int currentVolume, int nextVolume,
        int filesProcessed, int totalFiles, long bytesBackedup, long totalBytes)
        => true;

    /// <inheritdoc/>
    /// <returns>
    /// <see langword="false"/> if the requested volume index is out of range.
    /// </returns>
    /// <remarks>
    /// Inserts the next blank volume (0-based index = <paramref name="nextVolume"/> − 1)
    ///  from the list <c>volumes</c> provided as the c'tor parameter to
    ///  <see cref="MultiVolumeTapeServiceHost"/>.
    /// </remarks>
    public override bool OnInsertNewMediaConfirm(int nextVolume)
    {
        int idx = nextVolume - 1;
        if (idx < 0 || idx >= volumes.Count)
            return false;

        bool ok = Service.InsertVirtualMedia(MakeDescriptor(volumes[idx]), FileMode.Create);
        if (ok)
            OnVolumeInserted(nextVolume);
        return ok;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Inserts by default — the actual media swap happens in <see cref="OnInsertMediaConfirm"/>
    ///  after the service ejects the current volume.
    ///  Queue <see cref="TestTapeServiceHost.VolumeContinueAnswers"/> to skip or end instead.
    /// </remarks>
    public override VolumeAction OnVolumeContinueSelect(int volumeNeeded, RestoreMode mode)
        => VolumeContinueAnswers.Count > 0 ? VolumeContinueAnswers.Dequeue() : VolumeAction.Insert;

    /// <inheritdoc/>
    /// <remarks>
    /// Re-inserts a previously written volume (<paramref name="volumeNeeded"/> is
    ///  1-based) from the <c>volumes</c> list provided to the class c'tor.
    /// Returns <see langword="false"/> if the requested volume index is out of range.
    /// </remarks>
    public override bool OnInsertMediaConfirm(int volumeNeeded, RestoreMode mode)
    {
        int idx = volumeNeeded - 1;
        if (idx < 0 || idx >= volumes.Count)
            return false;

        bool ok = Service.InsertVirtualMedia(MakeDescriptor(volumes[idx]), FileMode.Open);
        if (ok)
            OnVolumeInserted(volumeNeeded);
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