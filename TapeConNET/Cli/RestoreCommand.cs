using System.CommandLine;

using TapeLibNET;

using TapeConNET.Infrastructure;
using TapeConNET.Services;
using TapeConNET.Ux;
using TapeConNET.Filtering;
using TapeLibNET.Services;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon restore</c> — restore the contents of a backup set (or a range
/// of sets) to a target directory. Set spec follows the dual convention
/// (positive=oldest-up, 0/negative=latest-down). The default is the latest
/// set (index 0).
/// </summary>
internal static class RestoreCommand
{
    public static Command Create(IConsoleUx ux) =>
        BuildRestoreLikeCommand(ux, "restore", "Restore files from the loaded media to a target folder.", RestoreMode.Restore, withTarget: true);

    public static Command CreateValidate(IConsoleUx ux) =>
        BuildRestoreLikeCommand(ux, "validate", "Validate per-file CRC integrity for a backup set without writing files.", RestoreMode.Validate, withTarget: false);

    public static Command CreateVerify(IConsoleUx ux) =>
        BuildRestoreLikeCommand(ux, "verify", "Verify a backup set against on-disk files (byte-by-byte compare).", RestoreMode.Verify, withTarget: false);

    private static Command BuildRestoreLikeCommand(IConsoleUx ux, string name, string desc, RestoreMode mode, bool withTarget)
    {
        var cmd = new Command(name, desc);
        GlobalOptions.Attach(cmd);
        FilterOptions.Attach(cmd);

        var setArg = new Argument<string?>("set")
        {
            Description = "Set index to process (positive = oldest-up, 0 = latest, -1/-2/... = older). Default: 0 (latest).",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var filterArgs = new Argument<string[]>("filter-args")
        {
            Description = "Optional filter arguments: DOS wildcard patterns, inline FCL, " +
                          "or a path to a .fcl file. Combined with --filter / --filter-file.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var targetOption = new Option<string?>("--target", "-t")
        {
            Description = "Target folder for restored files (only for restore).",
        };
        var subdirsOption = new Option<bool>("--subdirs", "-s")
        {
            Description = "Recreate the original directory structure under the target folder.",
        };
        var existingOption = new Option<TapeHowToHandleExisting>("--existing", "-x")
        {
            Description = "How to handle existing target files: Skip / Overwrite / KeepBoth.",
            DefaultValueFactory = _ => TapeHowToHandleExisting.KeepBoth,
        };
        var incrementalOption = new Option<bool?>("--incremental", "-i")
        {
            Description = "Force-on or force-off the incremental chain expansion (default: follow set's flag).",
        };
        var skipErrorsOption = new Option<bool>("--skip-errors")
        {
            Description = "Skip per-file errors automatically without prompting.",
        };

        cmd.Arguments.Add(setArg);
        cmd.Arguments.Add(filterArgs);
        if (withTarget) cmd.Options.Add(targetOption);
        cmd.Options.Add(subdirsOption);
        if (withTarget) cmd.Options.Add(existingOption);
        cmd.Options.Add(incrementalOption);
        cmd.Options.Add(skipErrorsOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            var setSpec     = parseResult.GetValue(setArg);
            var rawFilters  = parseResult.GetValue(filterArgs) ?? [];
            var target      = withTarget ? parseResult.GetValue(targetOption) : null;
            var subdirs     = parseResult.GetValue(subdirsOption);
            var existing    = withTarget ? parseResult.GetValue(existingOption) : TapeHowToHandleExisting.Skip;
            var incremental = parseResult.GetValue(incrementalOption);
            var skipErrors  = parseResult.GetValue(skipErrorsOption);
            var filterFcl   = parseResult.GetValue(FilterOptions.Filter);
            var filterFile  = parseResult.GetValue(FilterOptions.FilterFile);

            // Bare positional args after `set` are auto-classified as wildcard
            //  patterns, inline FCL, or .fcl files and combined with the
            //  explicit --filter / --filter-file into one selection filter.
            var resolved = FilterResolver.ResolveForSelection(rawFilters, filterFcl, filterFile);

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct);

            int setIndex = 0; // default: latest set
            if (!string.IsNullOrWhiteSpace(setSpec))
            {
                if (!service.TryParseSetIndex(setSpec, out setIndex))
                    throw new TapeConException(TapeConExitCode.UsageError,
                        $"Invalid set index >{setSpec}<.");
            }
            // Normalize 0/negative (latest-down) to standard 1..MaxSetIndex
            //  form expected by TOC.SelectFilesFromSets.
            if (service.TOC is not null)
                setIndex = service.TOC.SetIndexToStd(service.TOC.CapSetIndex(setIndex));

            if (mode == RestoreMode.Restore && string.IsNullOrEmpty(target))
                target = Directory.GetCurrentDirectory();

            // null value = "all files in that set"
            var checkedFiles = new Dictionary<int, IReadOnlyList<TapeFileInfo>?>
            {
                [setIndex] = null,
            };

            var options = new RestoreRequest(
                Mode:                 mode,
                CheckedFilesBySet:    checkedFiles,
                Incremental:          incremental ?? true,
                TargetDirectory:      target,
                RecurseSubdirectories: subdirs,
                HandleExisting:       existing,
                SkipAllErrors:        skipErrors,
                Filter:               resolved.Filter);

            var result = await service.ExecuteRestoreAsync(options);
            return (int)VerbHost.ToExitCode(result.WasAborted, result.HasFailed || result.FilesFailed > 0);
        });

        return cmd;
    }
}
