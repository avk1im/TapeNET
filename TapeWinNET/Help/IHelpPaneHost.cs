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
}
