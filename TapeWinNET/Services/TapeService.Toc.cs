using TapeLibNET.Agents;
using TapeLibNET.Scan;
using TapeLibNET.Services;

namespace TapeWinNET.Services;

/// <summary>
/// Partial class — TOC search progress for <see cref="TapeService"/> (Design-TocLocation S4).
/// The search itself lives in the library; this partial only shows its progress in the overlay that is already up for the
///  TOC read (the TOC load overlay, whose Cancel stops the search at the next block).
/// </summary>
public partial class TapeService
{
    /// <inheritdoc/>
    protected override ServiceTocProgressHandler CreateTocProgressHandler(TapeAgentBase agent)
    {
        // As for Scan Media: the media capacity in blocks is the yardstick; 0 = no estimate.
        long blockSize = DefaultBlockSize;
        long estimatedBlocks = blockSize > 0 ? Capacity / blockSize : 0L;
        return new GuiTocProgressHandler((WpfServiceHost)_host, agent, estimatedBlocks);
    }

    #region Helper Class — TOC search progress handler

    private sealed class GuiTocProgressHandler(WpfServiceHost host, TapeAgentBase agent, long estimatedBlocks)
        : ServiceTocProgressHandler(host, agent)
    {
        protected override void ReportProgress(TapeScanProgress progress)
            => host.UpdateTocSearchProgress(CurrentBlock, estimatedBlocks, TocCopiesFound);
    }

    #endregion
}
