using System.Text.RegularExpressions;

using FclNET.Ast;

namespace FclNET;

/// <summary>
/// Walks the AST produced by the parser and reports semantic errors
/// (field/operator mismatches, field/value type mismatches, invalid regex patterns, etc.).
/// </summary>
public static class FclValidator
{
    /// <summary>
    /// Validates an FCL expression tree and returns all diagnostics found.
    /// An empty list means the AST is valid and safe to evaluate.
    /// </summary>
    public static List<FclDiagnostic> Validate(FclExpression expression)
    {
        var diagnostics = new List<FclDiagnostic>();
        Visit(expression, diagnostics);
        return diagnostics;
    }

    private static void Visit(FclExpression node, List<FclDiagnostic> diagnostics)
    {
        switch (node)
        {
            case FclErrorExpression error:
                // Surface the parse error embedded in the AST.
                diagnostics.Add(error.Diagnostic);
                break;

            case FclCondition condition:
                ValidateCondition(condition, diagnostics);
                break;

            case FclOrExpression or:
                foreach (var operand in or.Operands)
                    Visit(operand, diagnostics);
                break;

            case FclAndExpression and:
                foreach (var operand in and.Operands)
                    Visit(operand, diagnostics);
                break;

            case FclNotExpression not:
                Visit(not.Operand, diagnostics);
                break;

            case FclGroupExpression group:
                Visit(group.Inner, diagnostics);
                break;
        }
    }

    private static void ValidateCondition(FclCondition condition, List<FclDiagnostic> diagnostics)
    {
        var fieldCategory = FclFieldTranslator.GetCategory(condition.Field);

        // ── Operator compatibility ──────────────────────────
        ValidateOperatorForField(condition, fieldCategory, diagnostics);

        // ── Value compatibility ─────────────────────────────
        ValidateValueForField(condition, fieldCategory, diagnostics);

        // ── Regex pattern validation ────────────────────────
        if (condition.Operator == FclOperator.Regex && condition.Value is FclStringValue regexVal)
        {
            ValidateRegexPattern(regexVal, diagnostics);
        }
    }

    private static void ValidateOperatorForField(
        FclCondition condition, FclFieldCategory category, List<FclDiagnostic> diagnostics)
    {
        var op = condition.Operator;

        switch (category)
        {
            case FclFieldCategory.String:
                if (IsDateOperator(op))
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.DateOperatorOnNonDate, condition.OperatorSpan,
                        $"Operator '{op}' is for date fields; '{condition.Field}' is a string field."));
                else if (IsSizeOperator(op))
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.SizeOperatorOnNonSize, condition.OperatorSpan,
                        $"Operator '{op}' is for the Size field; '{condition.Field}' is a string field."));
                else if (IsAttributeOperator(op))
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.AttributeOperatorOnNonAttribute, condition.OperatorSpan,
                        $"Operator '{op}' is for the Attributes field; '{condition.Field}' is a string field."));
                break;

            case FclFieldCategory.Date:
                if (IsStringOnlyOperator(op))
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.StringOperatorOnNonString, condition.OperatorSpan,
                        $"Operator '{op}' is for string fields; '{condition.Field}' is a date field."));
                else if (IsSizeOperator(op))
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.SizeOperatorOnNonSize, condition.OperatorSpan,
                        $"Operator '{op}' is for the Size field; '{condition.Field}' is a date field."));
                else if (IsAttributeOperator(op))
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.AttributeOperatorOnNonAttribute, condition.OperatorSpan,
                        $"Operator '{op}' is for the Attributes field; '{condition.Field}' is a date field."));
                break;

            case FclFieldCategory.Size:
                if (IsStringOnlyOperator(op))
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.StringOperatorOnNonString, condition.OperatorSpan,
                        $"Operator '{op}' is for string fields; 'Size' is a numeric field."));
                else if (IsDateOperator(op))
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.DateOperatorOnNonDate, condition.OperatorSpan,
                        $"Operator '{op}' is for date fields; 'Size' is a numeric field."));
                else if (IsAttributeOperator(op))
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.AttributeOperatorOnNonAttribute, condition.OperatorSpan,
                        $"Operator '{op}' is for the Attributes field; 'Size' is a numeric field."));
                break;

            case FclFieldCategory.Attribute:
                if (!IsAttributeOperator(op) && op != FclOperator.Equals && op != FclOperator.NotEquals)
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.IncompatibleOperator, condition.OperatorSpan,
                        $"Operator '{op}' is not valid for the Attributes field. Use 'has' or 'notHas'."));
                break;
        }
    }

    private static void ValidateValueForField(
        FclCondition condition, FclFieldCategory category, List<FclDiagnostic> diagnostics)
    {
        var value = condition.Value;

        switch (category)
        {
            case FclFieldCategory.String:
                if (value is not FclStringValue)
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.IncompatibleValue, value.Span,
                        $"Field '{condition.Field}' requires a string value."));
                break;

            case FclFieldCategory.Date:
                if (value is not (FclAbsoluteDateValue or FclRelativeDateValue))
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.IncompatibleValue, value.Span,
                        $"Field '{condition.Field}' requires a date value."));
                break;

            case FclFieldCategory.Size:
                if (value is not FclSizeValue)
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.IncompatibleValue, value.Span,
                        "Field 'Size' requires a size value."));
                break;

            case FclFieldCategory.Attribute:
                if (value is not FclAttributeValue)
                    diagnostics.Add(MakeDiagnostic(
                        FclDiagnosticCodes.IncompatibleValue, value.Span,
                        "Field 'Attributes' requires an attribute value (Hidden, ReadOnly, System, Archive, Temporary)."));
                break;
        }
    }

    private static void ValidateRegexPattern(FclStringValue value, List<FclDiagnostic> diagnostics)
    {
        try
        {
            _ = new Regex(value.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            diagnostics.Add(MakeDiagnostic(
                FclDiagnosticCodes.InvalidRegex, value.Span,
                $"Invalid regular expression: {ex.Message}"));
        }
    }

    // ── Helpers ──────────────────────────────────────────

    private static bool IsStringOnlyOperator(FclOperator op) =>
        op is FclOperator.Contains or FclOperator.NotContains
           or FclOperator.Matches or FclOperator.Regex;

    private static bool IsDateOperator(FclOperator op) =>
        op is FclOperator.Before or FclOperator.BeforeOrOn
           or FclOperator.After or FclOperator.AfterOrOn;

    private static bool IsSizeOperator(FclOperator op) =>
        op is FclOperator.GreaterThan or FclOperator.GreaterOrEqual
           or FclOperator.LessThan or FclOperator.LessOrEqual;

    private static bool IsAttributeOperator(FclOperator op) =>
        op is FclOperator.Has or FclOperator.NotHas;

    private static FclDiagnostic MakeDiagnostic(string code, SourceSpan span, string message) =>
        new(FclDiagnosticSeverity.Error, code, message, span);
}
