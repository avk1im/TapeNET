using System.Windows;

namespace TapeWinNET.Help;

/// <summary>
/// Implemented by windows that host a <see cref="Controls.HelpPane"/>.
/// MainWindow uses <see cref="HelpPaneHostMode.Embedded"/>; dialogs use
/// <see cref="HelpPaneHostMode.Adjacent"/>.
/// </summary>
public interface IHelpPaneHost
{
    /// <summary>
    /// Identifier matched against <c>HelpTopic.Host</c> front-matter to select
    /// contextually relevant content (e.g. <c>"MainWindow"</c>, <c>"RestoreWindow"</c>).
    /// </summary>
    string HostName { get; }

    /// <summary>How the pane is embedded or positioned relative to this host.</summary>
    HelpPaneHostMode HostMode { get; }

    /// <summary>
    /// Called by the HelpPane before it becomes visible so the host can expand
    /// its layout to accommodate <paramref name="desiredWidth"/> pixels.
    /// </summary>
    void OnPaneOpening(double desiredWidth);

    /// <summary>Called by the HelpPane when it is closed/hidden.</summary>
    void OnPaneClosed();

    /// <summary>
    /// Resolves a named control for v2 overlay features (Reveal / Guide Me).
    /// May return <c>null</c> if the name is not registered or v2 is not supported.
    /// </summary>
    FrameworkElement? ResolveControlByName(string name);

    /// <summary>
    /// Opens the help pane (first call builds the session) and optionally
    /// navigates to <paramref name="topicId"/>.  Passing <c>null</c> restores
    /// the last viewed topic or navigates home.
    /// </summary>
    void OpenHelpPane(string? topicId = null);

    // ── Phase 8a: Reveal overlay hooks ───────────────────────────────────────

    /// <summary>
    /// Returns the element whose <see cref="System.Windows.Documents.AdornerLayer"/>
    /// hosts help overlays (Reveal / Walkthrough).
    /// <para>
    /// The default implementation looks for a child named <c>"HelpOverlayRoot"</c>
    /// (the host's Column-0 content grid).  Hosts opt in simply by naming that element;
    /// no code-behind override is required.
    /// </para>
    /// </summary>
    FrameworkElement? GetOverlayRoot()
        => this is FrameworkElement fe
            ? fe.FindName("HelpOverlayRoot") as FrameworkElement
            : null;

    /// <summary>
    /// Returns the topic id that represents <em>this</em> host's primary help page —
    /// used by the Reveal overlay to look up the <c>## Controls</c> chapter when the
    /// content pane is showing an unrelated topic.
    /// <para>
    /// The default returns <c>null</c>; hosts that support Reveal should override
    /// (dialogs return their dialog topic id; MainWindow returns <c>"ui.main-window"</c>).
    /// </para>
    /// </summary>
    string? GetDefaultTopicId() => null;
}

