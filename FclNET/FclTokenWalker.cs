namespace FclNET;

/// <summary>
/// Wraps a <see cref="FclToken"/> list and a position cursor, providing
/// forward-only traversal that silently skips comment tokens.
/// <para>
/// <b>Invariant:</b> After construction and every call to <see cref="Advance"/>,
/// <see cref="Current"/> is guaranteed to be a non-comment token (or
/// <see cref="FclTokenKind.EndOfInput"/>).
/// </para>
/// </summary>
internal sealed class FclTokenWalker(List<FclToken> tokens)
{
    private readonly List<FclToken> _tokens = tokens;
    private int _pos;

    /// <summary>
    /// The current non-comment token.
    /// Returns an <see cref="FclTokenKind.EndOfInput"/> sentinel when past the end.
    /// </summary>
    public FclToken Current => _pos < _tokens.Count
        ? _tokens[_pos]
        : new FclToken(FclTokenKind.EndOfInput, string.Empty,
            new SourceSpan(_tokens.Count > 0 ? _tokens[^1].Span.End : 0, 0));

    /// <summary>Whether the current position is at end-of-input.</summary>
    public bool IsAtEnd => Current.Kind == FclTokenKind.EndOfInput;

    /// <summary>
    /// Returns the current token and advances the position past it
    /// (and past any subsequent comment tokens).
    /// </summary>
    public FclToken Advance()
    {
        var token = Current;
        if (_pos < _tokens.Count)
            _pos++;
        SkipComments();
        return token;
    }

    /// <summary>
    /// Looks ahead <paramref name="offset"/> non-comment tokens from
    /// the current position without advancing. <c>Peek(0)</c> is
    /// equivalent to <see cref="Current"/>.
    /// </summary>
    public FclToken Peek(int offset)
    {
        int remaining = offset;
        int i = _pos;
        while (remaining > 0 && i < _tokens.Count)
        {
            i++;
            // Skip comments, just like Advance does.
            while (i < _tokens.Count && _tokens[i].Kind == FclTokenKind.Comment)
                i++;
            remaining--;
        }

        return i < _tokens.Count
            ? _tokens[i]
            : new FclToken(FclTokenKind.EndOfInput, string.Empty,
                new SourceSpan(_tokens.Count > 0 ? _tokens[^1].Span.End : 0, 0));
    }

    /// <summary>
    /// Skips past the initial comments so <see cref="Current"/> starts
    /// on the first meaningful token. Called once by the owner after
    /// construction.
    /// </summary>
    public void SkipInitialComments() => SkipComments();

    // ── Internal ────────────────────────────────────────

    private void SkipComments()
    {
        while (_pos < _tokens.Count && _tokens[_pos].Kind == FclTokenKind.Comment)
            _pos++;
    }
}
