using System.Windows;
using System.Windows.Controls;

using TapeWinNET.Help;
using TapeWinNET.Models;
using TapeWinNET.Utils;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Interaction logic for RestoreWindow.xaml
/// </summary>
public partial class RestoreWindow : Window, IHelpPaneHost
{
    // All embedded help-pane boilerplate (window expansion, F1 resolution,
    //  session lifecycle, and AppSettings persistence) lives in this controller.
    private readonly DialogHelpPaneController _help;

    public RestoreWindow(RestoreViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        WindowPlacementApplicator.Attach(this);

        _help = new DialogHelpPaneController(
            this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl,
            defaultTopicId: "dialog.restore", helpButton: HelpButton);
    }

    private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // Identify the specific BackupSetListItem whose checkbox was toggled
        var item = (e.OriginalSource as CheckBox)?.DataContext as BackupSetListItem;
        (DataContext as RestoreViewModel)?.OnItemCheckChanged(item);
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => _help.ToggleHelpPane();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => _help.HandleF1(e);

    #region IHelpPaneHost

    public string HostName => nameof(RestoreWindow);

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
