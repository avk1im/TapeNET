namespace HelpNET.Content;

/// <summary>
/// Parser and validator for the <c>help://</c> URI scheme used in help content.
/// </summary>
/// <remarks>
/// Supported forms:
/// <list type="bullet">
///   <item><c>help://topic/&lt;id&gt;</c> — navigate to a topic</item>
///   <item><c>help://glossary/&lt;term&gt;</c> — show inline glossary popover</item>
///   <item><c>help://action/&lt;actionId&gt;</c> — invoke a host action</item>
/// </list>
/// </remarks>
public sealed class HelpUri
{
    /// <summary>The kind of <c>help://</c> URI.</summary>
    public HelpUriKind Kind { get; }

    /// <summary>
    /// The target value: a topic id, glossary term, or action id depending on <see cref="Kind"/>.
    /// </summary>
    public string Target { get; }

    private HelpUri(HelpUriKind kind, string target)
    {
        Kind = kind;
        Target = target;
    }

    /// <summary>
    /// Tries to parse a <c>help://</c> URI string.
    /// Returns <c>null</c> if the URI is not a recognised <c>help://</c> link.
    /// </summary>
    public static HelpUri? TryParse(string uriString)
    {
        if (!uriString.StartsWith("help://", StringComparison.OrdinalIgnoreCase))
            return null;

        // Strip scheme — remainder is "topic/<id>", "glossary/<term>", or "action/<id>"
        var path = uriString["help://".Length..];

        var slash = path.IndexOf('/');
        if (slash < 0)
            return null;

        var category = path[..slash];
        var target   = path[(slash + 1)..];

        if (string.IsNullOrEmpty(target))
            return null;

        return category.ToLowerInvariant() switch
        {
            "topic"    => new HelpUri(HelpUriKind.Topic,    target),
            "glossary" => new HelpUri(HelpUriKind.Glossary, target),
            "action"   => new HelpUri(HelpUriKind.Action,   target),
            _          => null,
        };
    }
}

/// <summary>Discriminates the three <c>help://</c> URI categories.</summary>
public enum HelpUriKind
{
    /// <summary>Navigate to a topic: <c>help://topic/&lt;id&gt;</c>.</summary>
    Topic,

    /// <summary>Show a glossary popover: <c>help://glossary/&lt;term&gt;</c>.</summary>
    Glossary,

    /// <summary>Invoke a host action: <c>help://action/&lt;actionId&gt;</c>.</summary>
    Action,
}
