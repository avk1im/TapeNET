using System.Text;

using FclNET.Ast;

namespace FclNET;

/// <summary>
/// Controls how <see cref="FclFormatter"/> renders an FCL expression tree
/// back to source text.
/// </summary>
public sealed class FclFormatOptions
{
    /// <summary>
    /// Default options: single-line, word operators, ISO 8601 dates.
    /// </summary>
    public static readonly FclFormatOptions Default = new();

    /// <summary>
    /// Multi-line preset: each condition on its own line, groups indented.
    /// </summary>
    public static readonly FclFormatOptions MultiLine = new() { ConditionPerLine = true };

    /// <summary>
    /// When <c>true</c>, each condition starts on a new line.
    /// Logical operators (<c>or</c>, <c>and</c>) appear at the beginning of
    /// continuation lines (SQL-style).
    /// </summary>
    public bool ConditionPerLine { get; init; }

    /// <summary>
    /// When <c>true</c> (and <see cref="ConditionPerLine"/> is also <c>true</c>),
    /// opening parentheses are placed on their own line (C-style braces).
    /// When <c>false</c>, the opening parenthesis stays inline and the first
    /// inner operand follows on the same line.
    /// </summary>
    public bool BracesOnNewLine { get; init; }

    /// <summary>
    /// When <c>true</c>, uses word-form operators (<c>equals</c>, <c>greaterThan</c>).
    /// When <c>false</c>, prefers symbolic operators where available (<c>==</c>, <c>&gt;</c>).
    /// </summary>
    public bool PreferWordOperators { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, absolute dates are formatted as ISO 8601 (<c>yyyy-MM-dd</c>).
    /// When <c>false</c>, uses <see cref="System.Globalization.CultureInfo.CurrentCulture"/>.
    /// </summary>
    public bool UseIso8601Dates { get; init; } = true;

    /// <summary>
    /// Number of spaces per indentation level (used inside groups
    /// when <see cref="ConditionPerLine"/> is <c>true</c>).
    /// </summary>
    public int IndentSize { get; init; } = 2;
} // FclFormatOptions


/// <summary>
/// Formats an <see cref="FclExpression"/> AST back to FCL source text.
/// <para>
/// This class provides the public entry point and internal helper methods
/// used by the polymorphic <see cref="FclExpression.FormatTo"/> /
/// <see cref="FclValue.FormatTo"/> methods on each AST node.
/// </para>
/// </summary>
public static class FclFormatter
{
    // ─────────────────────────────────────────────────────
    //  Public entry point
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Formats an expression tree to FCL source text using the given options.
    /// </summary>
    /// <param name="expression">The AST root to format.</param>
    /// <param name="options">
    /// Formatting options. Pass <c>null</c> (or omit) to use
    /// <see cref="FclFormatOptions.Default"/>.
    /// </param>
    public static string Format(FclExpression expression, FclFormatOptions? options = null)
    {
        var opts = options ?? FclFormatOptions.Default;
        var sb = new StringBuilder();
        expression.FormatTo(sb, opts, indent: 0);
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────
    //  Field / operator / enum name helpers
    // ─────────────────────────────────────────────────────

    /// <summary>Returns the canonical FCL field name.</summary>
    internal static string FieldToString(FclField field) => field switch
    {
        FclField.FullName   => "FullName",
        FclField.Name       => "Name",
        FclField.Extension  => "Extension",
        FclField.Path       => "Path",
        FclField.Size       => "Size",
        FclField.Created    => "Created",
        FclField.Modified   => "Modified",
        FclField.Attributes => "Attributes",
        _                   => field.ToString()
    };

    /// <summary>
    /// Returns the FCL operator keyword or symbol, depending on
    /// <paramref name="preferWord"/>.
    /// Operators that only have a word form always return the word form.
    /// </summary>
    internal static string OperatorToString(FclOperator op, bool preferWord) => op switch
    {
        // Equality — have both symbolic and word forms
        FclOperator.Equals    when !preferWord => "==",
        FclOperator.Equals                     => "equals",
        FclOperator.NotEquals when !preferWord => "!=",
        FclOperator.NotEquals                  => "notEquals",

        // String-only — word form only
        FclOperator.Contains    => "contains",
        FclOperator.NotContains => "notContains",
        FclOperator.Matches     => "matches",
        FclOperator.Regex       => "regex",

        // Date comparison — symbolic aliases shared with size operators
        FclOperator.Before      when !preferWord => "<",
        FclOperator.Before                       => "before",
        FclOperator.BeforeOrOn  when !preferWord => "<=",
        FclOperator.BeforeOrOn                   => "beforeOrOn",
        FclOperator.After       when !preferWord => ">",
        FclOperator.After                        => "after",
        FclOperator.AfterOrOn   when !preferWord => ">=",
        FclOperator.AfterOrOn                    => "afterOrOn",

        // Size comparison — have both symbolic and word forms
        FclOperator.GreaterThan    when !preferWord => ">",
        FclOperator.GreaterThan                     => "greaterThan",
        FclOperator.GreaterOrEqual when !preferWord => ">=",
        FclOperator.GreaterOrEqual                  => "greaterOrEqual",
        FclOperator.LessThan       when !preferWord => "<",
        FclOperator.LessThan                        => "lessThan",
        FclOperator.LessOrEqual    when !preferWord => "<=",
        FclOperator.LessOrEqual                     => "lessOrEqual",

        // Attribute — word form only
        FclOperator.Has    => "has",
        FclOperator.NotHas => "notHas",

        _ => op.ToString()
    };

    /// <summary>Returns the canonical FCL size-unit suffix.</summary>
    internal static string SizeUnitToString(FclSizeUnit unit) => unit switch
    {
        FclSizeUnit.Bytes => "B",
        FclSizeUnit.KB    => "KB",
        FclSizeUnit.MB    => "MB",
        FclSizeUnit.GB    => "GB",
        FclSizeUnit.TB    => "TB",
        _                 => unit.ToString()
    };

    /// <summary>Returns the canonical FCL date-anchor keyword.</summary>
    internal static string DateAnchorToString(FclDateAnchor anchor) => anchor switch
    {
        FclDateAnchor.Today     => "today",
        FclDateAnchor.Yesterday => "yesterday",
        FclDateAnchor.Now       => "now",
        _                       => anchor.ToString()
    };

    /// <summary>Returns the canonical FCL date-unit suffix.</summary>
    internal static string DateUnitToString(FclDateUnit unit) => unit switch
    {
        FclDateUnit.Minutes => "min",
        FclDateUnit.Hours   => "h",
        FclDateUnit.Days    => "d",
        FclDateUnit.Weeks   => "w",
        FclDateUnit.Months  => "m",
        FclDateUnit.Years   => "y",
        _                   => unit.ToString()
    };

    /// <summary>Returns the canonical FCL attribute name.</summary>
    internal static string AttributeToString(FclAttribute attr) => attr switch
    {
        FclAttribute.Hidden    => "Hidden",
        FclAttribute.ReadOnly  => "ReadOnly",
        FclAttribute.System    => "System",
        FclAttribute.Archive   => "Archive",
        FclAttribute.Temporary => "Temporary",
        _                      => attr.ToString()
    };

    // ─────────────────────────────────────────────────────
    //  Quoting helpers
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether a string value must be quoted to be safely
    /// round-tripped through the lexer / parser.
    /// </summary>
    internal static bool NeedsQuoting(string value)
    {
        if (value.Length == 0) return true;
        foreach (char c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.')
                return true;
        }
        return false;
    }

    /// <summary>
    /// Appends a double-quoted string to <paramref name="sb"/>, escaping
    /// embedded double-quotes by doubling them.
    /// </summary>
    internal static void AppendQuoted(StringBuilder sb, string value)
    {
        sb.Append('"');
        sb.Append(value.Replace("\"", "\"\""));
        sb.Append('"');
    }

    // ─────────────────────────────────────────────────────
    //  Indentation helper
    // ─────────────────────────────────────────────────────

    /// <summary>Appends <paramref name="spaces"/> space characters.</summary>
    internal static void AppendIndent(StringBuilder sb, int spaces)
    {
        if (spaces > 0)
            sb.Append(' ', spaces);
    }
}
