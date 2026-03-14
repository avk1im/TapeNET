using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace FclNET.Ast;

/// <summary>
/// Base class for all FCL Abstract Syntax Tree nodes.
/// Every node carries a <see cref="Span"/> referencing its position in the original source text.
/// </summary>
public abstract class FclNode(SourceSpan span)
{
    /// <summary>Position of this node in the original FCL source text.</summary>
    public SourceSpan Span { get; } = span;
}

// ─────────────────────────────────────────────────────────
//  Expression nodes
// ─────────────────────────────────────────────────────────

/// <summary>Base class for all expression nodes (conditions, logical ops, groups).</summary>
public abstract class FclExpression : FclNode
{
#pragma warning disable IDE0290 // Use primary constructor -- should not apply to protected constructor!
    protected FclExpression(SourceSpan span) : base(span) { }
#pragma warning restore IDE0290 // Use primary constructor

    /// <summary>
    /// Appends the formatted FCL representation of this expression to
    ///  <paramref name="sb"/>, respecting the given formatting options.
    /// </summary>
    /// <param name="sb">Target string builder.</param>
    /// <param name="options">Formatting options controlling layout and style.</param>
    /// <param name="indent">Current indentation level (in spaces).</param>
    public abstract void FormatTo(StringBuilder sb, FclFormatOptions options, int indent);

    /// <summary>Returns the FCL source text using default formatting options.</summary>
    public override string ToString() => FclFormatter.Format(this);
}

/// <summary>
/// Logical OR: two or more sub-expressions joined by <c>or</c>.
/// <code>expr1 or expr2 [or expr3 ...]</code>
/// </summary>
public sealed class FclOrExpression(ImmutableArray<FclExpression> operands, SourceSpan span)
    : FclExpression(span)
{
    /// <summary>The operands (at least two).</summary>
    public ImmutableArray<FclExpression> Operands { get; } = operands;

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options, int indent)
    {
        Operands[0].FormatTo(sb, options, indent);
        for (int i = 1; i < Operands.Length; i++)
        {
            if (options.ConditionPerLine)
            {
                sb.Append('\n');
                FclFormatter.AppendIndent(sb, indent);
            }
            else
            {
                sb.Append(' ');
            }
            sb.Append("or ");
            Operands[i].FormatTo(sb, options, indent);
        }
    }
}

/// <summary>
/// Logical AND: two or more sub-expressions joined by <c>and</c>.
/// <code>expr1 and expr2 [and expr3 ...]</code>
/// </summary>
public sealed class FclAndExpression(ImmutableArray<FclExpression> operands, SourceSpan span)
    : FclExpression(span)
{
    /// <summary>The operands (at least two).</summary>
    public ImmutableArray<FclExpression> Operands { get; } = operands;

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options, int indent)
    {
        Operands[0].FormatTo(sb, options, indent);
        for (int i = 1; i < Operands.Length; i++)
        {
            if (options.ConditionPerLine)
            {
                sb.Append('\n');
                FclFormatter.AppendIndent(sb, indent);
            }
            else
            {
                sb.Append(' ');
            }
            sb.Append("and ");
            Operands[i].FormatTo(sb, options, indent);
        }
    }
}

/// <summary>
/// Logical NOT: negation of a sub-expression.
/// <code>not expr</code>
/// </summary>
public sealed class FclNotExpression(FclExpression operand, SourceSpan span)
    : FclExpression(span)
{
    /// <summary>The negated sub-expression.</summary>
    public FclExpression Operand { get; } = operand;

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options, int indent)
    {
        sb.Append("not ");
        Operand.FormatTo(sb, options, indent);
    }
}

/// <summary>
/// Parenthesized sub-expression. Preserves grouping for round-trip formatting.
/// <code>( expr )</code>
/// </summary>
public sealed class FclGroupExpression(FclExpression inner, SourceSpan span)
    : FclExpression(span)
{
    /// <summary>The inner expression.</summary>
    public FclExpression Inner { get; } = inner;

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options, int indent)
    {
        if (!options.ConditionPerLine)
        {
            // Inline mode: (inner)
            sb.Append('(');
            Inner.FormatTo(sb, options, indent);
            sb.Append(')');
            return;
        }

        int innerIndent = indent + options.IndentSize;

        if (options.BracesOnNewLine)
        {
            // C-style: opening paren on its own line, inner indented, closing paren aligned.
            sb.Append('(');
            sb.Append('\n');
            FclFormatter.AppendIndent(sb, innerIndent);
            Inner.FormatTo(sb, options, innerIndent);
            sb.Append('\n');
            FclFormatter.AppendIndent(sb, indent);
            sb.Append(')');
        }
        else
        {
            // Inline open: first operand on same line as '(', subsequent indented.
            sb.Append('(');
            Inner.FormatTo(sb, options, innerIndent);
            sb.Append(')');
        }
    }
}

/// <summary>
/// Sentinel expression node representing a parse error. Carries the
/// <see cref="FclDiagnostic"/> that describes the error so that the
/// validator can surface it and the evaluator can safely ignore it.
/// <para>
/// Returned by the parser instead of <c>null</c> from expression-parsing
///  methods, ensuring the AST is always structurally complete.
/// </para>
/// </summary>
public sealed class FclErrorExpression(FclDiagnostic diagnostic)
    : FclExpression(diagnostic.Span)
{
    /// <summary>The diagnostic that describes the parse error.</summary>
    public FclDiagnostic Diagnostic { get; } = diagnostic;

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options, int indent)
    {
        sb.Append("<error>");
    }
}

/// <summary>
/// A single condition: <c>Field Operator Value</c>.
/// This is the leaf node of the expression tree.
/// </summary>
public sealed class FclCondition(
    FclField field, SourceSpan fieldSpan,
    FclOperator op, SourceSpan operatorSpan,
    FclValue value,
    SourceSpan span)
    : FclExpression(span)
{
    /// <summary>The field being tested.</summary>
    public FclField Field { get; } = field;

    /// <summary>Source span of the field token.</summary>
    public SourceSpan FieldSpan { get; } = fieldSpan;

    /// <summary>The comparison operator.</summary>
    public FclOperator Operator { get; } = op;

    /// <summary>Source span of the operator token.</summary>
    public SourceSpan OperatorSpan { get; } = operatorSpan;

    /// <summary>The value to compare against.</summary>
    public FclValue Value { get; } = value;

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options, int indent)
    {
        sb.Append(FclFormatter.FieldToString(Field));
        sb.Append(' ');
        sb.Append(FclFormatter.OperatorToString(Operator, options.PreferWordOperators));
        sb.Append(' ');
        Value.FormatTo(sb, options);
    }
}

// ─────────────────────────────────────────────────────────
//  Value nodes
// ─────────────────────────────────────────────────────────

/// <summary>Base class for all value nodes (right-hand side of a condition).</summary>
public abstract class FclValue : FclNode
{
#pragma warning disable IDE0290 // Use primary constructor -- should not apply to protected constructor!
    protected FclValue(SourceSpan span) : base(span) { }
#pragma warning restore IDE0290 // Use primary constructor

    /// <summary>
    /// Appends the formatted FCL representation of this value to
    ///  <paramref name="sb"/>, respecting the given formatting options.
    /// </summary>
    /// <param name="sb">Target string builder.</param>
    /// <param name="options">Formatting options (e.g. date format style).</param>
    public abstract void FormatTo(StringBuilder sb, FclFormatOptions options);

    /// <summary>Returns the FCL source text using default formatting options.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        FormatTo(sb, FclFormatOptions.Default);
        return sb.ToString();
    }
}

/// <summary>
/// A string literal value.
/// </summary>
public sealed class FclStringValue(string value, bool wasQuoted, SourceSpan span)
    : FclValue(span)
{
    /// <summary>The string content (unescaped, without surrounding quotes).</summary>
    public string Value { get; } = value;

    /// <summary>Whether the original source used quotes.</summary>
    public bool WasQuoted { get; } = wasQuoted;

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options)
    {
        if (WasQuoted || FclFormatter.NeedsQuoting(Value))
            FclFormatter.AppendQuoted(sb, Value);
        else
            sb.Append(Value);
    }
}

/// <summary>
/// A size literal value (e.g. <c>10MB</c>, <c>1.5GB</c>).
/// </summary>
public sealed class FclSizeValue(double numericValue, FclSizeUnit unit, long bytes, SourceSpan span)
    : FclValue(span)
{
    /// <summary>The numeric part as written by the user (e.g. 10, 1.5).</summary>
    public double NumericValue { get; } = numericValue;

    /// <summary>The unit suffix.</summary>
    public FclSizeUnit Unit { get; } = unit;

    /// <summary>The resolved size in bytes.</summary>
    public long Bytes { get; } = bytes;

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options)
    {
        // Omit decimal places for whole numbers.
        if (NumericValue == Math.Floor(NumericValue))
            sb.Append(((long)NumericValue).ToString(CultureInfo.InvariantCulture));
        else
            sb.Append(NumericValue.ToString("G", CultureInfo.InvariantCulture));

        sb.Append(FclFormatter.SizeUnitToString(Unit));
    }
}

/// <summary>
/// An absolute date/time literal (e.g. <c>2025-01-15</c>).
/// </summary>
public sealed class FclAbsoluteDateValue(DateTime value, bool hasTime, SourceSpan span)
    : FclValue(span)
{
    /// <summary>The parsed date/time value.</summary>
    public DateTime Value { get; } = value;

    /// <summary>Whether the original literal included a time component.</summary>
    public bool HasTime { get; } = hasTime;

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options)
    {
        if (options.UseIso8601Dates)
        {
            sb.Append(HasTime
                ? Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
                : Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(Value.ToString(CultureInfo.CurrentCulture));
        }
    }
}

/// <summary>
/// A relative date/time literal (e.g. <c>today-7d</c>, <c>now-2h</c>).
/// Resolved to a concrete <see cref="DateTime"/> at evaluation time.
/// </summary>
public sealed class FclRelativeDateValue(FclDateAnchor anchor, int offset, FclDateUnit unit, SourceSpan span)
    : FclValue(span)
{
    /// <summary>The anchor point (today, yesterday, now).</summary>
    public FclDateAnchor Anchor { get; } = anchor;

    /// <summary>
    /// Signed offset from the anchor. Negative = in the past, positive = in the future.
    /// Zero when no offset is specified (e.g. bare <c>today</c>).
    /// </summary>
    public int Offset { get; } = offset;

    /// <summary>The unit of the offset.</summary>
    public FclDateUnit Unit { get; } = unit;

    private DateTime? _cachedValue = null;
    /// <summary>The cached date/time value.</summary>
    public DateTime CachedValue
    {
        get
        {
            if (_cachedValue == null)
                _cachedValue = Resolve();
            return _cachedValue.Value;
        }
    }
    /// <summary>
    /// Updates the cached value by resolving the current state.
    /// </summary>
    public void UpdateCachedValue() => _cachedValue = Resolve();

    /// <summary>
    /// We assume that time is specified when the anchor is <c>now</c> 
    ///  or when the offset is non-zero and the unit is hours or smaller.
    /// </summary>
    public bool HasTime => Anchor == FclDateAnchor.Now || (Offset != 0 && Unit <= FclDateUnit.Hours);

    /// <summary>
    /// Resolves this relative date to a concrete <see cref="DateTime"/>
    /// based on the current system time.
    /// </summary>
    public DateTime Resolve()
    {
        var baseTime = Anchor switch
        {
            FclDateAnchor.Today => DateTime.Today,
            FclDateAnchor.Yesterday => DateTime.Today.AddDays(-1),
            FclDateAnchor.Now => DateTime.Now,
            _ => DateTime.Today
        };

        if (Offset == 0)
            return baseTime;

        return Unit switch
        {
            FclDateUnit.Minutes => baseTime.AddMinutes(Offset),
            FclDateUnit.Hours => baseTime.AddHours(Offset),
            FclDateUnit.Days => baseTime.AddDays(Offset),
            FclDateUnit.Weeks => baseTime.AddDays(Offset * 7),
            FclDateUnit.Months => baseTime.AddMonths(Offset),
            FclDateUnit.Years => baseTime.AddYears(Offset),
            _ => baseTime
        };
    }

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options)
    {
        sb.Append(FclFormatter.DateAnchorToString(Anchor));
        if (Offset != 0)
        {
            sb.Append(Offset > 0 ? '+' : '-');
            sb.Append(Math.Abs(Offset));
            sb.Append(FclFormatter.DateUnitToString(Unit));
        }
    }
}

/// <summary>
/// A file attribute value (e.g. <c>Hidden</c>, <c>ReadOnly</c>).
/// </summary>
public sealed class FclAttributeValue(FclAttribute attribute, SourceSpan span)
    : FclValue(span)
{
    /// <summary>The attribute flag.</summary>
    public FclAttribute Attribute { get; } = attribute;

    /// <summary>Converts to the corresponding <see cref="FileAttributes"/> flag.</summary>
    public FileAttributes ToFileAttributes() => Attribute switch
    {
        FclAttribute.Hidden => FileAttributes.Hidden,
        FclAttribute.ReadOnly => FileAttributes.ReadOnly,
        FclAttribute.System => FileAttributes.System,
        FclAttribute.Archive => FileAttributes.Archive,
        FclAttribute.Temporary => FileAttributes.Temporary,
        _ => 0
    };

    /// <inheritdoc />
    public override void FormatTo(StringBuilder sb, FclFormatOptions options)
    {
        sb.Append(FclFormatter.AttributeToString(Attribute));
    }
}
