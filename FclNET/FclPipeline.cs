using FclNET.Ast;

namespace FclNET;

/// <summary>
/// Result of <see cref="FclPipeline.TryParse"/>: holds the parsed AST
/// (if successful) and any diagnostics from lexing, parsing, or validation.
/// </summary>
/// <param name="Expression">The parsed AST root, or <c>null</c> if the input was empty.</param>
/// <param name="Diagnostics">All diagnostics accumulated across pipeline stages.</param>
public record FclParseResult(FclExpression? Expression, IReadOnlyList<FclDiagnostic> Diagnostics)
{
    /// <summary>
    /// <c>true</c> when parsing and validation both succeeded without errors.
    /// </summary>
    public bool IsValid => Expression is not null && Diagnostics.Count == 0;
}

/// <summary>
/// One-stop API for the complete FCL pipeline: parse, validate, evaluate, and select.
/// Wraps <see cref="FclLexer"/>, <see cref="FclParser"/>, <see cref="FclValidator"/>,
/// and <see cref="FclEvaluator"/> behind concise entry points.
/// </summary>
public static class FclPipeline
{
    // ─────────────────────────────────────────────────────
    //  Parsing
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Lexes, parses, and validates an FCL expression in one call.
    /// </summary>
    /// <param name="input">FCL source text.</param>
    /// <returns>
    /// A <see cref="FclParseResult"/> with the AST and any diagnostics.
    /// Check <see cref="FclParseResult.IsValid"/> before evaluating.
    /// </returns>
    public static FclParseResult TryParse(string input)
    {
        var lexer = new FclLexer(input);
        var tokens = lexer.Tokenize();

        var parser = new FclParser(tokens);
        var expression = parser.Parse();

        // Collect lexer + parser diagnostics.
        var diagnostics = new List<FclDiagnostic>();
        diagnostics.AddRange(lexer.Diagnostics);
        diagnostics.AddRange(parser.Diagnostics);

        // Only validate if parsing produced a clean AST — validating an AST
        //  with error nodes would just produce confusing secondary errors.
        if (expression is not null && diagnostics.Count == 0)
            diagnostics.AddRange(FclValidator.Validate(expression));

        return new FclParseResult(expression, diagnostics);
    }

    // ─────────────────────────────────────────────────────
    //  Single-file evaluation
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates a validated expression against a single file.
    /// Creates an <see cref="FclEvaluator"/> internally — for repeated
    /// evaluation across many files, prefer <see cref="Select"/> instead.
    /// </summary>
    /// <param name="expression">A validated AST (obtained via <see cref="TryParse"/>).</param>
    /// <param name="file">The file to test.</param>
    public static bool Evaluate(FclExpression expression, IFclFileInfo file)
    {
        var evaluator = new FclEvaluator(expression);
        return evaluator.Evaluate(file);
    }

    // ─────────────────────────────────────────────────────
    //  Batch selection
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Batch-filters a collection of files against a validated expression.
    /// Returns the subset of files that match the filter.
    /// </summary>
    /// <param name="expression">A validated AST (obtained via <see cref="TryParse"/>).</param>
    /// <param name="files">The files to filter.</param>
    /// <param name="runtimeDiagnostics">
    /// Runtime diagnostics (e.g. date overflow), if any. Empty on success.
    /// </param>
    public static List<IFclFileInfo> Select(
        FclExpression expression,
        IEnumerable<IFclFileInfo> files,
        out IReadOnlyList<FclDiagnostic> runtimeDiagnostics)
    {
        var evaluator = new FclEvaluator(expression);
        var result = new List<IFclFileInfo>();

        foreach (var file in files)
        {
            if (evaluator.Evaluate(file))
                result.Add(file);
        }

        runtimeDiagnostics = evaluator.RuntimeDiagnostics;
        return result;
    }

    /// <summary>
    /// Full end-to-end pipeline: parses, validates, and evaluates an FCL
    /// expression against a collection of files.
    /// <para>
    /// If parsing or validation fails, returns an empty list and
    /// <paramref name="diagnostics"/> contains the errors.
    /// </para>
    /// </summary>
    /// <param name="input">FCL source text.</param>
    /// <param name="files">The files to filter.</param>
    /// <param name="diagnostics">
    /// Parse/validation errors on failure, or runtime diagnostics on success.
    /// </param>
    public static List<IFclFileInfo> Select(
        string input,
        IEnumerable<IFclFileInfo> files,
        out IReadOnlyList<FclDiagnostic> diagnostics)
    {
        var parseResult = TryParse(input);
        if (!parseResult.IsValid)
        {
            diagnostics = parseResult.Diagnostics;
            return [];
        }

        return Select(parseResult.Expression!, files, out diagnostics);
    }
}
