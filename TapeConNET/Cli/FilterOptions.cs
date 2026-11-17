using System.CommandLine;
using TapeConNET.Cli.Help;

namespace TapeConNET.Cli;

/// <summary>
/// Selection-filter options shared by <c>backup</c>, <c>restore</c>,
/// <c>validate</c>, <c>verify</c>, and <c>list</c>. Both options accept FCL
/// (File Conditions Language) expressions; bare positional arguments on the
/// same verbs are auto-classified by
/// <see cref="TapeConNET.Filtering.FilterResolver"/>.
/// </summary>
/// <remarks>Help group: <i>File selection</i> (Design-CliPresentation §4.3).</remarks>
internal static class FilterOptions
{
    public static readonly Option<string?> Filter = new("--filter")
    {
        Description = "FCL condition the selected files must meet, e.g. " +
                      "`\"Name matches *.doc* and Size greaterThan 1MB\"`. See `tapecon docs fcl`.",
        HelpName = "FCL",
    };

    public static readonly Option<string?> FilterFile = new("--filter-file")
    {
        Description = "File holding an FCL condition the selected files must meet.",
        HelpName = "PATH",
    };

    static FilterOptions()
    {
        HelpGroups.Assign(HelpGroups.Filter, Filter, FilterFile);
    }

    /// <summary>Attaches both filter options to <paramref name="cmd"/>.</summary>
    public static void Attach(Command cmd)
    {
        cmd.Options.Add(Filter);
        cmd.Options.Add(FilterFile);
    }
}
