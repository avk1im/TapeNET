using System.Windows;
using System.Windows.Controls;

using TapeWinNET.Controls;
using TapeWinNET.Help;
using TapeWinNET.Models;
using TapeWinNET.Services;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Interaction logic for RestoreWindow.xaml
/// </summary>
public partial class RestoreWindow : Window, IHelpPaneHost
{
    private HelpPaneViewModel? _helpPaneVm;

    public RestoreWindow(RestoreViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // Identify the specific BackupSetListItem whose checkbox was toggled
        var item = (e.OriginalSource as CheckBox)?.DataContext as BackupSetListItem;
        (DataContext as RestoreViewModel)?.OnItemCheckChanged(item);
    }

    #region IHelpPaneHost

    public string HostName => "RestoreWindow";
    public HelpPaneHostMode HostMode => HelpPaneHostMode.Adjacent;

    public void OnPaneOpening(double desiredWidth)
    {
        // For adjacent mode the layout coordinator widens the window instead
        //  of reshuffling internal columns — nothing to do here.
    }

    public void OnPaneClosed() { }

    public FrameworkElement? ResolveControlByName(string name)
        => FindName(name) as FrameworkElement;

    /// <summary>
    /// Opens a floating help window adjacent to this dialog, navigating to
    /// <paramref name="topicId"/> (or home if <c>null</c>).
    /// </summary>
    public async void OpenHelpPane(string? topicId = null)
    {
        if (_helpPaneVm == null)
        {
            var session  = await AppHelpSessionFactory.CreateAsync(this);
            _helpPaneVm  = new HelpPaneViewModel(session, this, new HelpActionRouter());
        }

        // Accommodate the help pane width by expanding the window leftward
        var accommodatedWidth = HelpPaneLayoutCoordinator.OpenAdjacent(this, desiredWidth: 360);

        // Build and show a lightweight popup window that hosts the HelpPane control
        var paneWindow = new HelpPaneWindow(_helpPaneVm, accommodatedWidth)
        {
            Owner = this
        };
        paneWindow.Show();

        if (topicId != null)
            await _helpPaneVm.NavigateToAsync(topicId);
        else
            await _helpPaneVm.GoHomeAsync();
    }

    #endregion
}
