using System.CommandLine;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon format</c> — formats the loaded media (optionally creating an
/// initiator partition for the TOC) and writes an empty initial TOC.
/// </summary>
internal static class FormatCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("format",
            "Format the loaded media and write an empty initial TOC. WARNING: erases all data.");

        GlobalOptions.Attach(cmd);

        var mediaDescOption = MediaOptions.Description(
            "Media description written into the new TOC (default: 'Media created <date time>').");
        var singleOption = new Option<bool>("--single-partition", "--single")
        {
            Description = "Force single-partition format (TOC stored in the first set instead of an initiator partition).",
        };
        var yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Skip the confirmation prompt.",
        };

        cmd.Options.Add(mediaDescOption);
        cmd.Options.Add(singleOption);
        cmd.Options.Add(yesOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            var mediaDesc = parseResult.GetValue(mediaDescOption);
            var single    = parseResult.GetValue(singleOption);
            var yes       = parseResult.GetValue(yesOption);

            if (!yes && !ux.Confirm("WARNING: Formatting media will erase ALL data. Proceed?", defaultAnswer: false))
                return (int)TapeConExitCode.Cancelled;

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Media, ct);

            long initiatorSize = single ? -1 : service.DefaultTOCCapacity;
            var ok = await service.FormatMediaAsync(initiatorSize, mediaDesc);
            return ok ? (int)TapeConExitCode.Ok : (int)TapeConExitCode.OperationFailed;
        });

        return cmd;
    }
}
