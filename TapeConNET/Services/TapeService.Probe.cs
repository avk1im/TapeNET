using System.IO;

using Microsoft.Extensions.Logging.Abstractions;

using TapeLibNET;
using TapeLibNET.Virtual;
using TapeLibNET.Services;

namespace TapeConNET.Services;

public partial class TapeService
{
    /// <summary>
    /// Probes a virtual drive to determine whether valid existing media exists.
    /// Stateless: opens the backend, reads info, then disposes everything.
    /// Uses <see cref="FileMode.Open"/> so the call fails on a missing/empty
    /// metadata file (which is exactly what the caller wants for a probe).
    /// </summary>
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
                var metadataPath = contentPath + VirtualTapeDriveBackend.MetadataExtension;
                if (!File.Exists(metadataPath))
                {
                    return new VirtualDriveProbeResult(
                        false, null, null, null, null,
                        "Metadata file not found");
                }

                cancellationToken.ThrowIfCancellationRequested();

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
                        contentCapacity: 0,
                        initiatorFilePath: initiatorPath,
                        capabilities: caps,
                        mediaMode: FileMode.Open);
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
                    MinBlockSize = drive.MinimumBlockSize,
                    MaxBlockSize = drive.MaximumBlockSize,
                    DefaultBlockSize = drive.DefaultBlockSize,
                    SupportsSetmarks = drive.SupportsSetmarks,
                    SupportsSeqFilemarks = drive.SupportsSeqFilemarks,
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
                throw;
            }
            catch (Exception ex)
            {
                return new VirtualDriveProbeResult(
                    false, null, null, null, null,
                    $"Probe error: {ex.Message}");
            }
            finally
            {
                agent?.Dispose();
                drive?.Dispose();
            }
        }, cancellationToken);
    }
}
