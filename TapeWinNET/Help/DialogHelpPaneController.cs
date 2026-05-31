using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using TapeWinNET.Services;
using TapeWinNET.Utils;
using TapeWinNET.ViewModels;

namespace TapeWinNET.Help;

/// <summary>
/// Encapsulates the boilerplate required to host an embedded <see cref="Controls.HelpPane"/>
/// inside a dialog window in <see cref="HelpPaneHostMode.Adjacent"/> mode: the window expands
/// to the right to make room for the pane, shifts left if it would overrun the work area, and
/// shrinks back when closed. Also owns the <see cref="HelpPaneViewModel"/> lifecycle, F1
/// context-help resolution, and <see cref="AppSettings"/> persistence.
/// </summary>
/// <remarks>
/// A dialog wires this up by:
/// <list type="number">
///   <item>Adding the outer three-column grid (content / splitter / help column) plus a
///         <c>GridSplitter</c> and a <c>controls:HelpPane</c> in its XAML.</item>
///   <item>Constructing a <see cref="DialogHelpPaneController"/> with references to those
///         three controls and the dialog's default help topic id.</item>
///   <item>Forwarding its <see cref="IHelpPaneHost"/> members and F1 / Help-button handlers
///         to this controller.</item>
/// </list>
/// </remarks>
public sealed class DialogHelpPaneController
{
    private const double SplitterWidth = 4.0;

    private readonly IHelpPaneHost      _host;
    private readonly Window             _window;
    private readonly ColumnDefinition   _paneColumn;
    private readonly FrameworkElement   _splitter;
    private readonly Controls.HelpPane  _paneControl;
    private readonly string             _defaultTopicId;
    private readonly double             _defaultWidth;

    // Width of the dialog content area alone (without the help pane), captured at
    //  construction so the pane can shrink the window back to exactly this value.
    private readonly double _dialogContentWidth;

    private HelpPaneViewModel? _vm;

    /// <summary>
    /// Creates a controller bound to the host dialog and its embedded help-pane controls.
    /// </summary>
    /// <param name="host">The hosting window, which also implements <see cref="IHelpPaneHost"/>.</param>
    /// <param name="window">The window instance (usually the same object as <paramref name="host"/>).</param>
    /// <param name="paneColumn">The grid column that holds the help pane (starts collapsed at width 0).</param>
    /// <param name="splitter">The <c>GridSplitter</c> between content and pane.</param>
    /// <param name="paneControl">The embedded <see cref="Controls.HelpPane"/> control.</param>
    /// <param name="defaultTopicId">Topic shown when no contextual or persisted topic applies.</param>
    /// <param name="defaultWidth">Default pane width used when none is persisted.</param>
    public DialogHelpPaneController(
        IHelpPaneHost host,
        Window window,
        ColumnDefinition paneColumn,
        FrameworkElement splitter,
        Controls.HelpPane paneControl,
        string defaultTopicId,
        double defaultWidth = 340)
    {
        _host           = host;
        _window         = window;
        _paneColumn     = paneColumn;
        _splitter       = splitter;
        _paneControl    = paneControl;
        _defaultTopicId = defaultTopicId;
        _defaultWidth   = defaultWidth;

        // Snapshot the initial (no-pane) window width
        _dialogContentWidth = window.Width;

        // Persist state whenever the dialog closes (the pane may still be visible)
        window.Closing += (_, _) => { if (_vm is not null) PersistState(); };
    }

    // ── IHelpPaneHost forwarding ───────────────────────────────────────────────

    /// <summary>Expands the window to the right to reveal the pane, shifting left if needed.</summary>
    public void OnPaneOpening(double desiredWidth)
    {
        // Expand the dialog to the right to make room for splitter + pane
        _window.Width += desiredWidth + SplitterWidth;

        // If the window now protrudes beyond the right edge of the work area,
        //  shift it left until it fits (or clamp to the left edge)
        var workArea = SystemParameters.WorkArea;
        var overhang = (_window.Left + _window.Width) - workArea.Right;
        if (overhang > 0)
            _window.Left = Math.Max(workArea.Left, _window.Left - overhang);

        // Reveal the new column and splitter
        _paneColumn.Width    = new GridLength(desiredWidth, GridUnitType.Pixel);
        _paneColumn.MinWidth = 200;
        _splitter.Visibility = Visibility.Visible;
    }

    /// <summary>Persists state, then shrinks the window back and hides the pane.</summary>
    public void OnPaneClosed()
    {
        PersistState();

        // Shrink the window back to the dialog-content-only width
        var removedWidth = _paneColumn.ActualWidth + SplitterWidth; // pane + splitter
        _window.Width = Math.Max(_dialogContentWidth, _window.Width - removedWidth);

        _paneColumn.Width    = new GridLength(0);
        _paneColumn.MinWidth = 0;
        _splitter.Visibility    = Visibility.Collapsed;
        _paneControl.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Opens the help pane embedded in this dialog (expanding the window to the right)
    /// and navigates to <paramref name="topicId"/>, the persisted last topic, or the
    /// dialog's own default topic. Builds the session on first call.
    /// </summary>
    public async void OpenHelpPane(string? topicId = null)
    {
        var settings = AppSettings.LoadFromFile();

        if (_vm == null)
        {
            // First open — build the session and wire the VM
            var session = await AppHelpSessionFactory.CreateAsync(_host);
            _vm = new HelpPaneViewModel(session, _host, new HelpActionRouter())
            {
                // Restore persisted chat sub-pane height before binding so the
                //  DataContextChanged handler in HelpPane.xaml.cs applies it immediately
                ChatPaneHeight = settings.HelpPaneChatHeight ?? 200.0
            };
            // Dialogs have no log pane — surface warnings via a MessageBox
            _vm.SessionWarning += OnSessionWarning;
            _paneControl.DataContext = _vm;
        }

        if (_paneControl.Visibility != Visibility.Visible)
        {
            // Expand the window and reveal the column
            var desiredWidth = settings.HelpPaneWidthPerHost?.GetValueOrDefault(_host.HostName)
                               ?? _defaultWidth;
            OnPaneOpening(desiredWidth);
            _paneControl.Visibility = Visibility.Visible;
        }

        _vm.IsPaneOpen = true;

        // Navigate to requested topic, persisted last topic, or dialog default
        if (topicId != null)
        {
            await _vm.NavigateToAsync(topicId);
        }
        else
        {
            var lastTopic = settings.HelpPaneLastTopicPerHost?.GetValueOrDefault(_host.HostName);
            await _vm.NavigateToAsync(lastTopic ?? _defaultTopicId);
        }
    }

    // ── F1 context help ────────────────────────────────────────────────────────

    /// <summary>
    /// Handles an F1 key press: resolves the focused element's contextual topic id
    /// (if any) and opens the help pane on it. Marks the event handled when F1 fired.
    /// </summary>
    public void HandleF1(KeyEventArgs e)
    {
        if (e.Key != Key.F1)
            return;

        var focused = FocusManager.GetFocusedElement(_window) as DependencyObject;
        var topicId = focused is not null
            ? GlobalF1HelpBehavior.ResolveTopicId(focused)
            : null;
        OpenHelpPane(topicId);
        e.Handled = true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists current HelpPane layout state (pane width, chat height, last topic)
    /// back to <see cref="AppSettings"/>.
    /// </summary>
    private void PersistState()
    {
        if (_vm is null) return;

        var settings = AppSettings.LoadFromFile();

        // Per-host pane width (only when the column is actually visible)
        if (_paneColumn.ActualWidth > 0)
        {
            settings.HelpPaneWidthPerHost ??= [];
            settings.HelpPaneWidthPerHost[_host.HostName] = _paneColumn.ActualWidth;
        }

        // Shared chat sub-pane height
        settings.HelpPaneChatHeight = _vm.ChatPaneHeight;

        // Per-host last topic
        var topicId = _vm.CurrentTopicId;
        if (topicId is not null)
        {
            settings.HelpPaneLastTopicPerHost ??= [];
            settings.HelpPaneLastTopicPerHost[_host.HostName] = topicId;
        }

        settings.SaveToFile();
    }

    private static void OnSessionWarning(object? sender, string msg)
        => MessageBox.Show(msg, "Help", MessageBoxButton.OK, MessageBoxImage.Information);
}
