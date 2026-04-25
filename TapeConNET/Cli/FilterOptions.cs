using System.CommandLine;

namespace TapeConNET.Cli;

/// <summary>
/// Selection-filter options shared by <c>backup</c>, <c>restore</c>,
/// <c>validate</c>, <c>verify</c>, and <c>list</c>. Both options accept FCL
/// (File Conditions Language) expressions; bare positional arguments on the
/// same verbs are auto-classified by
/// <see cref="TapeConNET.Filtering.FilterResolver"/>.
/// </summary>
internal static class FilterOptions
{
    public static readonly Option<string?> Filter = new("--filter")
    {
        Description = "FCL expression used as the selection filter " +
                      "(e.g. 'name matches \"*.doc*\" && size > 1MB').",
    };

    public static readonly Option<string?> FilterFile = new("--filter-file")
    {
        Description = "Path to a file containing the FCL selection filter.",
    };

    /// <summary>Attaches both filter options to <paramref name="cmd"/>.</summary>
    public static void Attach(Command cmd)
    {
        cmd.Options.Add(Filter);
        cmd.Options.Add(FilterFile);
    }
}
