using TapeLibNET.Scan;
using TapeLibNET.Services;

namespace TapeWinNET.Services;


/// <summary>
/// Partial class — Scan Media factory override for <see cref="TapeService"/>.
/// All scan logic lives in <see cref="TapeServiceBase"/>; this partial only adds the
/// WPF-specific progress handler that drives the shared operation overlay.
/// </summary>
public partial class TapeService
{
    /// <inheritdoc/>
    protected override ServiceScanProgressHandler CreateScanProgressHandler(
        TapeScanner scanner,
        ScanMediaRequest request)
    {
        // A scan has no known total. The media capacity in blocks is the best available yardstick for the
        //  progress bar: a full cartridge ends near it, a part-full one simply ends early (the bar then
        //  jumps to 100% on completion). Unknown block size or capacity => indeterminate (0 => no estimate).
        long blockSize = DefaultBlockSize;
        long estimatedBlocks = blockSize > 0 ? Capacity / blockSize : 0L;

        return new GuiScanProgressHandler((WpfServiceHost)_host, scanner, estimatedBlocks);
    }

    #region Helper Class — Scan progress handler

    private sealed class GuiScanProgressHandler(
        WpfServiceHost host,
        TapeScanner scanner,
        long estimatedBlocks)
        : ServiceScanProgressHandler(host, scanner)
    {
        protected override void ReportProgress(TapeScanProgress progress)
            => host.UpdateScanProgress(FragmentsFound, SetsFound, TocCopiesFound, CurrentBlock, estimatedBlocks, CurrentPhase);
    }

    #endregion
}
