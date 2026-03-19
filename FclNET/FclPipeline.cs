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

    // ─────────────────────────────────────────────────────
    //  DNF (Disjunctive Normal Form)
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if <paramref name="expression"/> is already in
    /// Disjunctive Normal Form: <c>OR( AND(literal, …), … )</c> where each
    /// literal is an <see cref="Ast.FclCondition"/> or
    /// <c>NOT(FclCondition)</c>.
    /// </summary>
    public static bool IsDnf(FclExpression expression)
        => FclDnfConverter.IsDnf(expression);

    /// <summary>
    /// Converts <paramref name="expression"/> to Disjunctive Normal Form.
    /// Returns <c>null</c> if the conversion would produce more than
    /// <paramref name="maxClauses"/> OR-clauses (exponential blowup guard).
    /// </summary>
    /// <param name="expression">A validated FCL expression.</param>
    /// <param name="maxClauses">Maximum allowed OR-clauses (default 256).</param>
    public static FclExpression? ToDnf(FclExpression expression, int maxClauses = 256)
        => FclDnfConverter.ToDnf(expression, maxClauses);

    /// <summary>
    /// Converts <paramref name="expression"/> to DNF and extracts the
    /// groups as a list-of-lists: outer = OR groups, inner = AND literals.
    /// Returns <c>null</c> if the expression cannot be converted within
    /// the clause limit.
    /// </summary>
    /// <param name="expression">A validated FCL expression.</param>
    /// <param name="maxClauses">Maximum allowed OR-clauses (default 256).</param>
    public static List<List<FclExpression>>? ExtractDnfGroups(
        FclExpression expression, int maxClauses = 256)
    {
        var dnf = FclDnfConverter.ToDnf(expression, maxClauses);
        if (dnf is null)
            return null;

        return FclDnfConverter.ExtractDnfGroups(dnf);
    }

    // ─────────────────────────────────────────────────────
    //  Wildcard evaluator factory
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="FclEvaluator"/> that matches files against DOS-style
    /// wildcard patterns (<c>*</c>, <c>?</c>) using the <c>fullname matches</c> condition.
    /// <para>
    /// Builds a synthetic AST node internally — no lexer/parser involved.
    /// The evaluator pre-compiles all patterns for efficient repeated evaluation.
    /// </para>
    /// </summary>
    /// <param name="patterns">
    /// One or more DOS-style wildcard patterns (e.g. <c>*.txt</c>, <c>C:\Backup\*.*</c>).
    /// </param>
    /// <returns>A ready-to-use evaluator for the combined pattern.</returns>
    public static FclEvaluator CreateWildcardEvaluator(IReadOnlyList<string> patterns)
    {
        var joined = string.Join("; ", patterns);
        return CreateWildcardEvaluator(joined);
    }

    /// <summary>
    /// Creates an <see cref="FclEvaluator"/> that matches files against a single
    /// (potentially semicolon-separated) DOS-style wildcard pattern string using
    /// the <c>fullname matches</c> condition.
    /// </summary>
    /// <param name="pattern">
    /// A wildcard pattern string, optionally semicolon-separated
    /// (e.g. <c>"*.txt; *.log"</c>).
    /// </param>
    /// <returns>A ready-to-use evaluator for the pattern.</returns>
    public static FclEvaluator CreateWildcardEvaluator(string pattern)
    {
        // Build a synthetic AST: fullname matches "pattern"
        var value = new FclStringValue(pattern, wasQuoted: true, SourceSpan.None);
        var condition = new FclCondition(
            FclField.FullName, SourceSpan.None,
            FclOperator.Matches, SourceSpan.None,
            value, SourceSpan.None);
        return new FclEvaluator(condition);
    }
}

// ─────────────────────────────────────────────────────
//  Lightweight IFclFileInfo implementation
// ─────────────────────────────────────────────────────

/// <summary>
/// Lightweight, immutable <see cref="IFclFileInfo"/> snapshot.
/// A <see langword="readonly struct"/> to avoid heap allocation — ideal for
/// high-throughput evaluation of file descriptors from external sources
/// (e.g. <c>TapeFileDescriptor</c>).
/// </summary>
public readonly struct FclFileInfo(
    string fullName,
    long size,
    DateTime creationTime,
    DateTime lastWriteTime,
    FileAttributes attributes) : IFclFileInfo
{
    /// <inheritdoc />
    public string FullName { get; } = fullName;

    /// <inheritdoc />
    public long Size { get; } = size;

    /// <inheritdoc />
    public DateTime CreationTime { get; } = creationTime;

    /// <inheritdoc />
    public DateTime LastWriteTime { get; } = lastWriteTime;

    /// <inheritdoc />
    public FileAttributes Attributes { get; } = attributes;
}
