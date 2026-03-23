using FclNET;
using TapeLibNET;

namespace TapeConNET;

/// <summary>
/// <see cref="ITapeFileFilter"/> adapter that evaluates files using an
/// <see cref="FclEvaluator"/> from FclNET. Bridges <c>TapeFileDescriptor</c>
/// to <see cref="FclFileInfo"/> for each match test.
/// </summary>
public sealed class FclTapeFileFilter(FclEvaluator evaluator) : ITapeFileFilter
{
    public FclTapeFileFilter(List<string> filePatterns)
        : this(FclPipeline.CreateWildcardEvaluator(filePatterns))
    { }

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
