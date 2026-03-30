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

    /// <summary>
    /// Parses a semicolon-separated wildcard pattern string into a list of trimmed,
    /// non-empty individual patterns.
    /// </summary>
    public static List<string> ParsePatterns(string input)
    {
        return [.. input
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    }
}
