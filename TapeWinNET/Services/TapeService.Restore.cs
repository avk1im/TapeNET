using TapeLibNET.Agents;
using TapeLibNET.Scan;
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
    ///  <see cref="WpfServiceHost.UpdateRestoreProgress"/> directly — and shows the progress of a full-media TOC search,
    ///  the last resort when a requested volume identifies by nothing else (Design-TocLocation §7, step 4).
    /// All core logic (logging, abort, file-failed prompts) lives in the shared base.
    /// </remarks>
    protected override ServiceRestoreProgressHandler CreateRestoreProgressHandler(
        TapeFileRestoreBaseAgent agent, int totalFiles, RestoreMode mode, bool skipAllErrors)
    {
        // As for Scan Media: the media capacity in blocks is the yardstick; 0 = no estimate.
        long blockSize = DefaultBlockSize;
        long estimatedBlocks = blockSize > 0 ? Capacity / blockSize : 0L;
        return new GuiRestoreProgressHandler(
            (WpfServiceHost)_host, agent, totalFiles, skipAllErrors, mode, estimatedBlocks);
    }

    #region Helper Class — Restore progress handler

    /// <summary>
    /// <see cref="ServiceRestoreProgressHandler"/> subclass that drives the WPF
    ///  progress bar and current-file display via <see cref="WpfServiceHost.UpdateRestoreProgress"/>.
    /// All batch logging, abort handling, and file-error prompts live in the shared base.
    /// </summary>
    private sealed class GuiRestoreProgressHandler(
        WpfServiceHost host,
        TapeAgentBase agent,
        int totalFilesToProcess,
        bool skipAllErrors,
        RestoreMode mode,
        long estimatedBlocks)
        : ServiceRestoreProgressHandler(host, agent, totalFilesToProcess, skipAllErrors, mode)
    {
        private int _tocCopiesFound;
        private bool _searching;

        protected override void ReportProgress(in TapeFileStatistics stats, string? currentFile = null)
        {
            if (_searching)
            {
                // The search is over once files flow again: put the operation's own message back.
                _searching = false;
                host.EndTocSearchProgress($"{OperationName} in progress...");
            }
            int total = TotalFilesToProcess > 0 ? TotalFilesToProcess : stats.FilesTotal;
            host.UpdateRestoreProgress(stats.FilesProcessed, total, stats.FileBytesProcessed, stats.BytesTotal, currentFile);
        }

        protected override void ReportTocSearchProgress(TapeScanProgress progress)
        {
            _searching = true;
            if (progress.Fragment is { Kind: FragmentKind.TOC })
                _tocCopiesFound++;
            host.UpdateTocSearchProgress(progress.CurrentBlock, estimatedBlocks, _tocCopiesFound);
        }
    }

    #endregion
}
