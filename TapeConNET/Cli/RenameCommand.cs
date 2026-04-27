using System.CommandLine;

using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon rename-media</c>   — renames the loaded tape media.
/// <c>tapecon rename-set INDEX</c> — renames the backup set at <c>INDEX</c>.
/// </summary>
internal static class RenameCommand
{
    public static Command CreateRenameMedia(IConsoleUx ux)
    {
        var cmd = new Command("rename-media", "Rename the loaded tape media.");
        GlobalOptions.Attach(cmd);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();
            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct);
            var ok = await service.RenameMediaAsync();
            return ok ? (int)TapeConExitCode.Ok : (int)TapeConExitCode.OperationFailed;
        });

        return cmd;
    }

    public static Command CreateRenameSet(IConsoleUx ux)
    {
        var cmd = new Command("rename-set", "Rename a backup set on the loaded tape.");
        GlobalOptions.Attach(cmd);

        var indexArg = new Argument<int>("index")
        {
            Description = "Standard (1-based) set index to rename.",
        };
        cmd.Arguments.Add(indexArg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();
            int setIndex = parseResult.GetValue(indexArg);
            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct);
            var ok = await service.RenameBackupSetAsync(setIndex);
            return ok ? (int)TapeConExitCode.Ok : (int)TapeConExitCode.OperationFailed;
        });

        return cmd;
    }
}
