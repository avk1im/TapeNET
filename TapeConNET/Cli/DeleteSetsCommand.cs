using System.CommandLine;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon delete-sets SET</c> — deletes backup sets from <c>SET</c> through the last
///  set on the volume (physically overwrites the tape past the last retained set).
/// </summary>
/// <remarks>
/// Also the repair verb: it rewrites the damaged tail of a volume and saves the TOC — with <c>--import-toc</c>, the
///  imported one, which then is the volume's TOC again. A later volume's TOC is read-only here: refused by the service.
/// </remarks>
internal static class DeleteSetsCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("delete-sets",
            "Delete backup sets from a given set through the end of the volume, and save the TOC.");

        GlobalOptions.Attach(cmd);
        TocOptions.Attach(cmd);

        var setArg = new Argument<string>("set")
        {
            Description = "First set index to delete (positive = oldest-up, 0 = latest, -1/-2/... = older). " +
                          "All sets from this one through the last on the volume are removed.",
        };
        cmd.Arguments.Add(setArg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            var setSpec = parseResult.GetValue(setArg)!;
            var importToc = parseResult.GetValue(TocOptions.ImportToc);

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct,
                tocHint: VerbHost.TocRecoveryHint, importToc: importToc);

            if (!service.TryParseSetIndex(setSpec, out int setIndex))
                throw new TapeConException(TapeConExitCode.UsageError,
                    $"Invalid set index >{setSpec}<.");
            if (service.TOC is not null)
                setIndex = service.TOC.SetIndexToStd(service.TOC.CapSetIndex(setIndex));

            var result = await service.DeleteBackupSetsExAsync(setIndex);
            return (int)VerbHost.ToExitCode(result.WasAborted, result.HasFailed,
                setWriteBlocked: result.SetStats.SetWriteBlocked);
        });

        return cmd;
    }
}
