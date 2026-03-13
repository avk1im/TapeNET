namespace FclNET;

/// <summary>
/// Severity level for FCL diagnostics.
/// </summary>
public enum FclDiagnosticSeverity
{
    /// <summary>A warning that doesn't prevent evaluation (e.g. redundant parentheses).</summary>
    Warning,
    /// <summary>An error that prevents evaluation.</summary>
    Error
}

/// <summary>
/// A diagnostic message produced during FCL parsing, validation, or evaluation.
/// </summary>
/// <param name="Severity">Warning or error.</param>
/// <param name="Code">Machine-readable error code (e.g. "FCL001").</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="Span">Position in the original source text. <see cref="SourceSpan.None"/> if not applicable.</param>
public readonly record struct FclDiagnostic(
    FclDiagnosticSeverity Severity,
    string Code,
    string Message,
    SourceSpan Span)
{
    public override string ToString() =>
        Span.Length > 0
            ? $"{Severity} {Code} at {Span}: {Message}"
            : $"{Severity} {Code}: {Message}";
}

/// <summary>
/// Well-known FCL diagnostic codes.
/// </summary>
public static class FclDiagnosticCodes
{
    // ── Parse errors (FCL0xx) ───────────────────────────

    /// <summary>Unexpected token encountered.</summary>
    public const string UnexpectedToken = "FCL001";

    /// <summary>Expected a closing parenthesis.</summary>
    public const string ExpectedCloseParen = "FCL002";

    /// <summary>Expected a field name.</summary>
    public const string ExpectedField = "FCL003";

    /// <summary>Expected an operator.</summary>
    public const string ExpectedOperator = "FCL004";

    /// <summary>Expected a value.</summary>
    public const string ExpectedValue = "FCL005";

    /// <summary>Unterminated string literal (missing closing quote).</summary>
    public const string UnterminatedString = "FCL006";

    /// <summary>Empty expression (no input or only whitespace).</summary>
    public const string EmptyExpression = "FCL007";

    /// <summary>Invalid size literal format.</summary>
    public const string InvalidSizeLiteral = "FCL008";

    /// <summary>Invalid date literal format.</summary>
    public const string InvalidDateLiteral = "FCL009";

    /// <summary>Invalid relative date format.</summary>
    public const string InvalidRelativeDate = "FCL010";

    /// <summary>Unexpected characters after the end of the expression.</summary>
    public const string TrailingContent = "FCL011";

    // ── Validation errors (FCL1xx) ──────────────────────

    /// <summary>Operator is not compatible with the field type.</summary>
    public const string IncompatibleOperator = "FCL101";

    /// <summary>Value type does not match the field type.</summary>
    public const string IncompatibleValue = "FCL102";

    /// <summary>Unknown attribute name.</summary>
    public const string UnknownAttribute = "FCL103";

    /// <summary>String operator used with a non-string field.</summary>
    public const string StringOperatorOnNonString = "FCL104";

    /// <summary>Date operator used with a non-date field.</summary>
    public const string DateOperatorOnNonDate = "FCL105";

    /// <summary>Size operator used with a non-size field.</summary>
    public const string SizeOperatorOnNonSize = "FCL106";

    /// <summary>Attribute operator used with a non-attribute field.</summary>
    public const string AttributeOperatorOnNonAttribute = "FCL107";

    // ── Evaluation errors (FCL2xx) ──────────────────────

    /// <summary>Invalid regular expression pattern.</summary>
    public const string InvalidRegex = "FCL201";

    /// <summary>Date arithmetic overflow.</summary>
    public const string DateOverflow = "FCL202";

    /// <summary>Other evaluation errors.</summary>
    public const string GeneralEvaluationError = "FCL299";
}
