using System.Diagnostics;
using System.IO.Enumeration;
using System.Text.RegularExpressions;

using FclNET.Ast;

namespace FclNET;

/// <summary>
/// Evaluates a validated FCL expression tree against <see cref="IFclFileInfo"/> instances.
/// <para>
/// Designed for high-throughput batch evaluation (10,000s of files). Assumes the AST
/// has already been validated by <see cref="FclValidator"/> —> only runtime errors
/// (regex failures, date overflow) are caught and reported.
/// </para>
/// </summary>
public sealed class FclEvaluator
{
    private readonly FclExpression _root;
    private readonly Dictionary<FclCondition, Regex> _regexCache = [];
    private readonly Dictionary<FclCondition, Regex> _wildcardRegexCache = [];
    private readonly Dictionary<FclCondition, string[]> _wildcardPatterns = [];
    private readonly List<FclDiagnostic> _runtimeDiagnostics = [];

    /// <summary>
    /// Creates an evaluator for the given expression tree.
    /// Pre-compiles regex patterns, expands wildcard patterns, and resolves
    /// relative dates to a consistent snapshot of the current time.
    /// </summary>
    /// <param name="expression">A validated AST root.</param>
    public FclEvaluator(FclExpression expression)
    {
        _root = expression;
        Preprocess(expression);
    }

    /// <summary>
    /// Runtime diagnostics accumulated during evaluation (e.g. regex errors, date overflow).
    /// </summary>
    public IReadOnlyList<FclDiagnostic> RuntimeDiagnostics => _runtimeDiagnostics;

    /// <summary>
    /// Evaluates the expression against a single file.
    /// Returns <c>true</c> if the file matches the filter.
    /// </summary>
    public bool Evaluate(IFclFileInfo file)
    {
        return EvalNode(_root, file);
    }

    // ─────────────────────────────────────────────────────
    //  Pre-compilation
    // ─────────────────────────────────────────────────────

    private void Preprocess(FclExpression node)
    {
        switch (node)
        {
            case FclErrorExpression:
                break; // Nothing to preprocess for error nodes.

            case FclCondition condition:
                if (condition.Value is FclStringValue sv)
                {
                    if (condition.Operator == FclOperator.Regex)
                    {
                        try
                        {
                            _regexCache[condition] = new Regex(
                                sv.Value,
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                        }
                        catch (ArgumentException)
                        {
                            // Validator already reported this; store a never-match sentinel.
                            _regexCache[condition] = NeverMatchRegex();
                        }
                    }
                    else if (condition.Operator is FclOperator.Matches or FclOperator.NotMatches)
                    {
                        var patterns = sv.Value
                            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                        if (condition.Field is FclField.Name or FclField.Extension)
                        {
                            // Name/Extension never contain backslashes — use the faster
                            //  FileSystemName.MatchesSimpleExpression for evaluation.
                            _wildcardPatterns[condition] = patterns;
                        }
                        else if (condition.Field == FclField.FullName)
                        {
                            // Optimization: patterns without path separators (e.g. "*.txt",
                            //  "*.doc?", "MyPresentation.*") clearly target only the filename
                            //  component and can use the much faster
                            //  FileSystemName.MatchesSimpleExpression against Path.GetFileName().
                            //  Patterns with separators (e.g. "C:\docs\*", "\doc?") still need
                            //  regex for unanchored fragment matching across segments.
                            List<string>? nameTargetable = null;
                            List<string>? pathBased = null;

                            foreach (var p in patterns)
                            {
                                if (p.AsSpan().IndexOfAny('\\', '/') < 0)
                                    (nameTargetable ??= []).Add(p);
                                else
                                    (pathBased ??= []).Add(p);
                            }

                            if (nameTargetable is not null)
                                _wildcardPatterns[condition] = [.. nameTargetable];

                            if (pathBased is not null)
                            {
                                var combined = string.Join('|',
                                    pathBased.Select(p => WildcardPatternToRegex(p, isPathField: false)));
                                _wildcardRegexCache[condition] = new Regex(
                                    combined,
                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                            }
                        }
                        else
                        {
                            // Path: compile to regex for unanchored fragment matching.
                            //  FileSystemName.MatchesSimpleExpression is unsuitable here because
                            //  it treats '\' as a path separator and prevents '*'/'?' from
                            //  crossing directory segments.
                            var combined = string.Join('|',
                                patterns.Select(p => WildcardPatternToRegex(p, isPathField: true)));
                            _wildcardRegexCache[condition] = new Regex(
                                combined,
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                        }
                    }
                }
                else if (condition.Value is FclRelativeDateValue rel)
                {
                    // Resolve relative dates once so every file in the batch sees
                    //  the same "now" snapshot and we avoid per-file arithmetic.
                    try
                    {
                        rel.UpdateCachedValue();
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        _runtimeDiagnostics.Add(new FclDiagnostic(
                            FclDiagnosticSeverity.Error,
                            FclDiagnosticCodes.DateOverflow,
                            "Date arithmetic resulted in an overflow.",
                            condition.Value.Span));
                    }
                }
                break;

            case FclChainExpression chain:
                foreach (var op in chain.Operands) Preprocess(op);
                break;

            case FclNotExpression not:
                Preprocess(not.Operand);
                break;

            case FclGroupExpression group:
                Preprocess(group.Inner);
                break;
        }
    }

    // ─────────────────────────────────────────────────────
    //  Tree evaluation
    // ─────────────────────────────────────────────────────

    private bool EvalNode(FclExpression node, IFclFileInfo file) => node switch
    {
        FclErrorExpression => false, // Parse error — validator should have flagged this.
        FclCondition c => EvalCondition(c, file),
        FclOrExpression or => EvalOr(or, file),
        FclAndExpression and => EvalAnd(and, file),
        FclNotExpression not => !EvalNode(not.Operand, file),
        FclGroupExpression group => EvalNode(group.Inner, file),
        _ => false
    };

    private bool EvalOr(FclOrExpression or, IFclFileInfo file)
    {
        var operands = or.Operands;
        for (int i = 0; i < operands.Length; i++)
        {
            if (EvalNode(operands[i], file))
                return true;
        }
        return false;
    }

    private bool EvalAnd(FclAndExpression and, IFclFileInfo file)
    {
        var operands = and.Operands;
        for (int i = 0; i < operands.Length; i++)
        {
            if (!EvalNode(operands[i], file))
                return false;
        }
        return true;
    }

    // ─────────────────────────────────────────────────────
    //  Condition dispatch
    // ─────────────────────────────────────────────────────

    private bool EvalCondition(FclCondition c, IFclFileInfo file)
    {
        return c.Field switch
        {
            FclField.FullName => EvalString(c, file.FullName),
            FclField.Name => EvalString(c, Path.GetFileName(file.FullName)),
            FclField.Extension => EvalString(c, Path.GetExtension(file.FullName)),
            FclField.Path => EvalString(c, Path.GetDirectoryName(file.FullName) ?? string.Empty),
            FclField.Size => EvalSize(c, file.Size),
            FclField.Created => EvalDate(c, file.CreationTime),
            FclField.Modified => EvalDate(c, file.LastWriteTime),
            FclField.Attributes => EvalAttributes(c, file.Attributes),
            _ => false
        };
    }

    // ── String evaluation ───────────────────────────────

    private bool EvalString(FclCondition c, string actual)
    {
        var expected = ((FclStringValue)c.Value).Value;

        return c.Operator switch
        {
            FclOperator.Equals => actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            FclOperator.NotEquals => !actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            FclOperator.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            FclOperator.NotContains => !actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            FclOperator.Matches => EvalWildcard(c, actual),
            FclOperator.NotMatches => !EvalWildcard(c, actual),
            FclOperator.Regex => EvalRegex(c, actual),
            _ => false
        };
    }

    private bool EvalWildcard(FclCondition c, string actual)
    {
        // Name/Extension/FullName (name-targetable): fast path via FileSystemName.
        //  For FullName, name-targetable patterns (without path separators) are
        //  matched against just the filename component — much faster than regex.
        bool hasNamePatterns = _wildcardPatterns.TryGetValue(c, out var patterns);

        if (hasNamePatterns)
        {
            var target = c.Field == FclField.FullName
                ? Path.GetFileName(actual.AsSpan())
                : actual.AsSpan();

            for (int i = 0; i < patterns!.Length; i++) // since TryGetValue succeeded, patterns is non-null
            {
                if (FileSystemName.MatchesSimpleExpression(patterns[i], target, ignoreCase: true))
                    return true;
            }

            // For Name/Extension, all patterns live here — no regex fallthrough needed.
            if (c.Field is FclField.Name or FclField.Extension)
                return false;
        }

        // FullName/Path: regex-based fragment matching (path-containing patterns).
        //  For FullName, this is checked after the name-targetable fast path above,
        //  so a mixed condition like "*.txt; C:\docs\*" checks both branches.
        if (_wildcardRegexCache.TryGetValue(c, out var regex))
            return regex.IsMatch(actual);

        // FullName with only name-targetable patterns — no regex branch needed.
        if (hasNamePatterns)
            return false;

        // Fallback (shouldn't happen after Preprocess)
        Debug.WriteLine(c, "Warning: wildcard pattern not preprocessed; compiling on-the-fly");
        var value = ((FclStringValue)c.Value).Value;
        bool isPathField = c.Field == FclField.Path;
        regex = new Regex(
            WildcardPatternToRegex(value, isPathField),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        _wildcardRegexCache[c] = regex;
        return regex.IsMatch(actual);
    }

    private bool EvalRegex(FclCondition c, string actual)
    {
        if (_regexCache.TryGetValue(c, out var regex))
            return regex.IsMatch(actual);

        // Fallback: compile on-the-fly
        try
        {
            var pattern = ((FclStringValue)c.Value).Value;
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _regexCache[c] = regex;
            return regex.IsMatch(actual);
        }
        catch (ArgumentException ex)
        {
            _runtimeDiagnostics.Add(new FclDiagnostic(
                FclDiagnosticSeverity.Error,
                FclDiagnosticCodes.InvalidRegex,
                $"Invalid regular expression: {ex.Message}",
                c.Value.Span));
            return false;
        }
    }

    // ── Size evaluation ─────────────────────────────────

    private static bool EvalSize(FclCondition c, long actual)
    {
        var expected = ((FclSizeValue)c.Value).Bytes;

        return c.Operator switch
        {
            FclOperator.Equals => actual == expected,
            FclOperator.NotEquals => actual != expected,
            FclOperator.GreaterThan => actual > expected,
            FclOperator.GreaterOrEqual => actual >= expected,
            FclOperator.LessThan => actual < expected,
            FclOperator.LessOrEqual => actual <= expected,
            _ => false
        };
    }

    // ── Date evaluation ─────────────────────────────────

    private static bool EvalDate(FclCondition c, DateTime actual)
    {
        DateTime expected;
        bool dateOnly;

        if (c.Value is FclAbsoluteDateValue abs)
        {
            expected = abs.Value;
            dateOnly = !abs.HasTime;
        }
        else if (c.Value is FclRelativeDateValue rel)
        {
            expected = rel.CachedValue;
            dateOnly = !rel.HasTime;
        }
        else
        {
            // Shouldn't happen
            Debug.Fail("Unexpected date value type");
            return false;
        }

        // For date-only comparisons, truncate the actual value to its date part.
        if (dateOnly)
            actual = actual.Date;

        return c.Operator switch
        {
            FclOperator.Equals => dateOnly ? actual.Date == expected.Date : actual == expected,
            FclOperator.NotEquals => dateOnly ? actual.Date != expected.Date : actual != expected,
            FclOperator.Before => actual < expected,
            FclOperator.BeforeOrOn => dateOnly ? actual <= expected.Date.AddDays(1).AddTicks(-1) : actual <= expected,
            FclOperator.After => dateOnly ? actual > expected.Date.AddDays(1).AddTicks(-1) : actual > expected,
            FclOperator.AfterOrOn => actual >= expected,
            _ => false
        };
    }

    // ── Attribute evaluation ────────────────────────────

    private static bool EvalAttributes(FclCondition c, FileAttributes actual)
    {
        var expected = ((FclAttributeValue)c.Value).ToFileAttributes();

        return c.Operator switch
        {
            FclOperator.Have => (actual & expected) == expected,
            FclOperator.NotHave => (actual & expected) == 0,
            _ => false
        };
    }

    // ── Utility ─────────────────────────────────────────

    /// <summary>
    /// Converts a DOS-style wildcard pattern to a regex pattern string for
    /// <b>unanchored fragment matching</b>, following the same approach as
    /// <c>TapeSetTOC.FromFilePatternToRegexPattern()</c>.
    /// <list type="bullet">
    /// <item><c>*</c> matches any sequence of characters including path separators.</item>
    /// <item><c>?</c> matches any single character.</item>
    /// </list>
    /// <para>Trailing backslash handling depends on <paramref name="isPathField"/>:</para>
    /// <list type="bullet">
    /// <item><b>Path</b> (<c>true</c>): trailing <c>\</c> is stripped, since
    ///   <c>Path.GetDirectoryName</c> returns without trailing separator.</item>
    /// <item><b>FullName</b> (<c>false</c>): trailing <c>\</c> → append <c>*.*</c>
    ///   ("any file in this directory and subdirectories").</item>
    /// </list>
    /// <para>The result is <b>not anchored</b> — the pattern matches as a fragment
    ///  anywhere in the value, so e.g. <c>\doc?</c> matches
    ///  <c>C:\docs\test.txt</c> as well as <c>C:\users\docs\test.txt</c>.</para>
    /// </summary>
    internal static string WildcardPatternToRegex(string pattern, bool isPathField)
    {
        // Trailing backslash:
        //  Path  → strip (GetDirectoryName never returns trailing \)
        //  FullName → means "any file here", like TapeSetTOC: append *.*
        if (pattern.EndsWith('\\'))
            pattern = isPathField ? pattern[..^1] : pattern + "*.*";

        // Escape regex-special characters, then restore wildcards:
        //  \* → .*   (match any characters including \)
        //  \? → .    (match any single character)
        return Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");
    }

#pragma warning disable SYSLIB1045 // Convert to 'GeneratedRegexAttribute'. No need to optimize since
                                   //  this pattern is only used as a sentinel for invalid patterns.
    private static Regex NeverMatchRegex() =>
        new("(?!)", RegexOptions.Compiled);
#pragma warning restore SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.

}
