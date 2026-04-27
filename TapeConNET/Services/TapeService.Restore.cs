using TapeConNET.Ux;
using TapeLibNET;
using TapeLibNET.Services;

namespace TapeConNET.Services;

public partial class TapeService
{
    // ── Restore progress handler factory override ─────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a <see cref="GuiRestoreProgressHandler"/> that additionally drives
    ///  the bounded <see cref="IProgressScope"/> console progress bar.
    /// </remarks>
    protected override ServiceRestoreProgressHandler CreateRestoreProgressHandler(
        TapeFileRestoreBaseAgent agent, int totalFiles, RestoreMode mode, bool skipAllErrors)
    {
        var progress = _ux.BeginProgress(mode.ToVerb());
        // Pass _host directly — it is already the ConsoleUxServiceHost wired at construction.
        return new GuiRestoreProgressHandler(_host, agent, progress, totalFiles, skipAllErrors, mode);
    }

    #region Helper Class — Restore progress handler

    /// <summary>
    /// <see cref="ServiceRestoreProgressHandler"/> subclass that additionally
    ///  drives a bounded <see cref="IProgressScope"/> for the console progress bar.
    /// All core logic (logging, abort, file-failed prompts) lives in the shared base.
    /// </summary>
    private sealed class GuiRestoreProgressHandler(
        ITapeServiceHost host,
        TapeFileAgent agent,
        IProgressScope progress,
        int totalFilesToProcess,
        bool skipAllErrors,
        RestoreMode mode)
        : ServiceRestoreProgressHandler(host, agent, totalFilesToProcess, skipAllErrors, mode)
    {
        protected override void ReportProgress(in TapeFileStatistics stats, string? currentFile = null)
        {
            int total = TotalFilesToProcess > 0 ? TotalFilesToProcess : stats.FilesTotal;
            if (total > 0)
            {
                double pct = 100.0 * stats.FilesProcessed / total;
                progress.Report(pct, currentFile);
            }
            else if (currentFile is not null)
            {
                progress.Report(0, currentFile);
            }
        }
    }

    #endregion
}

