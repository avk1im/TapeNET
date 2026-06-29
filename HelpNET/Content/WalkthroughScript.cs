namespace HelpNET.Content;

/// <summary>
/// A single step inside a <see cref="WalkthroughScript"/>.
/// </summary>
/// <param name="Target">
/// Slugified name of the UI control this step points at (used by the overlay engine to
/// locate the live element via <c>IHelpPaneHost.ResolveControlByName</c>).
/// Empty string for action steps (<see cref="IsActionStep"/>).
/// </param>
/// <param name="Title">Short heading shown in the step panel.</param>
/// <param name="Body">Markdown body shown below the title in the pane.</param>
/// <param name="ActionId">
/// Non-null for action steps: the action id to invoke via the <c>HelpActionRouter</c>
/// when the user clicks "Do it ▶". Null for normal control steps.
/// </param>
public sealed record WalkthroughStep(
    string  Target,
    string  Title,
    string  Body,
    string? ActionId = null)
{
    /// <summary>
    /// <c>true</c> when this step opens a dialog/command rather than pointing at
    /// a control on screen. The step footer shows a "Do it ▶" button instead of "Next ▶".
    /// </summary>
    public bool IsActionStep => !string.IsNullOrEmpty(ActionId);
}

/// <summary>
/// A parsed walkthrough script for a <c>kind: walkthrough</c> topic.
/// Steps are sourced from the topic body's <c>## [Target] Title</c> sections.
/// </summary>
/// <param name="Steps">Ordered list of walkthrough steps.</param>
public sealed record WalkthroughScript(
    IReadOnlyList<WalkthroughStep> Steps);
