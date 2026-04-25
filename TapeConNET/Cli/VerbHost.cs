using System.CommandLine;
using System.IO;

using Microsoft.Extensions.Logging;

using TapeLibNET;
using TapeLibNET.Virtual;

using TapeConNET.Infrastructure;
using TapeConNET.Logging;
using TapeConNET.Services;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// Thin convenience wrapper that every verb uses to build a configured
/// <see cref="TapeService"/> from the parsed command line and (optionally)
/// open the drive + load media + restore TOC. Centralizes the lifecycle so
/// individual verbs stay short and focused on their own arguments.
/// </summary>
internal static class VerbHost
{
    /// <summary>
    /// What lifecycle steps the verb wants performed before its action runs.
    /// </summary>
    [Flags]
    public enum LifecycleSteps
    {
        None       = 0,
        OpenDrive  = 1,
        LoadMedia  = 2,
        RestoreTOC = 4,

        /// <summary>Drive only — for <c>format</c>, <c>eject</c>.</summary>
        Drive      = OpenDrive,
        /// <summary>Drive + media — for <c>format</c>, <c>eject</c>.</summary>
        Media      = OpenDrive | LoadMedia,
        /// <summary>Drive + media + TOC — for <c>backup</c>, <c>restore</c>, <c>list</c>.</summary>
        Full       = OpenDrive | LoadMedia | RestoreTOC,
    }

    /// <summary>
    /// Build a new <see cref="TapeService"/>, perform the requested
    /// <paramref name="steps"/>, and return it. Throws
    /// <see cref="TapeConException"/> when any step fails so the verb can
    /// just <c>using</c>-it and call its operation.
    /// </summary>
    public static TapeService BuildAndOpen(
        ParseResult parseResult,
        IConsoleUx ux,
        LifecycleSteps steps,
        CancellationToken ct)
    {
        var logLevel = parseResult.GetValue(GlobalOptions.LogLevel);
        var loggerFactory = LoggerFactoryBuilder.Build(ux, logLevel);

        var service = new TapeService(ux, loggerFactory, ct);
        try
        {
            if ((steps & LifecycleSteps.OpenDrive) != 0)
                OpenDriveFromOptions(service, parseResult, ux);

            if ((steps & LifecycleSteps.LoadMedia) != 0)
            {
                if (!service.LoadMediaAsync().GetAwaiter().GetResult())
                    throw new TapeConException(TapeConExitCode.OperationFailed,
                        $"Couldn't load media: {service.LastError}");
            }

            if ((steps & LifecycleSteps.RestoreTOC) != 0)
            {
                if (!service.RestoreTOCAsync().GetAwaiter().GetResult())
                    throw new TapeConException(TapeConExitCode.OperationFailed,
                        $"Couldn't restore TOC: {service.LastError}");
            }

            return service;
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the drive selected by global options. Exactly one of
    /// <c>--drive</c>, <c>--virtual</c>, or <c>--in-memory</c> must be
    /// specified; multiples or none yield <see cref="TapeConExitCode.UsageError"/>.
    /// </summary>
    private static void OpenDriveFromOptions(TapeService service, ParseResult parseResult, IConsoleUx ux)
    {
        var driveNumber = parseResult.GetValue(GlobalOptions.Drive);
        var virtualPath = parseResult.GetValue(GlobalOptions.Virtual);
        var inMemory    = parseResult.GetValue(GlobalOptions.InMemory);
        var initiator   = parseResult.GetValue(GlobalOptions.Initiator);
        var capacity    = parseResult.GetValue(GlobalOptions.Capacity);
        var initCap     = parseResult.GetValue(GlobalOptions.InitCapacity);

        int selected = (driveNumber.HasValue ? 1 : 0)
                     + (!string.IsNullOrEmpty(virtualPath) ? 1 : 0)
                     + (inMemory ? 1 : 0);
        if (selected > 1)
            throw new TapeConException(TapeConExitCode.UsageError,
                "--drive, --virtual and --in-memory are mutually exclusive.");

        // Architecture §4 shortcut: when no drive option is supplied, auto-open
        //  Win32 drive 0 with a single info-level note (suppressed under --quiet).
        if (selected == 0)
        {
            ux.Log(WarningLevel.Info, "No drive specified — opening Win32 drive 0.");
            driveNumber = 0;
        }

        if (driveNumber.HasValue)
        {
            if (!service.OpenDriveAsync(driveNumber.Value).GetAwaiter().GetResult())
                throw new TapeConException(TapeConExitCode.OperationFailed,
                    $"Couldn't open drive {driveNumber.Value}: {service.LastError}");
            return;
        }

        if (inMemory)
        {
            var capabilities = !string.IsNullOrEmpty(initiator) || initCap.HasValue
                ? VirtualTapeDriveCapabilities.WithPartitions
                : VirtualTapeDriveCapabilities.WithSetmarks;

            var contentCap = capacity ?? 64L * 1024 * 1024;        // 64 MiB default
            var initiatorCap = initCap ?? (capabilities.SupportsInitiatorPartition ? 24L * 1024 * 1024 : 0);

            var vmd = new VirtualMediaDescriptor(
                ContentPath: string.Empty,
                ContentCapacity: contentCap,
                InitiatorPath: capabilities.SupportsInitiatorPartition ? "<memory>" : null,
                InitiatorPartitionCapacity: initiatorCap,
                InMemory: true);

            if (!service.OpenVirtualDriveAsync(capabilities, vmd, FileMode.Create).GetAwaiter().GetResult())
                throw new TapeConException(TapeConExitCode.OperationFailed,
                    $"Couldn't open in-memory virtual drive: {service.LastError}");
            return;
        }

        // file-backed virtual drive
        var caps = !string.IsNullOrEmpty(initiator)
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        var contentCap2 = capacity ?? (caps.SupportsInitiatorPartition ? 1024L * 1024 * 1024 : 500L * 1024 * 1024);
        var initiatorCap2 = initCap ?? (caps.SupportsInitiatorPartition ? 24L * 1024 * 1024 : 0);

        var vmd2 = new VirtualMediaDescriptor(
            ContentPath: virtualPath!,
            ContentCapacity: contentCap2,
            InitiatorPath: initiator,
            InitiatorPartitionCapacity: initiatorCap2);

        if (!service.OpenVirtualDriveAsync(caps, vmd2, FileMode.OpenOrCreate).GetAwaiter().GetResult())
            throw new TapeConException(TapeConExitCode.OperationFailed,
                $"Couldn't open virtual drive >{virtualPath}<: {service.LastError}");
    }

    /// <summary>
    /// Maps a <see cref="BackupOperationResult"/> / <see cref="RestoreOperationResult"/>
    /// outcome to a <see cref="TapeConExitCode"/>. Used by backup/restore/validate/verify.
    /// </summary>
    public static TapeConExitCode ToExitCode(bool wasAborted, bool failed)
    {
        if (wasAborted) return TapeConExitCode.Cancelled;
        if (failed)     return TapeConExitCode.OperationFailed;
        return TapeConExitCode.Ok;
    }
}
