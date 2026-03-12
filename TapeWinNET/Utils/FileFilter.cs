using TapeLibNET;

namespace TapeWinNET.Utils;

/// <summary>
/// Utility class for filtering file lists using DOS-style wildcard patterns.
/// Patterns support * and ? wildcards and semicolon-separated lists.
/// Designed for large lists — <see cref="FilterAsync{T}"/> runs matching on a background thread.
/// </summary>
public static class FileFilter
{
    /// <summary>
    /// Parses a semicolon-separated wildcard pattern string into a list of trimmed,
    /// non-empty individual patterns.
    /// </summary>
    public static List<string> ParsePatterns(string input)
    {
        return [..input
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>
    /// Filters a list of items asynchronously on a background thread using
    /// DOS-style wildcard patterns. Returns only items whose full path matches
    /// at least one pattern.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="source">The full unfiltered list.</param>
    /// <param name="patterns">Wildcard patterns already parsed into a list.</param>
    /// <param name="pathSelector">Function to extract the full file path from an item.</param>
    /// <returns>A new filtered list.</returns>
    public static Task<List<T>> FilterAsync<T>(
        IReadOnlyList<T> source,
        List<string> patterns,
        Func<T, string> pathSelector)
    {
        // Pre-compile regex patterns on the calling thread (lightweight)
        var regexPatterns = TapeSetTOC.FromFilePatternsToRegexPatterns(patterns).ToList();

        return Task.Run(() =>
        {
            var result = new List<T>();
            foreach (var item in source)
            {
                if (TapeSetTOC.FileMatchesRegexPatterns(pathSelector(item), regexPatterns))
                    result.Add(item);
            }
            return result;
        });
    }
}
