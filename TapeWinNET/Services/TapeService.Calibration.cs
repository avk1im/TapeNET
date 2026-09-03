using TapeLibNET;
using TapeLibNET.Services;

namespace TapeWinNET.Services;

/// <summary>
/// Partial class — calibration factory override for <see cref="TapeService"/>.
/// All state-machine logic lives in <see cref="TapeServiceBase"/>; this partial only adds the
/// WPF-specific progress handler that drives the shared operation overlay.
/// </summary>
public partial class TapeService
{
    /// <inheritdoc/>
    protected override ServiceCalibrateProgressHandler CreateCalibrateProgressHandler(
        TapeCalibrator calibrator,
        CalibrateRequest request,
        long capacityReported)
        => new GuiCalibrateProgressHandler((WpfServiceHost)_host, calibrator, capacityReported);

    #region Helper Class — Calibration progress handler

    private sealed class GuiCalibrateProgressHandler(
        WpfServiceHost host,
        TapeCalibrator calibrator,
        long capacityReported)
        : ServiceCalibrateProgressHandler(host, calibrator, capacityReported)
    {
        protected override void ReportProgress(TapeCalibrationProgress progress)
            => host.UpdateCalibrateProgress(FilesProcessed, FilesTotal, BytesProcessed, BytesTotal, CurrentPhase);
    }

    #endregion
}
