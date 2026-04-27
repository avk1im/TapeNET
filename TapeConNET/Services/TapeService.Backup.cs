using TapeConNET.Ux;
using TapeLibNET;
using TapeLibNET.Services;

namespace TapeConNET.Services;

public partial class TapeService
{
    // ── Backup progress handler factory override ──────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a <see cref="GuiBackupProgressHandler"/> that additionally drives
    ///  the bounded <see cref="IProgressScope"/> console progress bar.
    /// </remarks>
    protected override ServiceBackupProgressHandler CreateBackupProgressHandler(
        TapeFileBackupAgent agent, bool skipAllErrors, ITapeFileFilter? filter)
    {
        var progress = _ux.BeginProgress("Backing up");
        // Pass _host directly — it is already the ConsoleUxServiceHost wired at construction.
        return new GuiBackupProgressHandler(_host, agent, progress, skipAllErrors, filter);
    }

    #region Helper Class — Backup progress handler

    /// <summary>
    /// <see cref="ServiceBackupProgressHandler"/> subclass that additionally
    ///  drives a bounded <see cref="IProgressScope"/> for the console progress bar.
    /// All core logic (logging, abort, file-failed prompts) lives in the shared base.
    /// </summary>
    private sealed class GuiBackupProgressHandler(
        ITapeServiceHost host,
        TapeFileAgent agent,
        IProgressScope progress,
        bool skipAllErrors,
        ITapeFileFilter? filter = null)
        : ServiceBackupProgressHandler(host, agent, skipAllErrors, filter)
    {
        protected override void ReportProgress(in TapeFileStatistics stats, string? currentFile = null)
        {
            if (stats.FilesTotal > 0)
            {
                double pct = 100.0 * stats.FilesProcessed / stats.FilesTotal;
                progress.Report(pct, currentFile);
            }
            else if (currentFile is not null)
            {
                progress.Report(0, currentFile);
            }
        }

        public override void CompleteProgress() => progress.Complete();
        public override void DisposeProgress()  => progress.Dispose();
    }

    #endregion
}
