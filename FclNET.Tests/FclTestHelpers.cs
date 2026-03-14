using FclNET.Ast;

namespace FclNET.Tests;

/// <summary>
/// Shared helpers that run the FCL pipeline stages and assert success,
/// reducing boilerplate across test classes.
/// </summary>
internal static class FclTestHelpers
{
    /// <summary>
    /// Lex + Parse an FCL string and assert no errors.
    /// Returns the parsed expression (never null).
    /// </summary>
    public static FclExpression ParseOk(string input)
    {
        var lexer = new FclLexer(input);
        var tokens = lexer.Tokenize();
        Assert.Empty(lexer.Diagnostics);

        var parser = new FclParser(tokens);
        var expr = parser.Parse();
        Assert.NotNull(expr);
        Assert.Empty(parser.Diagnostics);
        return expr;
    }

    /// <summary>
    /// Lex + Parse + Validate an FCL string and assert no errors at any stage.
    /// Returns the validated expression.
    /// </summary>
    public static FclExpression ValidateOk(string input)
    {
        var expr = ParseOk(input);
        var diags = FclValidator.Validate(expr);
        Assert.Empty(diags);
        return expr;
    }

    /// <summary>
    /// Full pipeline: Lex → Parse → Validate → Evaluate against a file.
    /// Returns the match result.
    /// </summary>
    public static bool Evaluate(string input, IFclFileInfo file)
    {
        var expr = ValidateOk(input);
        var evaluator = new FclEvaluator(expr);
        return evaluator.Evaluate(file);
    }
}
