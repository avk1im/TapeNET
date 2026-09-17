using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TapeLibNET.Services;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Progress handler that forwards every callback to the base implementation and additionally invokes
///  test-supplied hooks — the deterministic way to act at a precise point in the file loop.
/// </summary>
/// <remarks>
/// Replaces wall-clock polling entirely. Each hook runs ON the operation's worker thread, inline with
///  the callback, so "after the 2nd file commits" means exactly that regardless of machine speed or a
///  breakpoint pause. Never block inside a hook waiting for the operation to progress: the operation is
///  what called you, so it cannot proceed until you return.
/// </remarks>
public sealed class HookedBackupProgressHandler(
    ITapeServiceHost host,
    TapeFileAgent agent,
    bool skipAllErrors,
    ITapeFileFilter? filter = null)
    : ServiceBackupProgressHandler(host, agent, skipAllErrors, filter)
{
    /// <summary>Invoked after each file is processed (committed), with the running statistics.</summary>
    public Action<TapeFileInfo, TapeFileStatistics>? AfterFileProcessed { get; set; }

    /// <summary>Invoked before each file is processed. Return false to skip it.</summary>
    public Func<TapeFileInfo, TapeFileStatistics, bool>? BeforeFileProcessed { get; set; }

    public override bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        // Evaluate the hook FIRST: a hook returning false must skip the file even if the base
        //  implementation would have accepted it.
        if (BeforeFileProcessed is { } hook && !hook(fileInfo, stats))
            return false;

        return base.PreProcessFile(fileInfo, in stats);
    }

    public override bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        bool result = base.PostProcessFile(fileInfo, in stats);

        // AFTER the base call, so the statistics the hook sees include this file.
        AfterFileProcessed?.Invoke(fileInfo, stats);
        return result;
    }
}
