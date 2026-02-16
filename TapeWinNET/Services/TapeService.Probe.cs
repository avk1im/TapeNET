using System.IO;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using TapeLibNET;
using TapeLibNET.Virtual;

namespace TapeWinNET.Services;

/// <summary>
/// Result of probing a virtual drive for existing media.
/// </summary>
public record VirtualDriveProbeResult(
    bool Success,
    string? MediaName,
    long? Capacity,
    int? BackupSetCount,
    VirtualTapeDriveCapabilities? DetectedCapabilities,
    string? ErrorMessage
);

public partial class TapeService
{
    /// <summary>
    /// Probes a virtual drive to determine if valid existing media exists.
    /// This is a stateless operation - opens the drive, reads info, then closes everything.
    /// </summary>
    /// <param name="contentPath">Path to the content partition file.</param>
    /// <param name="initiatorPath">Optional path to the initiator partition file.</param>
    /// <param name="cancellationToken">Cancellation token to abort the probe.</param>
    /// <returns>Probe result with media information if successful.</returns>
    public static async Task<VirtualDriveProbeResult> ProbeVirtualDriveAsync(
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
                // Check if metadata file exists first (quick pre-check)
                var metadataPath = contentPath + VirtualTapeDriveBackend.MetadataExtension;
                if (!File.Exists(metadataPath))
                {
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null, 
                        "Metadata file not found");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Create backend with requireExistingState = true
                // Use null logger factory to avoid polluting logs during probe
                var loggerFactory = NullLoggerFactory.Instance;
                
                // Determine capabilities based on whether initiator path is provided
                var caps = initiatorPath != null
                    ? VirtualTapeDriveCapabilities.WithPartitions
                    : VirtualTapeDriveCapabilities.WithSetmarks;

                VirtualTapeDriveBackend backend;
                try
                {
                    backend = VirtualTapeDriveBackend.CreateFileBacked(
                        loggerFactory,
                        contentPath,
                        initiatorPath,
                        caps,
                        requireExistingState: true);
                }
                catch (Exception ex)
                {
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null,
                        $"Failed to open virtual drive: {ex.Message}");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Create TapeDrive and open
                drive = new TapeDrive(loggerFactory, backend);
                if (!drive.ReopenDrive(0))
                {
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null,
                        "Failed to open virtual drive");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Load media
                if (!drive.ReloadMedia())
                {
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null,
                        "Failed to load virtual media - may be new or corrupted");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Create agent and try to restore TOC
                agent = new TapeFileAgent(drive);
                
                // Set up abort handling via cancellation token
                using var registration = cancellationToken.Register(() => agent.IsAbortRequested = true);

                if (!agent.RestoreTOC())
                {
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null,
                        "Failed to read TOC - media may be empty or corrupted");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Extract information from TOC
                var toc = agent.TOC;
                var detectedCaps = new VirtualTapeDriveCapabilities
                {
                    MinBlockSize = drive.MinimumBlockSize,
                    MaxBlockSize = drive.MaximumBlockSize,
                    DefaultBlockSize = drive.DefaultBlockSize,
                    SupportsSetmarks = drive.SupportsSetmarks,
                    SupportsSeqFilemarks = drive.SupportsSeqFilemarks,
                    SupportsInitiatorPartition = drive.SupportsInitiatorPartition,
                    Capacity = drive.Capacity
                };

                return new VirtualDriveProbeResult(
                    Success: true,
                    MediaName: toc.Description,
                    Capacity: drive.Capacity,
                    BackupSetCount: toc.Count,
                    DetectedCapabilities: detectedCaps,
                    ErrorMessage: null);
            }
            catch (OperationCanceledException)
            {
                throw; // Rethrow cancellation
            }
            catch (Exception ex)
            {
                return new VirtualDriveProbeResult(
                    false, null, null, null, null,
                    $"Probe error: {ex.Message}");
            }
            finally
            {
                // Clean up - ensure everything is disposed
                agent?.Dispose();
                drive?.Dispose();
            }
        }, cancellationToken);
    }
}