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
                    else if (condition.Operator == FclOperator.Matches)
                    {
                        // Pre-split semicolon patterns (parser may have already expanded,
                        //  but if a single string value contains semicolons we handle it here).
                        _wildcardPatterns[condition] = sv.Value
                            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
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

            case FclOrExpression or:
                foreach (var op in or.Operands) Preprocess(op);
                break;

            case FclAndExpression and:
                foreach (var op in and.Operands) Preprocess(op);
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
            FclOperator.Regex => EvalRegex(c, actual),
            _ => false
        };
    }

    private bool EvalWildcard(FclCondition c, string actual)
    {
        if (_wildcardPatterns.TryGetValue(c, out var patterns))
        {
            for (int i = 0; i < patterns.Length; i++)
            {
                if (FileSystemName.MatchesSimpleExpression(patterns[i], actual, ignoreCase: true))
                    return true;
            }
            return false;
        }

        // Fallback (shouldn't happen after PreparePatterns)
        var value = ((FclStringValue)c.Value).Value;
        return FileSystemName.MatchesSimpleExpression(value, actual, ignoreCase: true);
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
            FclOperator.Has => (actual & expected) == expected,
            FclOperator.NotHas => (actual & expected) == 0,
            _ => false
        };
    }

    // ── Utility ─────────────────────────────────────────

#pragma warning disable SYSLIB1045 // Convert to 'GeneratedRegexAttribute'. No need to optimize since
                                   //  this pattern is only used as a sentinel for invalid patterns.
    private static Regex NeverMatchRegex() =>
        new("(?!)", RegexOptions.Compiled);
#pragma warning restore SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.

}
