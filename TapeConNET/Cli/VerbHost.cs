using System.CommandLine;
using TapeConNET.Infrastructure;
using TapeConNET.Logging;
using TapeConNET.Services;
using TapeConNET.Ux;
using TapeLibNET.Services;
using TapeLibNET.Virtual;

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
        None = 0,
        OpenDrive = 1,
        LoadMedia = 2,
        RestoreTOC = 4,
        /// <summary>
        /// Identify the medium (TOC or calibration) WITHOUT the historical EOD churn: a calibration
        ///  cartridge is reported, unidentified media prompts before any search. Tolerant — proceeds even
        ///  when no TOC results. For <c>list</c>. (Strict <see cref="RestoreTOC"/> stays for TOC-required verbs.)
        /// </summary>
        IdentifyMedia = 8,

        /// <summary>Drive only — for <c>format</c>, <c>eject</c>.</summary>
        Drive = OpenDrive,
        /// <summary>Drive + media — for <c>format</c>, <c>eject</c>.</summary>
        Media = OpenDrive | LoadMedia,
        /// <summary>Drive + media + TOC (required) — for <c>backup</c>, <c>restore</c>, <c>validate</c>, <c>verify</c>.</summary>
        Full = OpenDrive | LoadMedia | RestoreTOC,
        /// <summary>Drive + media + identify (TOC or calibration) — for <c>list</c>.</summary>
        FullOrCalibration = OpenDrive | LoadMedia | IdentifyMedia,
    }

    /// <summary>
    /// Build a new <see cref="TapeService"/> wired to <paramref name="ux"/> and the <c>--log-level</c>, without opening
    ///  anything. For verbs that manage the drive themselves (<c>create-virtual</c>).
    /// </summary>
    public static TapeService Build(ParseResult parseResult, IConsoleUx ux, CancellationToken ct)
    {
        var logLevel = parseResult.GetValue(GlobalOptions.LogLevel);
        var loggerFactory = LoggerFactoryBuilder.Build(ux, logLevel);
        return new TapeService(ux, loggerFactory, ct);
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
        var service = Build(parseResult, ux, ct);
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

            if ((steps & LifecycleSteps.IdentifyMedia) != 0)
            {
                // Smart identification: throw ONLY on a genuine failure. A calibration cartridge or unidentified
                //  medium is a valid state the verb renders (list already reports calibration internally); it does
                //  NOT throw and — crucially — does NOT churn to end-of-data hunting for an absent TOC.
                var outcome = service.IdentifyMediaAsync().GetAwaiter().GetResult();
                if (outcome == IdentifyMediaOutcome.Failed)
                    throw new TapeConException(TapeConExitCode.OperationFailed,
                        $"Couldn't identify media: {service.LastError}");
                // TocLoaded / CalibrationMedia / Unidentified → proceed.
            }

            return service;
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    /// <summary>True when the command line asks for NEW virtual media (<c>--virtual-create</c>).</summary>
    public static bool IsCreatingVirtualMedia(ParseResult parseResult)
        => !string.IsNullOrEmpty(parseResult.GetValue(VirtualMediaOptions.VirtualCreate));

    /// <summary>
    /// Creates new file-backed virtual media at <paramref name="contentPath"/> from the shape options, leaving it open,
    ///  loaded and formatted in <paramref name="service"/>. Shared by <c>--virtual-create</c> and <c>create-virtual</c>.
    ///  Existing files are replaced only on confirmation or with <c>--overwrite-virtual</c>.
    /// </summary>
    /// <returns>The drive profile the media was created with (and records).</returns>
    /// <exception cref="TapeConException">
    /// Cancelled when the overwrite was declined; OperationFailed when creation failed; UsageError on bad options.
    /// </exception>
    public static async Task<VirtualTapeDriveCapabilities> CreateVirtualFromOptionsAsync(
        TapeService service, ParseResult parseResult, string contentPath, string? mediaName)
    {
        var (caps, vmd) = VirtualMediaOptions.ResolveNewFileMedia(parseResult, contentPath);
        var outcome = await service.CreateVirtualMediaAsync(caps, vmd, mediaName,
            overwriteWithoutPrompt: parseResult.GetValue(VirtualMediaOptions.OverwriteVirtual));

        return outcome switch
        {
            VirtualMediaCreateOutcome.Created => caps,
            VirtualMediaCreateOutcome.Declined => throw new TapeConException(TapeConExitCode.Cancelled,
                $"Virtual media >{vmd.ContentPath}< left untouched. Confirm the prompt, or pass " +
                $"{VirtualMediaOptions.OverwriteVirtual.Name} to replace it — or use --virtual-open to keep it."),
            _ => throw new TapeConException(TapeConExitCode.OperationFailed,
                $"Couldn't create virtual media >{vmd.ContentPath}<: {service.LastError}"),
        };
    }

    /// <summary>
    /// Opens the drive selected by global options. At most one of
    /// <c>--drive</c>, <c>--virtual-open</c>, <c>--virtual-create</c>, <c>--in-memory</c> may be specified;
    /// multiples yield <see cref="TapeConExitCode.UsageError"/>, none opens Win32 drive 0.
    /// </summary>
    private static void OpenDriveFromOptions(TapeService service, ParseResult parseResult, IConsoleUx ux)
    {
        var driveNumber = parseResult.GetValue(GlobalOptions.Drive);
        var openPath    = parseResult.GetValue(GlobalOptions.Virtual);
        var createPath  = parseResult.GetValue(VirtualMediaOptions.VirtualCreate);
        var inMemory    = parseResult.GetValue(GlobalOptions.InMemory);

        int selected = (driveNumber.HasValue ? 1 : 0)
                     + (!string.IsNullOrEmpty(openPath) ? 1 : 0)
                     + (!string.IsNullOrEmpty(createPath) ? 1 : 0)
                     + (inMemory ? 1 : 0);

        if (selected > 1)
            throw new TapeConException(TapeConExitCode.UsageError,
                "--drive, --virtual-open (--virtual), --virtual-create and --in-memory are mutually exclusive.");

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
            OpenInMemoryFromOptions(service, parseResult);
            return;
        }

        if (!string.IsNullOrEmpty(createPath))
        {
            CreateVirtualFromOptionsAsync(service, parseResult, createPath, mediaName: null).GetAwaiter().GetResult();
            return;
        }

        OpenVirtualFromOptions(service, parseResult, openPath!, ux); // selected == 1 and nothing else matched
    }

    /// <summary>
    /// Opens EXISTING file-backed virtual media — never creates any. Sizes and the mark profile come from the media.
    ///  A path without extension also finds the media under the default extension.
    /// </summary>
    private static void OpenVirtualFromOptions(TapeService service, ParseResult parseResult, string path, IConsoleUx ux)
    {
        path = VirtualMediaPaths.ResolveExisting(path);

        var ignored = VirtualMediaOptions.CreateOnlyOptionsGiven(parseResult);
        if (ignored.Count > 0)
            ux.Log(WarningLevel.Warning,
                $"{string.Join(", ", ignored)} ignored: they apply to new virtual media (--virtual-create) only.");

        // The media records its mark profile; a flag given here matters only for legacy media without one.
        //  No flag: the service probes legacy media and opens it under the profile its TOC reads with.
        VirtualTapeDriveCapabilities? profile = VirtualMediaOptions.ProfileGiven(parseResult)
            ? VirtualMediaOptions.ResolveCapabilities(parseResult, withInitiator: false, applyBlockSizes: false)
            : null;

        if (!service.OpenExistingVirtualDriveAsync(path, parseResult.GetValue(GlobalOptions.Initiator), profile)
                .GetAwaiter().GetResult())
        {
            string hint = VirtualMediaPaths.MediaExists(path)
                ? string.Empty
                : " — create it with --virtual-create, or with 'tapecon create-virtual'";
            throw new TapeConException(TapeConExitCode.OperationFailed,
                $"Couldn't open virtual media >{path}<: {service.LastError}{hint}.");
        }
    }

    /// <summary>Opens a fresh in-memory virtual drive (no files; gone when the process ends).</summary>
    private static void OpenInMemoryFromOptions(TapeService service, ParseResult parseResult)
    {
        var initiator = parseResult.GetValue(GlobalOptions.Initiator);
        var initCap   = parseResult.GetValue(GlobalOptions.InitCapacity);
        bool withInitiator = !string.IsNullOrEmpty(initiator) || initCap.HasValue;

        var capabilities = VirtualMediaOptions.ResolveCapabilities(parseResult, withInitiator, applyBlockSizes: true);
        var vmd = new VirtualMediaDescriptor(
            ContentPath: VirtualMediaPaths.InMemoryPlaceholder,
            ContentCapacity: parseResult.GetValue(GlobalOptions.Capacity) ?? VirtualMediaDefaults.InMemoryContentCapacity,
            InitiatorPath: withInitiator ? VirtualMediaPaths.InMemoryPlaceholder : null,
            InitiatorPartitionCapacity: withInitiator ? initCap ?? VirtualMediaDefaults.InitiatorCapacity : 0,
            InMemory: true);

        if (!service.OpenVirtualDriveAsync(capabilities, vmd, FileMode.Create).GetAwaiter().GetResult())
            throw new TapeConException(TapeConExitCode.OperationFailed,
                $"Couldn't open in-memory virtual drive: {service.LastError}");
    }

    /// <summary>
    /// Win32 <c>ERROR_INVALID_PASSWORD</c> — the service's code for "no or wrong password" (Design-Encryption §10.7).
    /// </summary>
    public const uint ErrorInvalidPassword = 86;

    /// <summary>
    /// Maps a <see cref="BackupResult"/> / <see cref="RestoreResult"/> outcome to a <see cref="TapeConExitCode"/>. Used by
    ///  backup/restore/validate/verify.
    /// </summary>
    /// <param name="setWriteBlocked">
    /// True when a destructive write was refused by set-header verification (SH-13) — the tape is unchanged. Checked first:
    ///  a refusal is not the same as a partial failure.
    /// </param>
    /// <param name="passwordRequired">
    /// Locked sets were skipped, or an encrypted backup had no password. After abort and failure: a run that also failed
    ///  otherwise reports the failure.
    /// </param>
    public static TapeConExitCode ToExitCode(bool wasAborted, bool failed, bool setWriteBlocked = false,
        bool passwordRequired = false)
    {
        if (setWriteBlocked) return TapeConExitCode.WriteRefused;
        if (wasAborted) return TapeConExitCode.Cancelled;
        // passwordRequired ranks above failed: an encrypted backup refused for lack of a password, or a restore that
        //  skipped locked sets, also reports HasFailed / "nothing processed" — the password is the actionable cause.
        if (passwordRequired) return TapeConExitCode.PasswordRequired;
        if (failed) return TapeConExitCode.OperationFailed;
        return TapeConExitCode.Ok;
    }
}
