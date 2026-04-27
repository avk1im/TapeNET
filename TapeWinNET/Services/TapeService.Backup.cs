using TapeLibNET;
using TapeLibNET.Services;

namespace TapeWinNET.Services;

/// <summary>
/// Partial class — backup factory override and WPF-specific progress handler.
/// The state machine lives in <see cref="TapeServiceBase"/>.
/// </summary>
public partial class TapeService
{
    // ?? Backup progress handler factory override ??????????????????????????????

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a <see cref="GuiBackupProgressHandler"/> that calls
    ///  <see cref="WpfServiceHost.UpdateBackupProgress"/> for progress bar updates.
    ///  File error prompts and end-of-media handling are managed by the shared base
    ///  via <see cref="WpfServiceHost.OnFileErrorSelect"/>.
    /// </remarks>
    protected override ServiceBackupProgressHandler CreateBackupProgressHandler(
        TapeFileBackupAgent agent, bool skipAllErrors, ITapeFileFilter? filter)
    {
        var host = (WpfServiceHost)_host;
        return new GuiBackupProgressHandler(host, agent, skipAllErrors, filter);
    }

    #region Helper Class — Backup progress handler

    /// <summary>
    /// <see cref="ServiceBackupProgressHandler"/> subclass that drives the WPF
    ///  progress bar / current-file display via <see cref="WpfServiceHost.UpdateBackupProgress"/>.
    ///  All error handling, logging, and abort logic live in the shared base.
    /// </summary>
    private sealed class GuiBackupProgressHandler(
        WpfServiceHost host,
        TapeFileAgent agent,
        bool skipAllErrors,
        ITapeFileFilter? filter = null)
        : ServiceBackupProgressHandler(host, agent, skipAllErrors, filter)
    {
        protected override void ReportProgress(in TapeFileStatistics stats, string? currentFile = null)
            => host.UpdateBackupProgress(stats.FilesProcessed, stats.FilesTotal, stats.BytesProcessed, currentFile);
    }

    #endregion
}
