using FclNET;
using TapeLibNET.Agents;
using TapeLibNET.Toc;

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

    public FclTapeFileFilter(IReadOnlyList<string> filePatterns)
        : this(FclPipeline.CreateWildcardEvaluator(filePatterns))
    { }

    /// <inheritdoc />
    public bool Matches(in TapeFileDescriptor fileDescr)
    {
        var snapshot = new FclFileInfo(
            fileDescr.FullName,
            fileDescr.Length,
            fileDescr.CreationTime.ToLocalTime(),      // FCL dates are local by spec; TapeFileDescriptor holds UTC
            fileDescr.LastWriteTime.ToLocalTime(),
            fileDescr.Attributes);
        return evaluator.Evaluate(snapshot);
    }
}
