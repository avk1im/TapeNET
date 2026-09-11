using System.IO;

using Microsoft.Extensions.Logging.Abstractions;

using TapeLibNET;
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
public record VirtualDriveProbeResult(
    bool Success,
    VirtualMediaDescriptor? Media,
    string? MediaName,
    int? BackupSetCount,
    VirtualTapeDriveCapabilities? DetectedCapabilities,
    string? ErrorMessage,
    VirtualMediaKind Kind = VirtualMediaKind.None,    // what was found on the cartridge (default: None)
    TapeCalibrationHeader? CalibrationHeader = null); // calibration header if one was found (default: null)


// ── VirtualDriveProber ────────────────────────────────────────────────────────

/// <summary>
/// Stateless helper that probes a virtual drive to determine whether valid existing
///  media is present. Opens the backend, reads TOC info, then disposes everything.
/// <para>
/// Unlike <see cref="TapeServiceBase"/> operations, this helper has no agent ownership,
///  no semaphore, and no <see cref="ITapeServiceHost"/>; cancellation is accepted
///  directly via <see cref="CancellationToken"/>.
/// </para>
/// </summary>
public static class VirtualDriveProber
{
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
    public static async Task<VirtualDriveProbeResult> ProbeAsync(
        string contentPath,
        string? initiatorPath,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            TapeDrive? drive = null;
            TapeFileAgent? agent = null;

            try
            {
                var metadataPath = contentPath + VirtualTapeDriveBackend.MetadataExtension;
                if (!File.Exists(metadataPath))
                {
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null,
                        "Metadata file not found");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Use null logger factory to avoid polluting logs during probe
                var loggerFactory = NullLoggerFactory.Instance;

                var caps = initiatorPath != null
                    ? VirtualTapeDriveCapabilities.WithPartitions
                    : VirtualTapeDriveCapabilities.WithSetmarks;

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
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null,
                        $"Failed to open virtual drive: {ex.Message}");
                }

                cancellationToken.ThrowIfCancellationRequested();

                drive = new TapeDrive(loggerFactory, backend);
                if (!drive.ReopenDrive(0))
                {
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null,
                        "Failed to open virtual drive");
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!drive.ReloadMedia())
                {
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null,
                        "Failed to load virtual media — may be new or corrupted");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Capabilities are available once media is loaded — independent of content kind.
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
                agent = new TapeFileAgent(drive);
                
                var header = agent.ReadHeader();

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
                        CalibrationHeader: calHeader);
                }

                // Backup media (media header) or legacy (no header): read the TOC.
                using var registration = cancellationToken.Register(() => agent.IsAbortRequested = true);

                var tocResult = agent.RestoreTOC();
                if (!tocResult)
                {
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null,
                        $"Failed to read TOC: {tocResult.ErrorMessage}");
                }

                cancellationToken.ThrowIfCancellationRequested();
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
                    Kind: VirtualMediaKind.Backup);
            }
            catch (OperationCanceledException)
            {
                throw; // rethrow — caller decides how to handle cancellation
            }
            catch (Exception ex)
            {
                return new VirtualDriveProbeResult(
                    false, null, null, null, null,
                    $"Probe error: {ex.Message}");
            }
            finally
            {
                // Clean up — ensure everything is disposed regardless of outcome
                agent?.Dispose();
                drive?.Dispose();
            }
        }, cancellationToken);
    }
}
