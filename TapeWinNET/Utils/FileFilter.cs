using FclNET;
using TapeLibNET;

namespace TapeWinNET.Utils;

/// <summary>
/// <see cref="ITapeFileFilter"/> adapter that evaluates files using an
/// <see cref="FclEvaluator"/> from FclNET. Bridges <c>TapeFileDescriptor</c>
/// to <see cref="FclFileInfo"/> for each match test.
/// </summary>
public sealed class FclTapeFileFilter(FclEvaluator evaluator) : ITapeFileFilter
{
    /// <inheritdoc />
    public bool Matches(in TapeFileDescriptor fileDescr)
    {
        var snapshot = new FclFileInfo(
            fileDescr.FullName,
            fileDescr.Length,
            fileDescr.CreationTime,
            fileDescr.LastWriteTime,
            fileDescr.Attributes);
        return evaluator.Evaluate(snapshot);
    }
}

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
    /// Filters a list of items asynchronously on a background thread using a
    /// pre-built <see cref="FclEvaluator"/>. Returns only items that match.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="source">The full unfiltered list.</param>
    /// <param name="evaluator">A ready-to-use FCL evaluator.</param>
    /// <param name="pathSelector">Function to extract the full file path from an item.</param>
    /// <returns>A new filtered list.</returns>
    public static Task<List<T>> FilterAsync<T>(
        IReadOnlyList<T> source,
        FclEvaluator evaluator,
        Func<T, string> pathSelector)
    {
        return Task.Run(() =>
        {
            var result = new List<T>();
            foreach (var item in source)
            {
                var snapshot = new FclFileInfo(
                    pathSelector(item), 0, default, default, default);
                if (evaluator.Evaluate(snapshot))
                    result.Add(item);
            }
            return result;
        });
    }

    /// <summary>
    /// Filters a list of items asynchronously on a background thread using
    /// DOS-style wildcard patterns. Uses <see cref="FclPipeline.CreateWildcardEvaluator(string)"/>
    /// for behavioral consistency with FclNET. Returns only items whose full path
    /// matches at least one pattern.
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
        var evaluator = FclPipeline.CreateWildcardEvaluator(patterns);
        return FilterAsync(source, evaluator, pathSelector);
    }
}
