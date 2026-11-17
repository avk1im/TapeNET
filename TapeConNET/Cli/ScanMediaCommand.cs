using System.CommandLine;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;
using TapeLibNET.Scan;
using TapeLibNET.Services;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon scan-media</c> — surveys the loaded media from BOM to EOD with no TOC assumed and prints the
///  headline, details and one line per noteworthy fragment (all reported by the service). READ-ONLY on the media; saves
///  the scan map and every recovered TOC to a folder.
/// <c>tapecon recover-toc</c> — recovers a TOC from one TOC-copy fragment of a saved <c>.tapescan</c> map into a file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Files, not a session.</b> A tapecon run lasts one verb, so whatever a scan recovers must reach the disk before the
///  process ends — the map's JSON does not carry the recovered TOCs. Hence the scan saves by default (into the current
///  folder without <c>--export</c>), and <c>recover-toc</c> only saves; the next verb picks the file up with
///  <c>--import-toc</c>. TapeWinNET adopts instead, within its session; the service serves both.
/// </para>
/// <para>
/// Two routes to a TOC: the default scan recovers every copy it finds; <c>--no-recover-toc</c> maps only, for a user who
///  wants to see the fragments first and then recover one copy with <c>recover-toc</c>.
/// </para>
/// </remarks>
internal static class ScanMediaCommand
{
    /// <summary>Win32 <c>ERROR_MEDIA_CHANGED</c>: the swap guard refused to read a TOC off a different cartridge.</summary>
    private const uint ErrorMediaChanged = 1110;

    /// <summary>Builds <c>scan-media</c>.</summary>
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("scan-media",
            "Scan the loaded media from start to end without relying on its TOC, report what it holds, and recover " +
            "every TOC copy found. Read-only: nothing is written to the media.");

        GlobalOptions.Attach(cmd);

        var noRecoverOption = new Option<bool>("--no-recover-toc")
        {
            Description = "Map only: don't recover the TOC copies found. Recover one later with `recover-toc`.",
        };
        var exportOption = new Option<string?>("--export")
        {
            Description = $"Folder for the scan map (`{MediaScanMap.MapFileExtension}`) and the recovered TOCs " +
                          $"(`{TapeServiceBase.TOCFileExtension}`, for `--import-toc`). Default: the current folder; " +
                          "with `--json`, nothing is saved unless given.",
            HelpName = "PATH",
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
            var export    = parseResult.GetValue(exportOption) ?? (json ? null : Directory.GetCurrentDirectory());

            // A scan needs the media loaded, but deliberately NOT a TOC
            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Media, ct);

            var result = await service.ScanMediaAsync(new ScanMediaRequest
            {
                RecoverTocCopies = !noRecover,
                MapExportFolder = export,
                ExportRecoveredTocs = true,
                Cancellation = ct,
            });

            if (json && result.Map is { } map)
                Console.Out.WriteLine(map.ToJson());

            // The way on: the saved TOC for the next verb, or the map for recover-toc
            if (result.TocExportPaths.Count > 0)
                ux.Log(WarningLevel.Info,
                    $"Use a recovered TOC with --import-toc, e.g. 'tapecon list --import-toc \"{result.TocExportPaths[0]}\"'.");
            else if (result.TocCopiesFound > 0 && result.MapExportPath is { } mapPath)
                ux.Log(WarningLevel.Info,
                    $"Recover a TOC copy with 'tapecon recover-toc --map \"{mapPath}\" --fragment N --save FILE'.");

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
            "Recover the TOC from one TOC copy listed in a saved scan map, and save it to a file for `--import-toc`. " +
            "Reads the copy from the loaded media, which must be the scanned one.");

        GlobalOptions.Attach(cmd);

        var mapOption = new Option<string>("--map")
        {
            Description = $"Scan map file (`{MediaScanMap.MapFileExtension}`), saved by `scan-media`.",
            Required = true,
            HelpName = "PATH",
        };
        var fragmentOption = new Option<int>("--fragment", "-f")
        {
            Description = "Number of the TOC-copy fragment in the map, as the scan listed it.",
            Required = true,
        };
        var saveOption = new Option<string>("--save")
        {
            Description = $"File to save the recovered TOC to (e.g. `first{TapeServiceBase.TOCFileExtension}`).",
            Required = true,
            HelpName = "PATH",
        };

        cmd.Options.Add(mapOption);
        cmd.Options.Add(fragmentOption);
        cmd.Options.Add(saveOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            var mapPath  = parseResult.GetValue(mapOption)!;
            var ordinal  = parseResult.GetValue(fragmentOption);
            var savePath = parseResult.GetValue(saveOption)!;

            MediaScanMap? map;
            try
            {
                map = MediaScanMap.FromJson(File.ReadAllText(mapPath));
            }
            catch (Exception ex)
            {
                throw new TapeConException(TapeConExitCode.UsageError, $"Couldn't read scan map >{mapPath}<: {ex.Message}");
            }
            if (map is null)
                throw new TapeConException(TapeConExitCode.UsageError, $">{mapPath}< is not a valid scan map.");

            // The copy is read from the media, and saving needs it loaded; the media's own TOC is not read
            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Media, ct);

            var result = await service.RecoverTocAsync(new RecoverTocRequest(map, ordinal)
            {
                SaveToFilePath = savePath,
                Cancellation = ct,
            });

            if (result.Success && result.SavedPath is not null)
            {
                ux.Log(WarningLevel.Info, $"Use it with --import-toc, e.g. 'tapecon list --import-toc \"{savePath}\"'.");
                return (int)TapeConExitCode.Ok;
            }

            // The swap guard refusing is distinct from a failure part-way, so scripts can tell them apart
            return result.ErrorCode == ErrorMediaChanged
                ? (int)TapeConExitCode.WriteRefused
                : (int)TapeConExitCode.OperationFailed;     // includes: recovered, but not saved
        });

        return cmd;
    }
}
