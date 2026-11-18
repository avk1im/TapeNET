using FclNET;
using TapeConNET.Infrastructure;
using TapeLibNET.Agents;

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
/// <c>TapeConNET-2.0-Architecture.md §3.2</c>:
/// <list type="number">
///   <item>An existing file with the <c>.fcl</c> extension is treated as an FCL file.</item>
///   <item>Inline FCL: a string opening with <c>(</c>, <c>!</c> or <c>not</c>, or with an FCL field name
///         (<c>Name</c>, <c>FullName</c>, <c>Path</c>, <c>Extension</c>, <c>Size</c>, <c>Created</c>,
///         <c>Modified</c>, <c>Attributes</c>) followed by whitespace or an operator symbol.</item>
///   <item>An existing directory or file path is treated as a literal path.</item>
///   <item>Anything else is treated as a DOS-style wildcard pattern (e.g. <c>*.doc*</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Combining — as TapeWinNET does.</b> Paths and wildcards are the BASIC selection: any of them selects a file (OR).
///  Every FCL source — inline expressions, <c>.fcl</c> files, <c>--filter</c>, <c>--filter-file</c> — is an ADVANCED
///  condition that narrows it (AND): <c>restore 0 *.docx *.xlsx "Size &gt; 1MB"</c> selects the large Word and Excel
///  files. For <c>backup</c>, paths and wildcards are the sources; the FCL conditions filter what they name.
/// </para>
/// <para>
/// Each part is parenthesized on lines of its own, so an <c>.fcl</c> file ending in a <c>//</c> comment cannot swallow the
///  closing parenthesis, and a part's own <c>and</c>/<c>or</c> keep their precedence.
/// </para>
/// </remarks>
internal static class FilterResolver
{
    /// <summary>FCL's field names (FCL-Specification, Fields), matched case-insensitively.</summary>
    private static readonly HashSet<string> s_fclFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "FullName", "Name", "Extension", "Path", "Size", "Created", "Modified", "Attributes",
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
        var parts = Classify(positionalArgs, treatPathsAsSources: false);
        AppendExplicit(parts, filterFcl, filterFilePath);
        return new ResolvedFilter(parts.Sources, BuildFilter(parts.Patterns, parts.Conditions), parts.HasInline);
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
        var parts = Classify(positionalArgs, treatPathsAsSources: true);
        AppendExplicit(parts, filterFcl, filterFilePath);
        return new ResolvedFilter(parts.Sources, BuildFilter(parts.Patterns, parts.Conditions), parts.HasInline);
    }

    // ─── Implementation ─────────────────────────────────────────────────

    private enum ArgKind { FclFile, FclInline, Path, WildcardPattern }

    /// <summary>The classified arguments: sources (backup), basic patterns (as FCL), advanced FCL conditions.</summary>
    private sealed class Parts
    {
        public List<string> Sources { get; } = [];
        public List<string> Patterns { get; } = [];
        public List<string> Conditions { get; } = [];
        public bool HasInline { get; set; }
    }

    private static ArgKind Classify(string arg)
    {
        // .fcl file ─ trumps every other interpretation
        if (LooksLikeFclFile(arg))
            return ArgKind.FclFile;

        // FCL field (or not / ! / parenthesis) start
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

    /// <summary>
    /// True when <paramref name="arg"/> reads as inline FCL: it opens with <c>(</c>, <c>!</c> or the word <c>not</c>, or
    ///  with a field name followed by whitespace or an operator symbol (<c>Size&gt;1MB</c>).
    /// </summary>
    /// <remarks>
    /// A field name alone is not enough: <c>size-report.xlsx</c>, <c>Path\to\*.doc</c> and <c>names*.txt</c> are a file,
    ///  a folder pattern and a wildcard. Every condition needs an operator after its field, so requiring the separator
    ///  costs no valid expression.
    /// </remarks>
    private static bool StartsWithFclKeyword(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return false;

        var span = arg.AsSpan().TrimStart();
        if (span[0] is '(' or '!')
            return true;

        // First word, letters only: field names and `not` hold nothing else
        int end = 0;
        while (end < span.Length && char.IsLetter(span[end]))
            end++;
        if (end == 0 || end == span.Length)
            return false;                                       // no word, or a lone word: no condition

        var word = span[..end];
        char next = span[end];

        if (word.Equals("not", StringComparison.OrdinalIgnoreCase))
            return char.IsWhiteSpace(next) || next == '(';

        return s_fclFields.Contains(word.ToString())
            && (char.IsWhiteSpace(next) || next is '=' or '!' or '<' or '>');
    }

    /// <summary>
    /// Sorts positional args into sources (backup: paths and wildcards), basic patterns (other verbs: paths and
    ///  wildcards, as FCL) and advanced conditions (FCL files and inline FCL).
    /// </summary>
    private static Parts Classify(IReadOnlyList<string> args, bool treatPathsAsSources)
    {
        var parts = new Parts();
        foreach (var arg in args)
        {
            switch (Classify(arg))
            {
                case ArgKind.FclFile:
                    parts.Conditions.Add(LoadFclFile(arg));
                    parts.HasInline = true;
                    break;
                case ArgKind.FclInline:
                    parts.Conditions.Add(arg);
                    parts.HasInline = true;
                    break;
                case ArgKind.Path:
                case ArgKind.WildcardPattern:
                    if (treatPathsAsSources)
                        parts.Sources.Add(arg);
                    else
                        parts.Patterns.Add(WildcardToFcl(arg));
                    break;
            }
        }
        return parts;
    }

    private static void AppendExplicit(Parts parts, string? filterFcl, string? filterFilePath)
    {
        if (!string.IsNullOrWhiteSpace(filterFcl))
        {
            parts.Conditions.Add(filterFcl);
            parts.HasInline = true;
        }
        if (!string.IsNullOrWhiteSpace(filterFilePath))
        {
            parts.Conditions.Add(LoadFclFile(filterFilePath));
            parts.HasInline = true;
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
    /// The one FCL expression of a selection: the patterns OR-ed, then AND-ed with every condition (see the remarks).
    ///  Null when there is neither.
    /// </summary>
    internal static string? Combine(IReadOnlyList<string> patterns, IReadOnlyList<string> conditions)
    {
        var parts = new List<string>(conditions.Count + 1);
        if (patterns.Count > 0)
            parts.Add(patterns.Count == 1 ? patterns[0] : string.Join(" or ", patterns.Select(Group)));
        parts.AddRange(conditions);

        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => string.Join(" and ", parts.Select(Group)),
        };
    }

    /// <summary>Parenthesized on lines of its own: a trailing <c>//</c> comment must not swallow the closing parenthesis.</summary>
    private static string Group(string fcl) => $"(\n{fcl}\n)";

    /// <summary>Parses <see cref="Combine"/>'s expression into a filter; null when there is nothing to filter by.</summary>
    private static FclTapeFileFilter? BuildFilter(IReadOnlyList<string> patterns, IReadOnlyList<string> conditions)
    {
        if (Combine(patterns, conditions) is not { } combined)
            return null;

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
    /// <c>FullName matches "pattern"</c>. Used when bare positional args are
    /// being interpreted as selection filters (not as backup sources).
    /// </summary>
    private static string WildcardToFcl(string pattern)
    {
        // Escape embedded quotes by doubling, per FCL string-literal rules.
        var escaped = pattern.Replace("\"", "\"\"");
        return $"FullName matches \"{escaped}\"";
    }
}
