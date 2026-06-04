using System.Windows;

using TapeWinNET.Help;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

public partial class FormatMediaWindow : Window, IHelpPaneHost
{
    // All embedded help-pane boilerplate (window expansion, F1 resolution,
    //  session lifecycle, and AppSettings persistence) lives in this controller.
    private readonly DialogHelpPaneController _help;

    public FormatMediaWindow(FormatMediaViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        var icon = TapeIcons.GetTapeMediaIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }

        _help = new DialogHelpPaneController(
            this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl,
            defaultTopicId: "dialog.format-media", helpButton: HelpButton);
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => _help.ToggleHelpPane();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => _help.HandleF1(e);

    #region IHelpPaneHost

    public string HostName => nameof(FormatMediaWindow);

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
