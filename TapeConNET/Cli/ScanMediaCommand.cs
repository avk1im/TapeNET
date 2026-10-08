using System.CommandLine;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;
using TapeLibNET.Scan;
using TapeLibNET.Services;

namespace TapeConNET.Cli;


/// <summary>
/// <c>tapecon scan-media</c> — surveys the loaded media from BOM to EOD with no TOC assumed and prints the
///  headline, details and one line per noteworthy fragment (all reported by the service). READ-ONLY.
/// <c>tapecon recover-toc</c> — recovers a TOC from one TOC-copy fragment of a saved <c>.tapescan</c> map;
///  optionally saves it to a file and/or adopts it for the session.
/// </summary>
internal static class ScanMediaCommand
{
    /// <summary>Win32 <c>ERROR_MEDIA_CHANGED</c>: the swap guard refused to read a TOC off a different cartridge.</summary>
    private const uint ErrorMediaChanged = 1110;

    /// <summary>Builds <c>scan-media</c>.</summary>
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("scan-media",
            "Scan the loaded media from start to end without relying on its TOC, and report what it holds. " +
            "Read-only: nothing is written to the media.");
        GlobalOptions.Attach(cmd);

        var noRecoverOption = new Option<bool>("--no-recover-toc")
        {
            Description = "Don't recover the table-of-contents copies found on the media (recovery is on by default).",
        };
        var exportOption = new Option<string?>("--export")
        {
            Description = "Folder to save the scan map into (a .tapescan file usable by 'recover-toc').",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Print the scan map as JSON to stdout; informational output is suppressed.",
        };
        cmd.Options.Add(noRecoverOption);
        cmd.Options.Add(exportOption);
        cmd.Options.Add(jsonOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var json = parseResult.GetValue(jsonOption);
            // JSON goes to stdout: keep it clean by silencing the informational log
            if (json)
                ux.QuietMode = true;
            else
                ux.WriteBanner();

            var noRecover = parseResult.GetValue(noRecoverOption);
            var export    = parseResult.GetValue(exportOption);

            // A scan needs the media loaded, but deliberately NOT a TOC
            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Media, ct);

            var result = await service.ScanMediaAsync(new ScanMediaRequest
            {
                RecoverTocCopies = !noRecover,
                MapExportFolder = export,
                Cancellation = ct,
            });

            if (json && result.Map is { } map)
                Console.Out.WriteLine(map.ToJson());

            if (result.WasAborted)
                return (int)TapeConExitCode.Cancelled;
            return result.Success ? (int)TapeConExitCode.Ok : (int)TapeConExitCode.OperationFailed;
        });

        return cmd;
    }

    /// <summary>Builds <c>recover-toc</c>.</summary>
    public static Command CreateRecoverToc(IConsoleUx ux)
    {
        var cmd = new Command("recover-toc",
            "Recover the table of contents from a TOC-copy fragment of a saved scan map.");
        GlobalOptions.Attach(cmd);

        var mapOption = new Option<string>("--map")
        {
            Description = $"Scan map file ({MediaScanMap.MapFileExtension}) produced by 'scan-media --export' or '--json'.",
            Required = true,
        };
        var fragmentOption = new Option<int>("--fragment", "-f")
        {
            Description = "Ordinal of the TOC-copy fragment in the map (as listed by the scan).",
            Required = true,
        };
        var adoptOption = new Option<bool>("--adopt")
        {
            Description = "Use the recovered TOC as the session's TOC (same identity check as 'toc import').",
        };
        var saveOption = new Option<string?>("--save")
        {
            Description = "Save the recovered TOC to this file.",
        };
        var yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Skip the media-identity confirmation when adopting.",
        };
        cmd.Options.Add(mapOption);
        cmd.Options.Add(fragmentOption);
        cmd.Options.Add(adoptOption);
        cmd.Options.Add(saveOption);
        cmd.Options.Add(yesOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            var mapPath  = parseResult.GetValue(mapOption)!;
            var ordinal  = parseResult.GetValue(fragmentOption);
            var adopt    = parseResult.GetValue(adoptOption);
            var savePath = parseResult.GetValue(saveOption);
            var yes      = parseResult.GetValue(yesOption);

            MediaScanMap? map;
            try
            {
                map = MediaScanMap.FromJson(File.ReadAllText(mapPath));
            }
            catch (Exception ex)
            {
                throw new TapeConException(TapeConExitCode.UsageError, $"Couldn't read scan map '{mapPath}': {ex.Message}");
            }
            if (map is null)
                throw new TapeConException(TapeConExitCode.UsageError, $"'{mapPath}' is not a valid scan map.");

            // Saving and adoption need the media loaded; no TOC is required
            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Media, ct);

            var result = await service.RecoverTocAsync(new RecoverTocRequest(map, ordinal)
            {
                Adopt = adopt,
                SaveToFilePath = savePath,
                ProceedOnMediaMismatch = yes,
                Cancellation = ct,
            });

            if (result.Success)
                return (int)TapeConExitCode.Ok;

            // The swap guard refusing is distinct from a failure part-way, so scripts can tell them apart
            return result.ErrorCode == ErrorMediaChanged
                ? (int)TapeConExitCode.WriteRefused
                : (int)TapeConExitCode.OperationFailed;
        });

        return cmd;
    }
}
