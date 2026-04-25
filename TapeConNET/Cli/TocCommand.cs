using System.CommandLine;

using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon toc export PATH</c> — saves the on-tape TOC to a file.
/// <c>tapecon toc import PATH</c> — loads a TOC from a file (overrides the
/// on-tape TOC for the rest of the process; useful when the on-tape TOC is
/// damaged).
/// </summary>
internal static class TocCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("toc", "Export or import the table of contents (TOC) to/from a file.");

        cmd.Subcommands.Add(BuildExport(ux));
        cmd.Subcommands.Add(BuildImport(ux));
        return cmd;
    }

    private static Command BuildExport(IConsoleUx ux)
    {
        var sub = new Command("export", "Save the loaded TOC to a file.");
        GlobalOptions.Attach(sub);

        var pathArg = new Argument<string>("path") { Description = "Destination file path." };
        sub.Arguments.Add(pathArg);

        sub.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();
            var path = parseResult.GetValue(pathArg)!;

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct);
            var ok = await service.ExportTOCToFileAsync(path);
            return ok ? (int)TapeConExitCode.Ok : (int)TapeConExitCode.OperationFailed;
        });

        return sub;
    }

    private static Command BuildImport(IConsoleUx ux)
    {
        var sub = new Command("import", "Load a TOC from a file (overrides the on-tape TOC for this session).");
        GlobalOptions.Attach(sub);

        var pathArg = new Argument<string>("path") { Description = "TOC file to load." };
        sub.Arguments.Add(pathArg);

        sub.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();
            var path = parseResult.GetValue(pathArg)!;

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Media, ct);
            var ok = await service.ImportTOCFromFileAsync(path);
            return ok ? (int)TapeConExitCode.Ok : (int)TapeConExitCode.OperationFailed;
        });

        return sub;
    }
}
