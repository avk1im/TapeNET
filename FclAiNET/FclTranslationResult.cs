namespace FclAiNET;

/// <summary>
/// Result of <see cref="FclAiTranslator.TranslateAsync"/>: holds the generated
/// FCL expression (if successful), or an explanation of why translation failed.
/// </summary>
/// <param name="Success">Whether valid FCL was produced.</param>
/// <param name="Fcl">The generated FCL string in canonical form, or <c>null</c> on failure.</param>
/// <param name="Expression">The parsed and validated AST, or <c>null</c> on failure.</param>
/// <param name="Explanation">
/// On failure: description of what went wrong and how the user could rephrase.
///  <c>null</c> on success.
/// </param>
/// <param name="Attempts">Number of generation/validation attempts made.</param>
public record FclTranslationResult(
    bool Success,
    string? Fcl,
    FclNET.Ast.FclExpression? Expression,
    string? Explanation,
    int Attempts);
