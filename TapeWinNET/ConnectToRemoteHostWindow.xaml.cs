using System.Windows;

using TapeWinNET.Help;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

public partial class ConnectToRemoteHostWindow : Window, IHelpPaneHost
{
    // All embedded help-pane boilerplate (window expansion, F1 resolution,
    //  session lifecycle, and AppSettings persistence) lives in this controller.
    private readonly DialogHelpPaneController _help;

    public ConnectToRemoteHostWindow(ConnectToRemoteHostViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        var icon = TapeIcons.GetTapeDriveIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }

        // Wire callbacks so the VM can close the window without referencing it directly
        viewModel.OnConnectSuccess = () => DialogResult = true;
        viewModel.OnCancel        = () => DialogResult = false;

        _help = new DialogHelpPaneController(
            this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl,
            defaultTopicId: "dialog.connect-remote-host", helpButton: HelpButton);
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => _help.ToggleHelpPane();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => _help.HandleF1(e);

    #region IHelpPaneHost

    public string HostName => nameof(ConnectToRemoteHostWindow);

    // Adjacent: the window expands to the right; the HelpPane is a column inside
    //  the same window, not a separate floating window.
    public HelpPaneHostMode HostMode => HelpPaneHostMode.Adjacent;

    public void OnPaneOpening(double desiredWidth) => _help.OnPaneOpening(desiredWidth);

    public void OnPaneClosed() => _help.OnPaneClosed();

    public FrameworkElement? ResolveControlByName(string name)
        => FindName(name) as FrameworkElement;

    public void OpenHelpPane(string? topicId = null) => _help.OpenHelpPane(topicId);
    public string? GetDefaultTopicId() => _help.GetDefaultTopicId();

    #endregion
}
