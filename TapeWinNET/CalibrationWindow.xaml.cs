using System.Windows;

using TapeWinNET.Help;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

public partial class CalibrationWindow : Window, IHelpPaneHost
{
    private readonly DialogHelpPaneController _help;

    public CalibrationWindow(CalibrationResultViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        var icon = TapeIcons.GetTapeMediaIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }

        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = true;
            Close();
        };

        _help = new DialogHelpPaneController(
            this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl,
            defaultTopicId: "dialog.calibration-result", helpButton: HelpButton);
    }

    /// <summary>True when the user requested a follow-up full calibration via the result window's
    ///  "Run Full Calibration..." button (only offered when a recalibration was found unreliable).</summary>
    public bool FullCalibrationRequested => DataContext is CalibrationResultViewModel { FullCalibrationRequested: true };


    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => _help.ToggleHelpPane();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => _help.HandleF1(e);

    #region IHelpPaneHost

    public string HostName => nameof(CalibrationWindow);

    public HelpPaneHostMode HostMode => HelpPaneHostMode.Adjacent;

    public void OnPaneOpening(double desiredWidth) => _help.OnPaneOpening(desiredWidth);

    public void OnPaneClosed() => _help.OnPaneClosed();

    public FrameworkElement? ResolveControlByName(string name)
        => FindName(name) as FrameworkElement;

    public void OpenHelpPane(string? topicId = null) => _help.OpenHelpPane(topicId);
    public string? GetDefaultTopicId() => _help.GetDefaultTopicId();

    #endregion
}
