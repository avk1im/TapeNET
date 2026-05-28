namespace HelpNET.Content;

/// <summary>
/// A single step inside a <see cref="WalkthroughScript"/>.
/// </summary>
/// <param name="Target">
/// Name of the UI control this step points at (used by the overlay engine to
/// locate the live element via <c>IHelpPaneHost.ResolveControlByName</c>).
/// </param>
/// <param name="Title">Short heading shown in the callout balloon.</param>
/// <param name="Body">Markdown body shown below the title in the callout.</param>
public sealed record WalkthroughStep(
    string Target,
    string Title,
    string Body);

/// <summary>
/// A parsed walkthrough block from a topic's YAML front-matter.
/// Only present when <c>kind: walkthrough</c>.
/// </summary>
/// <param name="Steps">Ordered list of walkthrough steps.</param>
public sealed record WalkthroughScript(
    IReadOnlyList<WalkthroughStep> Steps);
