using System.Windows.Controls;

namespace TapeWinNET.Controls;

/// <summary>
/// Shared calibration-result display surface: verdict banner, measured-result figures (with an
/// optional before/after delta for recalibration), and the reported→actual curve. Inherits its
/// DataContext from the host window, so both <see cref="TapeWinNET.CalibrationWindow"/> and
/// <see cref="TapeWinNET.CalibrationProfilesWindow"/> just drop it in against a
/// <c>CalibrationResultViewModelBase</c>-derived view model.
/// </summary>
public partial class CalibrationResultView : UserControl
{
    public CalibrationResultView()
    {
        InitializeComponent();
    }
}
