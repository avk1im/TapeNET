namespace HelpNET.Content;

/// <summary>Lightweight reference to a topic, used in suggestions and citations lists.</summary>
/// <param name="Id">Topic id.</param>
/// <param name="Title">Display title.</param>
public sealed record HelpTopicRef(string Id, string Title);

/// <summary>
/// Reference to a host-defined action that can be invoked from the help pane
/// (e.g. opening a dialog, running a command).
/// </summary>
/// <param name="ActionId">Application-defined action identifier.</param>
/// <param name="DisplayName">Human-readable label shown on the action button.</param>
public sealed record HelpActionRef(string ActionId, string DisplayName);

/// <summary>
/// A citation linking a portion of an assistant answer back to its source topic.
/// </summary>
/// <param name="TopicId">Id of the cited topic.</param>
/// <param name="Title">Title of the cited topic.</param>
/// <param name="Excerpt">Short excerpt that backs the cited claim.</param>
public sealed record HelpCitation(string TopicId, string Title, string Excerpt);

/// <summary>Describes a navigation request directed at <see cref="IHelpSession"/>.</summary>
/// <param name="TopicId">The target topic id.</param>
/// <param name="ScrollToHeading">Optional heading anchor to scroll to after navigation.</param>
public sealed record HelpNavigationRequest(string TopicId, string? ScrollToHeading = null);

/// <summary>A single hit returned by a search or retrieval operation.</summary>
/// <param name="Topic">The matched topic.</param>
/// <param name="Score">Relevance score (higher is better).</param>
/// <param name="Excerpt">Short snippet from the topic that matched the query.</param>
public sealed record HelpSearchHit(HelpTopic Topic, float Score, string Excerpt);
