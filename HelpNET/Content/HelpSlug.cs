using System.Text.RegularExpressions;

namespace HelpNET.Content;

/// <summary>
/// Shared slug-generation rule: lowercase the display name and collapse
/// whitespace, slashes, and parentheses to hyphens.
/// Used both when building lookup caches (glossary, control definitions) and
/// when looking up a term by display name at runtime, so the two sides always
/// stay in lock-step.
/// </summary>
internal static class HelpSlug
{
    // Pre-compiled regex: one or more of: whitespace, slash, parentheses, brackets.
    private static readonly Regex SlugSeparatorsRegex =
        new(@"[\s/()\[\]]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Converts <paramref name="displayName"/> to a slug
    /// (lowercase, separator runs → single hyphen, leading/trailing hyphens stripped).
    /// </summary>
    public static string From(string displayName)
        => SlugSeparatorsRegex
            .Replace(displayName.ToLowerInvariant(), "-")
            .Trim('-');
}
