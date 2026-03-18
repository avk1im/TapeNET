using System.Collections.Immutable;
using System.Globalization;

using FclNET.Ast;

namespace FclNET;

/// <summary>
/// Recursive-descent parser that converts a token stream (produced by
/// <see cref="FclLexer"/>) into an immutable <see cref="FclExpression"/> AST.
/// <para>
/// Comment tokens are skipped automatically by the underlying
/// <see cref="FclTokenWalker"/>. The parser accumulates
/// <see cref="FclDiagnostic"/>s for any syntax errors encountered.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Grammar:</b></para>
/// <code>
/// Expression ::= OrExpr
/// OrExpr     ::= AndExpr { "or" AndExpr }
/// AndExpr    ::= NotExpr { "and" NotExpr }
/// NotExpr    ::= [ "not" ] Primary
/// Primary    ::= Condition | "(" Expression ")"
/// Condition  ::= Field Operator Value
/// </code>
/// </remarks>
public sealed class FclParser(List<FclToken> tokens)
{
    private readonly List<FclToken> _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private FclTokenWalker _walker = null!; // initialized in Parse()
    private readonly List<FclDiagnostic> _diagnostics = [];

    /// <summary>Placeholder value returned when a string value cannot be parsed.</summary>
    private const string ErrorStringValue = "<error>";

    /// <summary>Diagnostics (errors) accumulated during parsing.</summary>
    public IReadOnlyList<FclDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Records a parse error diagnostic and returns an <see cref="FclErrorExpression"/>
    /// that embeds it, allowing the parser to continue without null returns.
    /// </summary>
    private FclErrorExpression Error(string code, string message, SourceSpan span)
    {
        var diagnostic = new FclDiagnostic(FclDiagnosticSeverity.Error, code, message, span);
        _diagnostics.Add(diagnostic);
        return new FclErrorExpression(diagnostic);
    }

    /// <summary>
    /// Parses the entire token stream and returns the AST root.
    /// Returns <c>null</c> if the input is empty or contains only comments.
    /// Any errors are available via <see cref="Diagnostics"/>.
    /// </summary>
    public FclExpression? Parse()
    {
        _walker = new FclTokenWalker(_tokens);
        _walker.SkipInitialComments();
        _diagnostics.Clear();

        if (_walker.IsAtEnd)
        {
            _diagnostics.Add(new FclDiagnostic(
                FclDiagnosticSeverity.Error,
                FclDiagnosticCodes.EmptyExpression,
                "Empty expression — expected a condition.",
                _walker.Current.Span));
            return null;
        }

        var expr = ParseOrExpression();

        if (!_walker.IsAtEnd)
        {
            _diagnostics.Add(new FclDiagnostic(
                FclDiagnosticSeverity.Error,
                FclDiagnosticCodes.TrailingContent,
                $"Unexpected token '{_walker.Current.Text}' after end of expression.",
                _walker.Current.Span));
        }

        return expr;
    }

    // ─────────────────────────────────────────────────────
    //  Expression parsing (recursive descent)
    // ─────────────────────────────────────────────────────

    /// <summary>OrExpr ::= AndExpr { "or" AndExpr }</summary>
    private FclExpression ParseOrExpression()
    {
        var left = ParseAndExpression();
        if (left is FclErrorExpression) return left;

        if (!IsOrKeyword())
            return left;

        var operands = ImmutableArray.CreateBuilder<FclExpression>();
        operands.Add(left);

        while (IsOrKeyword())
        {
            _walker.Advance(); // consume "or" / "||"

            var right = ParseAndExpression();
            if (right is FclErrorExpression) break;
            operands.Add(right);
        }

        if (operands.Count == 1)
            return operands[0];

        var span = SourceSpan.Covering(operands[0].Span, operands[^1].Span);
        return new FclOrExpression(operands.ToImmutable(), span);
    }

    /// <summary>AndExpr ::= NotExpr { "and" NotExpr }</summary>
    private FclExpression ParseAndExpression()
    {
        var left = ParseNotExpression();
        if (left is FclErrorExpression) return left;

        if (!IsAndKeyword())
            return left;

        var operands = ImmutableArray.CreateBuilder<FclExpression>();
        operands.Add(left);

        while (IsAndKeyword())
        {
            _walker.Advance(); // consume "and" / "&&"

            var right = ParseNotExpression();
            if (right is FclErrorExpression) break;
            operands.Add(right);
        }

        if (operands.Count == 1)
            return operands[0];

        var span = SourceSpan.Covering(operands[0].Span, operands[^1].Span);
        return new FclAndExpression(operands.ToImmutable(), span);
    }

    /// <summary>NotExpr ::= [ "not" ] Primary</summary>
    private FclExpression ParseNotExpression()
    {
        if (IsNotKeyword())
        {
            var notToken = _walker.Current;
            _walker.Advance(); // consume "not" / "!"

            var operand = ParsePrimary();
            if (operand is FclErrorExpression) return operand;

            var span = SourceSpan.Covering(notToken.Span, operand.Span);
            return new FclNotExpression(operand, span);
        }

        return ParsePrimary();
    }

    /// <summary>Primary ::= Condition | "(" Expression ")"</summary>
    private FclExpression ParsePrimary()
    {
        if (_walker.IsAtEnd)
        {
            return Error(
                FclDiagnosticCodes.ExpectedField,
                "Expected a condition but reached end of input.",
                _walker.Current.Span);
        }

        // ── Grouped expression ──────────────────────────
        if (_walker.Current.Kind == FclTokenKind.OpenParen)
        {
            var openToken = _walker.Current;
            _walker.Advance(); // consume "("

            var inner = ParseOrExpression();
            if (inner is FclErrorExpression) return inner;

            if (_walker.Current.Kind == FclTokenKind.CloseParen)
            {
                var closeToken = _walker.Current;
                _walker.Advance(); // consume ")"

                var span = SourceSpan.Covering(openToken.Span, closeToken.Span);
                return new FclGroupExpression(inner, span);
            }
            else
            {
                _diagnostics.Add(new FclDiagnostic(
                    FclDiagnosticSeverity.Error,
                    FclDiagnosticCodes.ExpectedCloseParen,
                    "Expected closing parenthesis ')'.",
                    _walker.Current.Span));
                // Return the inner expression anyway for partial recovery.
                var span = SourceSpan.Covering(openToken.Span, inner.Span);
                return new FclGroupExpression(inner, span);
            }
        }

        // ── Condition ───────────────────────────────────
        return ParseCondition();
    }

    // ─────────────────────────────────────────────────────
    //  Condition parsing: Field Operator Value
    // ─────────────────────────────────────────────────────

    private FclExpression ParseCondition()
    {
        // ── Field ───────────────────────────────────────
        if (_walker.Current.Kind != FclTokenKind.Word || !TryParseField(_walker.Current.Text, out var field))
        {
            return Error(
                FclDiagnosticCodes.ExpectedField,
                $"Expected a field name (FullName, Name, Extension, Path, Size, Created, Modified, Attributes) but got '{_walker.Current.Text}'.",
                _walker.Current.Span);
        }

        var fieldToken = _walker.Current;
        _walker.Advance();

        // ── Operator ────────────────────────────────────
        if (_walker.IsAtEnd)
        {
            return Error(
                FclDiagnosticCodes.ExpectedOperator,
                "Expected an operator after field name.",
                _walker.Current.Span);
        }

        var fieldCategory = FclFieldTranslator.GetCategory(field);

        if (!TryParseOperator(fieldCategory, out var op, out var opSpan))
        {
            return Error(
                FclDiagnosticCodes.ExpectedOperator,
                $"Expected an operator but got '{_walker.Current.Text}'.",
                _walker.Current.Span);
        }

        // ── Value ───────────────────────────────────────
        if (_walker.IsAtEnd)
        {
            return Error(
                FclDiagnosticCodes.ExpectedValue,
                "Expected a value after operator.",
                _walker.Current.Span);
        }

        var value = ParseValue(fieldCategory);
        if (value is null)
        {
            // The value parser has already recorded a diagnostic.
            return new FclErrorExpression(_diagnostics[^1]);
        }

        // ── Semicolon expansion for matches / regex ─────
        if (op is FclOperator.Matches or FclOperator.NotMatches or FclOperator.Regex
            && value is FclStringValue sv
            && sv.Value.Contains(';'))
        {
            return ExpandSemicolonPatterns(field, fieldToken.Span, op, opSpan, sv);
        }

        var condSpan = SourceSpan.Covering(fieldToken.Span, value.Span);
        var condition = new FclCondition(field, fieldToken.Span, op, opSpan, value, condSpan);

        // ── Value chain shortcut ────────────────────────
        // "Extension equals doc or docx"      → expanded to individual conditions.
        // "Attributes have Hidden or System"   → expanded to individual conditions.
        // "FullName contains users and docs"  → expanded to individual conditions.
        if (fieldCategory is FclFieldCategory.String or FclFieldCategory.Attribute
            && IsValueChainContinuation(fieldCategory))
        {
            return ExpandValueChain(condition, fieldToken.Span, op, opSpan, fieldCategory);
        }

        // ── Range chain shortcut ────────────────────────
        // "Modified afterOrOn 2025-01-01 and before 2025-02-01"  → expanded.
        // "Size greaterThan 100KB and lessOrEqual 1MB"           → expanded.
        if (fieldCategory is FclFieldCategory.Date or FclFieldCategory.Size
            && IsRangeChainContinuation(fieldCategory))
        {
            return ExpandRangeChain(condition, field, fieldToken.Span, fieldCategory);
        }

        return condition;
    }

    // ─────────────────────────────────────────────────────
    //  Field resolution
    // ─────────────────────────────────────────────────────

    private static bool TryParseField(string text, out FclField field)
    {
        field = default;
        if (text.Equals("FullName", StringComparison.OrdinalIgnoreCase)) { field = FclField.FullName; return true; }
        if (text.Equals("Name", StringComparison.OrdinalIgnoreCase)) { field = FclField.Name; return true; }
        if (text.Equals("Extension", StringComparison.OrdinalIgnoreCase)) { field = FclField.Extension; return true; }
        if (text.Equals("Path", StringComparison.OrdinalIgnoreCase)) { field = FclField.Path; return true; }
        if (text.Equals("Size", StringComparison.OrdinalIgnoreCase)) { field = FclField.Size; return true; }
        if (text.Equals("Created", StringComparison.OrdinalIgnoreCase)) { field = FclField.Created; return true; }
        if (text.Equals("Modified", StringComparison.OrdinalIgnoreCase)) { field = FclField.Modified; return true; }
        if (text.Equals("Attributes", StringComparison.OrdinalIgnoreCase)) { field = FclField.Attributes; return true; }
        return false;
    }

    // ─────────────────────────────────────────────────────
    //  Operator resolution (context-dependent)
    // ─────────────────────────────────────────────────────

    private bool TryParseOperator(FclFieldCategory category, out FclOperator op, out SourceSpan span)
    {
        op = default;
        span = _walker.Current.Span;

        // ── Symbol operators ────────────────────────────
        switch (_walker.Current.Kind)
        {
            case FclTokenKind.DoubleEquals:
                op = FclOperator.Equals;
                span = _walker.Current.Span;
                _walker.Advance();
                return true;

            case FclTokenKind.BangEquals:
                op = FclOperator.NotEquals;
                span = _walker.Current.Span;
                _walker.Advance();
                return true;

            case FclTokenKind.LessThan:
                op = category == FclFieldCategory.Date ? FclOperator.Before : FclOperator.LessThan;
                span = _walker.Current.Span;
                _walker.Advance();
                return true;

            case FclTokenKind.LessOrEqual:
                op = category == FclFieldCategory.Date ? FclOperator.BeforeOrOn : FclOperator.LessOrEqual;
                span = _walker.Current.Span;
                _walker.Advance();
                return true;

            case FclTokenKind.GreaterThan:
                op = category == FclFieldCategory.Date ? FclOperator.After : FclOperator.GreaterThan;
                span = _walker.Current.Span;
                _walker.Advance();
                return true;

            case FclTokenKind.GreaterOrEqual:
                op = category == FclFieldCategory.Date ? FclOperator.AfterOrOn : FclOperator.GreaterOrEqual;
                span = _walker.Current.Span;
                _walker.Advance();
                return true;
        }

        // ── Word operators ──────────────────────────────
        if (_walker.Current.Kind != FclTokenKind.Word)
            return false;

        var text = _walker.Current.Text;

        if (text.Equals("equals", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.Equals; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("notEquals", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.NotEquals; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("contains", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.Contains; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("notContains", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.NotContains; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("matches", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.Matches; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("notMatches", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.NotMatches; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("regex", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.Regex; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("before", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.Before; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("beforeOrOn", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.BeforeOrOn; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("after", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.After; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("afterOrOn", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.AfterOrOn; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("greaterThan", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.GreaterThan; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("greaterOrEqual", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.GreaterOrEqual; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("lessThan", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.LessThan; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("lessOrEqual", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.LessOrEqual; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("have", StringComparison.OrdinalIgnoreCase)
            || text.Equals("has", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.Have; span = _walker.Current.Span; _walker.Advance(); return true; }

        if (text.Equals("notHave", StringComparison.OrdinalIgnoreCase)
            || text.Equals("notHas", StringComparison.OrdinalIgnoreCase))
        { op = FclOperator.NotHave; span = _walker.Current.Span; _walker.Advance(); return true; }

        return false;
    }

    // ─────────────────────────────────────────────────────
    //  Value parsing
    // ─────────────────────────────────────────────────────

    private FclValue? ParseValue(FclFieldCategory category)
    {
        return category switch
        {
            FclFieldCategory.String => ParseStringValue(),
            FclFieldCategory.Size => ParseSizeValue(),
            FclFieldCategory.Date => ParseDateValue(),
            FclFieldCategory.Attribute => ParseAttributeValue(),
            _ => ParseStringValue()
        };
    }

    // ── String values ───────────────────────────────────

    private FclStringValue ParseStringValue()
    {
        if (_walker.Current.Kind == FclTokenKind.QuotedString)
        {
            var token = _walker.Current;
            _walker.Advance();
            var content = UnescapeQuotedString(token.Text);
            return new FclStringValue(content, wasQuoted: true, token.Span);
        }

        if (_walker.Current.Kind is FclTokenKind.Word or FclTokenKind.Number)
        {
            var token = _walker.Current;
            _walker.Advance();
            return new FclStringValue(token.Text, wasQuoted: false, token.Span);
        }

        _diagnostics.Add(new FclDiagnostic(
            FclDiagnosticSeverity.Error,
            FclDiagnosticCodes.ExpectedValue,
            $"Expected a string value but got '{_walker.Current.Text}'.",
            _walker.Current.Span));
        return new FclStringValue(ErrorStringValue, wasQuoted: false, _walker.Current.Span);
    }

    /// <summary>
    /// Removes surrounding quotes and unescapes doubled quotes inside.
    /// Input: <c>"hello ""world"""</c> → Output: <c>hello "world"</c>.
    /// </summary>
    private static string UnescapeQuotedString(string raw)
    {
        // Strip surrounding quotes
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1];

        // Unescape doubled quotes
        return raw.Replace("\"\"", "\"");
    }

    // ── Size values ─────────────────────────────────────

    private FclSizeValue? ParseSizeValue()
    {
        var startToken = _walker.Current;

        if (_walker.Current.Kind != FclTokenKind.Number)
        {
            _diagnostics.Add(new FclDiagnostic(
                FclDiagnosticSeverity.Error,
                FclDiagnosticCodes.ExpectedValue,
                $"Expected a size value but got '{_walker.Current.Text}'.",
                _walker.Current.Span));
            return null;
        }

        var numberText = _walker.Current.Text;
        var lastSpan = _walker.Current.Span;
        _walker.Advance();

        // The number token may include the unit suffix (e.g. "10MB").
        // Separate the numeric part from the suffix.
        SplitNumberAndSuffix(numberText, out var numericPart, out var suffixPart);

        // If no suffix was embedded, check the next token for a unit.
        if (string.IsNullOrEmpty(suffixPart)
            && !_walker.IsAtEnd
            && _walker.Current.Kind == FclTokenKind.Word
            && TryParseSizeUnit(_walker.Current.Text, out _))
        {
            suffixPart = _walker.Current.Text;
            lastSpan = _walker.Current.Span;
            _walker.Advance();
        }

        // Parse the numeric part (strip locale group separators).
        var cleanNumeric = numericPart.Replace(",", "").Replace("_", "");

        if (!double.TryParse(cleanNumeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue))
        {
            _diagnostics.Add(new FclDiagnostic(
                FclDiagnosticSeverity.Error,
                FclDiagnosticCodes.InvalidSizeLiteral,
                $"Invalid size number: '{numericPart}'.",
                startToken.Span));
            return null;
        }

        // Resolve the unit.
        FclSizeUnit unit;
        if (!string.IsNullOrEmpty(suffixPart))
        {
            if (!TryParseSizeUnit(suffixPart, out unit))
            {
                _diagnostics.Add(new FclDiagnostic(
                    FclDiagnosticSeverity.Error,
                    FclDiagnosticCodes.InvalidSizeLiteral,
                    $"Unknown size unit: '{suffixPart}'. Expected B, KB, MB, GB, or TB.",
                    lastSpan));
                return null;
            }
        }
        else
        {
            unit = FclSizeUnit.Bytes;
        }

        long bytes = (long)(numericValue * GetSizeMultiplier(unit));
        var span = SourceSpan.Covering(startToken.Span, lastSpan);
        return new FclSizeValue(numericValue, unit, bytes, span);
    }

    private static void SplitNumberAndSuffix(string text, out string numeric, out string suffix)
    {
        // Find where the alphabetic suffix starts.
        int i = text.Length;
        for (int j = 0; j < text.Length; j++)
        {
            char c = text[j];
            if (char.IsLetter(c))
            {
                i = j;
                break;
            }
        }
        numeric = text[..i];
        suffix = text[i..];
    }

    private static bool TryParseSizeUnit(string text, out FclSizeUnit unit)
    {
        unit = default;
        if (text.Equals("B", StringComparison.OrdinalIgnoreCase)) { unit = FclSizeUnit.Bytes; return true; }
        if (text.Equals("KB", StringComparison.OrdinalIgnoreCase)) { unit = FclSizeUnit.KB; return true; }
        if (text.Equals("MB", StringComparison.OrdinalIgnoreCase)) { unit = FclSizeUnit.MB; return true; }
        if (text.Equals("GB", StringComparison.OrdinalIgnoreCase)) { unit = FclSizeUnit.GB; return true; }
        if (text.Equals("TB", StringComparison.OrdinalIgnoreCase)) { unit = FclSizeUnit.TB; return true; }
        return false;
    }

    private static double GetSizeMultiplier(FclSizeUnit unit) => unit switch
    {
        FclSizeUnit.Bytes => 1,
        FclSizeUnit.KB => 1024,
        FclSizeUnit.MB => 1024 * 1024,
        FclSizeUnit.GB => 1024 * 1024 * 1024,
        FclSizeUnit.TB => 1024L * 1024 * 1024 * 1024,
        _ => 1
    };

    // ── Date values ─────────────────────────────────────

    private FclValue? ParseDateValue()
    {
        // ── Relative date (anchor keyword) ──────────────
        if (_walker.Current.Kind == FclTokenKind.Word)
        {
            var tokenText = _walker.Current.Text;

            // Try single-token relative date (e.g. "today-7d", "yesterday", "now-2h").
            if (TryParseSingleTokenRelativeDate(tokenText, out var anchor, out var offset, out var dateUnit))
            {
                var token = _walker.Current;
                _walker.Advance();
                return new FclRelativeDateValue(anchor, offset, dateUnit, token.Span);
            }

            // Try multi-token relative date (anchor is just the first word).
            if (TryParseDateAnchor(tokenText, out anchor))
            {
                return ParseMultiTokenRelativeDate(anchor);
            }
        }

        // ── Absolute date (starts with a number) ────────
        if (_walker.Current.Kind == FclTokenKind.Number)
        {
            return ParseAbsoluteDateValue();
        }

        _diagnostics.Add(new FclDiagnostic(
            FclDiagnosticSeverity.Error,
            FclDiagnosticCodes.ExpectedValue,
            $"Expected a date value but got '{_walker.Current.Text}'.",
            _walker.Current.Span));
        return null;
    }

    /// <summary>
    /// Attempts to parse a complete relative date from a single token text.
    /// Matches patterns like: <c>today</c>, <c>today-7d</c>, <c>now-2h</c>,
    /// <c>yesterday+12h</c>, <c>now-30min</c>.
    /// </summary>
    private static bool TryParseSingleTokenRelativeDate(
        string text,
        out FclDateAnchor anchor,
        out int offset,
        out FclDateUnit unit)
    {
        anchor = default;
        offset = 0;
        unit = FclDateUnit.Days;

        // Find the anchor prefix.
        string remaining;
        if (text.StartsWith("today", StringComparison.OrdinalIgnoreCase))
        {
            anchor = FclDateAnchor.Today;
            remaining = text[5..];
        }
        else if (text.StartsWith("yesterday", StringComparison.OrdinalIgnoreCase))
        {
            anchor = FclDateAnchor.Yesterday;
            remaining = text[9..];
        }
        else if (text.StartsWith("now", StringComparison.OrdinalIgnoreCase))
        {
            anchor = FclDateAnchor.Now;
            remaining = text[3..];
        }
        else
        {
            return false;
        }

        // Bare anchor (no offset).
        if (remaining.Length == 0)
            return true;

        // Must start with + or -.
        if (remaining[0] is not '+' and not '-')
            return false;

        return TryParseOffset(remaining, out offset, out unit);
    }

    /// <summary>
    /// Parses an offset string like <c>-7d</c>, <c>+12h</c>, <c>-30min</c>.
    /// </summary>
    private static bool TryParseOffset(string text, out int offset, out FclDateUnit unit)
    {
        offset = 0;
        unit = FclDateUnit.Days;

        if (text.Length < 2) return false;

        int sign = text[0] == '-' ? -1 : 1;
        var rest = text[1..];

        // Separate digits from unit suffix.
        int digitEnd = 0;
        while (digitEnd < rest.Length && char.IsAsciiDigit(rest[digitEnd]))
            digitEnd++;

        if (digitEnd == 0) return false;

        if (!int.TryParse(rest[..digitEnd], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return false;

        var unitText = rest[digitEnd..];
        if (!TryParseDateUnit(unitText, out unit))
            return false;

        offset = sign * n;
        return true;
    }

    /// <summary>
    /// Parses a multi-token relative date. The anchor word has already been
    /// identified but not consumed. Handles forms like:
    /// <c>today - 7 d</c>, <c>today -7d</c>, <c>now - 30 min</c>.
    /// </summary>
    private FclRelativeDateValue ParseMultiTokenRelativeDate(FclDateAnchor anchor)
    {
        var anchorToken = _walker.Current;
        _walker.Advance(); // consume anchor word

        // No offset — bare anchor.
        if (_walker.IsAtEnd || !IsSignToken())
        {
            return new FclRelativeDateValue(anchor, 0, FclDateUnit.Days, anchorToken.Span);
        }

        // ── Sign token ──────────────────────────────────
        var signToken = _walker.Current;
        var signText = signToken.Text;

        // The sign token might contain the entire offset (e.g. "-7d") or just "-".
        if (signText.Length > 1)
        {
            // Token is like "-7d" or "+7d" or "-7".
            if (TryParseOffset(signText, out var offset1, out var unit1))
            {
                _walker.Advance(); // consume sign+offset token
                var span1 = SourceSpan.Covering(anchorToken.Span, signToken.Span);
                return new FclRelativeDateValue(anchor, offset1, unit1, span1);
            }

            // Could be "-7" without unit — need next token for unit.
            int sign = signText[0] == '-' ? -1 : 1;
            var numPart = signText[1..];

            // Check if it's digits possibly followed by a partial unit.
            int digitEnd = 0;
            while (digitEnd < numPart.Length && char.IsAsciiDigit(numPart[digitEnd]))
                digitEnd++;

            if (digitEnd > 0 && int.TryParse(numPart[..digitEnd], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                var unitPart = numPart[digitEnd..];
                _walker.Advance(); // consume sign+number token

                if (string.IsNullOrEmpty(unitPart))
                {
                    // Try to get unit from next token.
                    if (!_walker.IsAtEnd && _walker.Current.Kind == FclTokenKind.Word && TryParseDateUnit(_walker.Current.Text, out var nextUnit))
                    {
                        var unitToken = _walker.Current;
                        _walker.Advance();
                        var span2 = SourceSpan.Covering(anchorToken.Span, unitToken.Span);
                        return new FclRelativeDateValue(anchor, sign * n, nextUnit, span2);
                    }
                }
                else if (TryParseDateUnit(unitPart, out var embeddedUnit))
                {
                    var span3 = SourceSpan.Covering(anchorToken.Span, signToken.Span);
                    return new FclRelativeDateValue(anchor, sign * n, embeddedUnit, span3);
                }

                // Default to days if no unit found.
                var span4 = SourceSpan.Covering(anchorToken.Span, signToken.Span);
                return new FclRelativeDateValue(anchor, sign * n, FclDateUnit.Days, span4);
            }
        }

        // Sign token is just "+" or "-".
        int signValue = signText == "-" ? -1 : 1;
        _walker.Advance(); // consume sign

        if (_walker.IsAtEnd || _walker.Current.Kind is not (FclTokenKind.Number or FclTokenKind.Word))
        {
            _diagnostics.Add(new FclDiagnostic(
                FclDiagnosticSeverity.Error,
                FclDiagnosticCodes.InvalidRelativeDate,
                "Expected a number after '+'/'-' in relative date offset.",
                signToken.Span));
            return new FclRelativeDateValue(anchor, 0, FclDateUnit.Days, anchorToken.Span);
        }

        // Number token (may include unit suffix, e.g. "7d" or "30min").
        var numToken = _walker.Current;
        SplitNumberAndSuffix(numToken.Text, out var numericStr, out var unitStr);

        if (!int.TryParse(numericStr, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            _diagnostics.Add(new FclDiagnostic(
                FclDiagnosticSeverity.Error,
                FclDiagnosticCodes.InvalidRelativeDate,
                $"Invalid number in relative date offset: '{numericStr}'.",
                numToken.Span));
            _walker.Advance();
            return new FclRelativeDateValue(anchor, 0, FclDateUnit.Days, anchorToken.Span);
        }

        var lastSpan = numToken.Span;
        _walker.Advance(); // consume number token

        FclDateUnit dateUnit;
        if (!string.IsNullOrEmpty(unitStr))
        {
            if (!TryParseDateUnit(unitStr, out dateUnit))
            {
                _diagnostics.Add(new FclDiagnostic(
                    FclDiagnosticSeverity.Error,
                    FclDiagnosticCodes.InvalidRelativeDate,
                    $"Unknown date unit: '{unitStr}'. Expected d, w, m, y, h, or min.",
                    numToken.Span));
                dateUnit = FclDateUnit.Days;
            }
        }
        else
        {
            // Try next token for unit.
            if (!_walker.IsAtEnd && _walker.Current.Kind == FclTokenKind.Word && TryParseDateUnit(_walker.Current.Text, out dateUnit))
            {
                lastSpan = _walker.Current.Span;
                _walker.Advance();
            }
            else
            {
                // Default to days.
                dateUnit = FclDateUnit.Days;
            }
        }

        var finalSpan = SourceSpan.Covering(anchorToken.Span, lastSpan);
        return new FclRelativeDateValue(anchor, signValue * number, dateUnit, finalSpan);
    }

    private FclAbsoluteDateValue? ParseAbsoluteDateValue()
    {
        var token = _walker.Current;
        _walker.Advance();

        // Try ISO 8601 first, then locale format.
        var text = token.Text;
        bool hasTime = text.Contains('T') || text.Contains(':');

        // ISO 8601 formats: 2025-01-15, 2025-01-15T14:30, 2025-01-15T14:30:00
        if (DateTime.TryParseExact(text,
                ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var isoDate))
        {
            return new FclAbsoluteDateValue(isoDate, hasTime, token.Span);
        }

        // Locale format (system culture).
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var localeDate))
        {
            return new FclAbsoluteDateValue(localeDate, hasTime, token.Span);
        }

        _diagnostics.Add(new FclDiagnostic(
            FclDiagnosticSeverity.Error,
            FclDiagnosticCodes.InvalidDateLiteral,
            $"Invalid date literal: '{text}'. Expected ISO 8601 (yyyy-MM-dd) or locale format.",
            token.Span));
        return null;
    }

    private static bool TryParseDateAnchor(string text, out FclDateAnchor anchor)
    {
        anchor = default;
        if (text.Equals("today", StringComparison.OrdinalIgnoreCase)) { anchor = FclDateAnchor.Today; return true; }
        if (text.Equals("yesterday", StringComparison.OrdinalIgnoreCase)) { anchor = FclDateAnchor.Yesterday; return true; }
        if (text.Equals("now", StringComparison.OrdinalIgnoreCase)) { anchor = FclDateAnchor.Now; return true; }
        return false;
    }

    private static bool TryParseDateUnit(string text, out FclDateUnit unit)
    {
        unit = default;
        if (text.Equals("d", StringComparison.OrdinalIgnoreCase)) { unit = FclDateUnit.Days; return true; }
        if (text.Equals("w", StringComparison.OrdinalIgnoreCase)) { unit = FclDateUnit.Weeks; return true; }
        if (text.Equals("m", StringComparison.OrdinalIgnoreCase)) { unit = FclDateUnit.Months; return true; }
        if (text.Equals("y", StringComparison.OrdinalIgnoreCase)) { unit = FclDateUnit.Years; return true; }
        if (text.Equals("h", StringComparison.OrdinalIgnoreCase)) { unit = FclDateUnit.Hours; return true; }
        if (text.Equals("min", StringComparison.OrdinalIgnoreCase)) { unit = FclDateUnit.Minutes; return true; }
        return false;
    }

    /// <summary>
    /// Checks whether the current token looks like a sign (+/-) for a relative date offset.
    /// </summary>
    private bool IsSignToken()
    {
        if (_walker.Current.Kind != FclTokenKind.Word) return false;
        return _walker.Current.Text.Length > 0 && _walker.Current.Text[0] is '+' or '-';
    }

    // ── Attribute values ────────────────────────────────

    private FclAttributeValue? ParseAttributeValue()
    {
        if (_walker.Current.Kind != FclTokenKind.Word)
        {
            _diagnostics.Add(new FclDiagnostic(
                FclDiagnosticSeverity.Error,
                FclDiagnosticCodes.ExpectedValue,
                $"Expected an attribute value (Hidden, ReadOnly, System, Archive, Temporary) but got '{_walker.Current.Text}'.",
                _walker.Current.Span));
            return null;
        }

        var token = _walker.Current;
        if (!TryParseAttribute(token.Text, out var attribute))
        {
            _diagnostics.Add(new FclDiagnostic(
                FclDiagnosticSeverity.Error,
                FclDiagnosticCodes.ExpectedValue,
                $"Unknown attribute value: '{token.Text}'. Expected Hidden, ReadOnly, System, Archive, or Temporary.",
                token.Span));
            // Still advance so the parser can continue.
            _walker.Advance();
            return null;
        }

        _walker.Advance();
        return new FclAttributeValue(attribute, token.Span);
    }

    private static bool TryParseAttribute(string text, out FclAttribute attribute)
    {
        attribute = default;
        if (text.Equals("Hidden", StringComparison.OrdinalIgnoreCase)) { attribute = FclAttribute.Hidden; return true; }
        if (text.Equals("ReadOnly", StringComparison.OrdinalIgnoreCase)) { attribute = FclAttribute.ReadOnly; return true; }
        if (text.Equals("System", StringComparison.OrdinalIgnoreCase)) { attribute = FclAttribute.System; return true; }
        if (text.Equals("Archive", StringComparison.OrdinalIgnoreCase)) { attribute = FclAttribute.Archive; return true; }
        if (text.Equals("Temporary", StringComparison.OrdinalIgnoreCase)) { attribute = FclAttribute.Temporary; return true; }
        return false;
    }

    // ─────────────────────────────────────────────────────
    //  Value chain expansion
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the tokens ahead form a value chain continuation:
    /// a connective (<c>or</c>/<c>and</c>) followed by a bare value appropriate
    /// for the field category — not a field name that would start a new condition.
    /// <para>
    /// Examples of chains:
    /// <c>Extension equals doc or docx</c>,
    /// <c>Attributes has Hidden or System</c>,
    /// <c>Name matches "*.doc" or "*.txt"</c>.
    /// </para>
    /// </summary>
    private bool IsValueChainContinuation(FclFieldCategory category)
    {
        if (_walker.IsAtEnd) return false;

        // The current token must be a connective (or / and / || / &&).
        if (!IsOrKeyword() && !IsAndKeyword()) return false;

        // The next token must be a valid chain value for the field category.
        var next = _walker.Peek(1);
        return IsChainableValueToken(next, category);
    }

    /// <summary>
    /// Determines whether a token is a valid chain value for the given field
    /// category. A chain value must not be a field name (which would start a
    /// new condition) or a logical keyword (<c>not</c>).
    /// </summary>
    private static bool IsChainableValueToken(FclToken token, FclFieldCategory category)
    {
        return category switch
        {
            FclFieldCategory.String =>
                // Quoted strings are always valid chain values.
                token.Kind == FclTokenKind.QuotedString
                // Unquoted words and numbers are valid unless they are field names
                //  or the negation keyword, which would start a new expression.
                || (token.Kind is FclTokenKind.Word or FclTokenKind.Number
                    && !TryParseField(token.Text, out _)
                    && !IsNegationKeyword(token.Text)),

            FclFieldCategory.Attribute =>
                token.Kind == FclTokenKind.Word
                && !TryParseField(token.Text, out _)
                && TryParseAttribute(token.Text, out _),

            _ => false
        };
    }

    /// <summary>
    /// Returns <c>true</c> if the text is the <c>not</c> keyword, which must
    /// not be consumed as a chain value (it starts a negation expression).
    /// </summary>
    private static bool IsNegationKeyword(string text) =>
        text.Equals("not", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Expands a value chain shortcut into an OR/AND expression:
    /// <c>Extension equals doc or docx or txt</c> →
    /// <c>Extension equals doc or Extension equals docx or Extension equals txt</c>.
    /// <para>
    /// Works for both string fields (<c>Name</c>, <c>Extension</c>, etc.) and
    /// attribute fields (<c>Attributes has Hidden or System</c>). The chain
    /// connective (<c>or</c>/<c>and</c>) is determined by the first continuation
    /// and must be consistent throughout the chain.
    /// </para>
    /// </summary>
    private FclExpression ExpandValueChain(
        FclCondition firstCondition,
        SourceSpan fieldSpan,
        FclOperator op,
        SourceSpan opSpan,
        FclFieldCategory category)
    {
        var operands = ImmutableArray.CreateBuilder<FclExpression>();
        operands.Add(firstCondition);

        // Determine the chain connective from the first continuation.
        bool chainIsOr = IsOrKeyword();

        while (!_walker.IsAtEnd && IsValueChainContinuation(category))
        {
            // Verify consistent connective within the chain.
            bool currentIsOr = IsOrKeyword();
            if (currentIsOr != chainIsOr)
                break; // Connective changed — stop the chain.

            _walker.Advance(); // consume "or" / "and" / "||" / "&&"

            // Parse the chained value using the appropriate value parser.
            var chainedValue = ParseValue(category);
            if (chainedValue is null) break;

            var condSpan = SourceSpan.Covering(fieldSpan, chainedValue.Span);
            operands.Add(new FclCondition(firstCondition.Field, fieldSpan, op, opSpan, chainedValue, condSpan));
        }

        // Single operand — no actual chain was consumed.
        if (operands.Count == 1)
            return firstCondition;

        var span = SourceSpan.Covering(operands[0].Span, operands[^1].Span);
        return chainIsOr
            ? new FclOrExpression(operands.ToImmutable(), span)
            : new FclAndExpression(operands.ToImmutable(), span);
    }

    // ─────────────────────────────────────────────────────
    //  Range chain expansion (Date / Size)
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the tokens ahead form a range chain continuation:
    /// a connective (<c>or</c>/<c>and</c>) followed by an operator valid
    /// for the field category — not a field name or a bare value.
    /// <para>
    /// Examples of range chains:
    /// <c>Modified afterOrOn 2025-01-01 and before 2025-02-01</c>,
    /// <c>Size greaterThan 100KB and lessOrEqual 1MB</c>.
    /// </para>
    /// </summary>
    private bool IsRangeChainContinuation(FclFieldCategory category)
    {
        if (_walker.IsAtEnd) return false;

        // The current token must be a connective (or / and / || / &&).
        if (!IsOrKeyword() && !IsAndKeyword()) return false;

        // The next token must be an operator valid for this field category
        //  (word operator or symbol operator), NOT a field name.
        var next = _walker.Peek(1);
        return IsOperatorToken(next, category);
    }

    /// <summary>
    /// Determines whether a token looks like an operator for the given field
    /// category. Used by range chain detection to distinguish
    /// <c>and lessThan 1MB</c> (chain continues) from
    /// <c>and Name equals test</c> (new condition).
    /// </summary>
    private static bool IsOperatorToken(FclToken token, FclFieldCategory category)
    {
        // Symbol operators are always unambiguous.
        if (token.Kind is FclTokenKind.DoubleEquals or FclTokenKind.BangEquals
            or FclTokenKind.LessThan or FclTokenKind.LessOrEqual
            or FclTokenKind.GreaterThan or FclTokenKind.GreaterOrEqual)
            return true;

        if (token.Kind != FclTokenKind.Word)
            return false;

        var text = token.Text;

        // Operators shared by all categories.
        if (text.Equals("equals", StringComparison.OrdinalIgnoreCase)
            || text.Equals("notEquals", StringComparison.OrdinalIgnoreCase))
            return true;

        // Date-specific operators.
        if (category == FclFieldCategory.Date)
        {
            return text.Equals("before", StringComparison.OrdinalIgnoreCase)
                || text.Equals("beforeOrOn", StringComparison.OrdinalIgnoreCase)
                || text.Equals("after", StringComparison.OrdinalIgnoreCase)
                || text.Equals("afterOrOn", StringComparison.OrdinalIgnoreCase);
        }

        // Size-specific operators.
        if (category == FclFieldCategory.Size)
        {
            return text.Equals("greaterThan", StringComparison.OrdinalIgnoreCase)
                || text.Equals("greaterOrEqual", StringComparison.OrdinalIgnoreCase)
                || text.Equals("lessThan", StringComparison.OrdinalIgnoreCase)
                || text.Equals("lessOrEqual", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Expands a range chain shortcut into an OR/AND expression:
    /// <c>Size greaterThan 100KB and lessOrEqual 1MB</c> →
    /// <c>Size greaterThan 100KB and Size lessOrEqual 1MB</c>.
    /// <para>
    /// Unlike value chains (which share a single operator across all
    /// chained values), range chains carry their own operator+value pair
    /// for each continuation element. The field is inherited from the
    /// first condition.
    /// </para>
    /// </summary>
    private FclExpression ExpandRangeChain(
        FclCondition firstCondition,
        FclField field,
        SourceSpan fieldSpan,
        FclFieldCategory category)
    {
        var operands = ImmutableArray.CreateBuilder<FclExpression>();
        operands.Add(firstCondition);

        // Determine the chain connective from the first continuation.
        bool chainIsOr = IsOrKeyword();

        while (!_walker.IsAtEnd && IsRangeChainContinuation(category))
        {
            // Verify consistent connective within the chain.
            bool currentIsOr = IsOrKeyword();
            if (currentIsOr != chainIsOr)
                break; // Connective changed — stop the chain.

            _walker.Advance(); // consume "or" / "and" / "||" / "&&"

            // Parse the chained operator.
            if (!TryParseOperator(category, out var chainedOp, out var chainedOpSpan))
                break;

            // Parse the chained value.
            if (_walker.IsAtEnd)
                break;

            var chainedValue = ParseValue(category);
            if (chainedValue is null) break;

            var condSpan = SourceSpan.Covering(fieldSpan, chainedValue.Span);
            operands.Add(new FclCondition(field, fieldSpan, chainedOp, chainedOpSpan, chainedValue, condSpan));
        }

        // Single operand — no actual chain was consumed.
        if (operands.Count == 1)
            return firstCondition;

        var span = SourceSpan.Covering(operands[0].Span, operands[^1].Span);
        return chainIsOr
            ? new FclOrExpression(operands.ToImmutable(), span)
            : new FclAndExpression(operands.ToImmutable(), span);
    }

    // ─────────────────────────────────────────────────────
    //  Semicolon pattern expansion
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Expands a semicolon-separated value into an OR-chain of conditions:
    /// <c>Name matches "*.doc; *.txt"</c> → <c>Name matches "*.doc" or Name matches "*.txt"</c>.
    /// </summary>
    private static FclExpression ExpandSemicolonPatterns(
        FclField field, SourceSpan fieldSpan,
        FclOperator op, SourceSpan opSpan,
        FclStringValue sv)
    {
        var parts = sv.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            // No semicolons or only one pattern — no expansion needed.
            // If a trailing semicolon was stripped (e.g. "*.doc;"), use the cleaned value.
            var cleanedValue = parts.Length == 1 && parts[0] != sv.Value
                ? new FclStringValue(parts[0], sv.WasQuoted, sv.Span)
                : sv;
            var condSpan = SourceSpan.Covering(fieldSpan, sv.Span);
            return new FclCondition(field, fieldSpan, op, opSpan, cleanedValue, condSpan);
        }

        var operands = ImmutableArray.CreateBuilder<FclExpression>(parts.Length);
        foreach (var part in parts)
        {
            var partValue = new FclStringValue(part, wasQuoted: true, sv.Span);
            var condSpan = SourceSpan.Covering(fieldSpan, sv.Span);
            operands.Add(new FclCondition(field, fieldSpan, op, opSpan, partValue, condSpan));
        }

        var orSpan = SourceSpan.Covering(fieldSpan, sv.Span);
        return new FclOrExpression(operands.ToImmutable(), orSpan);
    }

    // ─────────────────────────────────────────────────────
    //  Keyword detection
    // ─────────────────────────────────────────────────────

    private bool IsOrKeyword()
    {
        if (_walker.Current.Kind == FclTokenKind.DoublePipe) return true;
        if (_walker.Current.Kind == FclTokenKind.Word && _walker.Current.Text.Equals("or", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool IsAndKeyword()
    {
        if (_walker.Current.Kind == FclTokenKind.DoubleAmpersand) return true;
        if (_walker.Current.Kind == FclTokenKind.Word && _walker.Current.Text.Equals("and", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool IsNotKeyword()
    {
        if (_walker.Current.Kind == FclTokenKind.Bang) return true;
        if (_walker.Current.Kind == FclTokenKind.Word && _walker.Current.Text.Equals("not", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
