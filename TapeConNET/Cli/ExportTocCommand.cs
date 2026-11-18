using System.CommandLine;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon export-toc PATH</c> — reads the loaded media's TOC and saves it to a file. Replaces <c>toc export</c>.
/// </summary>
/// <remarks>
/// Two uses: a spare copy for safety, kept apart from the cartridge — the only way back to an encrypted set's files if
///  both TOC copies on the tape are lost; and a SERIES TOC: the TOC of a later volume lists every set of the volumes
///  before it, so its file serves an earlier volume whose own TOC is damaged (<c>--import-toc</c>, read-only).
/// </remarks>
internal static class ExportTocCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("export-toc",
            "Save the TOC of the loaded media to a file: a spare copy, and a source for `--import-toc`.");

        GlobalOptions.Attach(cmd);

        var pathArg = new Argument<string>("path")
        {
            Description = "File to save the TOC to, e.g. `D:\\Safe\\first.tapetoc`.",
        };
        cmd.Arguments.Add(pathArg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            var path = parseResult.GetValue(pathArg)!;

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct,
                tocHint: VerbHost.TocRecoveryHint);
            var ok = await service.ExportTOCToFileAsync(path);
            return ok ? (int)TapeConExitCode.Ok : (int)TapeConExitCode.OperationFailed;
        });

        return cmd;
    }
}
