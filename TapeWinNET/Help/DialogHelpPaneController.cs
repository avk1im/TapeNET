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

    // Help-button labels for each state. The Help/Close labels carry the access
    //  key on "Help"; the Loading label needs none since the button is disabled.
    private const string IdleLabel    = "_Help ▶";
    private const string LoadingLabel = "Loading\u2026";
    private const string OpenLabel    = "◀ Close _Help";

    private readonly IHelpPaneHost      _host;
    private readonly Window             _window;
    private readonly ColumnDefinition   _paneColumn;
    private readonly FrameworkElement   _splitter;
    private readonly Controls.HelpPane  _paneControl;
    private readonly string             _defaultTopicId;
    private readonly double             _defaultWidth;
    private readonly double             _mininimalHeight;

    private double _originalHeight = -1.0; // captured on first pane open, used to restore window height when the pane closes

    // Optional toggle button this controller drives (label + enabled state).
    private readonly Button?            _helpButton;

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
    /// <param name="helpButton">
    /// Optional Help toggle button. When supplied, the controller manages its label and
    /// enabled state: <c>Help</c> when closed, <c>Loading…</c> (disabled) while the session
    /// builds, and <c>Close Help</c> once the pane is open. Wire the button's <c>Click</c>
    /// to <see cref="ToggleHelpPane"/>.
    /// </param>
    /// <param name="defaultWidth">Default pane width used when none is persisted.</param>
    /// <param name="minimalHeight">Minimal height of the dialog when Help pane opens.</param>
    public DialogHelpPaneController(
        IHelpPaneHost host,
        Window window,
        ColumnDefinition paneColumn,
        FrameworkElement splitter,
        Controls.HelpPane paneControl,
        string defaultTopicId,
        Button? helpButton = null,
        double defaultWidth = 340,
        double minimalHeight = 300)
    {
        _host           = host;
        _window         = window;
        _paneColumn     = paneColumn;
        _splitter       = splitter;
        _paneControl    = paneControl;
        _defaultTopicId = defaultTopicId;
        _helpButton     = helpButton;
        _defaultWidth   = defaultWidth;
        _mininimalHeight = minimalHeight;

        // Snapshot the initial (no-pane) window width
        _dialogContentWidth = window.Width;

        // Persist state whenever the dialog closes (the pane may still be visible)
        window.Closing += (_, _) => OnPaneClosed();
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

        // Check if the dialog height is at least _minimalHeight, expand downwards otehrwise
        if (_window.Height < _mininimalHeight)
        {
            _originalHeight = _window.Height; // capture the original height on first open
            _window.Height = _mininimalHeight;

            // If the window now protrudes beyond the bottom edge of the work area,
            // shift it up until it fits
            overhang = (_window.Top + _window.Height) - workArea.Bottom;
            if (overhang > 0)
                _window.Top = Math.Max(workArea.Top, _window.Top - overhang);
        }

        // Reveal the new column and splitter
        _paneColumn.Width    = new GridLength(desiredWidth, GridUnitType.Pixel);
        _paneColumn.MinWidth = 200;
        _splitter.Visibility = Visibility.Visible;
    }

    /// <summary>Persists state, then shrinks the window back and hides the pane.</summary>
    public void OnPaneClosed()
    {
        PersistState();

        // Shrink the window back to the dialog-content-only width...
        var removedWidth = _paneColumn.ActualWidth + SplitterWidth; // pane + splitter
        _window.Width = Math.Max(_dialogContentWidth, _window.Width - removedWidth);
        //  ...and original height
        if (_originalHeight > 0.0)
        {
            _window.Height = _originalHeight;
            _originalHeight = -1.0; // reset
        }

        _paneColumn.Width    = new GridLength(0);
        _paneColumn.MinWidth = 0;
        _splitter.Visibility    = Visibility.Collapsed;
        _paneControl.Visibility = Visibility.Collapsed;

        if (_vm is not null)
        {   
            _vm.SessionInfo -= OnSessionInfo;
            _vm.SessionWarning -= OnSessionWarning;
            _vm.SessionError -= OnSessionError;
        }

        // Reset the Help button to its idle state (also covers the in-pane close button,
        //  since that path routes through OnPaneClosed as well)
        SetButtonState(IdleLabel, enabled: true);
    }

    /// <summary>
    /// Toggles the help pane: opens it if closed, or closes it if already open.
    /// Wire the dialog's Help button <c>Click</c> to this method.
    /// </summary>
    public void ToggleHelpPane()
    {
        if (_paneControl.Visibility == Visibility.Visible)
            OnPaneClosed();
        else
            OpenHelpPane();
    }

    /// <summary>
    /// Opens the help pane embedded in this dialog (expanding the window to the right)
    /// and navigates to <paramref name="topicId"/>, the persisted last topic, or the
    /// dialog's own default topic. Builds the session on first call.
    /// </summary>
    public async void OpenHelpPane(string? topicId = null)
    {
        var settings = App.Settings;

        if (_vm == null)
        {
            // First open — building the session loads AI providers and can take a
            //  moment; show a disabled "Loading…" button until the pane is ready.
            SetButtonState(LoadingLabel, enabled: false);

            // First open — build the session and wire the VM.
            // Pass _defaultTopicId as the home topic so the Home button returns to
            //  this dialog's own topic rather than the application-wide home page.
            var session = await AppHelpSessionFactory.CreateAsync(_host, homeTopicId: _defaultTopicId);

            // Build a dialog-aware action router: borrows the full set of registered
            //  actions from MainWindow but wraps them so that clicking an action link:
            //  - does nothing (and activates this window) if the action would reopen
            //    this same dialog, or
            //  - asks for confirmation, closes this dialog, then runs the command.
            IHelpActionRouter router;
            if (Application.Current.MainWindow is MainWindow mw)
                router = new DialogHelpActionRouter(mw.BuildHelpActions(), _defaultTopicId, _window);
            else
                router = new HelpActionRouter(); // fallback: no actions registered

            _vm = new HelpPaneViewModel(session, _host, router)
            {
                // Restore persisted chat sub-pane height before binding so the
                //  DataContextChanged handler in HelpPane.xaml.cs applies it immediately
                ChatPaneHeight = settings.HelpPaneChatHeight ?? 200.0
            };

            _paneControl.DataContext = _vm;
        }

        if (_paneControl.Visibility != Visibility.Visible)
        {
            // Expand the window and reveal the column
            var desiredWidth = settings.HelpPaneWidthPerHost?.GetValueOrDefault(_host.HostName)
                               ?? _defaultWidth; // notice: default double value is 0.0, therefore...
            if (desiredWidth <= 0.0) // check for it here! and also for invalid negavtive values
                desiredWidth = _defaultWidth;
            OnPaneOpening(desiredWidth);
            _paneControl.Visibility = Visibility.Visible;
        }

        _vm.IsPaneOpen = true;

        // Dialogs have no log pane — surface warnings via MainWindow
        _vm.SessionInfo += OnSessionInfo;
        _vm.SessionWarning += OnSessionWarning;
        _vm.SessionError += OnSessionError;
        
        // Pane is now open — let the button close it on the next click
        SetButtonState(OpenLabel, enabled: true);

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

        // Walkthrough continuation handoff (§12.5.1):
        //  If the router carries a pending handoff from a main-window action step that opened
        //  this dialog, and this dialog has exactly one tour, auto-start it.
        if (_vm.GetActionRouter() is HelpActionRouter hardRouter
            && hardRouter.PendingWalkthroughHandoffActionId is not null)
        {
            hardRouter.ClearWalkthroughHandoff();
            var tours = _vm.Session.GetWalkthroughTopicsForHost(_host.HostName);
            if (tours.Count == 1)
                _vm.StartWalkthrough(tours[0].Topic, tours[0].Script);
        }
    }

    // ── F1 context help ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the default topic id for this dialog — used by Reveal to find the
    /// <c>## Controls</c> chapter when the content pane is showing a different topic.
    /// </summary>
    public string GetDefaultTopicId() => _defaultTopicId;

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
    /// Updates the optional Help toggle button's label and enabled state.
    /// No-ops when the dialog did not supply a button.
    /// </summary>
    private void SetButtonState(string label, bool enabled)
    {
        if (_helpButton is null)
            return;

        _helpButton.Content   = label;
        _helpButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Persists current HelpPane layout state (pane width, chat height, last topic)
    /// back to <see cref="AppSettings"/>.
    /// </summary>
    private void PersistState()
    {
        if (_vm is null) return;

        var settings = App.Settings;

        // Per-host pane width (only when the column is actually visible)
        if (_paneColumn.ActualWidth > 0.0)
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

        // settings.SaveToFile(); -- not needed, the app will save on exit
    }

    private static void OnSessionInfo(object? sender, string msg)
    {
        if (Application.Current.MainWindow is MainWindow mw)
            mw.Dispatcher.Invoke(() => mw.OnHelpSessionInfo(sender, msg));
    }
    private static void OnSessionWarning(object? sender, string msg)
    {
        if (Application.Current.MainWindow is MainWindow mw)
            mw.Dispatcher.Invoke(() => mw.OnHelpSessionWarning(sender, msg));
    }
    private static void OnSessionError(object? sender, string msg)
    {
        if (Application.Current.MainWindow is MainWindow mw)
            mw.Dispatcher.Invoke(() => mw.OnHelpSessionError(sender, msg));
    }

}
