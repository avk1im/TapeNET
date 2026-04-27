using System.CommandLine;

using TapeConNET.Infrastructure;
using TapeConNET.Services;
using TapeConNET.Ux;
using TapeConNET.Filtering;
using TapeLibNET.Services;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon list</c> — lists the contents of the loaded media. Optional
/// <c>start</c> and <c>end</c> set indexes select a range; remaining
/// positional args are FCL/wildcard patterns to filter the listing.
/// </summary>
internal static class ListCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("list",
            "List the contents of the loaded media. Optional set range and file patterns may be specified.");
        GlobalOptions.Attach(cmd);
        FilterOptions.Attach(cmd);

        var argsArg = new Argument<string[]>("args")
        {
            Description = "Optional set index(es) followed by filter arguments " +
                          "(DOS wildcard patterns, inline FCL, or .fcl file paths). " +
                          "Examples: '0', '1 3', '-2 0 *.txt', 'photos/*.jpg', " +
                          "'0 \"size > 1MB\"', './nightly.fcl'.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var noIncOption = new Option<bool>("--no-incremental")
        {
            Description = "List only the selected incremental set without expanding earlier dependencies.",
        };
        var nameOnlyOption = new Option<bool>("--name-only")
        {
            Description = "Show file names only (no full paths).",
        };
        var setsOnlyOption = new Option<bool>("--sets-only")
        {
            Description = "Show a compact backup-sets table only (no per-file listing).",
        };

        cmd.Arguments.Add(argsArg);
        cmd.Options.Add(noIncOption);
        cmd.Options.Add(nameOnlyOption);
        cmd.Options.Add(setsOnlyOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            var raw        = parseResult.GetValue(argsArg) ?? [];
            var noInc      = parseResult.GetValue(noIncOption);
            var nameOnly   = parseResult.GetValue(nameOnlyOption);
            var setsOnly   = parseResult.GetValue(setsOnlyOption);
            var filterFcl  = parseResult.GetValue(FilterOptions.Filter);
            var filterFile = parseResult.GetValue(FilterOptions.FilterFile);

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct);

            // Parse leading 0..2 tokens as set indexes; the rest are patterns
            int? startIdx = null, endIdx = null;
            int consumed = 0;
            if (raw.Length >= 1 && service.TryParseSetIndex(raw[0], out var s1))
            {
                startIdx = s1;
                consumed = 1;
                if (raw.Length >= 2 && service.TryParseSetIndex(raw[1], out var s2))
                {
                    endIdx = s2;
                    consumed = 2;
                }
                else
                {
                    endIdx = startIdx;
                }
            }
            string[] patterns = consumed < raw.Length ? raw[consumed..] : [];

            // Auto-classify any trailing tokens (wildcards, inline FCL, .fcl
            //  files) and combine them with --filter / --filter-file.
            var resolved = FilterResolver.ResolveForSelection(patterns, filterFcl, filterFile);

            var options = new ListRequest(
                StartSetIndex:       startIdx,
                EndSetIndex:         endIdx,
                FilePatterns:        null,
                IncrementalOverride: noInc ? false : null,
                ShowFullPath:        !nameOnly,
                Filter:              resolved.Filter,
                Depth:               setsOnly ? ListDepth.SetsOverview : ListDepth.Full);

            var result = await service.ListContentsAsync(options);
            return !result.Success
                ? (int)TapeConExitCode.OperationFailed
                : (int)TapeConExitCode.Ok;
        });

        return cmd;
    }
}
