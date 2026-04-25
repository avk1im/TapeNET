using System.IO;

using FclNET;
using FclNET.Ast;

using TapeConNET.Infrastructure;
using TapeLibNET;

namespace TapeConNET.Filtering;

/// <summary>
/// Result of classifying a verb's positional/filter arguments.
/// <see cref="Sources"/> are paths/wildcard patterns to expand into a file
/// list (relevant only for <c>backup</c>); <see cref="Filter"/> is an
/// optional <see cref="ITapeFileFilter"/> to apply during the operation.
/// </summary>
internal sealed record ResolvedFilter(
    List<string> Sources,
    ITapeFileFilter? Filter,
    bool HasInlineFilter);

/// <summary>
/// Centralized parsing of <c>--filter</c> / <c>--filter-file</c> options and
/// of bare positional arguments per the auto-detect order documented in
/// <c>TapeConNET-2.0-Architecture.md §4</c>:
/// <list type="number">
///   <item>An existing file with the <c>.fcl</c> extension is treated as an
///         FCL file.</item>
///   <item>A string starting with an FCL keyword
///         (<c>name</c>, <c>fullname</c>, <c>path</c>, <c>extension</c>,
///         <c>size</c>, <c>created</c>, <c>modified</c>, <c>date</c>,
///         <c>attr</c>/<c>attributes</c>, <c>(</c>) is treated as inline FCL.</item>
///   <item>An existing directory or file path is treated as a literal
///         path.</item>
///   <item>Anything else is treated as a DOS-style wildcard pattern
///         (e.g. <c>*.doc*</c>).</item>
/// </list>
/// </summary>
internal static class FilterResolver
{
    private static readonly HashSet<string> s_fclKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "fullname", "path", "extension",
        "size", "created", "modified", "date",
        "attr", "attributes",
    };

    /// <summary>
    /// Resolves the filter inputs of a non-backup verb (restore / validate /
    /// verify / list). All positional args are treated as filter inputs;
    /// <see cref="ResolvedFilter.Sources"/> is always empty.
    /// </summary>
    public static ResolvedFilter ResolveForSelection(
        IReadOnlyList<string> positionalArgs,
        string? filterFcl,
        string? filterFilePath)
    {
        var (sources, filterParts, hasInline) = Classify(positionalArgs, treatPathsAsSources: false);

        AppendExplicit(filterParts, ref hasInline, filterFcl, filterFilePath);

        var filter = BuildFilter(filterParts);
        return new ResolvedFilter(sources, filter, hasInline);
    }

    /// <summary>
    /// Resolves the filter inputs of <c>backup</c>. Path-like positional
    /// args are treated as backup sources; FCL-like args (and the explicit
    /// <c>--filter</c>/<c>--filter-file</c>) become the selection filter.
    /// </summary>
    public static ResolvedFilter ResolveForBackup(
        IReadOnlyList<string> positionalArgs,
        string? filterFcl,
        string? filterFilePath)
    {
        var (sources, filterParts, hasInline) = Classify(positionalArgs, treatPathsAsSources: true);

        AppendExplicit(filterParts, ref hasInline, filterFcl, filterFilePath);

        var filter = BuildFilter(filterParts);
        return new ResolvedFilter(sources, filter, hasInline);
    }

    // ─── Implementation ─────────────────────────────────────────────────

    private enum ArgKind { FclFile, FclInline, Path, WildcardPattern }

    private static ArgKind Classify(string arg)
    {
        // .fcl file ─ trumps every other interpretation
        if (LooksLikeFclFile(arg))
            return ArgKind.FclFile;

        // FCL keyword start
        if (StartsWithFclKeyword(arg))
            return ArgKind.FclInline;

        // Existing path
        if (Directory.Exists(arg) || File.Exists(arg))
            return ArgKind.Path;

        // Default: DOS-style wildcard pattern
        return ArgKind.WildcardPattern;
    }

    private static bool LooksLikeFclFile(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return false;
        if (!arg.EndsWith(".fcl", StringComparison.OrdinalIgnoreCase))
            return false;
        try { return File.Exists(arg); }
        catch { return false; }
    }

    private static bool StartsWithFclKeyword(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return false;
        var span = arg.AsSpan().TrimStart();
        if (span.Length == 0)
            return false;
        if (span[0] == '(')
            return true;
        // First word
        int end = 0;
        while (end < span.Length && (char.IsLetter(span[end]) || span[end] == '_'))
            end++;
        if (end == 0)
            return false;
        return s_fclKeywords.Contains(span[..end].ToString());
    }

    /// <summary>
    /// Splits positional args into source paths/patterns (only when
    /// <paramref name="treatPathsAsSources"/> is true) and a list of FCL
    /// filter fragments. Wildcard patterns become source patterns for backup
    /// and selection-only wildcard filters otherwise.
    /// </summary>
    private static (List<string> Sources, List<string> FilterParts, bool HasInline)
        Classify(IReadOnlyList<string> args, bool treatPathsAsSources)
    {
        var sources = new List<string>();
        var filterParts = new List<string>();
        bool hasInline = false;

        foreach (var arg in args)
        {
            switch (Classify(arg))
            {
                case ArgKind.FclFile:
                    filterParts.Add(LoadFclFile(arg));
                    hasInline = true;
                    break;

                case ArgKind.FclInline:
                    filterParts.Add(arg);
                    hasInline = true;
                    break;

                case ArgKind.Path:
                    if (treatPathsAsSources)
                        sources.Add(arg);
                    else
                        filterParts.Add(WildcardToFcl(arg));
                    break;

                case ArgKind.WildcardPattern:
                    if (treatPathsAsSources)
                        sources.Add(arg);
                    else
                        filterParts.Add(WildcardToFcl(arg));
                    break;
            }
        }

        return (sources, filterParts, hasInline);
    }

    private static void AppendExplicit(
        List<string> filterParts, ref bool hasInline,
        string? filterFcl, string? filterFilePath)
    {
        if (!string.IsNullOrWhiteSpace(filterFcl))
        {
            filterParts.Add(filterFcl);
            hasInline = true;
        }
        if (!string.IsNullOrWhiteSpace(filterFilePath))
        {
            filterParts.Add(LoadFclFile(filterFilePath));
            hasInline = true;
        }
    }

    private static string LoadFclFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new TapeConException(TapeConExitCode.UsageError,
                $"Couldn't read FCL file >{path}<: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the combined <see cref="ITapeFileFilter"/> for a list of FCL
    /// fragments (and bare wildcard FCL phrases). When the user supplied
    /// multiple fragments they are combined with OR. Returns <c>null</c>
    /// when no fragments were provided.
    /// </summary>
    private static FclTapeFileFilter? BuildFilter(List<string> filterParts)
    {
        if (filterParts.Count == 0)
            return null;

        // Combine fragments with OR. Each fragment is wrapped in parentheses
        //  so user-supplied AND/OR precedence is preserved within a fragment.
        var combined = filterParts.Count == 1
            ? filterParts[0]
            : string.Join(" || ", filterParts.Select(p => $"({p})"));

        var parseResult = FclPipeline.TryParse(combined);
        if (!parseResult.IsValid || parseResult.Expression is null)
        {
            var first = parseResult.Diagnostics.Count > 0
                ? parseResult.Diagnostics[0].Message
                : "unknown FCL parse error";
            throw new TapeConException(TapeConExitCode.UsageError,
                $"Invalid FCL filter: {first}");
        }

        return new FclTapeFileFilter(new FclEvaluator(parseResult.Expression));
    }

    /// <summary>
    /// Wraps a wildcard pattern (e.g. <c>*.doc*</c>) as the FCL phrase
    /// <c>fullname matches "pattern"</c>. Used when bare positional args are
    /// being interpreted as selection filters (not as backup sources).
    /// </summary>
    private static string WildcardToFcl(string pattern)
    {
        // Escape embedded quotes by doubling, per FCL string-literal rules.
        var escaped = pattern.Replace("\"", "\"\"");
        return $"fullname matches \"{escaped}\"";
    }
}
