using System.Windows;
using System.Windows.Input;
using TapeWinNET.Help;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Rename a backup set — and, for an encrypted set, check its current password and optionally change it
///  (Design-Encryption §11.1.6). Replaces the plain name prompt for every set. Hosts the help pane (Adjacent mode).
/// </summary>
public partial class RenameBackupSetWindow : Window, IHelpPaneHost
{
    private readonly RenameBackupSetViewModel _viewModel;

    // All embedded help-pane boilerplate (window expansion, F1 resolution,
    //  session lifecycle, and AppSettings persistence) lives in this controller.
    private readonly DialogHelpPaneController _help;

    public RenameBackupSetWindow(RenameBackupSetViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        _help = new DialogHelpPaneController(
            this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl,
            defaultTopicId: "dialog.rename-backup-set", helpButton: HelpButton);

        viewModel.CloseRequested += ok => DialogResult = ok;
        Loaded += (_, _) =>
        {
            // An encrypted set: the current password comes first (it may unlock the name); else the name, selected
            if (viewModel.IsEncrypted)
                CurrentPasswordInput.FocusBox();
            else
            {
                NameTextBox.Focus();
                NameTextBox.SelectAll();
            }
        };
    }

    /// <summary>Checks the current password when its field loses focus — a typo shows under the field (W3).</summary>
    private async void CurrentPasswordInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (CurrentPasswordInput.IsKeyboardFocusWithin)
            return;
        if (await _viewModel.VerifyCurrentPasswordAsync() && NameTextBox.IsEnabled && !NameTextBox.IsKeyboardFocusWithin)
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => _help.ToggleHelpPane();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        => _help.HandleF1(e);

    #region IHelpPaneHost

    public string HostName => nameof(RenameBackupSetWindow);

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
