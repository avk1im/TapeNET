using Microsoft.Extensions.Logging.Abstractions;
using TapeLibNET.Agents;
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Services;

/// <summary>What a virtual-media probe found on the cartridge.</summary>
public enum VirtualMediaKind
{
    /// <summary>Nothing recognizable (blank / unreadable).</summary>
    None,
    /// <summary>Backup media with a table of contents.</summary>
    Backup,
    /// <summary>A calibration cartridge (BOM calibration header; no backup TOC).</summary>
    Calibration,
}

/// <summary>
/// Result of probing a virtual drive for existing media.
/// Stateless: obtained by opening the backend, reading info, then disposing.
/// </summary>
/// <param name="DetectedCapabilities">
/// The capabilities the media read correctly under — its recorded mark profile, or for media without one the profile
///  the probe inferred (see <paramref name="ProfileRecorded"/>).
/// </param>
/// <param name="ProfileRecorded">
/// True when the mark profile comes from the media's metadata; false when inferred (legacy media).
/// </param>
public record VirtualDriveProbeResult(
    bool Success,
    VirtualMediaDescriptor? Media,
    string? MediaName,
    int? BackupSetCount,
    VirtualTapeDriveCapabilities? DetectedCapabilities,
    string? ErrorMessage,
    VirtualMediaKind Kind = VirtualMediaKind.None,    // what was found on the cartridge (default: None)
    TapeCalibrationHeader? CalibrationHeader = null,  // calibration header if one was found (default: null)
    bool ProfileRecorded = false);                    // mark profile from metadata (true) or inferred (false)

// ── VirtualDriveProber ────────────────────────────────────────────────────────

/// <summary>
/// Stateless helper that probes a virtual drive to determine whether valid existing
///  media is present. Opens the backend, reads TOC info, then disposes everything.
/// <para>
/// Unlike <see cref="TapeServiceBase"/> operations, this helper has no agent ownership,
///  no semaphore, and no <see cref="ITapeServiceHost"/>; cancellation is accepted
///  directly via <see cref="CancellationToken"/>.
/// </para>
/// <para>
/// Mark profile: media carrying a recorded profile opens under it (the backend reconciles), so one attempt settles it.
///  Media without one — legacy — is tried under each profile in turn (setmarks, sequential filemarks, filemarks only)
///  until its TOC reads. Read-only throughout: a probe never records a profile.
/// </para>
/// </summary>
public static class VirtualDriveProber
{
    /// <summary>Profiles tried, in order, on media that does not record its own.</summary>
    private static readonly VirtualTapeDriveCapabilities[] s_inferenceProfiles =
    [
        VirtualTapeDriveCapabilities.WithSetmarks,
        VirtualTapeDriveCapabilities.WithSeqFilemarks,
        VirtualTapeDriveCapabilities.WithFilemarksOnly,
    ];

    /// <summary>
    /// Probes a virtual drive to determine whether valid existing media is present.
    /// Uses <see cref="FileMode.Open"/> so the call fails on a missing or empty
    ///  metadata file (which is exactly what the caller wants for a probe).
    /// </summary>
    /// <param name="contentPath">Path to the content partition file.</param>
    /// <param name="initiatorPath">Optional path to the initiator partition file.</param>
    /// <param name="cancellationToken">Cancellation token to abort the probe.</param>
    /// <returns>
    /// A <see cref="VirtualDriveProbeResult"/> with media information on success,
    ///  or a failure result with an <see cref="VirtualDriveProbeResult.ErrorMessage"/>.
    /// </returns>
    public static Task<VirtualDriveProbeResult> ProbeAsync(
        string contentPath,
        string? initiatorPath,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Probe(contentPath, initiatorPath, cancellationToken), cancellationToken);

    private static VirtualDriveProbeResult Probe(string contentPath, string? initiatorPath, CancellationToken ct)
    {
        if (!File.Exists(contentPath + VirtualTapeDriveBackend.MetadataExtension))
            return Failure("Metadata file not found");

        VirtualDriveProbeResult? firstFailure = null;
        foreach (var profile in s_inferenceProfiles)
        {
            ct.ThrowIfCancellationRequested();
            var caps = profile with { SupportsInitiatorPartition = initiatorPath != null };
            var result = ProbeOnce(contentPath, initiatorPath, caps, ct, out bool retryable);

            // Settled: success, a recorded profile (another one would be overridden anyway), or a failure that has
            //  nothing to do with the profile (unreadable metadata, a locked file).
            if (result.Success || result.ProfileRecorded || !retryable)
                return result;
            firstFailure ??= result;
        }

        return firstFailure!;   // every inference profile was tried: report the first (setmarks) failure
    }

    /// <summary>One probe under <paramref name="caps"/>.</summary>
    /// <param name="retryable">The failure might go away under another mark profile (it happened reading the TOC).</param>
    private static VirtualDriveProbeResult ProbeOnce(string contentPath, string? initiatorPath,
        VirtualTapeDriveCapabilities caps, CancellationToken ct, out bool retryable)
    {
        retryable = false;
        TapeDrive? drive = null;
        TapeAgentBase? agent = null;
        try
        {
            // Use null logger factory to avoid polluting logs during probe
            var loggerFactory = NullLoggerFactory.Instance;

            VirtualTapeDriveBackend backend;
            try
            {
                backend = VirtualTapeDriveBackend.CreateFileBacked(
                    loggerFactory,
                    contentPath,
                    contentCapacity: 0, // irrelevant for Open mode — capacity is read from existing state
                    initiatorFilePath: initiatorPath,
                    capabilities: caps,
                    mediaMode: FileMode.Open); // fails if metadata is absent or invalid
            }
            catch (Exception ex)
            {
                return Failure($"Failed to open virtual drive: {ex.Message}");
            }

            ct.ThrowIfCancellationRequested();

            drive = new TapeDrive(loggerFactory, backend);
            if (!drive.ReopenDrive(0))
                return Failure("Failed to open virtual drive");

            ct.ThrowIfCancellationRequested();

            if (!drive.ReloadMedia())
                return Failure("Failed to load virtual media — may be new or corrupted");

            ct.ThrowIfCancellationRequested();

            // Loaded: the backend has reconciled block sizes and — when recorded — the mark profile from the media.
            bool profileRecorded = backend.IsMarkProfileRecorded;
            var detectedCaps = new VirtualTapeDriveCapabilities
            {
                MinBlockSize = drive.MinimumBlockSize,
                MaxBlockSize = drive.MaximumBlockSize,
                DefaultBlockSize = drive.DefaultBlockSize,
                SupportsSetmarks = drive.SupportsSetmarks,
                SupportsSeqFilemarks = drive.SupportsSeqFilemarks,
                SupportsInitiatorPartition = drive.SupportsInitiatorPartition,
            };

            // Identify the medium from its BOM header BEFORE assuming a TOC exists. A calibration
            //  cartridge carries a calibration header and NO TOC, so a TOC-only probe would wrongly
            //  report it as "new" — and the user could overwrite it. One cheap block read.
            drive.PrepareMedia();
            agent = new TapeAgentBase(drive);
            var header = agent.ReadBomHeader();
            if (header is TapeCalibrationHeader calHeader)
            {
                return new VirtualDriveProbeResult(
                    Success: true,
                    Media: new VirtualMediaDescriptor(
                        contentPath, drive.ContentCapacity, initiatorPath, 0),
                    MediaName: calHeader.ProfileKey,
                    BackupSetCount: null,
                    DetectedCapabilities: detectedCaps,
                    ErrorMessage: null,
                    Kind: VirtualMediaKind.Calibration,
                    CalibrationHeader: calHeader,
                    ProfileRecorded: profileRecorded);
            }

            // Backup media (media header) or legacy (no header): read the TOC.
            using var registration = ct.Register(() => agent.IsAbortRequested = true);
            var tocResult = agent.RestoreTOC();
            if (!tocResult)
            {
                retryable = !profileRecorded;   // a misread profile shows up exactly here
                return Failure($"Failed to read TOC: {tocResult.ErrorMessage}", profileRecorded);
            }

            ct.ThrowIfCancellationRequested();

            var toc = agent.TOC;
            long initiatorCapacity = 0;
            if (drive.HasInitiatorPartition && initiatorPath != null)
            {
                drive.MoveToPartition(MediaPartition.Initiator);
                initiatorCapacity = drive.Capacity;
            }

            return new VirtualDriveProbeResult(
                Success: true,
                Media: new VirtualMediaDescriptor(
                    contentPath, drive.ContentCapacity, initiatorPath, initiatorCapacity),
                MediaName: toc.Description,
                BackupSetCount: toc.Count,
                DetectedCapabilities: detectedCaps,
                ErrorMessage: null,
                Kind: VirtualMediaKind.Backup,
                ProfileRecorded: profileRecorded);
        }
        catch (OperationCanceledException)
        {
            throw; // rethrow — caller decides how to handle cancellation
        }
        catch (Exception ex)
        {
            retryable = true;   // e.g. a navigator exception under the wrong profile
            return Failure($"Probe error: {ex.Message}");
        }
        finally
        {
            // Clean up — ensure everything is disposed regardless of outcome
            agent?.Dispose();
            drive?.Dispose();
        }
    }

    private static VirtualDriveProbeResult Failure(string message, bool profileRecorded = false)
        => new(false, null, null, null, null, message, ProfileRecorded: profileRecorded);
}
