namespace FclNET;

/// <summary>
/// Splits an FCL source string into a flat list of <see cref="FclToken"/>s
/// for consumption by <see cref="FclParser"/>.
/// <para>
/// Comment tokens are emitted as <see cref="FclTokenKind.Comment"/> so that
/// tooling can access them; the parser skips them.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Design notes — whitespace in compound values:</b></para>
/// <para>
/// Sizes and relative dates may be written with or without whitespace between
/// their constituent parts. The lexer does not attempt to merge them — it
/// produces the natural tokens and leaves reassembly to the parser.
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Sizes:</b> <c>10MB</c> → Number <c>10MB</c> (one token).
///     <c>10 MB</c> → Number <c>10</c> + Word <c>MB</c> (parser re-joins).
///     Locale group separators (e.g. <c>1,000,000KB</c>) are preserved in the
///     token text; the parser strips them during numeric parsing.
///   </item>
///   <item>
///     <b>Relative dates:</b> <c>today-7d</c> → Word <c>today-7d</c> (one token).
///     <c>today - 7 d</c> → Word <c>today</c>, Word <c>-</c>, Number <c>7</c>,
///     Word <c>d</c> (parser reassembles). The formatter always emits the
///     compact form.
///   </item>
/// </list>
/// </remarks>
/// <remarks>Creates a lexer for the given FCL source text.</remarks>
public sealed class FclLexer(string source)
{
    private readonly string _source = source ?? string.Empty;
    private int _pos;
    private readonly List<FclDiagnostic> _diagnostics = [];

    /// <summary>Diagnostics (errors) accumulated during tokenization.</summary>
    public IReadOnlyList<FclDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Tokenizes the entire source string and returns the token list.
    /// The list always ends with an <see cref="FclTokenKind.EndOfInput"/> token.
    /// </summary>
    public List<FclToken> Tokenize()
    {
        _pos = 0;
        _diagnostics.Clear();

        var tokens = new List<FclToken>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == FclTokenKind.EndOfInput)
                break;
        }
        return tokens;
    }

    // ─────────────────────────────────────────────────────
    //  Main dispatch
    // ─────────────────────────────────────────────────────

    private FclToken NextToken()
    {
        SkipWhitespace();

        if (_pos >= _source.Length)
            return new FclToken(FclTokenKind.EndOfInput, string.Empty, new SourceSpan(_pos, 0));

        int start = _pos;
        char c = _source[_pos];
        char next = Peek(1);

        // ── Comment ─────────────────────────────────────
        if (c == '/' && next == '/')
            return ScanComment();

        // ── Quoted string ───────────────────────────────
        if (c == '"')
            return ScanQuotedString();

        // ── Punctuation & operators ─────────────────────
        switch (c)
        {
            case '(':
                _pos++;
                return new FclToken(FclTokenKind.OpenParen, "(", new SourceSpan(start, 1));

            case ')':
                _pos++;
                return new FclToken(FclTokenKind.CloseParen, ")", new SourceSpan(start, 1));

            case '=':
                if (next == '=') { _pos += 2; return new FclToken(FclTokenKind.DoubleEquals, "==", new SourceSpan(start, 2)); }
                return MakeErrorToken(start, 1, "Unexpected '='. Did you mean '=='?");

            case '!':
                if (next == '=') { _pos += 2; return new FclToken(FclTokenKind.BangEquals, "!=", new SourceSpan(start, 2)); }
                _pos++;
                return new FclToken(FclTokenKind.Bang, "!", new SourceSpan(start, 1));

            case '<':
                if (next == '=') { _pos += 2; return new FclToken(FclTokenKind.LessOrEqual, "<=", new SourceSpan(start, 2)); }
                _pos++;
                return new FclToken(FclTokenKind.LessThan, "<", new SourceSpan(start, 1));

            case '>':
                if (next == '=') { _pos += 2; return new FclToken(FclTokenKind.GreaterOrEqual, ">=", new SourceSpan(start, 2)); }
                _pos++;
                return new FclToken(FclTokenKind.GreaterThan, ">", new SourceSpan(start, 1));

            case '&':
                if (next == '&') { _pos += 2; return new FclToken(FclTokenKind.DoubleAmpersand, "&&", new SourceSpan(start, 2)); }
                return MakeErrorToken(start, 1, "Unexpected '&'. Did you mean '&&'?");

            case '|':
                if (next == '|') { _pos += 2; return new FclToken(FclTokenKind.DoublePipe, "||", new SourceSpan(start, 2)); }
                return MakeErrorToken(start, 1, "Unexpected '|'. Did you mean '||'?");
        }

        // ── Number (starts with a digit) ────────────────
        if (char.IsAsciiDigit(c))
            return ScanGenericToken(FclTokenKind.Number);

        // ── Word (everything else) ──────────────────────
        return ScanGenericToken(FclTokenKind.Word);
    }

    // ─────────────────────────────────────────────────────
    //  Scanners
    // ─────────────────────────────────────────────────────

    private FclToken ScanComment()
    {
        int start = _pos;
        _pos += 2; // skip //
        while (_pos < _source.Length && _source[_pos] is not '\r' and not '\n')
            _pos++;
        return new FclToken(FclTokenKind.Comment, _source[start.._pos], new SourceSpan(start, _pos - start));
    }

    private FclToken ScanQuotedString()
    {
        int start = _pos;
        _pos++; // skip opening "

        while (_pos < _source.Length)
        {
            if (_source[_pos] == '"')
            {
                _pos++;
                // Doubled quote → escaped literal, keep going
                if (_pos < _source.Length && _source[_pos] == '"')
                {
                    _pos++;
                    continue;
                }
                // Closing quote found
                return new FclToken(FclTokenKind.QuotedString, _source[start.._pos],
                    new SourceSpan(start, _pos - start));
            }
            _pos++;
        }

        // Unterminated
        var span = new SourceSpan(start, _pos - start);
        _diagnostics.Add(new FclDiagnostic(
            FclDiagnosticSeverity.Error, FclDiagnosticCodes.UnterminatedString,
            "Unterminated string literal — missing closing '\"'.", span));
        return new FclToken(FclTokenKind.Error, _source[start.._pos], span);
    }

    /// <summary>
    /// Scans a contiguous run of non-special characters.
    /// Used for both <see cref="FclTokenKind.Word"/> and <see cref="FclTokenKind.Number"/>.
    /// Stops at whitespace, operator starters, parentheses, quotes, and <c>//</c> comments.
    /// </summary>
    private FclToken ScanGenericToken(FclTokenKind kind)
    {
        int start = _pos;
        while (_pos < _source.Length)
        {
            char ch = _source[_pos];
            if (IsTokenBreak(ch))
                break;
            // Stop before a // comment
            if (ch == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '/')
                break;
            _pos++;
        }
        return new FclToken(kind, _source[start.._pos], new SourceSpan(start, _pos - start));
    }

    // ─────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────

    private void SkipWhitespace()
    {
        while (_pos < _source.Length && char.IsWhiteSpace(_source[_pos]))
            _pos++;
    }

    private char Peek(int offset)
    {
        int idx = _pos + offset;
        return idx < _source.Length ? _source[idx] : '\0';
    }

    private FclToken MakeErrorToken(int start, int length, string message)
    {
        _pos += length;
        var span = new SourceSpan(start, length);
        _diagnostics.Add(new FclDiagnostic(
            FclDiagnosticSeverity.Error, FclDiagnosticCodes.UnexpectedToken, message, span));
        return new FclToken(FclTokenKind.Error, _source[start..(start + length)], span);
    }

    /// <summary>
    /// Characters that always terminate a <see cref="FclTokenKind.Word"/> or
    /// <see cref="FclTokenKind.Number"/> token — they either start their own
    /// tokens (operators, parentheses, quotes) or separate tokens (whitespace).
    /// </summary>
    private static bool IsTokenBreak(char c) =>
        char.IsWhiteSpace(c) || c is '(' or ')' or '"' or '=' or '!' or '<' or '>' or '&' or '|';
}
