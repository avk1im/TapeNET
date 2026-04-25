using System.CommandLine;

using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon eject</c> — unloads the media from the selected drive.
/// </summary>
internal static class EjectCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("eject", "Unload (eject) the media from the selected drive.");
        GlobalOptions.Attach(cmd);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Drive, ct);
            var ok = await service.EjectMediaAsync();

            return ok ? (int)TapeConExitCode.Ok : (int)TapeConExitCode.OperationFailed;
        });

        return cmd;
    }
}
