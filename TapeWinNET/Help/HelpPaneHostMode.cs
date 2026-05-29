namespace TapeWinNET.Help;

/// <summary>
/// How the <see cref="IHelpPaneHost"/> embeds or positions the HelpPane.
/// </summary>
public enum HelpPaneHostMode
{
    /// <summary>
    /// The pane lives inside the host window as a right-side column
    /// (MainWindow pattern).
    /// </summary>
    Embedded,

    /// <summary>
    /// The pane is shown to the right of a dialog window.
    /// The host shifts/expands itself to accommodate the desired width
    /// via <see cref="HelpPaneLayoutCoordinator"/>.
    /// </summary>
    Adjacent,
}
