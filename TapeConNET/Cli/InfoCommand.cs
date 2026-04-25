using System.CommandLine;

using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon info</c> — opens the selected drive, loads media, restores
/// TOC, and prints drive + media + TOC information. Equivalent to running
/// <c>tapecon list</c> with no set range.
/// </summary>
internal static class InfoCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("info", "Show drive, media, and TOC information.");
        GlobalOptions.Attach(cmd);

        cmd.SetAction(async (parseResult, ct) =>
        {
            await Task.Yield();
            ux.WriteBanner();

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct);
            var result = await service.ListContentsAsync(new Services.ListOptions(
                StartSetIndex: null,
                EndSetIndex:   null,
                FilePatterns:  null,
                IncrementalOverride: false, // info: don't expand incrementals — only summary
                ShowFullPath:  true));

            return result.HasFailed
                ? (int)TapeConExitCode.OperationFailed
                : (int)TapeConExitCode.Ok;
        });

        return cmd;
    }
}
