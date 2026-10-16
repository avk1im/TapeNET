using System.Windows;

using TapeWinNET.Help;
using TapeWinNET.ViewModels;

namespace TapeWinNET;


/// <summary>Scan Media result window — see <see cref="ScanResultViewModel"/>.</summary>
public partial class ScanResultWindow : Window, IHelpPaneHost
{
    // All embedded help-pane boilerplate lives in this controller.
    private readonly DialogHelpPaneController _help;

    public ScanResultWindow(ScanResultViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // The view model asks for closing after adopting a TOC or handing over to Inspect Media.
        viewModel.CloseRequested = Close;

        var icon = TapeIcons.GetTapeMediaIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }

        _help = new DialogHelpPaneController(
            this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl,
            defaultTopicId: "dialog.scan-result", helpButton: HelpButton);
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => _help.ToggleHelpPane();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => _help.HandleF1(e);

    #region IHelpPaneHost

    public string HostName => nameof(ScanResultWindow);

    // Adjacent: the window expands to the right; the HelpPane is a column inside this window.
    public HelpPaneHostMode HostMode => HelpPaneHostMode.Adjacent;

    public void OnPaneOpening(double desiredWidth) => _help.OnPaneOpening(desiredWidth);

    public void OnPaneClosed() => _help.OnPaneClosed();

    public FrameworkElement? ResolveControlByName(string name)
        => FindName(name) as FrameworkElement;

    public void OpenHelpPane(string? topicId = null) => _help.OpenHelpPane(topicId);
    public string? GetDefaultTopicId() => _help.GetDefaultTopicId();

    #endregion
}
