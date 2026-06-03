using System.ComponentModel;
using System.Windows;
using TapeWinNET.ViewModels;
using TapeWinNET.Help;
using TapeWinNET.Utils;

namespace TapeWinNET;

public partial class OpenVirtualDriveWindow : Window, IHelpPaneHost
{
    private readonly OpenVirtualDriveViewModel _viewModel;

    // All embedded help-pane boilerplate (window expansion, F1 resolution,
    //  session lifecycle, and AppSettings persistence) lives in this controller.
    private readonly DialogHelpPaneController _help;

    public OpenVirtualDriveWindow(OpenVirtualDriveViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        WindowPlacementApplicator.Attach(this);

        _help = new DialogHelpPaneController(
            this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl,
            defaultTopicId: "dialog.open-virtual-drive", helpButton: HelpButton);

        // Cancel any running probe when window closes
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Cancel any running probe to release file locks
        _viewModel.CancelProbe();
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => _help.ToggleHelpPane();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => _help.HandleF1(e);

    #region IHelpPaneHost

    public string HostName => nameof(OpenVirtualDriveWindow);

    // Adjacent: the window expands to the right; the HelpPane is a column inside
    //  the same window, not a separate floating window.
    public HelpPaneHostMode HostMode => HelpPaneHostMode.Adjacent;

    public void OnPaneOpening(double desiredWidth) => _help.OnPaneOpening(desiredWidth);

    public void OnPaneClosed() => _help.OnPaneClosed();

    public FrameworkElement? ResolveControlByName(string name)
        => FindName(name) as FrameworkElement;

    public void OpenHelpPane(string? topicId = null) => _help.OpenHelpPane(topicId);

    #endregion
}