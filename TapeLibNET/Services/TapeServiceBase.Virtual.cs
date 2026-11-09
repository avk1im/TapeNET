using TapeLibNET.Virtual;

namespace TapeLibNET.Services;

/// <summary>Outcome of <see cref="TapeServiceBase.CreateVirtualMediaAsync"/>.</summary>
/// <remarks>
///  <see cref="Failed"/> is the <see langword="default"/> value <c>0</c> on purpose: a swallowed exception never reads as success.
///  E.g., in <c>TapeWinNET</c>, <c>MainViewModel.RunBusyAsync</c> returns <see langword="default"/> when an exception escapes.
/// </remarks>
public enum VirtualMediaCreateOutcome
{
    /// <summary>Creating, loading or formatting failed; see <see cref="TapeServiceBase.LastError"/>.</summary>
    Failed = 0,
    /// <summary>New media created, loaded and formatted with an empty TOC — ready for backup.</summary>
    Created,
    /// <summary>Existing media/files found and the overwrite was declined (or could not be confirmed unattended). Nothing touched.</summary>
    Declined,
}

// ── Virtual media: create new / open existing ──────────────────────────────────
//  The two intents every app needs, as one policy: CREATE probes what it would destroy and asks first;
//   OPEN never creates anything. Both build on OpenVirtualDriveAsync (TapeServiceBase.cs), which stays the
//   low-level, mode-explicit entry point.
public partial class TapeServiceBase
{
    #region Drive lifecycle: create / open virtual media

    /// <summary>
    /// Creates NEW virtual media and leaves it loaded and formatted (media header + empty TOC), ready for backup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// File-backed media: when files exist at the target, the existing content is probed and described — backup media
    ///  with its sets, a calibration cartridge, or a foreign file — and the overwrite is confirmed through
    ///  <see cref="ITapeServiceHost.Confirm"/> with a <see langword="false"/> default, so an unattended host declines.
    ///  <paramref name="overwriteWithoutPrompt"/> skips the question, never the description (logged as a warning).
    /// </para>
    /// <para>
    /// The media records the mark profile of <paramref name="capabilities"/>, so every later open reads it back.
    /// </para>
    /// <para>
    /// Single-partition media deletes stale conventional initiator files, so a later open does not detect a partition
    ///  that belonged to the media overwritten. The initiator partition is formatted at
    ///  <see cref="VirtualMediaDescriptor.InitiatorPartitionCapacity"/>.
    /// </para>
    /// </remarks>
    /// <param name="capabilities">Drive profile; <c>SupportsInitiatorPartition</c> must agree with the descriptor.</param>
    /// <param name="vmd">Where and how large; <see cref="VirtualMediaDescriptor.InMemory"/> for RAM-backed media.</param>
    /// <param name="mediaName">Media name for the new TOC; <see langword="null"/> for <see cref="DefaultNewMediaName"/>.</param>
    /// <param name="overwriteWithoutPrompt">Overwrite existing files without asking (e.g. a dialog already warned).</param>
    public async Task<VirtualMediaCreateOutcome> CreateVirtualMediaAsync(
        VirtualTapeDriveCapabilities capabilities,
        VirtualMediaDescriptor vmd,
        string? mediaName = null,
        bool overwriteWithoutPrompt = false,
        VirtualTapeDriveIoRate? ioRate = null,
        VirtualTapeEwProfile? ewProfile = null,
        CancellationToken cancellationToken = default)
    {
        if (!vmd.InMemory)
        {
            if (string.IsNullOrWhiteSpace(vmd.ContentPath))
            {
                LastError = "No content file given for the new virtual media";
                LogErr(LastError);
                return VirtualMediaCreateOutcome.Failed;
            }

            await ReleaseOwnVirtualFilesAsync(vmd.ContentPath).ConfigureAwait(false);

            string? existing = await DescribeExistingVirtualMediaAsync(vmd, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!overwriteWithoutPrompt && !_host.Confirm($"{existing} Overwrite it?", defaultAnswer: false))
                {
                    LastError = $"Not overwritten: {existing}";
                    LogWarn(LastError);
                    return VirtualMediaCreateOutcome.Declined;
                }
                LogWarn($"Overwriting: {existing}");
            }

            try
            {
                VirtualMediaPaths.PrepareForNewMedia(vmd);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Couldn't prepare the files for new virtual media: {ex.Message}");
                return VirtualMediaCreateOutcome.Failed;
            }
        }

        if (!await OpenVirtualDriveAsync(capabilities, vmd, FileMode.Create, ioRate, ewProfile).ConfigureAwait(false))
            return VirtualMediaCreateOutcome.Failed;
        if (!await LoadMediaAsync().ConfigureAwait(false))
            return VirtualMediaCreateOutcome.Failed;

        long initiatorSize = capabilities.SupportsInitiatorPartition
                             && vmd.InitiatorPath is not null
                             && vmd.InitiatorPartitionCapacity > 0
            ? vmd.InitiatorPartitionCapacity
            : -1;       // single partition: TOC in set
        if (!await FormatMediaAsync(initiatorSize, mediaName).ConfigureAwait(false))
            return VirtualMediaCreateOutcome.Failed;

        return VirtualMediaCreateOutcome.Created;
    }

    /// <summary>
    /// Opens EXISTING file-backed virtual media; never creates any. Fails with a clear <see cref="LastError"/> when no
    ///  media exists at <paramref name="contentPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sizes, block-size limits and — when recorded — the mark profile come from the media itself. The initiator
    ///  partition is <paramref name="initiatorPath"/> when given, else found by the <c>_init</c> convention.
    /// </para>
    /// <para>
    /// <paramref name="profile"/> matters only for legacy media that records no profile. When <see langword="null"/>,
    ///  such media is probed (read-only) and opened under the profile its TOC reads with; failing that, setmarks.
    /// </para>
    /// </remarks>
    public async Task<bool> OpenExistingVirtualDriveAsync(
        string contentPath,
        string? initiatorPath = null,
        VirtualTapeDriveCapabilities? profile = null,
        VirtualTapeDriveIoRate? ioRate = null,
        VirtualTapeEwProfile? ewProfile = null,
        CancellationToken cancellationToken = default)
    {
        if (!VirtualMediaPaths.MediaExists(contentPath))
        {
            LastError = $"No virtual media at >{contentPath}< (metadata file " +
                        $">{VirtualMediaPaths.GetMetadataPath(contentPath)}< not found)";
            LogErr($"Couldn't open virtual drive: {LastError}");
            return false;
        }

        string? initiator = VirtualMediaPaths.FindInitiatorFile(contentPath, initiatorPath);
        if (!string.IsNullOrWhiteSpace(initiatorPath) && initiator is null)
        {
            LastError = $"No initiator partition at >{initiatorPath}< (file or metadata missing)";
            LogErr($"Couldn't open virtual drive: {LastError}");
            return false;
        }

        if (profile is null)
        {
            await ReleaseOwnVirtualFilesAsync(contentPath).ConfigureAwait(false);
            var probe = await VirtualDriveProber.ProbeAsync(contentPath, initiator, cancellationToken).ConfigureAwait(false);
            profile = probe.DetectedCapabilities;
            if (probe is { Success: true, ProfileRecorded: false, DetectedCapabilities: { } inferred })
                LogInfoSub($"Legacy virtual media: mark profile inferred as " +
                           $"{inferred.ToMarkSupport().Describe()} (recorded on the next write)");
        }

        var caps = (profile ?? VirtualTapeDriveCapabilities.WithSetmarks) with
        {
            SupportsInitiatorPartition = initiator is not null,
        };
        var vmd = new VirtualMediaDescriptor(contentPath, ContentCapacity: 0, initiator, InitiatorPartitionCapacity: 0);
        return await OpenVirtualDriveAsync(caps, vmd, FileMode.Open, ioRate, ewProfile).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes our own drive when it stands on <paramref name="contentPath"/>: it holds the files open (FileShare.None),
    ///  so neither a probe nor a re-create could touch them.
    /// </summary>
    private async Task ReleaseOwnVirtualFilesAsync(string contentPath)
    {
        if (_vmdLast is { InMemory: false } last && VirtualMediaPaths.SamePath(last.ContentPath, contentPath))
            await CloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Describes what new media at <paramref name="vmd"/> would destroy, or <see langword="null"/> when no file is in the
    ///  way. Probes recognized virtual media (read-only) for its name and set count.
    /// </summary>
    private static async Task<string?> DescribeExistingVirtualMediaAsync(VirtualMediaDescriptor vmd, CancellationToken ct)
    {
        string path = vmd.ContentPath;
        if (!VirtualMediaPaths.AnyMediaFileExists(path, vmd.InitiatorPath))
            return null;

        if (!VirtualMediaPaths.MediaExists(path))
            return $"Files exist at >{path}< but hold no virtual media — their content will be lost.";

        string? initiator = VirtualMediaPaths.FindInitiatorFile(path, vmd.InitiatorPath);
        var probe = await VirtualDriveProber.ProbeAsync(path, initiator, ct).ConfigureAwait(false);
        return probe switch
        {
            { Success: true, Kind: VirtualMediaKind.Calibration } =>
                $"Virtual media >{path}< is a calibration cartridge " +
                $">{probe.CalibrationHeader?.ProfileKey ?? "unknown profile"}< — its calibration data will be permanently erased.",
            { Success: true } =>
                $"Virtual media >{path}< holds >{probe.MediaName ?? "unnamed"}< with " +
                $"{probe.BackupSetCount ?? 0} backup set(s) — all will be lost.",
            _ =>
                $"Virtual media >{path}< exists but could not be read ({probe.ErrorMessage}) — it will be lost.",
        };
    }

    #endregion
}
