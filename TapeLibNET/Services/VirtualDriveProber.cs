using System.IO;

using Microsoft.Extensions.Logging.Abstractions;

using TapeLibNET;
using TapeLibNET.Virtual;

namespace TapeLibNET.Services;

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

                agent = new TapeFileAgent(drive);
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
                var detectedCaps = new VirtualTapeDriveCapabilities
                {
                    MinBlockSize        = drive.MinimumBlockSize,
                    MaxBlockSize        = drive.MaximumBlockSize,
                    DefaultBlockSize    = drive.DefaultBlockSize,
                    SupportsSetmarks    = drive.SupportsSetmarks,
                    SupportsSeqFilemarks       = drive.SupportsSeqFilemarks,
                    SupportsInitiatorPartition = drive.SupportsInitiatorPartition,
                };

                long initiatorCapacity = 0;
                if (drive.HasInitiatorPartition && initiatorPath != null)
                {
                    drive.MoveToPartition(MediaPartition.Initiator);
                    initiatorCapacity = drive.Capacity;
                }

                return new VirtualDriveProbeResult(
                    Success: true,
                    Media: new VirtualMediaDescriptor(contentPath, drive.ContentCapacity, initiatorPath, initiatorCapacity),
                    MediaName: toc.Description,
                    BackupSetCount: toc.Count,
                    DetectedCapabilities: detectedCaps,
                    ErrorMessage: null);
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
