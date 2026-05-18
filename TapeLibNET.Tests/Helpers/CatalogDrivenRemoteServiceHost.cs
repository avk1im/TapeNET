using System.IO;

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// An <see cref="ITapeServiceHost"/> that drives multi-volume swaps using the server-side
/// session catalog (<see cref="TapeServiceBase.ListRemoteSessionVolumesAsync"/>) rather than
/// a pre-injected list of <see cref="TempVirtualMedia"/> objects.
/// <para>
/// This validates the "production" catalog-driven path used by <c>WpfServiceHost</c> and
///  <see cref="TapeServiceBase.InsertRemoteVirtualMediaAsync"/>:
/// </para>
/// <list type="bullet">
///  <item><description>
///   On backup: <see cref="OnInsertNewMediaConfirm"/> generates a new volume name from
///    the sequence number and inserts it into the session via
///    <see cref="TapeServiceBase.InsertRemoteVirtualMediaAsync"/>.
///  </description></item>
///  <item><description>
///   On restore: <see cref="OnInsertMediaConfirm"/> calls
///    <see cref="TapeServiceBase.ListRemoteSessionVolumesAsync"/>, picks the catalog entry
///    at position <c>volumeNeeded − 1</c> (ordered by creation time, i.e. the natural
///    catalog order), and re-inserts it by <see cref="VirtualMediaDescriptor"/> path.
///  </description></item>
/// </list>
/// <remarks>
/// <b>Naming convention:</b> vol-names follow the pattern <c>&lt;baseVolumeName&gt;_vol{N:D2}</c>,
/// e.g. <c>"tape_vol02"</c>.  The initial volume ("vol01") must already be open when the backup
/// starts; subsequent volumes are auto-created here.
/// </remarks>
/// </summary>
public sealed class CatalogDrivenRemoteServiceHost(
    string baseVolumeName,
    VirtualTapeDriveCapabilities caps,
    long volumeCapacity,
    string tempDirectory) : TestTapeServiceHost
{
    // ── Service reference ─────────────────────────────────────────────────────

    /// <summary>
    /// The <see cref="TapeServiceBase"/> this host drives. Must be set immediately after
    /// construction (before any backup/restore operations begin).
    /// </summary>
    public TapeServiceBase Service { get; set; } = null!;

    // ── Tracking ──────────────────────────────────────────────────────────────

    /// <summary>Total volume insertions serviced (across backup and restore phases).</summary>
    public int VolumesInserted { get; private set; }

    // ── Backup callbacks ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Always agrees — the actual swap happens in <see cref="OnInsertNewMediaConfirm"/>.</remarks>
    public override bool OnVolumeFullConfirm(int currentVolume, int nextVolume,
        int filesProcessed, int totalFiles, long bytesBackedup)
        => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Creates a new named volume for <paramref name="nextVolume"/> by calling
    /// <see cref="TapeServiceBase.InsertRemoteVirtualMediaAsync"/> with a fresh temp path.
    /// The volume name follows <c>&lt;baseVolumeName&gt;_vol{nextVolume:D2}</c>.
    /// </remarks>
    public override bool OnInsertNewMediaConfirm(int nextVolume)
    {
        // Build a deterministic name and full server-side path within the shared temp directory,
        //  so the restore host can resolve by position in the catalog.
        string name     = $"{baseVolumeName}_vol{nextVolume:D2}";
        string filePath = Path.Combine(tempDirectory, $"{name}.vtape");

        var vmd = new VirtualMediaDescriptor(
            ContentPath:                filePath,
            ContentCapacity:            volumeCapacity,
            InitiatorPath:              null,
            InitiatorPartitionCapacity: 0);

        bool ok = Service.InsertRemoteVirtualMedia(vmd, caps, mediaMode: FileMode.Create);
        if (ok) VolumesInserted++;
        return ok;
    }

    // ── Restore callbacks ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Always agrees — the actual swap happens in <see cref="OnInsertMediaConfirm"/>.</remarks>
    public override bool OnVolumeContinueConfirm(int volumeNeeded, RestoreMode mode)
        => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Looks up <paramref name="volumeNeeded"/> (1-based) in the live session catalog
    /// obtained from <see cref="TapeServiceBase.ListRemoteSessionVolumesAsync"/> and
    /// re-inserts it via <see cref="TapeServiceBase.InsertRemoteVirtualMedia"/>.
    /// </remarks>
    public override bool OnInsertMediaConfirm(int volumeNeeded, RestoreMode mode)
    {
        // Catalog is ordered by creation time (oldest first) — same order as volume sequence.
        var catalog = Service.ListRemoteSessionVolumesAsync()
                             .GetAwaiter().GetResult();

        int idx = volumeNeeded - 1;
        if (idx < 0 || idx >= catalog.Count)
            return false;

        var vol = catalog[idx];
        bool ok = Service.InsertRemoteVirtualMedia(vol.Media, vol.Capabilities, mediaMode: FileMode.Open);
        if (ok) VolumesInserted++;
        return ok;
    }
}
