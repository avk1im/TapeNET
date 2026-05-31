using System.Windows;
using System.Windows.Controls;

using TapeWinNET.Help;
using TapeWinNET.Models;
using TapeWinNET.Services;
using TapeWinNET.Utils;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Interaction logic for RestoreWindow.xaml
/// </summary>
public partial class RestoreWindow : Window, IHelpPaneHost
{
    private HelpPaneViewModel? _helpPaneVm;

    // Width of the dialog content area alone (without the help pane), captured at
    //  construction so OnPaneClosed can shrink back to exactly this value.
    private double _dialogContentWidth;

    // Default help pane width for this dialog
    private const double DefaultHelpPaneWidth = 340;

    public RestoreWindow(RestoreViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Snapshot the initial (no-pane) window width
        _dialogContentWidth = Width;

        Closing += RestoreWindow_Closing;
    }

    private void RestoreWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Persist state whenever the dialog is closed (pane may still be visible)
        if (_helpPaneVm is not null)
            PersistHelpPaneState();
    }

    private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // Identify the specific BackupSetListItem whose checkbox was toggled
        var item = (e.OriginalSource as CheckBox)?.DataContext as BackupSetListItem;
        (DataContext as RestoreViewModel)?.OnItemCheckChanged(item);
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => OpenHelpPane();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F1)
        {
            // Resolve the focused element's topic id (if any) for context-sensitive help
            var focused = System.Windows.Input.FocusManager
                .GetFocusedElement(this) as System.Windows.DependencyObject;
            var topicId = focused is not null
                ? GlobalF1HelpBehavior.ResolveTopicId(focused)
                : null;
            OpenHelpPane(topicId);
            e.Handled = true;
        }
    }

    #region IHelpPaneHost

    public string HostName => "RestoreWindow";

    // Adjacent: the window expands to the right; the HelpPane is a column inside
    //  the same window, not a separate floating window.
    public HelpPaneHostMode HostMode => HelpPaneHostMode.Adjacent;

    public void OnPaneOpening(double desiredWidth)
    {
        const double SplitterWidth = 4.0;

        // Expand the dialog to the right to make room for splitter + pane
        Width += desiredWidth + SplitterWidth;

        // If the window now protrudes beyond the right edge of the work area,
        //  shift it left until it fits (or clamp to the left edge)
        var workArea = SystemParameters.WorkArea;
        var overhang = (Left + Width) - workArea.Right;
        if (overhang > 0)
            Left = Math.Max(workArea.Left, Left - overhang);

        // Reveal the new column and splitter
        HelpPaneColumn.Width    = new GridLength(desiredWidth, GridUnitType.Pixel);
        HelpPaneColumn.MinWidth = 200;
        HelpPaneSplitter.Visibility = Visibility.Visible;
    }

    public void OnPaneClosed()
    {
        PersistHelpPaneState();

        // Shrink the window back to the dialog-content-only width
        var removedWidth = HelpPaneColumn.ActualWidth + 4.0; // pane + splitter
        Width = Math.Max(_dialogContentWidth, Width - removedWidth);

        HelpPaneColumn.Width    = new GridLength(0);
        HelpPaneColumn.MinWidth = 0;
        HelpPaneSplitter.Visibility = Visibility.Collapsed;
        HelpPaneControl.Visibility  = Visibility.Collapsed;
    }

    public FrameworkElement? ResolveControlByName(string name)
        => FindName(name) as FrameworkElement;

    /// <summary>
    /// Opens the help pane embedded in this dialog (expanding the window to the right)
    /// and navigates to <paramref name="topicId"/>, the persisted last topic, or the
    /// dialog's own topic. Builds the session on first call; no-ops if already open.
    /// </summary>
    public async void OpenHelpPane(string? topicId = null)
    {
        var settings = AppSettings.LoadFromFile();

        if (_helpPaneVm == null)
        {
            // First open — build the session and wire the VM
            var session = await AppHelpSessionFactory.CreateAsync(this);
            _helpPaneVm = new HelpPaneViewModel(session, this, new HelpActionRouter())
            {
                // Restore persisted chat sub-pane height before binding so the
                //  DataContextChanged handler in HelpPane.xaml.cs applies it immediately
                ChatPaneHeight = settings.HelpPaneChatHeight ?? 200.0
            };
            // Dialogs have no log pane — surface warnings via a MessageBox
            _helpPaneVm.SessionWarning += OnHelpSessionWarning;
            HelpPaneControl.DataContext = _helpPaneVm;
        }

        if (HelpPaneControl.Visibility != Visibility.Visible)
        {
            // Expand the window and reveal the column
            var desiredWidth = settings.HelpPaneWidthPerHost?.GetValueOrDefault(HostName)
                               ?? DefaultHelpPaneWidth;
            OnPaneOpening(desiredWidth);
            HelpPaneControl.Visibility = Visibility.Visible;
        }

        _helpPaneVm.IsPaneOpen = true;

        // Navigate to requested topic, persisted last topic, or dialog default
        if (topicId != null)
        {
            await _helpPaneVm.NavigateToAsync(topicId);
        }
        else
        {
            var lastTopic = settings.HelpPaneLastTopicPerHost?.GetValueOrDefault(HostName);
            if (lastTopic != null)
                await _helpPaneVm.NavigateToAsync(lastTopic);
            else
                await _helpPaneVm.NavigateToAsync("dialog.restore");
        }
    }

    #endregion

    // -- Helpers ----------------------------------------------------------------

    /// <summary>
    /// Persists current HelpPane layout state (pane width, chat height, last topic)
    /// back to <see cref="AppSettings"/>.
    /// </summary>
    private void PersistHelpPaneState()
    {
        if (_helpPaneVm is null) return;

        var settings = AppSettings.LoadFromFile();

        // Per-host pane width (only when the column is actually visible)
        if (HelpPaneColumn.ActualWidth > 0)
        {
            settings.HelpPaneWidthPerHost ??= [];
            settings.HelpPaneWidthPerHost[HostName] = HelpPaneColumn.ActualWidth;
        }

        // Shared chat sub-pane height
        settings.HelpPaneChatHeight = _helpPaneVm.ChatPaneHeight;

        // Per-host last topic
        var topicId = _helpPaneVm.CurrentTopicId;
        if (topicId is not null)
        {
            settings.HelpPaneLastTopicPerHost ??= [];
            settings.HelpPaneLastTopicPerHost[HostName] = topicId;
        }

        settings.SaveToFile();
    }

    private static void OnHelpSessionWarning(object? sender, string msg)
        => MessageBox.Show(msg, "Help", MessageBoxButton.OK, MessageBoxImage.Information);
}
