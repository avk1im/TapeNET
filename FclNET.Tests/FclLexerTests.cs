namespace FclNET.Tests;

/// <summary>
/// Tests for <see cref="FclLexer"/> — tokenization of FCL source text.
/// </summary>
public class FclLexerTests
{
    // ─────────────────────────────────────────────────────
    //  Basic token production
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Name", FclTokenKind.Word)]
    [InlineData("contains", FclTokenKind.Word)]
    [InlineData("today-7d", FclTokenKind.Word)]
    public void Tokenize_Word(string input, FclTokenKind expected)
    {
        var tokens = Lex(input);
        Assert.Equal(2, tokens.Count); // Word + EndOfInput
        Assert.Equal(expected, tokens[0].Kind);
        Assert.Equal(input, tokens[0].Text);
    }

    [Theory]
    [InlineData("\"hello\"", "\"hello\"")]
    [InlineData("\"hello world\"", "\"hello world\"")]
    [InlineData("\"she said \"\"hi\"\"\"", "\"she said \"\"hi\"\"\"")]
    public void Tokenize_QuotedString(string input, string expectedText)
    {
        var tokens = Lex(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(FclTokenKind.QuotedString, tokens[0].Kind);
        Assert.Equal(expectedText, tokens[0].Text);
    }

    [Theory]
    [InlineData("42", "42")]
    [InlineData("10MB", "10MB")]
    [InlineData("1.5GB", "1.5GB")]
    [InlineData("1,000KB", "1,000KB")]
    public void Tokenize_Number(string input, string expectedText)
    {
        var tokens = Lex(input);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(FclTokenKind.Number, tokens[0].Kind);
        Assert.Equal(expectedText, tokens[0].Text);
    }

    // ─────────────────────────────────────────────────────
    //  Operators and punctuation
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("==", FclTokenKind.DoubleEquals)]
    [InlineData("!=", FclTokenKind.BangEquals)]
    [InlineData("<", FclTokenKind.LessThan)]
    [InlineData("<=", FclTokenKind.LessOrEqual)]
    [InlineData(">", FclTokenKind.GreaterThan)]
    [InlineData(">=", FclTokenKind.GreaterOrEqual)]
    [InlineData("!", FclTokenKind.Bang)]
    [InlineData("&&", FclTokenKind.DoubleAmpersand)]
    [InlineData("||", FclTokenKind.DoublePipe)]
    [InlineData("(", FclTokenKind.OpenParen)]
    [InlineData(")", FclTokenKind.CloseParen)]
    public void Tokenize_Operators(string input, FclTokenKind expected)
    {
        var tokens = Lex(input);
        Assert.Equal(expected, tokens[0].Kind);
        Assert.Equal(input, tokens[0].Text);
    }

    // ─────────────────────────────────────────────────────
    //  Comments
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_CommentOnly()
    {
        var tokens = Lex("// this is a comment");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(FclTokenKind.Comment, tokens[0].Kind);
        Assert.StartsWith("//", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_ExpressionWithTrailingComment()
    {
        var tokens = Lex("Name equals test // filter by name");
        // Word(Name) + Word(equals) + Word(test) + Comment + EndOfInput
        Assert.Equal(5, tokens.Count);
        Assert.Equal(FclTokenKind.Word, tokens[0].Kind);
        Assert.Equal(FclTokenKind.Word, tokens[1].Kind);
        Assert.Equal(FclTokenKind.Word, tokens[2].Kind);
        Assert.Equal(FclTokenKind.Comment, tokens[3].Kind);
    }

    // ─────────────────────────────────────────────────────
    //  Multi-token expressions
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_SimpleCondition()
    {
        var tokens = Lex("Name equals \"test.txt\"");
        Assert.Equal(4, tokens.Count); // Word + Word + QuotedString + EOI
        Assert.Equal("Name", tokens[0].Text);
        Assert.Equal("equals", tokens[1].Text);
        Assert.Equal("\"test.txt\"", tokens[2].Text);
    }

    [Fact]
    public void Tokenize_CompoundExpression()
    {
        var tokens = Lex("Name contains doc and Size > 10MB");
        // Name, contains, doc, and, Size, >, 10MB, EOI
        Assert.Equal(8, tokens.Count);
        Assert.Equal(FclTokenKind.GreaterThan, tokens[5].Kind);
    }

    [Fact]
    public void Tokenize_ParenthesizedExpression()
    {
        var tokens = Lex("(Name equals test)");
        Assert.Equal(FclTokenKind.OpenParen, tokens[0].Kind);
        Assert.Equal(FclTokenKind.CloseParen, tokens[^2].Kind);
    }

    // ─────────────────────────────────────────────────────
    //  SourceSpan correctness
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_SpansAreCorrect()
    {
        var tokens = Lex("Name == \"test\"");
        // "Name" at [0..4), "==" at [5..7), "\"test\"" at [8..14)
        Assert.Equal(0, tokens[0].Span.Start);
        Assert.Equal(4, tokens[0].Span.Length);
        Assert.Equal(5, tokens[1].Span.Start);
        Assert.Equal(2, tokens[1].Span.Length);
        Assert.Equal(8, tokens[2].Span.Start);
        Assert.Equal(6, tokens[2].Span.Length);
    }

    // ─────────────────────────────────────────────────────
    //  Error cases
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_UnterminatedString_ProducesErrorToken()
    {
        var lexer = new FclLexer("\"unterminated");
        var tokens = lexer.Tokenize();
        Assert.Equal(FclTokenKind.Error, tokens[0].Kind);
        Assert.Single(lexer.Diagnostics);
        Assert.Equal(FclDiagnosticCodes.UnterminatedString, lexer.Diagnostics[0].Code);
    }

    [Fact]
    public void Tokenize_SingleAmpersand_ProducesErrorToken()
    {
        var lexer = new FclLexer("&");
        var tokens = lexer.Tokenize();
        Assert.Equal(FclTokenKind.Error, tokens[0].Kind);
        Assert.Single(lexer.Diagnostics);
    }

    [Fact]
    public void Tokenize_SinglePipe_ProducesErrorToken()
    {
        var lexer = new FclLexer("|");
        var tokens = lexer.Tokenize();
        Assert.Equal(FclTokenKind.Error, tokens[0].Kind);
        Assert.Single(lexer.Diagnostics);
    }

    [Fact]
    public void Tokenize_EmptyInput_ProducesEndOfInput()
    {
        var tokens = Lex("");
        Assert.Single(tokens);
        Assert.Equal(FclTokenKind.EndOfInput, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_WhitespaceOnly_ProducesEndOfInput()
    {
        var tokens = Lex("   \t  \n  ");
        Assert.Single(tokens);
        Assert.Equal(FclTokenKind.EndOfInput, tokens[0].Kind);
    }

    // ─────────────────────────────────────────────────────
    //  Escaped quotes in strings
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_DoubledQuotesInString()
    {
        // Input: "say ""hi"""  →  token text includes quotes and the doubled pair
        var tokens = Lex("\"say \"\"hi\"\"\"");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(FclTokenKind.QuotedString, tokens[0].Kind);
    }

    // ─────────────────────────────────────────────────────
    //  Helper
    // ─────────────────────────────────────────────────────

    private static List<FclToken> Lex(string input)
    {
        var lexer = new FclLexer(input);
        var tokens = lexer.Tokenize();
        Assert.Empty(lexer.Diagnostics);
        return tokens;
    }
}
