using System.ComponentModel;
using System.Text;

using FclNET;

using Microsoft.Extensions.AI;

namespace FclAiNET;

/// <summary>
/// Provides <see cref="AIFunction"/> tool definitions that the LLM can invoke
/// during conversation to parse, validate, and format FCL expressions.
/// <para>
/// These tools enable an iterative self-correction loop: the LLM generates
/// FCL, calls <see cref="ValidateFcl"/> to check it, receives diagnostics,
/// and fixes errors — all within a single chat completion cycle managed by
/// <see cref="FunctionInvokingChatClient"/>.
/// </para>
/// </summary>
public static class FclAiTools
{
    /// <summary>
    /// Creates the list of <see cref="AITool"/>s to register on
    /// <see cref="ChatOptions.Tools"/> for FCL generation sessions.
    /// </summary>
    public static IList<AITool> CreateToolSet() =>
    [
        AIFunctionFactory.Create(ValidateFcl),
        AIFunctionFactory.Create(FormatFcl)
    ];

    // ─────────────────────────────────────────────────────
    //  Tool implementations
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses and validates an FCL expression string.
    /// Returns "Valid" if the expression is correct, or a list of
    /// diagnostic messages describing each error found.
    /// </summary>
    /// <param name="fcl">The FCL expression string to validate.</param>
    /// <returns>
    /// "Valid" on success, or a newline-separated list of error descriptions.
    /// </returns>
    [Description("Parses and validates an FCL expression. Returns 'Valid' if correct, "
        + "or a list of error messages. Always call this to verify generated FCL.")]
    private static string ValidateFcl(
        [Description("The FCL expression string to validate.")] string fcl)
    {
        if (string.IsNullOrWhiteSpace(fcl))
            return "Error: empty FCL expression.";

        var result = FclPipeline.TryParse(fcl);

        if (result.IsValid)
            return "Valid";

        // Format diagnostics for the LLM — concise and actionable.
        var sb = new StringBuilder();
        sb.AppendLine($"Invalid FCL ({result.Diagnostics.Count} error(s)):");
        foreach (var diag in result.Diagnostics)
        {
            // Include position info when available so the LLM can locate the error.
            if (diag.Span.Length > 0)
                sb.AppendLine($"  - [{diag.Code}] at position {diag.Span.Start}: {diag.Message}");
            else
                sb.AppendLine($"  - [{diag.Code}]: {diag.Message}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Parses an FCL expression and re-formats it in canonical form.
    /// Useful for normalizing the output after validation succeeds.
    /// </summary>
    /// <param name="fcl">The FCL expression string to format.</param>
    /// <returns>
    /// The canonical FCL string on success, or an error message if parsing fails.
    /// </returns>
    [Description("Parses an FCL expression and returns its canonical (normalized) form. "
        + "Only works if the expression is valid.")]
    private static string FormatFcl(
        [Description("The FCL expression string to format.")] string fcl)
    {
        if (string.IsNullOrWhiteSpace(fcl))
            return "Error: empty FCL expression.";

        var result = FclPipeline.TryParse(fcl);

        if (!result.IsValid)
            return "Error: cannot format invalid FCL. Call ValidateFcl first to see the errors.";

        return FclFormatter.Format(result.Expression!);
    }
}
