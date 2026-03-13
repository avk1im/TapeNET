namespace FclNET;

/// <summary>
/// Token types produced by the FCL lexer.
/// </summary>
public enum FclTokenKind
{
    // ── Literals / identifiers ──────────────────────────

    /// <summary>An unquoted word (field name, operator keyword, attribute value, or unquoted string).</summary>
    Word,

    /// <summary>A double-quoted string literal.</summary>
    QuotedString,

    /// <summary>A numeric literal (integer or decimal), possibly with a size/date unit suffix.</summary>
    Number,

    // ── Punctuation ─────────────────────────────────────

    /// <summary>Opening parenthesis <c>(</c>.</summary>
    OpenParen,

    /// <summary>Closing parenthesis <c>)</c>.</summary>
    CloseParen,

    // ── C-style operators ───────────────────────────────

    /// <summary><c>==</c></summary>
    DoubleEquals,

    /// <summary><c>!=</c></summary>
    BangEquals,

    /// <summary><c>&lt;</c></summary>
    LessThan,

    /// <summary><c>&lt;=</c></summary>
    LessOrEqual,

    /// <summary><c>&gt;</c></summary>
    GreaterThan,

    /// <summary><c>&gt;=</c></summary>
    GreaterOrEqual,

    /// <summary><c>!</c> (prefix not, when not followed by <c>=</c>).</summary>
    Bang,

    /// <summary><c>&amp;&amp;</c></summary>
    DoubleAmpersand,

    /// <summary><c>||</c></summary>
    DoublePipe,

    // ── Comments ────────────────────────────────────────

    /// <summary>A <c>//</c> line comment (content runs to end of line). Skipped by the parser.</summary>
    Comment,

    // ── Special ─────────────────────────────────────────

    /// <summary>End of input.</summary>
    EndOfInput,

    /// <summary>An unrecognized character sequence.</summary>
    Error
}

/// <summary>
/// A single token produced by the FCL lexer.
/// </summary>
/// <param name="Kind">The type of this token.</param>
/// <param name="Text">The raw text of the token from the source input.</param>
/// <param name="Span">Position in the original source text.</param>
public readonly record struct FclToken(FclTokenKind Kind, string Text, SourceSpan Span)
{
    public override string ToString() => $"{Kind} \"{Text}\" {Span}";
}
