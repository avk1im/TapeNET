namespace HelpNET.Content;

/// <summary>
/// Categorises a help topic for routing, UI presentation, and assistant behaviour.
/// </summary>
public enum HelpTopicKind
{
    /// <summary>Conceptual background article.</summary>
    Concept,

    /// <summary>Step-by-step walkthrough with UI targets.</summary>
    Walkthrough,

    /// <summary>Reference material (keyboard shortcuts, FCL syntax, etc.).</summary>
    Reference,

    /// <summary>Maps a host window to its controls and sub-topics.</summary>
    UiMap,

    /// <summary>Short task-oriented getting-started guide.</summary>
    QuickStart,

    /// <summary>Feature overview article.</summary>
    Feature,

    /// <summary>Describes a specific dialog window.</summary>
    Dialog,

    /// <summary>The help home / landing page.</summary>
    Home,

    /// <summary>Glossary term definition.</summary>
    Glossary,
}
