using TapeLibNET;
using TapeLibNET.Services;

namespace TapeWinNET.Services;

/// <summary>
/// Partial class — restore/validate/verify factory override for <see cref="TapeService"/>.
/// All state-machine logic lives in <see cref="TapeServiceBase"/> (TapeServiceBase.Restore.cs);
///  this partial only adds the WPF-specific progress handler that updates the ViewModel
///  directly via <see cref="WpfServiceHost.UpdateRestoreProgress"/>.
/// </summary>
public partial class TapeService
{
    // ── Restore progress handler factory override ─────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a <see cref="GuiRestoreProgressHandler"/> that drives the WPF progress
    ///  bar and current-file display by calling
    ///  <see cref="WpfServiceHost.UpdateRestoreProgress"/> directly.
    /// All core logic (logging, abort, file-failed prompts) lives in the shared base.
    /// </remarks>
    protected override ServiceRestoreProgressHandler CreateRestoreProgressHandler(
        TapeFileRestoreBaseAgent agent, int totalFiles, RestoreMode mode, bool skipAllErrors)
        => new GuiRestoreProgressHandler(
               (WpfServiceHost)_host, agent, totalFiles, skipAllErrors, mode);

    #region Helper Class — Restore progress handler

    /// <summary>
    /// <see cref="ServiceRestoreProgressHandler"/> subclass that drives the WPF
    ///  progress bar and current-file display via <see cref="WpfServiceHost.UpdateRestoreProgress"/>.
    /// All batch logging, abort handling, and file-error prompts live in the shared base.
    /// </summary>
    private sealed class GuiRestoreProgressHandler(
        WpfServiceHost host,
        TapeFileAgent agent,
        int totalFilesToProcess,
        bool skipAllErrors,
        RestoreMode mode)
        : ServiceRestoreProgressHandler(host, agent, totalFilesToProcess, skipAllErrors, mode)
    {
        protected override void ReportProgress(in TapeFileStatistics stats, string? currentFile = null)
        {
            int total = TotalFilesToProcess > 0 ? TotalFilesToProcess : stats.FilesTotal;
            host.UpdateRestoreProgress(stats.FilesProcessed, total, stats.FileBytesProcessed, stats.BytesTotal, currentFile);
        }
    }

    #endregion
}

